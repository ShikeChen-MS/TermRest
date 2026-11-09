using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TermRest.Core.Scripting;

/// <summary>
/// Builds C# scripts into programs with the user's .NET SDK (10 or later). The scripts are written into one C# file
/// (<see cref="CSharpWorkerCode"/>), always the same one, which <c>dotnet run --file</c> builds: a change to the code
/// alone builds in about half a second. Each program that built is copied into the cache folder, named after its
/// content, and runs from there: a running program never blocks the next build (not even another TermRest's), and
/// scripts built once start at once the next time.
/// </summary>
internal sealed partial class CSharpPrograms(string dotnet, string workspaceRoot)
{
    /// <summary>How many built programs are kept (each a few hundred KB, with its packages).</summary>
    private const int Kept = 16;

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Programs that did not compile, by key: the same code fails again, so it is not built again.</summary>
    private readonly Dictionary<string, List<CSharpError>> _failed = new(StringComparer.Ordinal);

    /// <summary>Whether this TermRest built C# scripts since it last stopped the compiler server.</summary>
    private static int _built;

    private bool _sdkChecked;

    /// <summary>Environment variable that moves TermRest's cache folder.</summary>
    public const string CacheVariable = "TERMREST_CACHE_DIR";

    /// <summary>
    /// The folder of built C# scripts: <c>$TERMREST_CACHE_DIR/csharp</c>, else in <c>%LOCALAPPDATA%\TermRest</c>
    /// (Windows), <c>~/Library/Caches/TermRest</c> (macOS) or <c>$XDG_CACHE_HOME/termrest</c> (<c>~/.cache/termrest</c>).
    /// </summary>
    public static string CacheDirectory()
    {
        string root;
        if (Environment.GetEnvironmentVariable(CacheVariable) is { Length: > 0 } custom)
        {
            root = Path.GetFullPath(custom);
        }
        else if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(local.Length > 0 ? local : Path.GetTempPath(), "TermRest");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            root = home.Length == 0 ? Path.Combine(Path.GetTempPath(), "termrest-cache")
                : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "TermRest")
                : Path.Combine(string.IsNullOrEmpty(cache) ? Path.Combine(home, ".cache") : cache, "termrest");
        }

        return Path.Combine(root, "csharp");
    }

    /// <summary>
    /// Makes sure a program can run the scripts of <paramref name="message"/>: it holds them and the other scripts
    /// that may run next (<paramref name="related"/>: the collection's), so the next requests need no new build. Each
    /// script in the message gets its <c>id</c>; one that does not compile gets its <c>error</c>, which it reports when
    /// its turn comes (the scripts before it run, as in Python). Errors in related scripts only leave them out.
    /// </summary>
    /// <returns>The program to run, and a note when it had to be built.</returns>
    /// <exception cref="ScriptException">The scripts could not be built at all (not for errors in them).</exception>
    public async Task<(string Program, string? Note)> PrepareAsync(JsonObject message, IEnumerable<ScriptCode> related, CancellationToken cancellationToken)
    {
        var scripts = (message["scripts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            var code = CSharpWorkerCode.Normalize(script["code"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "");
            var id = CSharpWorkerCode.Id(code);
            script["id"] = id;
            codes[id] = code;
        }

        var needed = codes.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var script in related)
        {
            var code = CSharpWorkerCode.Normalize(script.Code);
            codes.TryAdd(CSharpWorkerCode.Id(code), code);
        }

        var program = codes.Keys.ToHashSet(StringComparer.Ordinal);
        var broken = new Dictionary<string, List<CSharpError>>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        var built = false;
        for (var round = 0; round < 8; round++)
        {
            var (path, errors, compiled) = await BuildAsync(program, codes, cancellationToken).ConfigureAwait(false);
            built |= compiled;
            if (path is not null)
            {
                foreach (var script in scripts)
                {
                    if (broken.TryGetValue(script["id"]!.GetValue<string>(), out var found))
                    {
                        var (text, traceback) = Describe(found, script["name"]?.GetValue<string>() ?? "", codes[script["id"]!.GetValue<string>()]);
                        script["error"] = new JsonObject { ["message"] = text, ["traceback"] = traceback };
                    }
                }

                var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                return (path, built ? $"The C# scripts were built ({seconds} s; they start at once next time)." : null);
            }

            var inScripts = errors.Where(e => e.Script is not null && program.Contains(e.Script)).GroupBy(e => e.Script!).ToList();
            if (inScripts.Count > 0)
            {
                foreach (var group in inScripts)
                {
                    broken[group.Key] = [.. group];
                    program.Remove(group.Key);
                }

                continue;
            }

            if (program.Any(id => !needed.Contains(id)))
            {
                program.IntersectWith(needed); // the cause may be another script of the collection
                continue;
            }

            // Errors outside the scripts' lines, from a script that does not end (an unclosed { or comment) or from a
            // directive: each script built alone shows which (one left is the one).
            if (program.Count == 1)
            {
                broken[program.Single()] = errors;
                program.Clear();
                continue;
            }

            var culprits = 0;
            foreach (var id in program.ToList())
            {
                var (_, alone, compiledAlone) = await BuildAsync([id], codes, cancellationToken).ConfigureAwait(false);
                built |= compiledAlone;
                if (alone.Count > 0)
                {
                    broken[id] = alone;
                    program.Remove(id);
                    culprits++;
                }
            }

            if (culprits == 0)
            {
                throw new ScriptException("The C# scripts could not be built:\n" + string.Join('\n', errors.Select(e => e.Text).Take(12)));
            }
        }

        throw new ScriptException("The C# scripts could not be built.");
    }

    /// <summary>Forgets a built program that could not start: the next run builds it again.</summary>
    public static void Forget(string program)
    {
        try
        {
            File.Delete(Path.Combine(Path.GetDirectoryName(program)!, ".ok"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // gone already
        }
    }

    /// <summary>
    /// Stops the .NET SDK's C# compiler server when this TermRest built scripts: builds keep it running for 10 minutes
    /// (100 MB or more), as every <c>dotnet build</c> does. It starts again for the next build, which then takes a few
    /// seconds instead of one. Runs in the background.
    /// </summary>
    public static void StopCompilerServer(string dotnet)
    {
        if (Interlocked.Exchange(ref _built, 0) == 0)
        {
            return;
        }

        try
        {
            var info = new ProcessStartInfo(dotnet, ["build-server", "shutdown", "--vbcscompiler"])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.Environment["DOTNET_NOLOGO"] = "1";
            var process = Process.Start(info);
            if (process is not null)
            {
                _ = Task.Run(async () =>
                {
                    using (process)
                    {
                        process.StandardInput.Close();
                        await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync()).ConfigureAwait(false);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                });
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // no SDK to ask
        }
    }

    /// <summary>The program of these scripts: a copy built before, or a new build. Null with the errors when it does not build.</summary>
    private async Task<(string? Program, List<CSharpError> Errors, bool Built)> BuildAsync(IEnumerable<string> ids, Dictionary<string, string> codes,
        CancellationToken cancellationToken)
    {
        var source = CSharpWorkerCode.Generate(ids.ToDictionary(id => id, id => codes[id], StringComparer.Ordinal), workspaceRoot);
        var key = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(dotnet + "\n" + source)))[..20];
        var cache = CacheDirectory();
        var copy = Path.Combine(cache, "programs", key);
        if (Ready(copy) is { } ready)
        {
            return (ready, [], false);
        }

        lock (_failed)
        {
            if (_failed.TryGetValue(key, out var known))
            {
                return (null, known, false);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(cache);
        }
        else
        {
            // The scripts' code is in here: for the user only, as the history is.
            Directory.CreateDirectory(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.CreateDirectory(Path.Combine(cache, "programs"));
        using var gate = await LockAsync(Path.Combine(cache, "build.lock"), cancellationToken).ConfigureAwait(false);
        if (Ready(copy) is { } builtMeanwhile)
        {
            return (builtMeanwhile, [], false); // by another TermRest
        }

        var folder = Path.Combine(cache, "source");
        var file = Path.Combine(folder, CSharpWorkerCode.AssemblyName + ".cs");
        Directory.CreateDirectory(folder);
        await CheckSdkAsync(folder, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(file) || await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) != source)
        {
            await File.WriteAllTextAsync(file, source, Utf8, cancellationToken).ConfigureAwait(false);
        }

        var (exitCode, output) = await RunAsync(["run", "--file", file, "--", CSharpWorkerCode.WhereArgument], folder, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _built, 1);
        var where = output.Select(l => l.IndexOf(CSharpWorkerCode.ReplyMarker + "where ", StringComparison.Ordinal) is var at and >= 0
            ? l[(at + CSharpWorkerCode.ReplyMarker.Length + 6)..].Trim() : null).LastOrDefault(l => l is not null);
        if (exitCode == 0 && where is not null && File.Exists(Path.Combine(where, CSharpWorkerCode.AssemblyName + ".dll")))
        {
            try
            {
                Keep(where, copy);
                Prune(Path.Combine(cache, "programs"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ScriptException($"The built C# scripts could not be copied to {copy}: {ex.Message}", ex);
            }

            return (Path.Combine(copy, CSharpWorkerCode.AssemblyName + ".dll"), [], true);
        }

        var errors = Errors(output);
        if (errors.Count == 0)
        {
            errors.Add(new CSharpError(null, 0, 0, "", "", $"dotnet run failed (exit code {exitCode}):\n{string.Join('\n', output.TakeLast(8))}".TrimEnd()));
        }

        if (errors.Any(e => e.Code.StartsWith("CS", StringComparison.Ordinal)))
        {
            lock (_failed)
            {
                if (_failed.Count > 50)
                {
                    _failed.Clear();
                }

                _failed[key] = errors; // compiler errors come back with the same code; a failed restore may not
            }
        }

        return (null, errors, true);
    }

    private static string? Ready(string copy)
    {
        var marker = Path.Combine(copy, ".ok");
        var program = Path.Combine(copy, CSharpWorkerCode.AssemblyName + ".dll");
        if (!File.Exists(marker) || !File.Exists(program))
        {
            return null;
        }

        try
        {
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow); // recently used: kept
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still usable
        }

        return program;
    }

    /// <summary>Copies the build output into the cache (the files of its packages too).</summary>
    private static void Keep(string output, string copy)
    {
        var temporary = copy + ".tmp";
        if (Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(temporary, Path.GetRelativePath(output, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        var moved = true;
        if (Directory.Exists(copy))
        {
            try
            {
                Directory.Delete(copy, recursive: true); // incomplete, or forgotten
            }
            catch (IOException)
            {
                moved = false; // a TermRest runs it: it is complete
                Directory.Delete(temporary, recursive: true);
            }
        }

        if (moved)
        {
            Directory.Move(temporary, copy);
        }

        File.WriteAllText(Path.Combine(copy, ".ok"), "");
    }

    /// <summary>Deletes all but the most recently used programs, except running ones (on Windows they cannot be deleted).</summary>
    private static void Prune(string programs)
    {
        var old = new DirectoryInfo(programs).EnumerateDirectories()
            .OrderByDescending(d => File.Exists(Path.Combine(d.FullName, ".ok")) ? File.GetLastWriteTimeUtc(Path.Combine(d.FullName, ".ok")) : DateTime.MinValue)
            .Skip(Kept);
        foreach (var directory in old)
        {
            try
            {
                // The program first: when it runs, deleting fails and the folder stays whole.
                File.Delete(Path.Combine(directory.FullName, CSharpWorkerCode.AssemblyName + ".dll"));
                directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use
            }
        }
    }

    /// <summary>The first build checks that the SDK is .NET 10 or later: older ones cannot build C# files without a project.</summary>
    private async Task CheckSdkAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (_sdkChecked)
        {
            return;
        }

        var (exitCode, output) = await RunAsync(["--version"], workingDirectory, cancellationToken).ConfigureAwait(false);
        var text = output.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (exitCode != 0 || !Version.TryParse(text.Split('-')[0], out var version))
        {
            throw new ScriptException($"{dotnet} has no .NET SDK: C# scripts need the .NET {ScriptRuntimeLocator.MinimumDotnetSdk.Major} SDK or later " +
                                      $"(a .NET runtime alone cannot build them). {string.Join(' ', output.Take(3))}".TrimEnd());
        }

        if (version.Major < ScriptRuntimeLocator.MinimumDotnetSdk.Major)
        {
            throw new ScriptException($"The .NET SDK {text} of {dotnet} is too old: C# scripts need the .NET {ScriptRuntimeLocator.MinimumDotnetSdk.Major} SDK or later.");
        }

        _sdkChecked = true;
    }

    /// <summary>One build at a time, across TermRest processes: they share the source file.</summary>
    private static async Task<FileStream> LockAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (deadline.Elapsed < BuildTimeout)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new ScriptException($"The C# scripts could not be built: another TermRest has been building for {BuildTimeout.TotalMinutes:0} minutes ({ex.Message}).");
            }
        }
    }

    /// <summary>Runs dotnet with hidden output; returns its exit code and what it printed.</summary>
    private async Task<(int ExitCode, List<string> Output)> RunAsync(string[] arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["DOTNET_NOLOGO"] = "1";
        var output = new List<string>();
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ScriptException($"The .NET SDK could not be started: {dotnet}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ScriptException($"The .NET SDK could not be started ({dotnet}): {ex.Message}", ex);
        }

        using (process)
        {
            void Add(object sender, DataReceivedEventArgs e)
            {
                if (e.Data is { } line)
                {
                    lock (output)
                    {
                        output.Add(line);
                    }
                }
            }

            process.OutputDataReceived += Add;
            process.ErrorDataReceived += Add;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(BuildTimeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // ended meanwhile
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw new ScriptException($"Building the C# scripts took more than {BuildTimeout.TotalMinutes:0} minutes, so it was stopped.");
            }

            process.WaitForExit(); // the last lines of output
            lock (output)
            {
                return (process.ExitCode, [.. output.Distinct(StringComparer.Ordinal)]);
            }
        }
    }

    /// <summary>The errors in the build output; those in a script's lines name the script.</summary>
    private static List<CSharpError> Errors(List<string> output)
    {
        var errors = new List<CSharpError>();
        foreach (var line in output.Select(l => l.Trim()).Where(l => !Warning().IsMatch(l)))
        {
            if (PositionedError().Match(line) is { Success: true } positioned)
            {
                var script = CSharpWorkerCode.ScriptIdOf(positioned.Groups["file"].Value);
                var code = positioned.Groups["code"].Value;
                var text = positioned.Groups["message"].Value;
                errors.Add(new CSharpError(script, int.Parse(positioned.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(positioned.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture), code, text,
                    script is null ? $"{code}: {text} (at the end of the script)" : $"{code}: {text}"));
            }
            else if (OtherError().Match(line) is { Success: true } other)
            {
                var code = other.Groups["code"].Value;
                var text = other.Groups["message"].Value;
                errors.Add(new CSharpError(null, 0, 0, code, text, code.Length == 0 ? text : $"{code}: {text}"));
            }
        }

        return errors;
    }

    /// <summary>A script's compiler errors, as Python shows a syntax error: the first in the message, all in the traceback.</summary>
    private static (string Message, string Traceback) Describe(List<CSharpError> errors, string name, string code)
    {
        var lines = code.Split('\n');
        var traceback = new StringBuilder();
        foreach (var error in errors.Take(10))
        {
            if (error.Script is not null && error.Line >= 1)
            {
                traceback.Append($"  File \"{name}\", line {error.Line}, column {error.Column}\n");
                if (error.Line <= lines.Length && lines[error.Line - 1].Trim().Length > 0)
                {
                    traceback.Append("    ").Append(lines[error.Line - 1].Trim()).Append('\n');
                }
            }

            traceback.Append(error.Text).Append('\n');
        }

        if (errors.Count > 10)
        {
            traceback.Append($"… and {errors.Count - 10} more errors\n");
        }

        var first = errors[0];
        return (first.Script is not null && first.Line >= 1 ? $"{first.Text} (line {first.Line})" : first.Text, traceback.ToString().TrimEnd());
    }

    // path(line,column): error CS1002: ; expected [project]
    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]*\])?$")]
    private static partial Regex PositionedError();

    // path : error NU1101: Unable to find package …   error: …
    [GeneratedRegex(@"(?:^|\s|:)error(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s*(?<message>.+?)(?:\s+\[[^\]]*\])?$")]
    private static partial Regex OtherError();

    [GeneratedRegex(@"(?:^|\s|:)warning\s+[A-Za-z]+\d+\s*:")]
    private static partial Regex Warning();
}

/// <summary>A compiler or build error; <see cref="Script"/> is the id of the script whose lines it is in.</summary>
internal sealed record CSharpError(string? Script, int Line, int Column, string Code, string Message, string Text);
