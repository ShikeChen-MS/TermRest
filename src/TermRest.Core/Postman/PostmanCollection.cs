using System.Text.Json.Nodes;
using TermRest.Core.IO;
using TermRest.Core.Json;
using TermRest.Core.Model;

namespace TermRest.Core.Postman;

/// <summary>
/// A Postman collection (v2.1, with v2.0 read support) backed by its JSON DOM. All edits are made in place on the
/// DOM so that anything TermRest does not understand is written back untouched.
/// </summary>
public sealed class PostmanCollection
{
    public const string SchemaV21 = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";
    public const string FileSuffix = ".postman_collection.json";

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

    public string? FilePath { get; set; }

    public TextFileInfo FileInfo { get; }

    public PostmanSchemaVersion SchemaVersion { get; }

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
    public string BaseDirectory => FilePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(FilePath))!;

    public bool IsDirty { get; private set; }

    public event EventHandler? DirtyChanged;

    public static PostmanCollection Load(string path)
    {
        var (text, info) = TextFile.Read(path);
        try
        {
            return Parse(text, path, info);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"'{path}' is not a valid Postman collection: {ex.Message}", ex);
        }
    }

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

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("The collection has no file path yet.");
        TextFile.WriteAtomic(path, Serialize(), FileInfo);
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

    public IEnumerable<CollectionNode> Descendants() => Walk(RootNode);

    public IEnumerable<RequestNode> AllRequests() => Descendants().OfType<RequestNode>();

    /// <summary>Finds a request by path (<c>Folder/Sub/Name</c>) or, failing that, by unique name.</summary>
    public RequestNode? FindRequest(string pathOrName)
    {
        var trimmed = pathOrName.Trim('/');
        var byPath = AllRequests().FirstOrDefault(r => string.Equals(r.PathInCollection, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            return byPath;
        }

        var byName = AllRequests().Where(r => string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public FolderNode? FindFolder(string path)
    {
        var trimmed = path.Trim('/');
        return trimmed.Length == 0
            ? RootNode
            : Descendants().OfType<FolderNode>().FirstOrDefault(f => string.Equals(f.PathInCollection, trimmed, StringComparison.OrdinalIgnoreCase));
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
