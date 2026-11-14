using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

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

    /// <summary>
    /// The files that builds read, by extension or name: code, what is compiled in with it (resources, Razor pages),
    /// assemblies, and what directs the build (project files, MSBuild's .props and .targets, global.json, NuGet's
    /// settings). Not the others, which a project's own program may write in its folder whenever it runs (a database, logs).
    /// </summary>
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> BuildInputs = new HashSet<string>(
        [".cs", ".vb", ".fs", ".fsi", ".resx", ".razor", ".cshtml", ".dll", ".csproj", ".vbproj", ".fsproj", ".proj", ".props", ".targets", "global.json", "nuget.config"],
        StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// The files that MSBuild and NuGet look for in the folders above a project, by these names (NuGet takes these three
    /// spellings of nuget.config where names are case-sensitive). The other files there are not read: they may be many.
    /// </summary>
    private static readonly string[] SettingsAbove =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "nuget.config", "NuGet.config", "NuGet.Config"];

    /// <summary>Programs that did not compile, by key: the same code fails again, so it is not built again.</summary>
    private readonly Dictionary<string, List<CSharpError>> _failed = new(StringComparer.Ordinal);

    /// <summary>
    /// Programs that did not build while they held scripts besides those that ran (the collection's), by key, whatever the
    /// error (a package that cannot be restored, a #:project that is not there): this session leaves those scripts out at
    /// once instead of building them again. A program of the scripts that run alone is built again: they may have been fixed.
    /// </summary>
    private readonly Dictionary<string, List<CSharpError>> _failedWithOthers = new(StringComparer.Ordinal);

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

    /// <summary><see cref="CacheDirectory"/>, created when it is not there.</summary>
    internal static string CreateCacheDirectory()
    {
        var cache = CacheDirectory();
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(cache);
        }
        else
        {
            // The scripts' code is in here: for the user only, as the history is.
            Directory.CreateDirectory(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return cache;
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
        for (var round = 0; round < 8; round++)
        {
            var withOthers = program.Any(id => !needed.Contains(id));
            var (path, errors, built) = await BuildAsync(program, codes, withOthers, cancellationToken).ConfigureAwait(false);
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

                // Only when the program that runs was built now: builds that failed leave nothing to start next time, and a
                // script of this request that did not build is built again next time (it may have been fixed).
                var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                var next = broken.Keys.Any(needed.Contains) ? "" : "; they start at once next time";
                return (path, built ? $"The C# scripts were built ({seconds} s{next})." : null);
            }

            var inScripts = errors.Where(e => e.Script is not null && program.Contains(e.Script)).GroupBy(e => e.Script!).ToList();
            if (withOthers && inScripts.Any(group => needed.Contains(group.Key)))
            {
                // Every script's directives (#:property, #:sdk, #:package) apply to the whole program, so another script of
                // the collection may break the lines of those that run: built without the others, the errors left are theirs.
                program.IntersectWith(needed);
                continue;
            }

            if (inScripts.Count > 0)
            {
                foreach (var group in inScripts)
                {
                    broken[group.Key] = [.. group];
                    program.Remove(group.Key);
                }

                continue;
            }

            if (withOthers)
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
                var (_, alone, _) = await BuildAsync([id], codes, withOthers: false, cancellationToken).ConfigureAwait(false);
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
    /// seconds instead of one. Runs in the background, where the builds run: the SDK that built the scripts stops its
    /// server (in TermRest's current folder, a global.json could choose another SDK).
    /// </summary>
    public static void StopCompilerServer(string dotnet)
    {
        if (Interlocked.Exchange(ref _built, 0) == 0 || ScriptRuntimeLocator.Executable(dotnet) is not { } executable)
        {
            return;
        }

        try
        {
            var folder = Path.Combine(CreateCacheDirectory(), "source");
            Directory.CreateDirectory(folder);
            var info = new ProcessStartInfo(executable, ["build-server", "shutdown", "--vbcscompiler"])
            {
                WorkingDirectory = folder,
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
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // no SDK to ask, or no folder to ask it in
        }
    }

    /// <summary>The program of these scripts: a copy built before, or a new build. Null with the errors when it does not build.</summary>
    /// <param name="withOthers">Whether the program holds scripts besides those that run (see <see cref="_failedWithOthers"/>).</param>
    private async Task<(string? Program, List<CSharpError> Errors, bool Built)> BuildAsync(IEnumerable<string> ids, Dictionary<string, string> codes,
        bool withOthers, CancellationToken cancellationToken)
    {
        var source = CSharpWorkerCode.Generate(ids.ToDictionary(id => id, id => codes[id], StringComparer.Ordinal), workspaceRoot);
        source += Inputs(source);
        var key = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(dotnet + "\n" + source)))[..20];
        var cache = CacheDirectory();
        var copy = Path.Combine(cache, "programs", key);
        if (Ready(copy) is { } ready)
        {
            return (ready, [], false);
        }

        lock (_failed)
        {
            if (_failed.TryGetValue(key, out var known) || (withOthers && _failedWithOthers.TryGetValue(key, out known)))
            {
                return (null, known, false);
            }
        }

        CreateCacheDirectory();
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

        var errors = Errors(output, workspaceRoot);
        if (errors.Count == 0)
        {
            errors.Add(new CSharpError(null, 0, 0, "", "", $"dotnet run failed (exit code {exitCode}):\n{string.Join('\n', output.TakeLast(8))}".TrimEnd()));
        }

        // Compiler errors come back with the same code; a failed restore may not, but other scripts are left out then.
        var compiler = errors.Any(e => e.Code.StartsWith("CS", StringComparison.Ordinal));
        if (compiler || withOthers)
        {
            lock (_failed)
            {
                var failed = compiler ? _failed : _failedWithOthers;
                if (failed.Count > 50)
                {
                    failed.Clear();
                }

                failed[key] = errors;
            }
        }

        return (null, errors, true);
    }

    /// <summary>
    /// The files that the program's #:include and #:project directives bring in, as they are now (paths, sizes, times), as a
    /// comment for its end: when one of them changes, so does the program, which is then built again (the SDK too sees a
    /// changed file) instead of the one built before. For a project, those are the files of its folder, the settings that
    /// MSBuild and NuGet find in the folders above it (<see cref="SettingsAbove"/>), and the same for the projects that it
    /// references, in turn. In a project's folder and in an #:include with wildcards, only files that builds read count
    /// (<see cref="BuildInputs"/>). Paths with MSBuild properties ($(…)) are not known here.
    /// </summary>
    private static string Inputs(string source)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var projects = new Queue<string>();
        foreach (var (directive, path) in CSharpWorkerCode.PathsOf(source).Where(p => !p.Path.Contains("$(", StringComparison.Ordinal)))
        {
            var wildcard = path.IndexOfAny(['*', '?']);
            if (wildcard >= 0)
            {
                files.UnionWith(Files(path[..(path[..wildcard].LastIndexOfAny(['/', '\\']) + 1)], Path.GetFileName(path)));
            }
            else if (directive == "include" && File.Exists(path))
            {
                var file = new FileInfo(path);
                files.Add($"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            }
            else
            {
                projects.Enqueue(path); // a project file, or a folder with one
            }
        }

        var folders = new HashSet<string>(StringComparer.Ordinal);
        var above = new HashSet<string>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);
        while (projects.TryDequeue(out var path))
        {
            var isFile = File.Exists(path);
            var folder = isFile ? Path.GetDirectoryName(path)! : path;
            if (folders.Add(folder))
            {
                files.UnionWith(Files(folder, "*"));

                // The folders above it, up to the first one already looked at (those above it were, too).
                for (var parent = Path.GetDirectoryName(folder); parent is not null && above.Add(parent); parent = Path.GetDirectoryName(parent))
                {
                    foreach (var name in SettingsAbove)
                    {
                        var file = new FileInfo(Path.Combine(parent, name));
                        if (file.Exists)
                        {
                            files.Add($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                        }
                    }
                }
            }

            string[] found = isFile ? [path]
                : Directory.Exists(path) ? Directory.GetFiles(path, "*.*proj", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                : [];
            foreach (var project in found)
            {
                if (read.Add(project))
                {
                    foreach (var reference in References(project))
                    {
                        projects.Enqueue(reference);
                    }
                }
            }
        }

        if (files.Count == 0)
        {
            return "";
        }

        var hash = SHA256.HashData(Utf8.GetBytes(string.Join('\n', files.Order(StringComparer.Ordinal))));
        return $"\n// Files of #:include and #:project: {Convert.ToHexStringLower(hash)[..20]}\n";
    }

    /// <summary>
    /// The projects that a project file references (ProjectReference), with full paths. Those whose path MSBuild works out
    /// from properties ($(…)) or wildcards are not known here and are left out: their changes build nothing again.
    /// </summary>
    private static List<string> References(string project)
    {
        var references = new List<string>();
        try
        {
            using var stream = File.OpenRead(project);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ProjectReference" && reader.GetAttribute("Include") is { } include)
                {
                    foreach (var item in include.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (item.IndexOfAny(['$', '@', '%', '*', '?']) < 0)
                        {
                            // Relative to the project; MSBuild takes \ as / on Linux and macOS.
                            references.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, item.Replace('\\', '/'))));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // its build says what is wrong with it
        }

        return references;
    }

    /// <summary>
    /// The files that builds read (<see cref="BuildInputs"/>) in a folder and its subfolders whose names match, but in those
    /// that builds write (bin, obj), node_modules and hidden ones (.git…).
    /// </summary>
    private static List<string> Files(string folder, string pattern)
    {
        if (!Directory.Exists(folder))
        {
            return [$"{folder}|missing"];
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return
        [
            .. new FileSystemEnumerable<string>(folder, (ref FileSystemEntry entry) => $"{entry.ToFullPath()}|{entry.Length}|{entry.LastWriteTimeUtc.Ticks}", options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && FileSystemName.MatchesSimpleExpression(pattern, entry.FileName)
                    && (BuildInputs.Contains(Path.GetExtension(entry.FileName)) || BuildInputs.Contains(entry.FileName)),
                ShouldRecursePredicate = (ref FileSystemEntry entry) => entry.FileName is not ("bin" or "obj" or "node_modules") && entry.FileName[0] != '.',
            },
        ];
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

        if (Directory.Exists(copy))
        {
            try
            {
                // The program first: while a TermRest runs it, deleting it fails (on Windows) and the folder stays whole.
                File.Delete(Path.Combine(copy, CSharpWorkerCode.AssemblyName + ".dll"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.WriteAllText(Path.Combine(copy, ".ok"), ""); // it runs, so it is complete
                return;
            }

            Directory.Delete(copy, recursive: true); // incomplete, or forgotten
        }

        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(temporary, Path.GetRelativePath(output, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        Directory.Move(temporary, copy);
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
        var executable = ScriptRuntimeLocator.Executable(dotnet) ?? throw new ScriptException($"The .NET SDK could not be started ({dotnet}): it is not on PATH.");
        var info = new ProcessStartInfo(executable)
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

    /// <summary>
    /// The errors in the build output; those in a script's lines name the script, those in a file that a script brings in
    /// (#:include, #:project) name that file (relative to the workspace when inside it) and their position.
    /// </summary>
    private static List<CSharpError> Errors(List<string> output, string workspaceRoot)
    {
        var errors = new List<CSharpError>();
        foreach (var line in output.Select(l => l.Trim()).Where(l => !Warning().IsMatch(l)))
        {
            if (PositionedError().Match(line) is { Success: true } positioned)
            {
                var file = positioned.Groups["file"].Value;
                var script = CSharpWorkerCode.ScriptIdOf(file);
                var code = positioned.Groups["code"].Value;
                var text = positioned.Groups["message"].Value;
                var row = int.Parse(positioned.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var column = int.Parse(positioned.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture);
                errors.Add(new CSharpError(script, row, column, code, text,
                    script is not null ? $"{code}: {text}"
                    : Path.GetFileName(file).Equals(CSharpWorkerCode.AssemblyName + ".cs", StringComparison.OrdinalIgnoreCase) ? $"{code}: {text} (at the end of the script)"
                    : $"{code}: {text} ({Shown(file, workspaceRoot)}, line {row}, column {column})"));
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

    /// <summary>
    /// <paramref name="file"/> relative to the workspace when it is inside it, else as it is. The files of a #:project come
    /// by their project's path (<see cref="CSharpWorkerCode.ProjectPath"/>), which may be the workspace's real one.
    /// </summary>
    private static string Shown(string file, string workspaceRoot)
    {
        foreach (var root in (string[])[workspaceRoot, CSharpWorkerCode.ProjectPath(workspaceRoot)])
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative;
            }
        }

        return file;
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
