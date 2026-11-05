using System.Text.Json.Nodes;
using TermRest.Core.IO;
using TermRest.Core.Json;
using TermRest.Core.Model;

namespace TermRest.Core.Postman;

/// <summary>A Postman environment or globals file (<c>*.postman_environment.json</c> / <c>*.postman_globals.json</c>).</summary>
public sealed class PostmanEnvironment
{
    public const string EnvironmentSuffix = ".postman_environment.json";
    public const string GlobalsSuffix = ".postman_globals.json";

    private static readonly string[] Order = ["id", "name", "values", "_postman_variable_scope", "_postman_exported_at", "_postman_exported_using"];

    private PostmanEnvironment(JsonObject root, string? filePath, TextFileInfo fileInfo)
    {
        Root = root;
        FilePath = filePath;
        FileInfo = fileInfo;
        Values = PostmanRequestMapper.ReadKeyValues(root["values"], environmentStyle: true);
    }

    public JsonObject Root { get; }

    public string? FilePath { get; set; }

    public TextFileInfo FileInfo { get; }

    public List<KeyValueEntry> Values { get; private set; }

    public string Name
    {
        get => Root["name"].GetString() ?? (FilePath is null ? "Environment" : Path.GetFileName(FilePath).Split('.')[0]);
        set
        {
            Root.SetOrdered("name", value, Order);
            MarkDirty();
        }
    }

    public bool IsGlobals => Root["_postman_variable_scope"].GetString() == "globals";

    public bool IsDirty { get; private set; }

    public static PostmanEnvironment Load(string path)
    {
        var (text, info) = TextFile.Read(path);
        if (PostmanJson.Parse(text) is not JsonObject root)
        {
            throw new InvalidDataException($"'{path}' is not a Postman environment file.");
        }

        return new PostmanEnvironment(root, path, info);
    }

    public static PostmanEnvironment Create(string name, string? filePath = null, bool globals = false)
    {
        var root = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = name,
            ["values"] = new JsonArray(),
            ["_postman_variable_scope"] = globals ? "globals" : "environment",
        };
        return new PostmanEnvironment(root, filePath, TextFileInfo.PostmanDefault) { IsDirty = true };
    }

    public void SetValues(IEnumerable<KeyValueEntry> values)
    {
        Root.SetOrdered("values", PostmanRequestMapper.WriteKeyValues(values, KeyValueStyle.Environment), Order);
        Values = PostmanRequestMapper.ReadKeyValues(Root["values"], environmentStyle: true);
        MarkDirty();
    }

    /// <summary>Sets (or adds) a single value, e.g. a token captured from a response.</summary>
    public void SetValue(string key, string value)
    {
        var values = KeyValueEntry.CloneList(Values);
        var existing = values.FirstOrDefault(v => v.Key == key);
        if (existing is null)
        {
            values.Add(new KeyValueEntry { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        SetValues(values);
    }

    public void MarkDirty() => IsDirty = true;

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("The environment has no file path yet.");
        TextFile.WriteAtomic(path, PostmanJson.Serialize(Root), FileInfo);
        FilePath = path;
        IsDirty = false;
    }

    public override string ToString() => Name;
}
