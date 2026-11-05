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

    void Save(PostmanCollection collection);
}

/// <summary>
/// A Postman collection format v3 folder: <c>.resources/definition.yaml</c> for the collection and each folder, one
/// <c>*.request.yaml</c> per request, ordered by their <c>order</c> keys. In memory the collection is the same v2.1 JSON
/// model TermRest edits everywhere; saving writes back only what changed: edited requests (only the edited YAML
/// entries), renamed or moved items (their files and <c>.resources</c> move along), new and deleted items, and the
/// <c>order</c> of reordered siblings. Files TermRest does not show (GraphQL, gRPC … requests, unreadable files) are
/// never touched.
/// </summary>
internal sealed class V3CollectionStorage : ICollectionStorage
{
    private static readonly TextFileInfo NewFile = new(false, true, false);

    private readonly Dictionary<JsonObject, Origin> _origins = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsonObject, List<JsonObject>> _loadedChildren = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private YamlStyle _style = YamlStyle.Default;

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

        public YamlFile? File { get; set; }

        public TextFileInfo FileInfo { get; set; } = NewFile;

        /// <summary>The v3 content as last read or written.</summary>
        public JsonObject? V3 { get; set; }

        /// <summary>The v2.1 item (without children) as last read or written.</summary>
        public required JsonObject Baseline { get; set; }

