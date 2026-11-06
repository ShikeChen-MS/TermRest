using System.ComponentModel;
using System.Diagnostics;

namespace TermRest.Core.Scripting;

/// <summary>Finds and checks a Python interpreter for scripts.</summary>
public static class PythonLocator
{
    /// <summary>The oldest Python that runs TermRest's scripts.</summary>
    public static readonly Version Minimum = new(3, 8);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Interpreters worth trying for a workspace: its virtual environment, then <c>python3</c> and <c>python</c> on PATH.</summary>
    public static IEnumerable<string> Candidates(string workspaceRoot)
    {
        foreach (var folder in (string[])[".venv", "venv", "env"])
        {
            var relative = OperatingSystem.IsWindows() ? Path.Combine(folder, "Scripts", "python.exe") : Path.Combine(folder, "bin", "python");
            if (File.Exists(Path.Combine(workspaceRoot, relative)))
            {
                yield return relative;
            }
        }

        yield return "python3";
        yield return "python";
    }

    /// <summary>The first of the <see cref="Candidates"/> that runs, with its description; null when none does.</summary>
    /// <param name="resolve">Turns a candidate into what to start (relative paths are relative to the workspace).</param>
    public static async Task<(string Interpreter, string Description)?> FindAsync(string workspaceRoot, Func<string, string> resolve, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in Candidates(workspaceRoot))
        {
            var (ok, description) = await CheckAsync(resolve(candidate), workspaceRoot, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return (candidate, description);
            }
        }

        return null;
    }

    /// <summary>Runs the interpreter once: its version and location, or why it cannot run scripts.</summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(string interpreter, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(interpreter)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import sys; print('.'.join(map(str, sys.version_info[:3]))); print(sys.executable)");
        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                return (false, $"{interpreter} could not be started.");
            }

            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(CheckTimeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            var lines = (await output.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (process.ExitCode != 0 || lines.Length < 2 || !Version.TryParse(lines[0], out var version))
            {
                var reason = (await error.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                return (false, $"{interpreter} did not run as Python ({reason ?? $"exit code {process.ExitCode}"}).");
            }

            return version < Minimum
                ? (false, $"Python {version} is too old: scripts need Python {Minimum.ToString(2)} or later ({lines[1]}).")
                : (true, $"Python {version} · {lines[1]}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return (false, $"{interpreter} could not be started: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"{interpreter} did not answer within {CheckTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // already gone
                }

                process.Dispose();
            }
        }
    }
}
