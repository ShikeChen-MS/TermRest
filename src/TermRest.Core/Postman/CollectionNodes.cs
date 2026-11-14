using System.Text.Json.Nodes;
using TermRest.Core.Json;
using TermRest.Core.Model;

namespace TermRest.Core.Postman;

/// <summary>A folder or request in a collection tree. Wraps (and edits in place) the underlying item JSON.</summary>
public abstract class CollectionNode
{
    private protected CollectionNode(PostmanCollection collection, JsonObject json, FolderNode? parent)
    {
        Collection = collection;
        Json = json;
        Parent = parent;
    }

    public PostmanCollection Collection { get; }

    public JsonObject Json { get; }

    public FolderNode? Parent { get; internal set; }

    public virtual string Name
    {
        get => Json["name"].GetString() ?? "(unnamed)";
        internal set => Json.SetOrdered("name", value, PostmanRequestMapper.ItemOrder);
    }

    /// <summary>Slash-separated path inside the collection, e.g. <c>Users/Get user</c>.</summary>
    public string PathInCollection
    {
        get
        {
            var names = new Stack<string>();
            for (CollectionNode? node = this; node is not null and not CollectionRootNode; node = node.Parent)
            {
                names.Push(node.Name);
            }

            return string.Join('/', names);
        }
    }

    public IEnumerable<FolderNode> Ancestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
        {
            yield return node;
        }
    }

    public override string ToString() => Name;
}

public class FolderNode : CollectionNode
{
    internal FolderNode(PostmanCollection collection, JsonObject json, FolderNode? parent)
        : base(collection, json, parent)
    {
    }

    public List<CollectionNode> Children { get; } = [];

    /// <summary>Folder-level auth; null means inherit. A v3 folder's credential sets come with it (see <see cref="AuthSpec.CredentialSets"/>).</summary>
    public AuthSpec? Auth
    {
        get
        {
            var auth = PostmanRequestMapper.ReadAuth(Json["auth"], Collection.SchemaVersion);
            if (auth is not null && Json[V3.V3Convert.AuthSetsProperty] is JsonArray sets)
            {
                auth.CredentialSets = [.. sets.OfType<JsonObject>().Select(set => new CredentialSet(
                    set["name"].GetString() ?? "",
                    [.. (set["rules"] as JsonArray ?? []).OfType<JsonObject>().Select(rule => KeyValuePair.Create(rule["key"].GetString() ?? "", rule["value"].GetString() ?? ""))],
                    PostmanRequestMapper.ReadAuth(set["auth"], Collection.SchemaVersion)))];
            }

            return auth;
        }
    }

    internal JsonArray EnsureItemArray()
    {
        if (Json["item"] is JsonArray items)
        {
            return items;
        }

        var array = new JsonArray();
        Json.SetOrdered("item", array, this is CollectionRootNode ? PostmanRequestMapper.RootOrder : PostmanRequestMapper.ItemOrder);
        return array;
    }
}

public sealed class CollectionRootNode : FolderNode
{
    internal CollectionRootNode(PostmanCollection collection, JsonObject json)
        : base(collection, json, null)
    {
    }

    public override string Name
    {
        get => (Json["info"] as JsonObject)?["name"].GetString() ?? "Untitled collection";
        internal set
        {
            if (Json["info"] is not JsonObject info)
            {
                info = new JsonObject();
                Json.SetOrdered("info", info, PostmanRequestMapper.RootOrder);
            }

            info["name"] = value;
        }
    }
}

public sealed class RequestNode : CollectionNode
{
    private HttpRequestSpec? _baseline;

    internal RequestNode(PostmanCollection collection, JsonObject json, FolderNode? parent)
        : base(collection, json, parent)
    {
    }

    public string Method => Json["request"] switch
    {
        JsonObject request => request["method"].GetString() ?? "GET",
        _ => "GET",
    };

    /// <summary>The request as currently stored in the collection JSON (a fresh copy every call).</summary>
    public HttpRequestSpec ReadSpec() => Baseline.Clone();

    internal HttpRequestSpec Baseline => _baseline ??= PostmanRequestMapper.Read(Json, Collection.SchemaVersion);

    internal void ResetBaseline() => _baseline = null;
}