        public JsonNode? Order { get; set; }
    }

    // ----- loading ---------------------------------------------------------------------------------

    public static PostmanCollection Load(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"'{directory}' does not exist.");
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
        var (definition, info) = storage.Read(definitionPath, warnings, trees);
        V3Convert.DefinitionToV21(definition?.Json, root, isRoot: true);
        if (definition?.Json["name"].GetString() is { Length: > 0 } name)
        {
            root["info"]!["name"] = name;
        }

        storage._origins[root] = new Origin { Path = directory, File = definition, FileInfo = info, V3 = definition?.Json, Baseline = Snapshot(root) };
        var hidden = storage.LoadChildren(directory, root, warnings, trees);
        if (hidden > 0)
        {
            warnings.Add($"{hidden} non-HTTP request(s) (GraphQL, gRPC, WebSocket …) are not shown; their files are left untouched.");
        }

        storage._style = YamlStyle.Detect(trees);
        var collection = PostmanCollection.FromStorage(root, directory, storage);
        collection.LoadWarnings.AddRange(warnings);
        return collection;
    }

    /// <summary>Creates an empty collection folder (with its definition file) and loads it.</summary>
    public static PostmanCollection Create(string directory, string name, int? lineWidth = null)
    {
        directory = Path.GetFullPath(directory);
        var definition = new JsonObject { ["$kind"] = V3Convert.CollectionKind };
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) != name)
        {
            definition["name"] = name;
        }

        TextFile.WriteAtomic(V3Names.DefinitionPath(directory), YamlFile.Write(definition, YamlStyle.Default.WithLineWidth(lineWidth)).TrimEnd('\n'), NewFile);
        var collection = Load(directory);
        collection.YamlLineWidth = lineWidth;
        return collection;
    }

    /// <summary>
    /// Writes <paramref name="source"/> (any format) as a new v3 collection folder, including saved responses as
    /// <c>.example.yaml</c> files, and returns the loaded result. Long values are wrapped at
    /// <paramref name="lineWidth"/> (null or 0: not wrapped, like current Postman versions).
    /// </summary>
    public static PostmanCollection ConvertFrom(PostmanCollection source, string directory, int? lineWidth = null)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException($"'{directory}' already exists and is not empty.");
        }

        System.IO.Directory.CreateDirectory(directory);
        var root = (JsonObject)source.Root.DeepClone();
        var storage = new V3CollectionStorage(directory) { LineWidth = lineWidth };
        var collection = PostmanCollection.FromStorage(root, directory, storage);
        storage.Save(collection);
        foreach (var request in collection.AllRequests())
        {
            storage.WriteExamples(request.Json);
        }

        var converted = Load(directory);
        converted.YamlLineWidth = lineWidth;
        return converted;
    }

    /// <summary>Saved responses (v2.1 <c>response</c>) as v3 example files next to the request.</summary>
    private void WriteExamples(JsonObject item)
    {
        if (item["response"] is not JsonArray { Count: > 0 } responses || !_origins.TryGetValue(item, out var origin))
        {
            return;
        }

        var folder = Path.Combine(V3Names.ResourcesPath(origin.Path), "examples");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var response in responses.OfType<JsonObject>())
        {
            var name = V3Names.Unique(V3Names.Safe(response["name"].GetString() ?? "Example"), taken);
            taken.Add(name);
            var example = V3Convert.ExampleToV3(response);
            order += 1000;
            example["order"] = order;
            TextFile.WriteAtomic(Path.Combine(folder, name + ".example.yaml"), YamlFile.Write(example, Style).TrimEnd('\n'), NewFile);
        }

        var v3 = (JsonObject)origin.V3!.DeepClone();
        v3.SetOrdered("examples", $"./{V3Names.ResourcesDirectory}/{V3Names.RequestStem(origin.Path)}.resources/examples", V3Convert.RequestOrder);
        var text = origin.File!.Render(v3, Style);
        TextFile.WriteAtomic(origin.Path, text[..^1], origin.FileInfo);
        origin.File = YamlFile.Parse(text);
        origin.V3 = v3;
    }

    private (YamlFile? File, TextFileInfo Info) Read(string path, List<string> warnings, List<YamlTree> trees)
    {
        if (!File.Exists(path))
        {
            return (null, NewFile);
        }

        try
        {
            var (text, info) = TextFile.Read(path);
            var file = YamlFile.Parse(text);
            trees.Add(file.Tree);
            return (file, info);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _unreadable.Add(path);
            warnings.Add($"{Path.GetRelativePath(Directory, path)}: {ex.Message}");
            return (null, NewFile);
        }
    }

    /// <summary>Loads requests and sub-folders of <paramref name="directory"/>. Returns the number of non-HTTP requests skipped.</summary>
    private int LoadChildren(string directory, JsonObject parent, List<string> warnings, List<YamlTree> trees)
    {
        var hidden = 0;
        var children = new List<(double? Order, string Name, JsonObject Item)>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory).Where(V3Names.IsRequestFile))
        {
            var (yaml, info) = Read(file, warnings, trees);
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
                Path = file, Parent = parent, File = yaml, FileInfo = info, V3 = yaml.Json, Baseline = Snapshot(item), Order = yaml.Json["order"]?.DeepClone(),
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

            var (definition, info) = Read(V3Names.DefinitionPath(sub), warnings, trees);
            var name = definition?.Json["name"].GetString() is { Length: > 0 } explicitName ? explicitName : dirName;
            var folder = new JsonObject { ["name"] = name, ["item"] = new JsonArray() };
            V3Convert.DefinitionToV21(definition?.Json, folder, isRoot: false);
            _origins[folder] = new Origin
            {
                Path = sub, Parent = parent, File = definition, FileInfo = info, V3 = definition?.Json, Baseline = Snapshot(folder),
                Order = definition?.Json["order"]?.DeepClone(),
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

    public void Save(PostmanCollection collection)
    {
        var root = collection.Root;
        var parents = new Dictionary<JsonObject, JsonObject>(ReferenceEqualityComparer.Instance);
        var desired = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance) { [root] = Directory };
        AssignPaths(root, Directory, desired, parents);

        var current = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);
        foreach (var (item, origin) in _origins)
        {
            current[item] = origin.Path;
        }

        var staging = Path.Combine(Directory, $".termrest-saving-{Guid.NewGuid():N}");
        var staged = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);
        var stagedResources = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);
        var oldStems = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);

        // 1. Take out everything that moves or is renamed (a moving folder takes its content along).
        foreach (var item in PreOrder(root).Where(i => i != root && _origins.ContainsKey(i)))
        {
            var origin = _origins[item];
            var moved = origin.Parent != parents[item] || Path.GetFileName(current[item]) != Path.GetFileName(desired[item]);
            if (!moved)
            {
                continue;
            }

            System.IO.Directory.CreateDirectory(staging);
            var target = Path.Combine(staging, staged.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (IsFolder(item))
            {
                System.IO.Directory.Move(current[item], target);
                Rebase(current, current[item], target);
            }
            else
            {
                oldStems[item] = V3Names.RequestStem(current[item]);
                var resources = V3Names.ResourcesPath(current[item]);
                File.Move(current[item], target);
                if (System.IO.Directory.Exists(resources))
                {
                    System.IO.Directory.Move(resources, target + ".resources");
                    stagedResources[item] = target + ".resources";
                }

                current[item] = target;
            }

            staged[item] = target;
        }

        // 2. Delete what was removed (moved-out content is already safe in staging). Folders are only removed once
        //    empty: files TermRest does not show (GraphQL, gRPC … requests) are never deleted.
        var inTree = new HashSet<JsonObject>(PreOrder(root), ReferenceEqualityComparer.Instance);
        var removed = _origins.Keys.Where(i => !inTree.Contains(i)).ToList();
        foreach (var item in removed.Where(i => !IsFolder(i)))
        {
            DeleteRequestFiles(current[item]);
        }

        foreach (var item in removed.Where(IsFolder).OrderByDescending(i => current[i].Length))
        {
            DeleteFolderIfEmpty(current[item]);
        }

        foreach (var item in removed)
        {
            _origins.Remove(item);
            _loadedChildren.Remove(item);
        }

        // 3. Put moved items in place, parents first.
        foreach (var item in PreOrder(root).Where(staged.ContainsKey))
        {
            var target = desired[item];
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (IsFolder(item))
            {
                System.IO.Directory.Move(current[item], target);
                Rebase(current, current[item], target);
            }
            else
            {
                File.Move(current[item], target);
                current[item] = target;
                if (stagedResources.TryGetValue(item, out var resources))
                {
                    var resourcesTarget = V3Names.ResourcesPath(target);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(resourcesTarget)!);
                    System.IO.Directory.Move(resources, resourcesTarget);
                }
            }
        }

        if (System.IO.Directory.Exists(staging))
        {
            System.IO.Directory.Delete(staging, recursive: false);
        }

        // 4. Write content: new items, edited items, renamed items and reordered siblings.
        var orders = new Dictionary<JsonObject, JsonNode>(ReferenceEqualityComparer.Instance);
        foreach (var folder in PreOrder(root).Where(IsFolder))
        {
            AssignOrders(folder, orders);
        }

        foreach (var item in PreOrder(root))
        {
            if (IsFolder(item))
            {
                System.IO.Directory.CreateDirectory(desired[item]);
            }

            WriteItem(item, item == root, desired[item], parents.GetValueOrDefault(item), orders, oldStems.GetValueOrDefault(item));
        }

        foreach (var folder in PreOrder(root).Where(IsFolder))
        {
            _loadedChildren[folder] = Children(folder).ToList();
        }
    }

    private void WriteItem(JsonObject item, bool isRoot, string path, JsonObject? parent, Dictionary<JsonObject, JsonNode> orders, string? oldStem)
    {
        _origins.TryGetValue(item, out var origin);
        var isFolder = IsFolder(item);
        var file = isFolder ? V3Names.DefinitionPath(path) : path;
        if (_unreadable.Contains(file))
        {
            return; // never overwrite a file that could not be read
        }

        var snapshot = Snapshot(item);
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

        if (oldStem is not null && v3["examples"].GetString() is { } examples)
        {
            // Examples moved with the request's resources folder.
            var oldPrefix = $"./{V3Names.ResourcesDirectory}/{oldStem}.resources";
            if (examples.StartsWith(oldPrefix, StringComparison.Ordinal))
            {
                v3["examples"] = $"./{V3Names.ResourcesDirectory}/{fileName}.resources{examples[oldPrefix.Length..]}";
            }
        }

        var existing = origin?.File is not null && File.Exists(file);

        // Folders get a definition file when they are new or have something to store; requests always have a file.
        var needsFile = !isFolder || existing || origin is null || v3.Count > 1;
        if (needsFile && !(existing && JsonNode.DeepEquals(v3, origin!.V3)))
        {
            var text = existing ? origin!.File!.Render(v3, Style) : YamlFile.Write(v3, Style);
            var info = origin?.FileInfo ?? NewFile;
            TextFile.WriteAtomic(file, text.EndsWith('\n') && info.HasTrailingNewline ? text[..^1] : text, info);
            origin = Remember(item, origin, path, parent, YamlFile.Parse(text), info, v3, snapshot);
            return;
        }

        Remember(item, origin, path, parent, origin?.File, origin?.FileInfo ?? NewFile, origin?.V3 ?? (needsFile ? v3 : null), snapshot);
    }

    private Origin Remember(JsonObject item, Origin? origin, string path, JsonObject? parent, YamlFile? file, TextFileInfo info, JsonObject? v3, JsonObject snapshot)
    {
        origin ??= new Origin { Path = path, Baseline = snapshot };
        origin.Path = path;
        origin.Parent = parent;
        origin.File = file;
        origin.FileInfo = info;
        origin.V3 = v3;
        origin.Baseline = snapshot;
        origin.Order = v3?["order"]?.DeepClone();
        _origins[item] = origin;
        return origin;
    }

    /// <summary>Desired paths for the current tree. Items keep their current file name while their name is unchanged.</summary>
    private void AssignPaths(JsonObject folder, string directory, Dictionary<JsonObject, string> desired, Dictionary<JsonObject, JsonObject> parents)
    {
        var children = Children(folder).ToList();
        var takenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Files and folders on disk that TermRest does not manage keep their names.
        var managed = new HashSet<string>(_origins.Values.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory).Where(V3Names.IsRequestFile).Where(f => !managed.Contains(f)))
            {
                takenFiles.Add(V3Names.RequestStem(file));
            }

            foreach (var sub in System.IO.Directory.EnumerateDirectories(directory).Where(d => !managed.Contains(d)))
            {
                takenFolders.Add(Path.GetFileName(sub));
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
            AssignPaths(child, desired[child], desired, parents);
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
    private static JsonObject Snapshot(JsonObject item)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in item)
        {
            if (key != "item")
            {
                copy[key] = value?.DeepClone();
            }
        }

        return copy;
    }

    private static double? OrderValue(JsonNode? node) =>
        node is JsonValue value && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
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
