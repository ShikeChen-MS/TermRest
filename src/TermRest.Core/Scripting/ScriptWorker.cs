using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TermRest.Core.Scripting;

/// <summary>
/// A process of the user's own runtime (Python, Node.js or Bun for JavaScript, .NET for C#, PowerShell) that runs
/// scripts. It is started for the first script, reused for the next ones (starting a runtime takes a tenth of a second
/// or more; a running one answers in milliseconds), and ends once it has been idle for <see cref="IdleTimeout"/>:
/// scripting costs nothing until it is used, and little once it is not. A script that takes longer than the timeout, or
/// a cancelled one, ends the process; the next run starts a new one. C# scripts are built into a program first
/// (<see cref="CSharpPrograms"/>), which the process runs; other scripts start another program.
/// </summary>
public sealed partial class ScriptWorker : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How many of the last characters the runtime wrote besides its answers are kept (see <see cref="Remember"/>).</summary>
    private const int Kept = 4000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly char[] _said = new char[2 * Kept];
    private readonly string _workingDirectory;
    private readonly string? _preamble;
    private readonly string? _replyMarker;
    private readonly CSharpPrograms? _programs;
    private readonly bool _ownSession;
    private string[] _arguments;
    private Process? _process;
    private Task _errorReader = Task.CompletedTask;
    private int _saidLength;
    private volatile bool _disposed;

    private ScriptWorker(string runtime, string name, string workingDirectory, string[] arguments, string? preamble, string? replyMarker,
        CSharpPrograms? programs = null, bool ownSession = false)
    {
        Runtime = runtime;
        Name = name;
        _workingDirectory = workingDirectory;
        _arguments = arguments;
        _preamble = preamble;
        _replyMarker = replyMarker;
        _programs = programs;
        _ownSession = ownSession;
        _idleTimer = new Timer(_ => StopWhenIdle());
    }

    /// <summary>The runtime's executable, for example a venv's <c>python</c>, <c>node</c>, <c>dotnet</c> or <c>pwsh</c>.</summary>
    public string Runtime { get; }

    /// <summary>What to call the runtime in messages: Python, Node.js, Bun, .NET or PowerShell.</summary>
    public string Name { get; }

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the runtime's process is running now.</summary>
    public bool IsRunning
    {
        get
        {
            try
            {
                return Volatile.Read(ref _process) is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false; // stopped (and disposed) just as it was read
            }
        }
    }

    /// <summary>A worker that runs scripts in <paramref name="language"/> with <paramref name="runtime"/>.</summary>
    public static ScriptWorker For(string language, string runtime, string workingDirectory)
    {
        if (language == ScriptLanguage.Python)
        {
            return new(runtime, "Python", workingDirectory, ["-X", "utf8", "-u", "-c", PythonWorkerCode.Bootstrap], PythonWorkerCode.Preamble, null);
        }

        if (language == ScriptLanguage.CSharp)
        {
            return new(runtime, ".NET", workingDirectory, [], null, CSharpWorkerCode.ReplyMarker, new CSharpPrograms(runtime, workingDirectory));
        }

        if (language == ScriptLanguage.PowerShell)
        {
            return new(runtime, "PowerShell", workingDirectory, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", PowerShellWorkerCode.Bootstrap],
                PowerShellWorkerCode.Preamble, PowerShellWorkerCode.ReplyMarker);
        }

        // Node.js (V8) halves its memory with a small young generation and size-optimized code: 35 MB private instead
        // of 70, for 40 ms more at start. Bun has its own engine and options.
        var name = ScriptLanguage.JavaScriptRuntimeName(runtime);
        string[] tuning = name == "Node.js" ? ["--max-semi-space-size=1", "--optimize-for-size"] : [];
        return new(runtime, name, workingDirectory, [.. tuning, "-e", JavaScriptWorkerCode.Bootstrap],
            JsonValue.Create(JavaScriptWorkerCode.Source).ToJsonString(JsonOptions), JavaScriptWorkerCode.ReplyMarker, ownSession: true);
    }

    /// <summary>Sends one message to the runtime and returns its answer.</summary>
    /// <param name="related">
    /// Scripts that may run next, in the same language (C#: the collection's, built into the same program so that its
    /// other requests need no new build).
    /// </param>
    /// <exception cref="ScriptException">The runtime could not start, stopped, or did not answer within <paramref name="timeout"/>.</exception>
    public async Task<JsonObject> RunAsync(JsonObject message, TimeSpan timeout, IEnumerable<ScriptCode>? related = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var note = _programs is null ? null : await UseProgramAsync(message, related ?? [], cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this); // stopped while C# scripts were built: their program does not start
            var started = _process is not { HasExited: false };
            var process = started ? Start() : _process!;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                if (started && _preamble is not null)
                {
                    await process.StandardInput.WriteLineAsync(_preamble.AsMemory(), limit.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
                }

                await WriteAsync(process.StandardInput.BaseStream, message, limit.Token).ConfigureAwait(false);
                var line = await ReadReplyAsync(process, limit.Token).ConfigureAwait(false);
                if (line is null)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this); // stopped while the scripts ran

                    // Why it stopped is in what it wrote last, which stderr brings soon (at once, unless a program that a
                    // script started still holds stderr open).
                    await Task.WhenAny(_errorReader, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
                    var diagnostics = Diagnostics();
                    Stop(kill: true);
                    if (started && _programs is not null)
                    {
                        CSharpPrograms.Forget(_arguments[0]); // built again next time
                    }

                    throw new ScriptException($"{Name} stopped.{diagnostics}");
                }

                var reply = JsonNode.Parse(WellFormed(line)) as JsonObject ?? throw new ScriptException($"{Name} answered with something that is not JSON.");
                if (reply["fatal"]?.GetValue<string>() is { } fatal)
                {
                    throw new ScriptException($"The script runner failed:\n{fatal}");
                }

                if (note is not null)
                {
                    reply["notes"] = new JsonArray(note);
                }

                return reply;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Stop(kill: true);
                throw new ScriptException($"The scripts did not finish within {timeout.TotalSeconds:0} s, so {Name} was stopped.");
            }
            catch (OperationCanceledException)
            {
                Stop(kill: true); // a cancelled script may still be running
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException and not ObjectDisposedException)
            {
                Stop(kill: true);
                ObjectDisposedException.ThrowIf(_disposed, this); // stopped while the scripts ran, so the pipes closed
                throw new ScriptException($"{Name} stopped: {ex.Message}{Diagnostics()}", ex);
            }
        }
        finally
        {
            try
            {
                if (_disposed)
                {
                    Stop(kill: false); // stopped meanwhile: a process started since must not outlive the worker
                }
                else
                {
                    _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
                }
            }
            catch (ObjectDisposedException)
            {
                Stop(kill: false); // stopped just now, between the check and the timer, which is disposed
            }
            finally
            {
                _gate.Release(); // or a run waiting for this one would wait until cancelled
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer.Dispose();

        // A runtime that runs a script reads the end of its input only once the script ends: it is stopped at once instead
        // of holding the caller up (Tools › Scripts › Stop scripts, on the UI thread), and the run reports that it was.
        Stop(kill: _gate.CurrentCount == 0);
    }

    /// <summary>
    /// C#: builds (or finds) the program with the message's scripts, and runs it instead of the one running now. The
    /// build does not count towards the scripts' timeout. Returns a note when a build was needed.
    /// </summary>
    private async Task<string?> UseProgramAsync(JsonObject message, IEnumerable<ScriptCode> related, CancellationToken cancellationToken)
    {
        string program;
        string? note;
        try
        {
            (program, note) = await _programs!.PrepareAsync(message, related, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScriptException($"The C# scripts could not be built: {ex.Message}", ex);
        }

        if (_arguments.Length != 1 || _arguments[0] != program)
        {
            Stop(kill: false);
            _arguments = [program];
        }

        return note;
    }

    /// <summary>
    /// Writes the message as one line of UTF-8 JSON, straight to the runtime's input. Long texts (a response body…) and
    /// bytes (a binary body, as base64) are written in parts: System.Text.Json would otherwise need a few times their size.
    /// </summary>
    private static async Task WriteAsync(Stream input, JsonObject message, CancellationToken cancellationToken)
    {
        await using (var writer = new Utf8JsonWriter(input, WriterOptions))
        {
            await WriteAsync(writer, message, cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await input.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Utf8JsonWriter writer, JsonNode? node, CancellationToken cancellationToken)
    {
        const int Part = 3 << 15; // characters or bytes (a multiple of 3: whole base64 groups)
        switch (node)
        {
            case JsonObject members:
                writer.WriteStartObject();
                foreach (var (name, value) in members)
                {
                    writer.WritePropertyName(name);
                    await WriteAsync(writer, value, cancellationToken).ConfigureAwait(false);
                }

                writer.WriteEndObject();
                break;
            case JsonArray items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    await WriteAsync(writer, item, cancellationToken).ConfigureAwait(false);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > Part:
                for (var at = 0; at < text.Length; at += Part)
                {
                    writer.WriteStringValueSegment(text.AsSpan(at, Math.Min(Part, text.Length - at)), at + Part >= text.Length);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            case JsonValue value when value.TryGetValue<byte[]>(out var bytes):
                var offset = 0;
                do
                {
                    var length = Math.Min(Part, bytes.Length - offset);
                    writer.WriteBase64StringSegment(bytes.AsSpan(offset, length), offset + length == bytes.Length);
                    offset += length;
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                while (offset < bytes.Length);
                break;
            case null:
                writer.WriteNullValue();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    /// <summary>The reply line; with a reply marker, other lines (output of programs that scripts started) are skipped.</summary>
    private async Task<string?> ReadReplyAsync(Process process, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (_replyMarker is null)
            {
                return line;
            }

            var at = line.IndexOf(_replyMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                return line[(at + _replyMarker.Length)..];
            }

            Remember(line);
            Remember("\n");
        }

        return null;
    }

    /// <summary>
    /// The reply with escaped halves of surrogate pairs that stand alone (\ud83d: a script's substring of an emoji, as
    /// JavaScript and Python write it) as U+FFFD: they are not text, and System.Text.Json refuses to read them as text.
    /// </summary>
    private static string WellFormed(string reply)
    {
        if (reply.Contains(@"\ud", StringComparison.OrdinalIgnoreCase))
        {
            // Whole pairs (emoji, which Python and .NET write as escapes) need no copy: a match of 6 characters is a half.
            foreach (var escape in Escape().EnumerateMatches(reply))
            {
                if (escape.Length == 6)
                {
                    return Escape().Replace(reply, m => m.Groups[1].Success ? @"\uFFFD" : m.Value);
                }
            }
        }

        return reply;
    }

    private Process Start()
    {
        var executable = ScriptRuntimeLocator.Executable(Runtime) ?? throw new ScriptException($"{Name} could not be started ({Runtime}): it is not on PATH.");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // A hidden console of its own (on Windows, about 1 MB): programs that scripts start, such as git or ssh
            // asking for a password, then cannot write over TermRest's screen or take its keystrokes.
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        // On Linux and macOS, the workers leave TermRest's terminal themselves, for a session of their own: programs that
        // scripts start (ssh, sudo, gpg) then cannot open /dev/tty to ask over TermRest's screen, and fail at once. Node.js
        // and Bun cannot, so on Linux setsid starts them in one; as it is not a process group leader, it does not fork but
        // becomes the runtime, the process that TermRest stops.
        if (_ownSession && OperatingSystem.IsLinux() && ScriptRuntimeLocator.Executable("setsid") is { } setsid)
        {
            info.FileName = setsid;
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(executable);
        }

        foreach (var argument in _arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        lock (_said)
        {
            _saidLength = 0;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ScriptException($"{Name} could not be started: {Runtime}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ScriptException($"{Name} could not be started ({Runtime}): {ex.Message}", ex);
        }

        _errorReader = ReadErrorsAsync(process.StandardError);
        Volatile.Write(ref _process, process);
        return process;
    }

    /// <summary>
    /// Reads stderr, where the runtime says why it stops and where the Python, C# and PowerShell workers send the output of
    /// programs that scripts start, until it ends; only its end is kept. Process's own reader would hold each line whole
    /// first: a program printing hundreds of megabytes without a newline (curl -s) would take as much memory.
    /// </summary>
    private async Task ReadErrorsAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var afterReturn = false;
        try
        {
            using (reader)
            {
                int read;
                while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    // Lines end with \n here, as they did from the line reader (\r\n, \r or \n).
                    var length = 0;
                    for (var i = 0; i < read; i++)
                    {
                        var c = buffer[i];
                        if (c != '\n' || !afterReturn)
                        {
                            buffer[length++] = c == '\r' ? '\n' : c;
                        }

                        afterReturn = c == '\r';
                    }

                    Remember(buffer.AsSpan(0, length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the runtime is gone
        }
    }

    /// <summary>Keeps the last 4,000 characters the runtime wrote besides its answers (why it stopped, usually), in a buffer of a fixed size.</summary>
    private void Remember(ReadOnlySpan<char> text)
    {
        lock (_said)
        {
            if (text.Length > Kept)
            {
                text = text[^Kept..];
            }

            if (_saidLength + text.Length > _said.Length)
            {
                var kept = Kept - text.Length;
                _said.AsSpan(_saidLength - kept, kept).CopyTo(_said);
                _saidLength = kept;
            }

            text.CopyTo(_said.AsSpan(_saidLength));
            _saidLength += text.Length;
        }
    }

    private string Diagnostics()
    {
        lock (_said)
        {
            var text = _said.AsSpan(Math.Max(0, _saidLength - Kept), Math.Min(_saidLength, Kept));

            // Not half of a surrogate pair (an emoji) where the text was cut, or whose other half is still to be read: it is
            // not text, and the UI cannot draw it.
            if (text.Length > 0 && char.IsLowSurrogate(text[0]))
            {
                text = text[1..];
            }

            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }

            text = text.Trim();
            return text.Length == 0 ? "" : string.Concat("\n", text);
        }
    }

    private void StopWhenIdle()
    {
        // A run in progress re-arms the timer when it ends.
        if (_gate.Wait(0))
        {
            try
            {
                Stop(kill: false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void Stop(bool kill)
    {
        if (Interlocked.Exchange(ref _process, null) is not { } process)
        {
            return;
        }

        try
        {
            if (!kill && !process.HasExited)
            {
                process.StandardInput.Close(); // the worker ends at the end of its input
                if (process.WaitForExit(2000))
                {
                    return;
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); // a venv's python.exe may be a launcher with a child
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or NotSupportedException)
        {
            // already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    // A JSON escape: a surrogate pair, a lone half of one (group 1), or any other (\\, \", \u00e9…), so that the halves are
    // found in order (\\ud83d is a backslash, then text).
    [GeneratedRegex(@"\\(?:u(?:d[89ab][0-9a-f]{2}\\ud[c-f][0-9a-f]{2}|(d[89a-f][0-9a-f]{2}))|[\s\S])", RegexOptions.IgnoreCase)]
    private static partial Regex Escape();
}
