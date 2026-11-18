using System.Text.Json.Nodes;
using TermRest.Core.IO;
using TermRest.Core.Json;
using TermRest.Core.Yaml;

namespace TermRest.Core.Postman.V3;

/// <summary>Saves a <see cref="PostmanCollection"/> somewhere other than a single v2.1 JSON file.</summary>
internal interface ICollectionStorage
{
    /// <summary>Directory that relative file paths (body files, certificates) are resolved against.</summary>
    string BaseDirectory { get; }

    /// <param name="overwriteChanged">Also write files that changed on disk since they were read (see <see cref="FileChangedException"/>).</param>
    void Save(PostmanCollection collection, bool overwriteChanged);

    /// <summary>
    /// Whether a request has saved examples that are not loaded yet (into its <c>response</c> array: see
    /// <see cref="LoadExamples"/>). Answered without reading files.
    /// </summary>
    bool HasUnloadedExamples(JsonObject item);

    /// <summary>
    /// Loads a request's saved examples into its <c>response</c> array, once (a v3 collection keeps them in files of their
    /// own, read only when needed). Returns problems with files that could not be read (they are left alone).
    /// </summary>
    IReadOnlyList<string> LoadExamples(JsonObject item);
}

/// <summary>
/// A Postman collection format v3 folder: <c>.resources/definition.yaml</c> for the collection and each folder, one
/// <c>*.request.yaml</c> per request, ordered by their <c>order</c> keys. In memory the collection is the same v2.1 JSON
/// model TermRest edits everywhere; saving writes back only what changed: edited requests (only the edited YAML
/// entries), renamed or moved items (their files and <c>.resources</c> move along), new and deleted items, and the
/// <c>order</c> of reordered siblings. Files TermRest does not show (GraphQL, gRPC … requests, unreadable files) are
/// never touched, and files another program changed since they were read are only saved over when that is allowed.
/// </summary>
internal sealed class V3CollectionStorage : ICollectionStorage
{
    private const string StagingPrefix = ".termrest-saving-";

    private static readonly TextFileInfo NewFile = new(false, true, false);

    private readonly Dictionary<JsonObject, Origin> _origins = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsonObject, List<JsonObject>> _loadedChildren = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The saved examples of the requests whose examples are loaded (see <see cref="LoadExamples"/>), by request: what each
    /// example was read from or last written to. The examples of other requests stay on disk, untouched.
    /// </summary>
    private readonly Dictionary<JsonObject, Dictionary<JsonObject, ExampleOrigin>> _examples = new(ReferenceEqualityComparer.Instance);
    private YamlStyle _style = YamlStyle.Default;

    /// <summary>On Unix, the permissions of the files a conversion creates (see <see cref="ConvertFrom"/>); null: the default.</summary>
    private UnixFileMode? _newFileMode;

    private V3CollectionStorage(string directory)
    {
        Directory = directory;
        BaseDirectory = V3Names.WorkspaceRootFor(directory);
    }

    public string Directory { get; }

    public string BaseDirectory { get; }

    /// <summary>
    /// Column at which new and changed values are wrapped (0: never); null follows the files, as detected when loading
    /// (see <see cref="Workspace.WorkspaceSettings.YamlLineWidth"/>).
    /// </summary>
    public int? LineWidth { get; set; }

    /// <summary>The style new and changed content is written in (each file's own quoting and wrapping still win).</summary>
    public YamlStyle Style => _style.WithLineWidth(LineWidth);

    /// <summary>What was read from (or last written to) disk for one item.</summary>
    private sealed class Origin
    {
        public required string Path { get; set; }

        public JsonObject? Parent { get; set; }

        /// <summary>The file as last read or written; null when there is none, or it could not be read (it is then never overwritten).</summary>
        public YamlFile? File { get; set; }

        /// <summary>What the file held when last read or written, to tell whether another program changed it since (with <see cref="File"/>).</summary>
        public FileStamp? Stamp { get; set; }

        /// <summary>Why the file could not be read when the collection was loaded; null when it could, or there was none.</summary>
        public string? ReadError { get; set; }

        public TextFileInfo FileInfo { get; set; } = NewFile;

        /// <summary>The v3 content as last read or written.</summary>
        public JsonObject? V3 { get; set; }

        /// <summary>The v2.1 item (without children) as last read or written.</summary>
        public required JsonObject Baseline { get; set; }

        public JsonNode? Order { get; set; }

        /// <summary>For requests: the file name (without suffix) <see cref="V3"/> was read or written under; its <c>examples</c> path refers to it.</summary>
        public string? Stem { get; set; }
    }

    /// <summary>What was read from (or last written to) disk for one saved example of a request.</summary>
    private sealed class ExampleOrigin
    {
        public required string Path { get; set; }

        /// <summary>The file as last read or written.</summary>
        public required YamlFile File { get; set; }

        public FileStamp? Stamp { get; set; }

        public TextFileInfo FileInfo { get; set; } = NewFile;

        /// <summary>The v3 content as last read or written.</summary>
        public required JsonObject V3 { get; set; }

        /// <summary>The v2.1 example (as in the request's <c>response</c> array) as last read or written.</summary>
        public required JsonObject Baseline { get; set; }
    }

    // ----- examples ----------------------------------------------------------------------------------

    public bool HasUnloadedExamples(JsonObject item) =>
        !_examples.ContainsKey(item) && _origins.TryGetValue(item, out var origin) && origin.V3?["examples"].GetString() is { Length: > 0 };

