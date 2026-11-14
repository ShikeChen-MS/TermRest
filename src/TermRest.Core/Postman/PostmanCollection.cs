using System.Text.Json.Nodes;
using TermRest.Core.IO;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman.V3;
using TermRest.Core.Scripting;

namespace TermRest.Core.Postman;

/// <summary>
/// A Postman collection backed by its v2.1 JSON DOM: a v2.1/v2.0 JSON file, or a collection format v3 folder of YAML
/// files (converted to the same model, see <see cref="V3CollectionStorage"/>). All edits are made in place on the DOM
/// so that anything TermRest does not understand is written back untouched.
/// </summary>
public sealed class PostmanCollection
{
    public const string SchemaV21 = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";
    public const string FileSuffix = ".postman_collection.json";

    private ICollectionStorage? _storage;

    /// <summary>What the JSON file held when last read or written (see <see cref="Save"/>).</summary>
    private FileStamp? _stamp;

    private PostmanCollection(JsonObject root, string? filePath, TextFileInfo fileInfo)
    {
        Root = root;
        FilePath = filePath;
        FileInfo = fileInfo;
        var schema = (root["info"] as JsonObject)?["schema"].GetString() ?? "";
        SchemaVersion = schema.Contains("v2.0", StringComparison.OrdinalIgnoreCase) ? PostmanSchemaVersion.V20 : PostmanSchemaVersion.V21;
        RootNode = new CollectionRootNode(this, root);
        BuildChildren(RootNode);
    }

    public JsonObject Root { get; }

    /// <summary>The collection file, or for a v3 collection its folder.</summary>
    public string? FilePath { get; set; }

    public TextFileInfo FileInfo { get; }

    public PostmanSchemaVersion SchemaVersion { get; }

    /// <summary>True for a collection format v3 folder of YAML files.</summary>
    public bool IsV3 => _storage is V3CollectionStorage;

    /// <summary>Human-readable file format, e.g. "Postman v2.1 JSON".</summary>
    public string FormatName => IsV3 ? "Postman v3 YAML" : SchemaVersion == PostmanSchemaVersion.V20 ? "Postman v2.0 JSON" : "Postman v2.1 JSON";

    /// <summary>Problems found while loading (files that could not be read, items that are not shown).</summary>
    public List<string> LoadWarnings { get; } = [];

    /// <summary>
    /// For a v3 collection: the column at which new and changed values are wrapped when saving (0: never); null (the
    /// default) follows the collection's files. See <see cref="Workspace.WorkspaceSettings.YamlLineWidth"/>. JSON
    /// collections ignore it.
    /// </summary>
    public int? YamlLineWidth
    {
        get => (_storage as V3CollectionStorage)?.LineWidth;
        set
        {
            if (_storage is V3CollectionStorage v3)
            {
                v3.LineWidth = value;
            }
        }
    }

    /// <summary>For a v3 collection: the column its new and changed values are wrapped at (0: not wrapped); null for JSON.</summary>
    public int? EffectiveYamlLineWidth => (_storage as V3CollectionStorage)?.Style.LineWidth;

    public CollectionRootNode RootNode { get; }

    public string Name
    {
        get => RootNode.Name;
        set
        {
            RootNode.Name = value;
            MarkDirty();
        }
    }

    /// <summary>Directory used to resolve relative file paths (body files, certificates).</summary>
    public string BaseDirectory => _storage?.BaseDirectory
                                   ?? (FilePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

    public bool IsDirty { get; private set; }

    public event EventHandler? DirtyChanged;

    /// <summary>Loads a v2.0/v2.1 JSON collection file, or a collection format v3 folder.</summary>
    public static PostmanCollection Load(string path)
    {
        if (Directory.Exists(path))
        {
            return V3CollectionStorage.Load(path);
        }

        var (text, info) = TextFile.Read(path, out var stamp);
        try
        {
            var collection = Parse(text, path, info);
            collection._stamp = stamp;
            return collection;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"'{path}' is not a valid Postman collection: {ex.Message}", ex);
        }
    }

    internal static PostmanCollection FromStorage(JsonObject root, string path, ICollectionStorage storage) =>
        new(root, path, TextFileInfo.PostmanDefault) { _storage = storage };

    public static PostmanCollection Parse(string json, string? filePath = null, TextFileInfo? info = null)
    {
        if (PostmanJson.Parse(json) is not JsonObject root || root["info"] is not JsonObject)
        {
            throw new InvalidDataException("Missing 'info' object; expected a Postman collection v2.0/v2.1 export.");
        }

        return new PostmanCollection(root, filePath, info ?? TextFileInfo.PostmanDefault);
    }

