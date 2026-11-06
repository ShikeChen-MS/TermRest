using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermRest.Core.Scripting;

/// <summary>
/// A Python process that runs scripts. It is started for the first script, reused for the next ones (starting Python
/// takes a tenth of a second or more; a reused process answers in a fraction of a millisecond), and ends once it has
/// been idle for <see cref="IdleTimeout"/>: scripting costs nothing until it is used, and little once it is not.
/// A script that takes longer than the timeout, or a cancelled one, ends the process; the next run starts a new one.
/// </summary>
public sealed class PythonWorker : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly StringBuilder _stderr = new();
    private readonly string _workingDirectory;
    private Process? _process;
    private volatile bool _disposed;

    public PythonWorker(string interpreter, string workingDirectory)
    {
        Interpreter = interpreter;
        _workingDirectory = workingDirectory;
        _idleTimer = new Timer(_ => StopWhenIdle());
    }

    /// <summary>The Python executable, for example a venv's <c>python</c>.</summary>
    public string Interpreter { get; }

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the Python process is running now.</summary>
    public bool IsRunning => Volatile.Read(ref _process) is { HasExited: false };

    /// <summary>Sends one message to Python and returns its answer.</summary>
    /// <exception cref="ScriptException">Python could not start, stopped, or did not answer within <paramref name="timeout"/>.</exception>
    public async Task<JsonObject> RunAsync(JsonObject message, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var process = _process is { HasExited: false } running ? running : Start();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.StandardInput.WriteLineAsync(message.ToJsonString(JsonOptions).AsMemory(), limit.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
                var line = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
                if (line is null)
                {
                    var diagnostics = Diagnostics();
                    Stop(kill: true);
                    throw new ScriptException($"Python stopped.{diagnostics}");
                }

                var reply = JsonNode.Parse(line) as JsonObject ?? throw new ScriptException("Python answered with something that is not JSON.");
                if (reply["fatal"]?.GetValue<string>() is { } fatal)
                {
                    throw new ScriptException($"The script runner failed:\n{fatal}");
                }

                return reply;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Stop(kill: true);
                throw new ScriptException($"The scripts did not finish within {timeout.TotalSeconds:0} s, so Python was stopped.");
            }
            catch (OperationCanceledException)
            {
                Stop(kill: true); // a cancelled script may still be running
                throw;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                Stop(kill: true);
                throw new ScriptException($"Python stopped: {ex.Message}{Diagnostics()}", ex);
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

    private Process Start()
    {
        var info = new ProcessStartInfo(Interpreter)
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
        foreach (var argument in (string[])["-X", "utf8", "-u", "-c", PythonWorkerCode.Source])
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
            process = Process.Start(info) ?? throw new ScriptException($"Python could not be started: {Interpreter}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ScriptException($"Python could not be started ({Interpreter}): {ex.Message}", ex);
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } text)
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
        };
        process.BeginErrorReadLine();
        Volatile.Write(ref _process, process);
        return process;
    }

    /// <summary>What Python wrote to stderr lately (why it stopped, usually).</summary>
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
