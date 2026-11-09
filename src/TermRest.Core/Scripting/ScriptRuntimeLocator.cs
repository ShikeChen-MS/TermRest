using System.ComponentModel;
using System.Diagnostics;

namespace TermRest.Core.Scripting;

/// <summary>Finds and checks the runtimes that run scripts: Python, Node.js or Bun for JavaScript, and the .NET SDK for C#.</summary>
public static class ScriptRuntimeLocator
{
    /// <summary>The oldest Python that runs TermRest's scripts.</summary>
    public static readonly Version MinimumPython = new(3, 8);

    /// <summary>The oldest Node.js that runs TermRest's scripts (fetch, structuredClone).</summary>
    public static readonly Version MinimumNode = new(18, 0);

    public static readonly Version MinimumBun = new(1, 1);

    /// <summary>The oldest .NET SDK that builds C# scripts: .NET 10 builds C# files without a project (dotnet run --file).</summary>
    public static readonly Version MinimumDotnetSdk = new(10, 0);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runtimes worth trying for a workspace: for Python, its virtual environment, then <c>python3</c> and <c>python</c>
    /// on PATH; for JavaScript, <c>node</c> and <c>bun</c> on PATH; for C#, <c>dotnet</c> on PATH, then the .NET installed
    /// for the user (<c>~/.dotnet</c>).
    /// </summary>
    public static IEnumerable<string> Candidates(string language, string workspaceRoot)
    {
        if (language == ScriptLanguage.CSharp)
        {
            yield return "dotnet";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var user = Path.Combine(home, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (home.Length > 0 && File.Exists(user))
            {
                yield return user;
            }

            yield break;
        }

        if (language != ScriptLanguage.Python)
        {
            yield return "node";
            yield return "bun";
            yield break;
        }

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
    public static async Task<(string Runtime, string Description)?> FindAsync(string language, string workspaceRoot, Func<string, string> resolve,
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in Candidates(language, workspaceRoot))
        {
            var (ok, description) = await CheckAsync(language, resolve(candidate), workspaceRoot, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return (candidate, description);
            }
        }

        return null;
    }

    /// <summary>Runs the runtime once: its version and location, or why it cannot run scripts.</summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(string language, string runtime, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var python = language == ScriptLanguage.Python;
        var csharp = language == ScriptLanguage.CSharp;
        var info = new ProcessStartInfo(runtime)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (csharp)
        {
            info.ArgumentList.Add("--list-sdks");
            info.Environment["DOTNET_NOLOGO"] = "1";
        }
        else
        {
            info.ArgumentList.Add(python ? "-c" : "-e");
            info.ArgumentList.Add(python
                ? "import sys; print('Python ' + '.'.join(map(str, sys.version_info[:3]))); print(sys.executable)"
                : "console.log(typeof Bun !== 'undefined' ? 'Bun ' + Bun.version : 'Node.js ' + process.versions.node); console.log(process.execPath)");
        }

        var expected = python ? "Python" : "Node.js or Bun";
        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                return (false, $"{runtime} could not be started.");
            }

            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(CheckTimeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            var lines = (await output.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (csharp)
            {
                return DescribeSdks(runtime, process.ExitCode, lines, await error.ConfigureAwait(false));
            }

            var name = lines.Length >= 2 ? lines[0][..Math.Max(0, lines[0].LastIndexOf(' '))] : "";
            if (process.ExitCode != 0 || lines.Length < 2 || !Version.TryParse(lines[0][(lines[0].LastIndexOf(' ') + 1)..], out var version)
                || (python ? name != "Python" : name is not ("Node.js" or "Bun")))
            {
                var reason = (await error.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                return (false, $"{runtime} did not run as {expected} ({reason ?? $"exit code {process.ExitCode}"}).");
            }

            var minimum = name switch { "Python" => MinimumPython, "Bun" => MinimumBun, _ => MinimumNode };
            return version < minimum
                ? (false, $"{name} {version} is too old: scripts need {name} {minimum.ToString(2)} or later ({lines[1]}).")
                : (true, $"{name} {version} · {lines[1]}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return (false, $"{runtime} could not be started: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"{runtime} did not answer within {CheckTimeout.TotalSeconds:0} s.");
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

    /// <summary>
    /// The newest SDK in <c>dotnet --list-sdks</c> (lines such as <c>10.0.403 [C:\Program Files\dotnet\sdk]</c>): it builds
    /// the scripts, as TermRest builds them outside the workspace (no global.json picks another).
    /// </summary>
    internal static (bool Ok, string Message) DescribeSdks(string runtime, int exitCode, string[] lines, string error)
    {
        var sdks = lines.Select(l => (Line: l, Space: l.IndexOf(' ', StringComparison.Ordinal)))
            .Where(l => l.Space > 0)
            .Select(l => (Text: l.Line[..l.Space], Folder: l.Line[(l.Space + 1)..].Trim().TrimStart('[').TrimEnd(']')))
            .Select(l => (l.Text, l.Folder, Version: Version.TryParse(l.Text.Split('-')[0], out var version) ? version : null))
            .Where(l => l.Version is not null)
            .OrderByDescending(l => l.Version)
            .ThenBy(l => l.Text.Contains('-', StringComparison.Ordinal)) // 10.0.100 before 10.0.100-rc.2
            .ToList();
        if (exitCode != 0 || sdks.Count == 0)
        {
            var reason = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return (false, $"{runtime} has no .NET SDK: C# scripts need the .NET {MinimumDotnetSdk.Major} SDK or later" +
                           $" (a .NET runtime alone cannot build them){(reason is null ? "" : $": {reason}")}.");
        }

        var (text, folder, newest) = sdks[0];
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? folder;
        return newest!.Major < MinimumDotnetSdk.Major
            ? (false, $".NET SDK {text} is too old: C# scripts need the .NET {MinimumDotnetSdk.Major} SDK or later ({root}).")
            : (true, $".NET SDK {text} · {root}");
    }
}
