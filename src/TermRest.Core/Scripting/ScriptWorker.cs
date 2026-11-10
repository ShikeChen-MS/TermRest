using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermRest.Core.Scripting;

/// <summary>
/// A process of the user's own runtime (Python, Node.js or Bun for JavaScript, .NET for C#, PowerShell) that runs
/// scripts. It is started for the first script, reused for the next ones (starting a runtime takes a tenth of a second
/// or more; a running one answers in milliseconds), and ends once it has been idle for <see cref="IdleTimeout"/>:
/// scripting costs nothing until it is used, and little once it is not. A script that takes longer than the timeout, or
/// a cancelled one, ends the process; the next run starts a new one. C# scripts are built into a program first
/// (<see cref="CSharpPrograms"/>), which the process runs; other scripts start another program.
/// </summary>
public sealed class ScriptWorker : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly StringBuilder _stderr = new();
    private readonly string _workingDirectory;
    private readonly string? _preamble;
    private readonly string? _replyMarker;
    private readonly CSharpPrograms? _programs;
    private string[] _arguments;
    private Process? _process;
    private volatile bool _disposed;

    private ScriptWorker(string runtime, string name, string workingDirectory, string[] arguments, string? preamble, string? replyMarker,
        CSharpPrograms? programs = null)
    {
        Runtime = runtime;
        Name = name;
        _workingDirectory = workingDirectory;
        _arguments = arguments;
        _preamble = preamble;
        _replyMarker = replyMarker;
        _programs = programs;
        _idleTimer = new Timer(_ => StopWhenIdle());
    }

    /// <summary>The runtime's executable, for example a venv's <c>python</c>, <c>node</c>, <c>dotnet</c> or <c>pwsh</c>.</summary>
    public string Runtime { get; }

    /// <summary>What to call the runtime in messages: Python, Node.js, Bun, .NET or PowerShell.</summary>
    public string Name { get; }

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the runtime's process is running now.</summary>
    public bool IsRunning => Volatile.Read(ref _process) is { HasExited: false };

    /// <summary>A worker that runs scripts in <paramref name="language"/> with <paramref name="runtime"/>.</summary>
    public static ScriptWorker For(string language, string runtime, string workingDirectory)
    {
        if (language == ScriptLanguage.Python)
        {
            return new(runtime, "Python", workingDirectory, ["-X", "utf8", "-u", "-c", PythonWorkerCode.Source], null, null);
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
            JsonValue.Create(JavaScriptWorkerCode.Source).ToJsonString(JsonOptions), JavaScriptWorkerCode.ReplyMarker);
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
            var started = _process is not { HasExited: false };
            var process = started ? Start() : _process!;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                if (started && _preamble is not null)
                {
                    await process.StandardInput.WriteLineAsync(_preamble.AsMemory(), limit.Token).ConfigureAwait(false);
                }

                await process.StandardInput.WriteLineAsync(message.ToJsonString(JsonOptions).AsMemory(), limit.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
                var line = await ReadReplyAsync(process, limit.Token).ConfigureAwait(false);
                if (line is null)
                {
                    var diagnostics = Diagnostics();
                    Stop(kill: true);
                    if (started && _programs is not null)
                    {
                        CSharpPrograms.Forget(_arguments[0]); // built again next time
                    }

                    throw new ScriptException($"{Name} stopped.{diagnostics}");
                }

                var reply = JsonNode.Parse(line) as JsonObject ?? throw new ScriptException($"{Name} answered with something that is not JSON.");
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
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                Stop(kill: true);
                throw new ScriptException($"{Name} stopped: {ex.Message}{Diagnostics()}", ex);
            }
        }
        finally
        {
            if (!_disposed)
            {
                _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }

            _gate.Release();
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
        Stop(kill: false);
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
        }

        return null;
    }

    private Process Start()
    {
        var info = new ProcessStartInfo(Runtime)
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
        foreach (var argument in _arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        lock (_stderr)
        {
            _stderr.Clear();
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

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } text)
            {
                Remember(text);
            }
        };
        process.BeginErrorReadLine();
        Volatile.Write(ref _process, process);
        return process;
    }

    /// <summary>Keeps the last 4,000 characters the runtime wrote besides its answers (why it stopped, usually).</summary>
    private void Remember(string text)
    {
        lock (_stderr)
        {
            _stderr.Append(text).Append('\n');
            if (_stderr.Length > 4000)
            {
                _stderr.Remove(0, _stderr.Length - 4000);
            }
        }
    }

    private string Diagnostics()
    {
        lock (_stderr)
        {
            var text = _stderr.ToString().Trim();
            return text.Length == 0 ? "" : $"\n{text}";
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
}