    public static PostmanCollection Create(string name, string? filePath = null)
    {
        var root = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["_postman_id"] = Guid.NewGuid().ToString(),
                ["name"] = name,
                ["schema"] = SchemaV21,
            },
            ["item"] = new JsonArray(),
        };
        var collection = new PostmanCollection(root, filePath, TextFileInfo.PostmanDefault);
        collection.MarkDirty();
        return collection;
    }

    public string Serialize() => PostmanJson.Serialize(Root);

    /// <summary>
    /// For a v3 collection: files TermRest could not edit without changing details (empty when every file is fully
    /// supported). JSON collections are checked by comparing <see cref="Serialize"/> with the file.
    /// </summary>
    public IReadOnlyList<string> Verify() => _storage is V3CollectionStorage v3 ? v3.Verify() : [];

    /// <summary>
    /// Writes this collection (any format) as a new Postman v3 YAML collection folder and returns it. Long values are
    /// wrapped at <paramref name="lineWidth"/> (null or 0: not wrapped). On Unix it is as private as this collection's file.
    /// </summary>
    public PostmanCollection ConvertToV3(string directory, int? lineWidth = null) => V3CollectionStorage.ConvertFrom(this, directory, lineWidth);

    /// <summary>
    /// The secret variables (of the collection and its folders) whose values <see cref="ConvertToV3"/> leaves out: Postman's
    /// v3 collections keep secret values out of their files.
    /// </summary>
    public List<string> SecretValuesLeftOutOfV3() => IsV3 ? [] : V3Convert.SecretValuesLeftOut(Root);

    /// <summary>
    /// The collection and folders whose auth is a list of v3 credential sets chosen by URL rules: <see cref="ExportV21Json"/>
    /// keeps the first set only (v2.1 has one auth per folder), which Postman then applies to every URL.
    /// </summary>
    public List<string> CredentialSetsLeftOutOfV21() =>
        [.. Descendants().OfType<FolderNode>().Prepend(RootNode).Where(f => f.Json[V3Convert.AuthSetsProperty] is JsonArray)
            .Select(f => f is CollectionRootNode ? $"collection '{Name}'" : $"folder '{f.PathInCollection}'")];

    /// <summary>The collection as a Postman v2.1 JSON document (for any source format).</summary>
    public string ExportV21Json()
    {
        var copy = (JsonObject)Root.DeepClone();
        if (copy["info"] is JsonObject info)
        {
            info.SetOrdered("_postman_id", info["_postman_id"]?.DeepClone() ?? Guid.NewGuid().ToString(), ["_postman_id", "name", "description", "schema"]);
            info["schema"] = SchemaV21;
        }

        RemoveV3Markers(copy);
        return PostmanJson.Serialize(copy);

        static void RemoveV3Markers(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    obj.Remove("x-v3-type");
                    obj.Remove(V3.V3Convert.AuthSetsProperty); // v2.1 has one auth per folder: the first set, in "auth"
                    foreach (var (_, value) in obj)
                    {
                        RemoveV3Markers(value);
                    }

                    break;
                case JsonArray array:
                    foreach (var value in array)
                    {
                        RemoveV3Markers(value);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Saves the collection: a JSON file whole, a v3 folder only where it changed. A v3 file that could not be read when
    /// the collection was loaded is never saved over: when changes belong in it, everything else is saved and an
    /// <see cref="IOException"/> names it (the collection stays dirty).
    /// </summary>
    /// <param name="overwriteChanged">
    /// Write files that changed on disk since they were read too; otherwise they are left alone and
    /// <see cref="IO.FileChangedException"/> names them (the collection stays dirty).
    /// </param>
    public void Save(string? path = null, bool overwriteChanged = false)
    {
        if (_storage is not null)
        {
            if (path is not null && !string.Equals(Path.GetFullPath(path), FilePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("A v3 collection folder is saved in place; use 'termrest convert' to write another format.");
            }

            _storage.Save(this, overwriteChanged);
            SetDirty(false);
            return;
        }

        path ??= FilePath ?? throw new InvalidOperationException("The collection has no file path yet.");
        var fullPath = Path.GetFullPath(path);
        var samePath = FilePath is not null && string.Equals(fullPath, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase);
        if (!overwriteChanged && samePath && _stamp is not null && File.Exists(fullPath) && _stamp.HasChanged(fullPath))
        {
            throw new FileChangedException([fullPath]);
        }

        TextFile.WriteAtomic(path, Serialize(), FileInfo, out var stamp);
        _stamp = stamp;
        FilePath = path;
        SetDirty(false);
    }

    public void MarkDirty() => SetDirty(true);

    private void SetDirty(bool value)
    {
        if (IsDirty != value)
        {
            IsDirty = value;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ----- queries ---------------------------------------------------------------------------

    public List<KeyValueEntry> GetVariables() => PostmanRequestMapper.ReadKeyValues(Root["variable"]);

    public void SetVariables(IEnumerable<KeyValueEntry> variables)
    {
        Root.SetOrdered("variable", PostmanRequestMapper.WriteKeyValues(variables, KeyValueStyle.CollectionVariable), PostmanRequestMapper.RootOrder);
        MarkDirty();
    }

    /// <summary>The scripts of the collection (<see cref="RootNode"/>) or of a folder: they run around every request inside.</summary>
    public ScriptSet GetScripts(FolderNode folder)
    {
        EnsureOwned(folder);
        return PostmanRequestMapper.ReadScripts(folder.Json["event"]);
    }

    /// <summary>Sets the scripts of the collection (<see cref="RootNode"/>) or of a folder; false when nothing changed.</summary>
    public bool SetScripts(FolderNode folder, ScriptSet scripts)
    {
        var current = GetScripts(folder);
        if (scripts.Equals(current))
        {
            return false;
        }

        var order = folder is CollectionRootNode ? PostmanRequestMapper.RootOrder : PostmanRequestMapper.ItemOrder;
        PostmanRequestMapper.ApplyScripts(folder.Json, scripts, current, order);
        MarkDirty();
        return true;
    }

    public IEnumerable<CollectionNode> Descendants() => Walk(RootNode);

    public IEnumerable<RequestNode> AllRequests() => Descendants().OfType<RequestNode>();

    /// <summary>
    /// Finds a request by path (<c>Folder/Sub/Name</c>) or, failing that, by unique name. Names may contain slashes
    /// (Postman allows <c>/users/:id</c>), so the exact text is tried before surrounding slashes are ignored.
    /// </summary>
    public RequestNode? FindRequest(string pathOrName)
    {
        var requests = AllRequests().ToList();
        string[] candidates = [pathOrName, pathOrName.Trim('/')];
        foreach (var candidate in candidates)
        {
            if (requests.FirstOrDefault(r => string.Equals(r.PathInCollection, candidate, StringComparison.OrdinalIgnoreCase)) is { } byPath)
            {
                return byPath;
            }
        }

        foreach (var candidate in candidates)
        {
            var byName = requests.Where(r => string.Equals(r.Name, candidate, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0)
            {
                return byName.Count == 1 ? byName[0] : null;
            }
        }

        return null;
    }

    public FolderNode? FindFolder(string path)
    {
        var folders = Descendants().OfType<FolderNode>().ToList();
        return folders.FirstOrDefault(f => string.Equals(f.PathInCollection, path, StringComparison.OrdinalIgnoreCase))
               ?? (path.Trim('/') is { Length: > 0 } trimmed
                   ? folders.FirstOrDefault(f => string.Equals(f.PathInCollection, trimmed, StringComparison.OrdinalIgnoreCase))
                   : RootNode);
    }

    /// <summary>Resolves "inherit" by walking up folders to the collection.</summary>
    public AuthSpec? GetEffectiveAuth(RequestNode node, HttpRequestSpec? spec = null)
    {
        EnsureOwned(node);
        var auth = (spec ?? node.Baseline).Auth;
        if (auth is not null)
        {
            return auth;
        }

        foreach (var folder in node.Ancestors())
        {
            if (folder.Auth is { } folderAuth)
            {
                return folderAuth;
            }
        }

        return null;
    }

    // ----- edits -----------------------------------------------------------------------------

    /// <summary>Writes the parts of <paramref name="spec"/> that changed back into the request JSON.</summary>
    public bool UpdateRequest(RequestNode node, HttpRequestSpec spec)
    {
        EnsureOwned(node);
        if (spec.ContentEquals(node.Baseline))
        {
            return false;
        }

        PostmanRequestMapper.Apply(node.Json, spec, node.Baseline, SchemaVersion);
        node.ResetBaseline();
        MarkDirty();
        return true;
    }

    public RequestNode AddRequest(FolderNode parent, string name, HttpRequestSpec? spec = null)
    {
        EnsureOwned(parent);
        var json = new JsonObject
        {
            ["name"] = name,
            ["request"] = new JsonObject
            {
                ["method"] = spec?.Method ?? "GET",
                ["header"] = new JsonArray(),
            },
            ["response"] = new JsonArray(),
        };
        parent.EnsureItemArray().Add((JsonNode)json);
        var node = new RequestNode(this, json, parent);
        parent.Children.Add(node);
        if (spec is not null)
        {
            PostmanRequestMapper.Apply(json, spec, node.Baseline, SchemaVersion);
            node.ResetBaseline();
        }

        MarkDirty();
        return node;
    }

    public FolderNode AddFolder(FolderNode parent, string name)
    {
        EnsureOwned(parent);
        var json = new JsonObject { ["name"] = name, ["item"] = new JsonArray() };
        parent.EnsureItemArray().Add((JsonNode)json);
        var node = new FolderNode(this, json, parent);
        parent.Children.Add(node);
        MarkDirty();
        return node;
    }

    public void Rename(CollectionNode node, string name)
    {
        EnsureOwned(node);
        node.Name = name;
        MarkDirty();
    }

    public void Remove(CollectionNode node)
    {
        EnsureOwned(node);
        if (node.Parent is not { } parent)
        {
            throw new InvalidOperationException("The collection root cannot be removed.");
        }

        parent.EnsureItemArray().Remove(node.Json);
        parent.Children.Remove(node);
        node.Parent = null;
        MarkDirty();
    }

    public CollectionNode Duplicate(CollectionNode node)
    {
        EnsureOwned(node);
        if (node.Parent is not { } parent)
        {
            throw new InvalidOperationException("Duplicate the collection file instead.");
        }

        var clone = (JsonObject)node.Json.DeepClone();
        clone["name"] = $"{node.Name} Copy";
        StripIds(clone);
        var items = parent.EnsureItemArray();
        var index = items.IndexOf(node.Json);
        items.Insert(index + 1, clone);
        var copy = CreateNode(clone, parent);
        parent.Children.Insert(parent.Children.IndexOf(node) + 1, copy);
        MarkDirty();
        return copy;
    }

    public void Move(CollectionNode node, FolderNode newParent, int index)
    {
        EnsureOwned(node);
        EnsureOwned(newParent);
        if (node.Parent is not { } oldParent)
        {
            throw new InvalidOperationException("The collection root cannot be moved.");
        }

        if (node is FolderNode folder && (newParent == folder || newParent.Ancestors().Contains(folder)))
        {
            throw new InvalidOperationException("A folder cannot be moved into itself.");
        }

        oldParent.EnsureItemArray().Remove(node.Json);
        oldParent.Children.Remove(node);
        var items = newParent.EnsureItemArray();
        index = Math.Clamp(index, 0, newParent.Children.Count);
        var jsonIndex = index < newParent.Children.Count ? items.IndexOf(newParent.Children[index].Json) : items.Count;
        items.Insert(jsonIndex, node.Json);
        newParent.Children.Insert(index, node);
        node.Parent = newParent;
        MarkDirty();
    }

    // ----- tree building ---------------------------------------------------------------------

    private void BuildChildren(FolderNode folder)
    {
        folder.Children.Clear();
        if (folder.Json["item"] is not JsonArray items)
        {
            return;
        }

        foreach (var element in items)
        {
            if (element is JsonObject obj)
            {
                folder.Children.Add(CreateNode(obj, folder));
            }
        }
    }

    private CollectionNode CreateNode(JsonObject json, FolderNode parent)
    {
        if (json["item"] is JsonArray)
        {
            var folder = new FolderNode(this, json, parent);
            BuildChildren(folder);
            return folder;
        }

        return new RequestNode(this, json, parent);
    }

    private static IEnumerable<CollectionNode> Walk(FolderNode folder)
    {
        foreach (var child in folder.Children)
        {
            yield return child;
            if (child is FolderNode sub)
            {
                foreach (var descendant in Walk(sub))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static void StripIds(JsonObject item)
    {
        item.Remove("id");
        item.Remove("_postman_id");
        if (item["item"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                StripIds(child);
            }
        }
    }

    private void EnsureOwned(CollectionNode node)
    {
        if (node.Collection != this)
        {
            throw new InvalidOperationException("The node belongs to a different collection.");
        }
    }
}
