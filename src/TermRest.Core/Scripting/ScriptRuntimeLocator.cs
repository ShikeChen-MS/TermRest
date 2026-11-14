using System.ComponentModel;
using System.Diagnostics;

namespace TermRest.Core.Scripting;

/// <summary>Finds and checks the runtimes that run scripts: Python, Node.js or Bun for JavaScript, the .NET SDK for C#, and PowerShell.</summary>
public static class ScriptRuntimeLocator
{
    /// <summary>The oldest Python that runs TermRest's scripts.</summary>
    public static readonly Version MinimumPython = new(3, 8);

    /// <summary>The oldest Node.js that runs TermRest's scripts (fetch, structuredClone).</summary>
    public static readonly Version MinimumNode = new(18, 0);

    public static readonly Version MinimumBun = new(1, 1);

    /// <summary>The oldest .NET SDK that builds C# scripts: .NET 10 builds C# files without a project (dotnet run --file).</summary>
    public static readonly Version MinimumDotnetSdk = new(10, 0);

    /// <summary>The oldest PowerShell that runs TermRest's scripts: PowerShell 7 (pwsh), not Windows PowerShell 5.1.</summary>
    public static readonly Version MinimumPowerShell = new(7, 0);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runtimes worth trying for a workspace: for Python, its virtual environment, then <c>python3</c> and <c>python</c>
    /// on PATH; for JavaScript, <c>node</c> and <c>bun</c> on PATH; for C#, <c>dotnet</c> on PATH, then the .NET installed
    /// for the user (<c>~/.dotnet</c>); for PowerShell, <c>pwsh</c> on PATH, then where its installer puts it on Windows.
    /// </summary>
    public static IEnumerable<string> Candidates(string language, string workspaceRoot)
    {
        if (language == ScriptLanguage.PowerShell)
        {
            yield return "pwsh";
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installed = Path.Combine(programs, "PowerShell", "7", "pwsh.exe");
            if (OperatingSystem.IsWindows() && programs.Length > 0 && File.Exists(installed))
            {
                yield return installed;
            }

            yield break;
        }

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

    /// <summary>
    /// The first of the <see cref="Candidates"/> that runs, with its description; null when none does. Those in the
    /// workspace (its <c>.venv</c>'s Python) are not run, as they may have come with a repository: the first of them is
    /// the answer, unchecked (<c>Checked</c> is false), for the user to check. The others run as <see cref="CheckAsync"/>
    /// runs them: outside the workspace.
    /// </summary>
    /// <param name="resolve">Turns a candidate into what to start (relative paths are relative to the workspace).</param>
    public static async Task<(string Runtime, string Description, bool Checked)?> FindAsync(string language, string workspaceRoot,
        Func<string, string> resolve, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in Candidates(language, workspaceRoot))
        {
            if (!Path.IsPathRooted(candidate) && candidate.IndexOfAny(['/', '\\']) >= 0)
            {
                return (candidate, "Found in this workspace, not run: Check runs it.", false);
            }

            var (ok, description) = await CheckAsync(language, resolve(candidate), cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return (candidate, description, true);
            }
        }

        return null;
    }

    /// <summary>
    /// The program to start for <paramref name="runtime"/>: a path as it is, and a command (no folder: <c>python3</c>,
    /// <c>node</c>) from the folders of PATH, on Windows with <c>.exe</c> unless it ends with an extension that Windows
    /// runs (PATHEXT: <c>.exe</c>, <c>.cmd</c>…), so <c>python3.12</c> is <c>python3.12.exe</c>. Windows and .NET would
    /// look in TermRest's own folder and in the current one first (the workspace, often), where a repository may put a
    /// <c>node.exe</c> of its own. Null when the command is not on PATH.
    /// </summary>
    /// <param name="path">The folders to look in (PATH when null); relative ones, which are the current folder's, are not.</param>
    public static string? Executable(string runtime, string? path = null)
    {
        if (runtime.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(runtime))
        {
            return runtime;
        }

        var name = OperatingSystem.IsWindows() && !IsProgramName(runtime) ? runtime + ".exe" : runtime;
        foreach (var folder in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.TrimEntries))
        {
            var directory = folder.Trim('"');
            if (Path.IsPathFullyQualified(directory) && IsProgram(Path.Combine(directory, name)))
            {
                return Path.Combine(directory, name);
            }
        }

        return null;
    }

    /// <summary>Whether a command's name ends with an extension of programs on Windows (PATHEXT), as shells take it.</summary>
    private static bool IsProgramName(string name)
    {
        var extension = Path.GetExtension(name);
        var programs = Environment.GetEnvironmentVariable("PATHEXT") is { Length: > 0 } list ? list : ".COM;.EXE;.BAT;.CMD";
        return extension.Length > 1 && (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || programs.Split(';', StringSplitOptions.TrimEntries).Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsProgram(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(file);
        }

        return File.Exists(file) && (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    /// <summary>
    /// Runs the runtime once: its version and location, or why it cannot run scripts. It runs in TermRest's cache folder,
    /// not in the workspace, whose files it would obey before it is chosen: Bun runs the code that a bunfig.toml
    /// preloads, and dotnet lists the SDKs that a global.json points to.
    /// </summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(string language, string runtime, CancellationToken cancellationToken = default)
    {
        if (Executable(runtime) is not { } executable)
        {
            return (false, $"{runtime} could not be started: it is not on PATH.");
        }

        string folder;
        try
        {
            folder = CSharpPrograms.CreateCacheDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"{runtime} could not be checked: {ex.Message}");
        }

        var python = language == ScriptLanguage.Python;
        var csharp = language == ScriptLanguage.CSharp;
        var powerShell = language == ScriptLanguage.PowerShell;
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder,
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
        else if (powerShell)
        {
            foreach (var argument in (string[])["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "'PowerShell ' + $PSVersionTable.PSVersion; (Get-Process -Id $PID).Path"])
            {
                info.ArgumentList.Add(argument);
            }
        }
        else
        {
            info.ArgumentList.Add(python ? "-c" : "-e");
            info.ArgumentList.Add(python
                ? "import sys; print('Python ' + '.'.join(map(str, sys.version_info[:3]))); print(sys.executable)"
                : "console.log(typeof Bun !== 'undefined' ? 'Bun ' + Bun.version : 'Node.js ' + process.versions.node); console.log(process.execPath)");
        }

        var expected = python ? "Python" : powerShell ? "PowerShell" : "Node.js or Bun";
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
            var text = lines.Length >= 2 ? lines[0][(lines[0].LastIndexOf(' ') + 1)..] : "";
            var expectedName = python ? name == "Python" : powerShell ? name == "PowerShell" : name is "Node.js" or "Bun";
            if (process.ExitCode != 0 || lines.Length < 2 || !Version.TryParse(text.Split('-')[0], out var version) || !expectedName)
            {
                var reason = (await error.ConfigureAwait(false)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                return (false, $"{runtime} did not run as {expected} ({reason ?? $"exit code {process.ExitCode}"}).");
            }

            var minimum = name switch { "Python" => MinimumPython, "Bun" => MinimumBun, "PowerShell" => MinimumPowerShell, _ => MinimumNode };
            return version < minimum
                ? (false, $"{name} {text} is too old: scripts need {name} {minimum.ToString(2)} or later ({lines[1]}).")
                : (true, $"{name} {text} · {lines[1]}");
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