    public IReadOnlyList<string> LoadExamples(JsonObject item)
    {
        if (IsFolder(item) || _examples.ContainsKey(item))
        {
            return [];
        }

        var loaded = new Dictionary<JsonObject, ExampleOrigin>(ReferenceEqualityComparer.Instance);
        _examples[item] = loaded;

        // A request not saved yet has its examples in memory only.
        if (!_origins.TryGetValue(item, out var origin) || origin.V3?["examples"].GetString() is not { Length: > 0 })
        {
            return [];
        }

        var folder = ExamplesFolder(origin.Path, origin.V3);
        if (!System.IO.Directory.Exists(folder))
        {
            return [];
        }

        var warnings = new List<string>();
        var examples = new List<(double? Order, string Name, JsonObject Example)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(folder).Where(f => f.EndsWith(V3Names.ExampleSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            // One that cannot be read, or is not an example, is left alone (and out).
            var (yaml, info, stamp, _) = Read(file, warnings, []);
            if (yaml is null || (yaml.Json["$kind"].GetString() is { } kind && kind != V3Convert.HttpExampleKind))
            {
                continue;
            }

            var name = yaml.Json["name"].GetString() is { Length: > 0 } given ? given : Path.GetFileName(file)[..^V3Names.ExampleSuffix.Length];
            var example = V3Convert.ExampleToV21(yaml.Json, name);
            loaded[example] = new ExampleOrigin { Path = file, File = yaml, Stamp = stamp, FileInfo = info, V3 = yaml.Json, Baseline = (JsonObject)example.DeepClone() };
            examples.Add((OrderValue(yaml.Json["order"]), name, example));
        }

        if (item["response"] is not JsonArray responses)
        {
            responses = [];
            item.SetOrdered("response", responses, PostmanRequestMapper.ItemOrder);
        }

        foreach (var (_, _, example) in examples.OrderBy(e => e.Order ?? double.MaxValue).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            responses.Add((JsonNode)example);
        }

        return warnings;
    }

    /// <summary>
    /// The folder of a request's example files: the one its <c>examples</c> reference names (relative to the request's
    /// folder) while that is inside the collection, else where Postman puts them.
    /// </summary>
    private string ExamplesFolder(string requestFile, JsonObject? v3)
    {
        if (v3?["examples"].GetString() is { Length: > 0 } reference)
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(requestFile)!, reference)));
            if (folder.StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return folder;
            }
        }

        return Path.Combine(V3Names.ResourcesPath(requestFile), "examples");
    }

    // ----- loading ---------------------------------------------------------------------------------

    public static PostmanCollection Load(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"'{directory}' does not exist.");
        }

        // As the workspace takes them: any other folder (a workspace, a source folder) would be read as one whole collection.
        if (!V3Names.IsCollectionDirectory(directory))
        {
            throw new IOException($"'{directory}' is not a Postman v3 collection folder: one with a {V3Names.ResourcesDirectory}/{V3Names.DefinitionFile} " +
                                  $"or *{V3Names.RequestSuffix} files (in its folders too, in a postman/collections folder).");
        }

        var storage = new V3CollectionStorage(directory);
        var warnings = new List<string>();
        var trees = new List<YamlTree>();
        var root = new JsonObject
        {
            ["info"] = new JsonObject { ["name"] = Path.GetFileName(directory), ["schema"] = PostmanCollection.SchemaV21 },
            ["item"] = new JsonArray(),
        };

        var definitionPath = V3Names.DefinitionPath(directory);
        var definition = storage.Read(definitionPath, warnings, trees);
        V3Convert.DefinitionToV21(definition.File?.Json, root, isRoot: true);
        if (definition.File?.Json["name"].GetString() is { Length: > 0 } name)
        {
            root["info"]!["name"] = name;
        }

        storage._origins[root] = new Origin
        {
            Path = directory, File = definition.File, Stamp = definition.Stamp, ReadError = definition.Error, FileInfo = definition.Info,
            V3 = definition.File?.Json, Baseline = Snapshot(root),
        };
        var hidden = storage.LoadChildren(directory, root, warnings, trees);
        var unreadable = warnings.Count > 0;
        if (hidden > 0)
        {
            warnings.Add($"{hidden} non-HTTP request(s) (GraphQL, gRPC, WebSocket …) are not shown; their files are left untouched.");
        }

        foreach (var leftover in System.IO.Directory.EnumerateDirectories(directory, StagingPrefix + "*"))
        {
            if (System.IO.Directory.EnumerateFileSystemEntries(leftover).Any())
            {
                warnings.Add($"{Path.GetFileName(leftover)}: a save that did not finish left files here; move them back into the collection.");
                unreadable = true;
            }
        }

        storage._style = YamlStyle.Detect(trees);
        var collection = PostmanCollection.FromStorage(root, directory, storage);
        collection.LoadWarnings.AddRange(warnings);
        collection.HasUnreadableFiles = unreadable;
        return collection;
    }

    /// <summary>Creates an empty collection folder (with its definition file) and loads it.</summary>
    public static PostmanCollection Create(string directory, string name, int? lineWidth = null)
    {
        directory = Path.GetFullPath(directory);
        if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException($"'{directory}' already exists and is not empty.");
        }

        var definition = new JsonObject { ["$kind"] = V3Convert.CollectionKind };
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) != name)
        {
            definition["name"] = name;
        }

        var definitionPath = V3Names.DefinitionPath(directory);
        var (text, info) = YamlFile.WriteChecked(null, definition, YamlStyle.Default.WithLineWidth(lineWidth), definitionPath, NewFile, out _);
        TextFile.WriteAtomic(definitionPath, text, info);
        var collection = Load(directory);
        collection.YamlLineWidth = lineWidth;
        return collection;
    }

    /// <summary>
    /// Writes <paramref name="source"/> (any format) as a new v3 collection folder, including saved responses as
    /// <c>.example.yaml</c> files, and returns the loaded result. Long values are wrapped at
    /// <paramref name="lineWidth"/> (null or 0: not wrapped, like current Postman versions). On Unix the folder and its
    /// files get the permissions of the source's file (see <see cref="TextFile.CopyMode"/>) from the start. When writing
    /// fails, nothing is left behind: a collection that is only partly written would look complete.
    /// </summary>
    public static PostmanCollection ConvertFrom(PostmanCollection source, string directory, int? lineWidth = null)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException($"'{directory}' already exists and is not empty.");
        }

        // The outermost folder created here (null when the target folder exists): removed again when writing fails.
        string? created = null;
        for (var missing = directory; missing is not null && !System.IO.Directory.Exists(missing); missing = Path.GetDirectoryName(missing))
        {
            created = missing;
        }

        var fileMode = source.FilePath is { } sourcePath ? TextFile.CopyMode(sourcePath) : null;
        try
        {
            if (fileMode is { } mode && !OperatingSystem.IsWindows())
            {
                // Private from the start for a private source: others never get to read what is being written.
                var folderMode = TextFile.FolderMode(mode);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory) ?? directory);
                System.IO.Directory.CreateDirectory(directory, folderMode);
                File.SetUnixFileMode(directory, folderMode); // the create mode is subject to the umask
            }
            else
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var root = (JsonObject)source.Root.DeepClone();
            var storage = new V3CollectionStorage(directory) { LineWidth = lineWidth, _newFileMode = fileMode };
            var collection = PostmanCollection.FromStorage(root, directory, storage);
            storage.Save(collection, overwriteChanged: false); // new requests are written with their examples
        }
        catch
        {
            RemoveWritten(directory, created);
            throw;
        }

        var converted = Load(directory);
        converted.YamlLineWidth = lineWidth;
        return converted;
    }

    /// <summary>Removes what a failed conversion wrote: <paramref name="created"/> with everything in it, else what is in <paramref name="directory"/>.</summary>
    private static void RemoveWritten(string directory, string? created)
    {
        try
        {
            if (created is not null)
            {
                System.IO.Directory.Delete(created, recursive: true);
                return;
            }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo folder)
                {
                    folder.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the error that stopped the conversion is the one to report.
        }
    }

    /// <summary>
    /// Whether saving writes or deletes example files of a request: its examples were loaded, and some are new, edited,
    /// renamed or deleted (see <see cref="SaveExamples"/>). The snapshot that tells whether a request changed leaves them out.
    /// </summary>
    private bool ExamplesChange(JsonObject item)
    {
        if (IsFolder(item) || !_examples.TryGetValue(item, out var loaded))
        {
            return false;
        }

        var examples = (item["response"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        return examples.Count != loaded.Count || examples.Any(e => !loaded.TryGetValue(e, out var origin) || !JsonNode.DeepEquals(e, origin.Baseline));
    }

    /// <summary>
    /// Writes a request's saved examples where they changed: new ones as new files named for them (a name a file can't have
    /// is kept in it, as Postman keeps it), edited ones in place (only the edited entries), renamed ones under their new
    /// name, and deleted ones removed. The examples of a request that were never loaded are as they were: nothing to write.
    /// Files another program changed since they were read are left alone unless that is allowed. Returns the request's
    /// <c>examples</c> reference when it has example files and lacks one.
    /// </summary>
    private string? SaveExamples(JsonObject item, string requestFile, bool isNew, JsonObject v3, LeftAlone leftAlone)
    {
        if (!_examples.TryGetValue(item, out var loaded))
        {
            if (!isNew)
            {
                return null;
            }

            loaded = new Dictionary<JsonObject, ExampleOrigin>(ReferenceEqualityComparer.Instance);
            _examples[item] = loaded;
        }

        var examples = (item["response"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var folder = ExamplesFolder(requestFile, v3);

        // Moved along with the request's resources folder (the request was renamed or moved).
        foreach (var origin in loaded.Values.Where(o => Path.GetDirectoryName(o.Path) != folder))
        {
            origin.Path = Path.Combine(folder, Path.GetFileName(origin.Path));
        }

        var current = new HashSet<JsonObject>(examples, ReferenceEqualityComparer.Instance);
        foreach (var (json, origin) in loaded.Where(e => !current.Contains(e.Key)).ToList())
        {
            if (File.Exists(origin.Path))
            {
                if (origin.Stamp?.HasChanged(origin.Path) == true && !leftAlone.OverwriteChanged)
                {
                    leftAlone.Changed.Add(origin.Path);
                    continue;
                }

                File.Delete(origin.Path);
            }

            loaded.Remove(json);
        }

        // Names in use, also by files TermRest does not show (one it could not read).
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(folder))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(folder).Where(f => f.EndsWith(V3Names.ExampleSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                taken.Add(Path.GetFileName(file)[..^V3Names.ExampleSuffix.Length]);
            }
        }

        var order = loaded.Values.Select(o => OrderValue(o.V3["order"])).Max() ?? 0;
        double? previous = null; // the order of the example before, as the request shows them
        for (var index = 0; index < examples.Count; index++)
        {
            var json = examples[index];
            var name = json["name"].GetString() is { Length: > 0 } given ? given : "Example";
            if (loaded.TryGetValue(json, out var origin))
            {
                previous = OrderValue(origin.V3["order"]) ?? previous;
                if (JsonNode.DeepEquals(json, origin.Baseline))
                {
                    continue;
                }

                if (File.Exists(origin.Path) && origin.Stamp?.HasChanged(origin.Path) == true && !leftAlone.OverwriteChanged)
                {
                    leftAlone.Changed.Add(origin.Path);
                    continue;
                }

                var stem = Path.GetFileName(origin.Path)[..^V3Names.ExampleSuffix.Length];
                var edited = V3Convert.ExampleToV3(json, origin.V3);
                if (origin.Baseline["name"].GetString() != json["name"].GetString())
                {
                    taken.Remove(stem);
                    stem = V3Names.Unique(V3Names.Safe(name), taken);
                    NameExample(edited, name, stem);
                }

                taken.Add(stem);
                var path = Path.Combine(folder, stem + V3Names.ExampleSuffix);
                if (path != origin.Path && File.Exists(origin.Path))
                {
                    File.Move(origin.Path, path); // a new name, also one that differs in case only
                    origin.Path = path; // where it is now, should writing it fail: the next save finds it there
                }

                CreateFolder(folder);
                var (text, info) = YamlFile.WriteChecked(origin.File, edited, Style, path, origin.FileInfo, out var written);
                TextFile.WriteAtomic(path, text, info, out var stamp, _newFileMode);
                origin.Path = path;
                origin.File = written;
                origin.Stamp = stamp;
                origin.FileInfo = info;
                origin.V3 = edited;
                origin.Baseline = (JsonObject)json.DeepClone();
            }
            else
            {
                var stem = V3Names.Unique(V3Names.Safe(name), taken);
                taken.Add(stem);
                var created = V3Convert.ExampleToV3(json);
                NameExample(created, name, stem);

                // Where the request shows it (a copy right after its original): between its neighbors, after the last in steps of 1000.
                var next = examples.Skip(index + 1).Select(e => loaded.TryGetValue(e, out var o) ? OrderValue(o.V3["order"]) : null).FirstOrDefault(v => v is not null);
                double value;
                if (next is { } high && (previous is null || high > previous))
                {
                    value = previous is { } low ? low + ((high - low) / 2) : high - 1000;
                    if (previous is { } below && high - below >= 2)
                    {
                        value = Math.Floor(value);
                    }
                }
                else
                {
                    order = (Math.Floor(order / 1000) + 1) * 1000;
                    value = order;
                }

                previous = value;
                created["order"] = value == Math.Floor(value) && Math.Abs(value) < 1e15 ? JsonValue.Create((long)value) : JsonValue.Create(value);
                CreateFolder(folder);
                var path = Path.Combine(folder, stem + V3Names.ExampleSuffix);
                var (text, info) = YamlFile.WriteChecked(null, created, Style, path, NewFile, out var written);
                TextFile.WriteAtomic(path, text, info, out var stamp, _newFileMode);
                loaded[json] = new ExampleOrigin { Path = path, File = written, Stamp = stamp, FileInfo = info, V3 = created, Baseline = (JsonObject)json.DeepClone() };
            }
        }

        return loaded.Count > 0 && v3["examples"] is null ? $"./{V3Names.ResourcesDirectory}/{V3Names.RequestStem(requestFile)}.resources/examples" : null;

        // A name its file can't have (cut, made safe or unique) is kept in the file, as Postman keeps it.
        static void NameExample(JsonObject example, string name, string stem)
        {
            if (stem != name)
            {
                example.SetOrdered("name", name, V3Convert.ExampleFileOrder);
            }
            else
            {
                example.Remove("name");
            }
        }
    }

    private (YamlFile? File, TextFileInfo Info, FileStamp? Stamp, string? Error) Read(string path, List<string> warnings, List<YamlTree> trees)
    {
        if (!File.Exists(path))
        {
            return (null, NewFile, null, null);
        }

        try
        {
            var (text, info) = TextFile.Read(path, out var stamp);
            var file = YamlFile.Parse(text);
            trees.Add(file.Tree);
            return (file, info, stamp, null);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{Path.GetRelativePath(Directory, path)}: {ex.Message}");
            return (null, NewFile, null, ex.Message);
        }
    }

    /// <summary>Loads requests and sub-folders of <paramref name="directory"/>. Returns the number of non-HTTP requests skipped.</summary>
    private int LoadChildren(string directory, JsonObject parent, List<string> warnings, List<YamlTree> trees)
    {
        var hidden = 0;
        var children = new List<(double? Order, string Name, JsonObject Item)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory).Where(V3Names.IsRequestFile))
        {
            var (yaml, info, stamp, _) = Read(file, warnings, trees);
            if (yaml is null)
            {
                continue;
            }

            if (yaml.Json["$kind"].GetString() is { } kind && kind != V3Convert.HttpRequestKind)
            {
                hidden++;
                continue;
            }

            var name = yaml.Json["name"].GetString() is { Length: > 0 } explicitName ? explicitName : V3Names.RequestStem(file);
            var item = V3Convert.RequestToV21(yaml.Json, name);
            _origins[item] = new Origin
            {
                Path = file, Parent = parent, File = yaml, Stamp = stamp, FileInfo = info, V3 = yaml.Json, Baseline = Snapshot(item),
                Order = yaml.Json["order"]?.DeepClone(), Stem = V3Names.RequestStem(file),
            };
            children.Add((OrderValue(yaml.Json["order"]), name, item));
        }

        foreach (var sub in System.IO.Directory.EnumerateDirectories(directory))
        {
            var dirName = Path.GetFileName(sub);
            if (dirName.StartsWith('.'))
            {
                continue;
            }

            var (definition, info, stamp, error) = Read(V3Names.DefinitionPath(sub), warnings, trees);
            var name = definition?.Json["name"].GetString() is { Length: > 0 } explicitName ? explicitName : dirName;
            var folder = new JsonObject { ["name"] = name, ["item"] = new JsonArray() };
            V3Convert.DefinitionToV21(definition?.Json, folder, isRoot: false);
            _origins[folder] = new Origin
            {
                Path = sub, Parent = parent, File = definition, Stamp = stamp, ReadError = error, FileInfo = info, V3 = definition?.Json,
                Baseline = Snapshot(folder), Order = definition?.Json["order"]?.DeepClone(),
            };
            hidden += LoadChildren(sub, folder, warnings, trees);
            children.Add((OrderValue(definition?.Json["order"]), name, folder));
        }

        // Postman sorts by order; items without one come last, by name.
        var items = (JsonArray)parent["item"]!;
        var sorted = children.OrderBy(c => c.Order ?? double.MaxValue).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.Item).ToList();
        foreach (var item in sorted)
        {
            items.Add((JsonNode)item);
        }

        _loadedChildren[parent] = sorted;
        return hidden;
    }

    // ----- saving ----------------------------------------------------------------------------------

    public void Save(PostmanCollection collection, bool overwriteChanged)
    {
        var root = collection.Root;
        var parents = new Dictionary<JsonObject, JsonObject>(ReferenceEqualityComparer.Instance);
        var desired = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance) { [root] = Directory };

        // Requests to move or rename whose file another program changed since it was read stay where they are, unwritten,
        // unless overwriting is allowed: moved without being written, what they say would no longer match where they are
        // (their examples path, say). Their names stay taken.
        var pinned = overwriteChanged ? new HashSet<JsonObject>(ReferenceEqualityComparer.Instance) : ChangedRequestsToMove(root);
        var managed = new HashSet<string>(_origins.Where(o => !pinned.Contains(o.Key)).Select(o => o.Value.Path), StringComparer.OrdinalIgnoreCase);
        AssignPaths(root, Directory, desired, parents, managed);

        // New orders depend on where items were loaded from: computed before the moves below update that.
        var orders = new Dictionary<JsonObject, JsonNode>(ReferenceEqualityComparer.Instance);
        foreach (var folder in PreOrder(root).Where(IsFolder))
        {
            AssignOrders(folder, orders);
        }

        var current = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);
        foreach (var (item, origin) in _origins)
        {
            current[item] = origin.Path;
        }

        var inTree = new HashSet<JsonObject>(PreOrder(root), ReferenceEqualityComparer.Instance);
        var removed = _origins.Keys.Where(i => !inTree.Contains(i)).ToList();
        var staging = Path.Combine(Directory, $"{StagingPrefix}{Guid.NewGuid():N}");
        var staged = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);
        var stagedResources = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);

        // Folders whose folder disappeared and that are written anew (see WriteAnew), and the folders in them: created anew,
        // as new ones are.
        var writtenAnew = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);

        bool Moved(JsonObject item) =>
            _origins[item].Parent != parents.GetValueOrDefault(item) || Path.GetFileName(current[item]) != Path.GetFileName(desired[item]);

        // Gone meanwhile (a git checkout, say): written anew where it belongs, with everything a folder held. Examples it had
        // stay where they are.
        void WriteAnew(JsonObject item)
        {
            var inside = current[item] + Path.DirectorySeparatorChar;
            foreach (var (gone, goneOrigin) in _origins)
            {
                if (current[gone] == current[item] || current[gone].StartsWith(inside, StringComparison.Ordinal))
                {
                    goneOrigin.File = null;
                    goneOrigin.Stamp = null;
                    goneOrigin.ReadError = null;
                    goneOrigin.Stem = null;
                    if (IsFolder(gone))
                    {
                        writtenAnew.Add(gone);
                    }
                }
            }
        }

        // Whether this save writes the item or puts it in place: it is new, moved, reordered or edited, or its examples are. A
        // folder without an order gets one whenever its siblings change: while it is gone (see below), that alone is no change
        // (see step 4).
        var changes = new Dictionary<JsonObject, bool>(ReferenceEqualityComparer.Instance);
        bool Changes(JsonObject item)
        {
            if (!changes.TryGetValue(item, out var changed))
            {
                changed = !pinned.Contains(item)
                          && (!_origins.TryGetValue(item, out var origin) || Moved(item) || !JsonNode.DeepEquals(Snapshot(item), origin.Baseline)
                              || ExamplesChange(item)
                              || (orders.ContainsKey(item) && (!IsFolder(item) || OrderValue(origin.Order) is not null || System.IO.Directory.Exists(current[item]))));
                changes[item] = changed;
            }

            return changed;
        }

        // A folder that disappeared where it is (the collection's own too) stays gone, unless something is written into it or
        // it is written itself: then it is written anew, so that what is written there keeps the folder's definition (its
        // auth, scripts …) and the folder on disk holds what TermRest shows. Moved ones are written anew in step 1.
        foreach (var folder in PreOrder(root).Where(f => IsFolder(f) && _origins.ContainsKey(f)))
        {
            if (!writtenAnew.Contains(folder) && !Moved(folder) && !System.IO.Directory.Exists(current[folder]) && PreOrder(folder).Any(Changes))
            {
                WriteAnew(folder);
            }
        }

        // Every move and created folder, so that a failure moves everything back (To is null for a created folder).
        var journal = new List<(string From, string? To)>();
        try
        {
            // 1. Take out everything that moves or is renamed (a moving folder takes its content along).
            foreach (var item in PreOrder(root).Where(i => i != root && _origins.ContainsKey(i) && !pinned.Contains(i)))
            {
                if (!Moved(item))
                {
                    continue;
                }

                if (IsFolder(item) ? !System.IO.Directory.Exists(current[item]) : !File.Exists(current[item]))
                {
                    WriteAnew(item);
                    continue;
                }

                CreateDirectory(staging, journal);
                var target = Path.Combine(staging, staged.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (IsFolder(item))
                {
                    Move(current[item], target, isDirectory: true, journal);
                    Rebase(current, current[item], target);
                }
                else
                {
                    var resources = V3Names.ResourcesPath(current[item]);
                    Move(current[item], target, isDirectory: false, journal);
                    current[item] = target;
                    if (System.IO.Directory.Exists(resources))
                    {
                        Move(resources, target + ".resources", isDirectory: true, journal);
                        stagedResources[item] = target + ".resources";
                    }
                }

                staged[item] = target;
            }

            // 2. Delete what was removed (moved-out content is already safe in staging). Folders are only removed once
            //    empty: files TermRest does not show (GraphQL, gRPC … requests) are never deleted.
            foreach (var item in removed.Where(i => !IsFolder(i)))
            {
                DeleteRequestFiles(current[item]);
            }

            foreach (var item in removed.Where(IsFolder).OrderByDescending(i => current[i].Length))
            {
                DeleteFolderIfEmpty(current[item]);
            }

            // 3. Put moved items in place, parents first. No item takes over the place of a deleted folder that is still there (it
            //    holds files TermRest does not show, or a request held back from moving out) or of anything else TermRest does
            //    not show: it gets a free name, and what belongs in it follows. New folders, and folders written anew, are checked
            //    here too, once their parent is in place: such a deleted folder may only have arrived with it.
            foreach (var item in PreOrder(root).Where(i => i != root && (staged.ContainsKey(i) || writtenAnew.Contains(i) || (IsFolder(i) && !_origins.ContainsKey(i)))))
            {
                var target = desired[item];
                if (Path.Exists(target) || (stagedResources.ContainsKey(item) && Path.Exists(V3Names.ResourcesPath(target))))
                {
                    var free = FreePath(target, IsFolder(item), desired.Where(d => d.Key != item).Select(d => d.Value));
                    Rebase(desired, target, free);
                    target = free;
                }

                if (!staged.ContainsKey(item))
                {
                    continue; // created when its content is written
                }

                CreateDirectory(Path.GetDirectoryName(target)!, journal);
                if (IsFolder(item))
                {
                    Move(current[item], target, isDirectory: true, journal);
                    Rebase(current, current[item], target);
                }
                else
                {
                    Move(current[item], target, isDirectory: false, journal);
                    current[item] = target;
                    if (stagedResources.TryGetValue(item, out var resources))
                    {
                        var resourcesTarget = V3Names.ResourcesPath(target);
                        CreateDirectory(Path.GetDirectoryName(resourcesTarget)!, journal);
                        Move(resources, resourcesTarget, isDirectory: true, journal);
                    }
                }
            }

            if (System.IO.Directory.Exists(staging))
            {
                System.IO.Directory.Delete(staging, recursive: false);
            }
        }
        catch
        {
            Undo(journal, current);
            foreach (var (item, origin) in _origins)
            {
                origin.Path = current[item];
            }

            throw;
        }

        // Everything is in place: remembered now, so that the next save starts from there even if writing fails. A deleted
        // folder that still holds a request held back from moving out stays known: a later save deletes it once that request
        // has left (else it would come back as an empty folder).
        var heldBack = pinned.Select(p => current[p]).ToList();
        foreach (var item in removed)
        {
            if (IsFolder(item) && heldBack.Any(p => p.StartsWith(current[item] + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                continue;
            }

            _origins.Remove(item);
            _loadedChildren.Remove(item);
            _examples.Remove(item);
        }

        foreach (var (item, origin) in _origins)
        {
            origin.Path = current[item];
            if (!pinned.Contains(item))
            {
                origin.Parent = parents.GetValueOrDefault(item);
            }
        }

        // 4. Write content: new items, edited items, renamed items and reordered siblings. Files that could not be read when
        //    loading, and ones another program changed since, are left alone and reported once everything else is written.
        var leftAlone = new LeftAlone(overwriteChanged);
        foreach (var item in pinned)
        {
            leftAlone.Changed.Add(current[item]);
            leftAlone.Items.Add(item);
        }

        foreach (var item in PreOrder(root))
        {
            // Other folders are in place already, or stay gone (see above). One that stays gone does not keep the order it got as
            // it had none (writing that would bring it back): left alone, it gets one again with each save, until it is back.
            if (IsFolder(item) && (writtenAnew.Contains(item) || !_origins.ContainsKey(item)))
            {
                CreateFolder(desired[item]);
            }
            else if (IsFolder(item) && orders.ContainsKey(item) && !System.IO.Directory.Exists(desired[item]))
            {
                orders.Remove(item);
                leftAlone.Items.Add(item);
            }

            if (!pinned.Contains(item))
            {
                WriteItem(item, item == root, desired[item], parents.GetValueOrDefault(item), orders, leftAlone);
            }
        }

        foreach (var folder in PreOrder(root).Where(IsFolder))
        {
            // Where a child was left alone, its new order is assigned again next time, also when the children are then as they
            // were before: the new orders of the others are written.
            var children = Children(folder).ToList();
            if (children.Any(leftAlone.Items.Contains))
            {
                _loadedChildren.Remove(folder);
            }
            else
            {
                _loadedChildren[folder] = children;
            }
        }

        leftAlone.ThrowIfAny();
    }

    /// <summary>Requests whose parent or name changed while another program changed their file since it was read.</summary>
    private HashSet<JsonObject> ChangedRequestsToMove(JsonObject root)
    {
        var changed = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var folder in PreOrder(root).Where(IsFolder))
        {
            foreach (var request in Children(folder).Where(c => !IsFolder(c)))
            {
                if (_origins.TryGetValue(request, out var origin) && origin.Stamp is { } stamp
                    && (origin.Parent != folder || origin.Baseline["name"].GetString() != request["name"].GetString())
                    && File.Exists(origin.Path) && stamp.HasChanged(origin.Path))
                {
                    changed.Add(request);
                }
            }
        }

        return changed;
    }

    /// <summary>What a save did not write: files that could not be read when loading, and ones another program changed since.</summary>
    private sealed class LeftAlone(bool overwriteChanged)
    {
        /// <summary>Write files another program changed since they were read anyway.</summary>
        public bool OverwriteChanged { get; } = overwriteChanged;

        /// <summary>The items not written (also folders that stay gone with a new order): their siblings' orders are assigned again next time.</summary>
        public HashSet<JsonObject> Items { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Files that could not be read, with the reason.</summary>
        public List<string> Unreadable { get; } = [];

        public List<string> Changed { get; } = [];

        /// <summary>
        /// Reports what was left alone: an <see cref="IOException"/> when files could not be read (overwriting cannot help
        /// there; changed files are named too), else a <see cref="FileChangedException"/>.
        /// </summary>
        public void ThrowIfAny()
        {
            if (Unreadable.Count > 0)
            {
                var (files, it, holds) = Unreadable.Count == 1 ? ("this file", "it", "it holds") : ("these files", "them", "they hold");
                var message = $"{string.Join("\n", Unreadable)}\nTermRest could not read {files} when it loaded the collection, so it did not save over {it} " +
                              $"(that would lose what {holds}), and the changes that belong in {it} are not saved. Fix {it} and reload the collection, " +
                              $"or delete {it} and save again.";
                if (Changed.Count > 0)
                {
                    var (they, them) = Changed.Count == 1 ? ("it", "it") : ("they", "them");
                    message += $"\nNot saved either, as {they} changed on disk since TermRest read {them}: {string.Join(", ", Changed)}.";
                }

                throw new IOException(message);
            }

            if (Changed.Count > 0)
            {
                throw new FileChangedException(Changed);
            }
        }
    }

    private void WriteItem(JsonObject item, bool isRoot, string path, JsonObject? parent, Dictionary<JsonObject, JsonNode> orders, LeftAlone leftAlone)
    {
        _origins.TryGetValue(item, out var origin);
        var isFolder = IsFolder(item);
        var file = isFolder ? V3Names.DefinitionPath(path) : path;
        var snapshot = Snapshot(item);
        var examplesChange = ExamplesChange(item); // before they are written
        JsonObject v3;
        if (origin is not null && JsonNode.DeepEquals(snapshot, origin.Baseline))
        {
            v3 = origin.V3 is null ? new JsonObject { ["$kind"] = V3Convert.CollectionKind } : (JsonObject)origin.V3.DeepClone();
        }
        else
        {
            v3 = isFolder ? V3Convert.DefinitionToV3(item, origin?.V3, isRoot) : V3Convert.RequestToV3(item, origin?.V3);
        }

        var order = isFolder ? V3Convert.DefinitionOrder : V3Convert.RequestOrder;
        var name = isRoot ? (item["info"] as JsonObject)?["name"].GetString() ?? "" : item["name"].GetString() ?? "";
        var fileName = isFolder ? Path.GetFileName(path) : V3Names.RequestStem(path);
        if (name != fileName)
        {
            v3.SetOrdered("name", name, order);
        }
        else if (v3["name"].GetString() is { } stored && stored != name)
        {
            v3.Remove("name");
        }

        if (orders.TryGetValue(item, out var newOrder))
        {
            v3.SetOrdered("order", newOrder.DeepClone(), order);
        }

        if (origin?.Stem is { } oldStem && oldStem != fileName && v3["examples"].GetString() is { } examples)
        {
            // Examples moved with the request's resources folder: the path follows it, written as the file writes it (Postman
            // writes both "./.resources/…" and ".resources/…").
            var dot = examples.StartsWith("./", StringComparison.Ordinal) ? "./" : "";
            var oldFolder = $"{dot}{V3Names.ResourcesDirectory}/{oldStem}.resources";
            if (examples.StartsWith(oldFolder, StringComparison.Ordinal) && (examples.Length == oldFolder.Length || examples[oldFolder.Length] == '/'))
            {
                v3["examples"] = $"{dot}{V3Names.ResourcesDirectory}/{fileName}.resources{examples[oldFolder.Length..]}";
            }
        }

        if (!isFolder && SaveExamples(item, path, origin is null, v3, leftAlone) is { } examplesFolder)
        {
            v3.SetOrdered("examples", examplesFolder, order);
        }

        // Folders get a definition file when they are new, have something to store or had one (also when it is gone: see
        // WriteAnew); requests always have a file. A request whose file is gone stays gone, as a folder does, unless its
        // examples are written: they belong to it, so it is written anew.
        var hadFile = origin?.File is not null;
        var needsFile = !isFolder || origin is null || origin.V3 is not null || v3.Count > 1;
        var writtenAnew = !isFolder && hadFile && examplesChange && !File.Exists(file);
        if (needsFile && (writtenAnew || !(hadFile && JsonNode.DeepEquals(v3, origin!.V3))))
        {
            var existing = File.Exists(file);
            if (existing && origin?.ReadError is { } error)
            {
                // Never overwritten, as it could not be read: it moves along with its folder unchanged. What it would have to
                // hold besides its position is not saved, and is reported.
                if (v3.Any(p => p.Key is not ("$kind" or "order")))
                {
                    leftAlone.Unreadable.Add($"{file}: {error}");
                    leftAlone.Items.Add(item);
                    return;
                }

                Remember(item, origin, path, parent, null, null, origin.FileInfo, null, snapshot);
                return;
            }

            // Changed by another program since it was read, or created where there was none: not saved over unless allowed.
            if (existing && (!hadFile || origin!.Stamp?.HasChanged(file) == true))
            {
                if (origin is null)
                {
                    throw new IOException($"'{file}' already exists.");
                }

                if (!leftAlone.OverwriteChanged)
                {
                    leftAlone.Changed.Add(file);
                    leftAlone.Items.Add(item);
                    return;
                }
            }

            var (text, info) = YamlFile.WriteChecked(existing && hadFile ? origin!.File : null, v3, Style, file, origin?.FileInfo ?? NewFile, out var written);
            CreateFolder(Path.GetDirectoryName(file)!); // a folder's .resources
            TextFile.WriteAtomic(file, text, info, out var stamp, _newFileMode);
            Remember(item, origin, path, parent, written, stamp, info, v3, snapshot);
            return;
        }

        Remember(item, origin, path, parent, origin?.File, origin?.Stamp, origin?.FileInfo ?? NewFile, origin?.V3 ?? (needsFile ? v3 : null), snapshot);
    }

    private void Remember(JsonObject item, Origin? origin, string path, JsonObject? parent, YamlFile? file, FileStamp? stamp, TextFileInfo info, JsonObject? v3, JsonObject snapshot)
    {
        origin ??= new Origin { Path = path, Baseline = snapshot };
        origin.Path = path;
        origin.Parent = parent;
        origin.File = file;
        origin.Stamp = stamp;
        origin.ReadError = file is null ? origin.ReadError : null;
        origin.FileInfo = info;
        origin.V3 = v3;
        origin.Baseline = snapshot;
        origin.Order = v3?["order"]?.DeepClone();
        origin.Stem = IsFolder(item) ? null : V3Names.RequestStem(path);
        _origins[item] = origin;
    }

    /// <summary>Desired paths for the current tree. Items keep their current file name while their name is unchanged.</summary>
    private void AssignPaths(JsonObject folder, string directory, Dictionary<JsonObject, string> desired, Dictionary<JsonObject, JsonObject> parents, HashSet<string> managed)
    {
        var children = Children(folder).ToList();
        var takenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Files and folders on disk that TermRest does not manage keep their names. They are where the folder is now (they
        // move along with it), or for a new folder where it will be.
        var location = _origins.TryGetValue(folder, out var self) ? self.Path : directory;
        if (System.IO.Directory.Exists(location))
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(location).Where(e => !managed.Contains(e)))
            {
                var entryName = Path.GetFileName(entry);
                takenFolders.Add(entryName);
                if (V3Names.IsRequestFile(entryName))
                {
                    takenFiles.Add(V3Names.RequestStem(entryName));
                }
            }
        }

        var pending = new List<JsonObject>();
        foreach (var child in children)
        {
            parents[child] = folder;
            var current = _origins.TryGetValue(child, out var origin) ? origin.Path : null;
            var currentName = current is null ? null : IsFolder(child) ? Path.GetFileName(current) : V3Names.RequestStem(current);
            var taken = IsFolder(child) ? takenFolders : takenFiles;
            if (origin is not null && currentName is not null && origin.Baseline["name"].GetString() == child["name"].GetString() && !taken.Contains(currentName))
            {
                taken.Add(currentName);
                desired[child] = Path.Combine(directory, Path.GetFileName(current!));
            }
            else
            {
                pending.Add(child);
            }
        }

        foreach (var child in pending)
        {
            var taken = IsFolder(child) ? takenFolders : takenFiles;
            var name = V3Names.Unique(V3Names.Safe(child["name"].GetString() ?? "untitled"), taken);
            taken.Add(name);
            desired[child] = Path.Combine(directory, IsFolder(child) ? name : name + V3Names.RequestSuffix);
        }

        foreach (var child in children.Where(IsFolder))
        {
            AssignPaths(child, desired[child], desired, parents, managed);
        }
    }

    /// <summary>
    /// New <c>order</c> values for a folder whose children changed: the longest run of children that are still in
    /// increasing order keeps its values; the others get values between their neighbors (steps of 1000 at the end).
    /// </summary>
    private void AssignOrders(JsonObject folder, Dictionary<JsonObject, JsonNode> orders)
    {
        var children = Children(folder).ToList();
        if (_loadedChildren.TryGetValue(folder, out var loaded) && loaded.SequenceEqual(children, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        var values = children
            .Select(c => _origins.TryGetValue(c, out var o) && o.Parent == folder ? OrderValue(o.Order) : null)
            .ToArray();
        var keep = LongestIncreasing(values);
        var i = 0;
        while (i < children.Count)
        {
            if (keep[i])
            {
                i++;
                continue;
            }

            var start = i;
            while (i < children.Count && !keep[i])
            {
                i++;
            }

            double? high = i < children.Count ? values[i] : null;
            var count = i - start;
            var low = start > 0 ? values[start - 1]!.Value : high is { } first && first <= 0 ? first - (1000 * (count + 1)) : 0;
            for (var k = 1; k <= count; k++)
            {
                double value;
                if (high is null)
                {
                    value = (Math.Floor(low / 1000) + k) * 1000;
                }
                else
                {
                    var step = (high.Value - low) / (count + 1);
                    if (double.IsInfinity(step))
                    {
                        step = (high.Value / (count + 1)) - (low / (count + 1)); // orders near the limits of a double
                    }

                    value = low + (step * k);
                    if (step >= 1)
                    {
                        value = Math.Floor(value);
                    }
                }

                orders[children[start + k - 1]] = value == Math.Floor(value) && Math.Abs(value) < 1e15
                    ? JsonValue.Create((long)value)
                    : JsonValue.Create(value);
            }
        }
    }

    private static bool[] LongestIncreasing(double?[] values)
    {
        var n = values.Length;
        var length = new int[n];
        var previous = new int[n];
        var best = -1;
        for (var i = 0; i < n; i++)
        {
            previous[i] = -1;
            if (values[i] is not { } value)
            {
                continue;
            }

            length[i] = 1;
            for (var j = 0; j < i; j++)
            {
                if (values[j] is { } before && before < value && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            }

            if (best < 0 || length[i] > length[best])
            {
                best = i;
            }
        }

        var keep = new bool[n];
        for (var i = best; i >= 0; i = previous[i])
        {
            keep[i] = true;
        }

        return keep;
    }

    private static void DeleteRequestFiles(string path)
    {
        if (File.Exists(path))
        {
            var resources = V3Names.ResourcesPath(path);
            File.Delete(path);
            if (System.IO.Directory.Exists(resources))
            {
                System.IO.Directory.Delete(resources, recursive: true);
            }
        }
    }

    private static void DeleteFolderIfEmpty(string path)
    {
        var definition = V3Names.DefinitionPath(path);
        if (File.Exists(definition))
        {
            File.Delete(definition);
        }

        foreach (var directory in (string[])[Path.Combine(path, V3Names.ResourcesDirectory), path])
        {
            if (System.IO.Directory.Exists(directory) && !System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
            {
                System.IO.Directory.Delete(directory);
            }
        }
    }

    /// <summary>Creates a folder and its missing parents, noting each in <paramref name="journal"/> so a failed save removes them.</summary>
    private static void CreateDirectory(string directory, List<(string From, string? To)> journal)
    {
        var missing = new Stack<string>();
        for (var d = directory; d is not null && !System.IO.Directory.Exists(d); d = Path.GetDirectoryName(d))
        {
            missing.Push(d);
        }

        System.IO.Directory.CreateDirectory(directory);
        while (missing.TryPop(out var created))
        {
            journal.Add((created, null));
        }
    }

    /// <summary>Creates a folder and its missing parents; in a conversion as private as the converted folder (see <see cref="ConvertFrom"/>).</summary>
    private void CreateFolder(string directory)
    {
        if (_newFileMode is not { } mode || OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(directory);
            return;
        }

        // One by one: Directory.CreateDirectory gives the missing parents the default permissions.
        var missing = new Stack<string>();
        for (var d = directory; d is not null && !System.IO.Directory.Exists(d); d = Path.GetDirectoryName(d))
        {
            missing.Push(d);
        }

        var folderMode = TextFile.FolderMode(mode);
        while (missing.TryPop(out var created))
        {
            System.IO.Directory.CreateDirectory(created, folderMode);
            File.SetUnixFileMode(created, folderMode); // the create mode is subject to the umask
        }
    }

    /// <summary>Moves a file or folder, noting it in <paramref name="journal"/> so a failed save moves it back.</summary>
    private static void Move(string from, string to, bool isDirectory, List<(string From, string? To)> journal)
    {
        if (isDirectory)
        {
            System.IO.Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }

        journal.Add((from, to));
    }

    /// <summary>
    /// Undoes the moves and created folders of a failed save, last first. What cannot be moved back stays where it is
    /// (<paramref name="current"/> says where): the next save moves it from there, and loading reports a staging folder
    /// that is left with files.
    /// </summary>
    private static void Undo(List<(string From, string? To)> journal, Dictionary<JsonObject, string> current)
    {
        for (var i = journal.Count - 1; i >= 0; i--)
        {
            var (from, to) = journal[i];
            try
            {
                if (to is null)
                {
                    if (System.IO.Directory.Exists(from) && !System.IO.Directory.EnumerateFileSystemEntries(from).Any())
                    {
                        System.IO.Directory.Delete(from);
                    }

                    continue;
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                if (System.IO.Directory.Exists(to))
                {
                    System.IO.Directory.Move(to, from);
                }
                else
                {
                    File.Move(to, from);
                }

                Rebase(current, to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left where it is; see above.
            }
        }
    }

    /// <summary>The first "name-1", "name-2" … next to <paramref name="taken"/> that is free on disk and not <paramref name="claimed"/> by another item.</summary>
    private static string FreePath(string taken, bool isFolder, IEnumerable<string> claimed)
    {
        var others = new HashSet<string>(claimed, StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(taken)!;
        var fileName = Path.GetFileName(taken);
        var name = isFolder ? fileName : V3Names.RequestStem(taken);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name}-{i}{fileName[name.Length..]}");
            if (!Path.Exists(candidate) && !others.Contains(candidate) && (isFolder || !Path.Exists(V3Names.ResourcesPath(candidate))))
            {
                return candidate;
            }
        }
    }

    private static void Rebase(Dictionary<JsonObject, string> current, string from, string to)
    {
        var prefix = from + Path.DirectorySeparatorChar;
        foreach (var key in current.Keys.ToList())
        {
            var path = current[key];
            if (path.Equals(from, StringComparison.Ordinal))
            {
                current[key] = to;
            }
            else if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                current[key] = to + path[from.Length..];
            }
        }
    }

    private static IEnumerable<JsonObject> PreOrder(JsonObject folder)
    {
        yield return folder;
        foreach (var child in Children(folder))
        {
            if (IsFolder(child))
            {
                foreach (var descendant in PreOrder(child))
                {
                    yield return descendant;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<JsonObject> Children(JsonObject folder) => (folder["item"] as JsonArray)?.OfType<JsonObject>() ?? [];

    private static bool IsFolder(JsonObject item) => item["item"] is JsonArray;

    /// <summary>The item without its children (folder content changes are tracked per child).</summary>
    /// <summary>An item without its children and saved examples, which have files of their own: what its file is written from.</summary>
    private static JsonObject Snapshot(JsonObject item)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in item)
        {
            if (key is not ("item" or "response"))
            {
                copy[key] = value?.DeepClone();
            }
        }

        return copy;
    }

    private static double? OrderValue(JsonNode? node) =>
        node is JsonValue value && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number)
            ? number
            : null;

    /// <summary>
    /// Files whose content TermRest could not edit losslessly: converting them to the editable model and back changes
    /// them, or the YAML writer cannot reproduce their values. An empty list means every file is fully supported.
    /// </summary>
    public List<string> Verify()
    {
        var problems = new List<string>();
        foreach (var (item, origin) in _origins)
        {
            if (origin.V3 is not { } original)
            {
                continue;
            }

            var file = IsFolder(item) ? V3Names.DefinitionPath(origin.Path) : origin.Path;
            var relative = Path.GetRelativePath(Directory, file);
            JsonObject back;
            if (IsFolder(item))
            {
                var isRoot = origin.Parent is null;
                var shape = isRoot
                    ? new JsonObject { ["info"] = new JsonObject { ["name"] = "x" } }
                    : new JsonObject { ["name"] = "x", ["item"] = new JsonArray() };
                V3Convert.DefinitionToV21(original, shape, isRoot);
                back = V3Convert.DefinitionToV3(shape, original, isRoot);
            }
            else
            {
                back = V3Convert.RequestToV3(V3Convert.RequestToV21(original, "x"), original);
            }

            var changed = original.Select(p => p.Key).Union(back.Select(p => p.Key)).Where(k => !JsonNode.DeepEquals(original[k], back[k])).ToList();
            if (changed.Count > 0)
            {
                problems.Add($"{relative}: editing would rewrite {string.Join(", ", changed)}");
            }

            if (!JsonNode.DeepEquals(YamlFile.Parse(YamlFile.Write(original, Style)).Json, original))
            {
                problems.Add($"{relative}: some values cannot be written back exactly");
            }
        }

        return problems;
    }
}
