using System.Text.Json.Nodes;
using TermRest.Core.IO;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman.V3;
using TermRest.Core.Yaml;

namespace TermRest.Core.Postman;

/// <summary>
/// A Postman environment or globals file: v2.1 JSON (<c>*.postman_environment.json</c> / <c>*.postman_globals.json</c>)
/// or v3 YAML (<c>postman/environments/*.environment.yaml</c>, <c>postman/globals/workspace.globals.yaml</c>).
/// </summary>
public sealed class PostmanEnvironment
{
    public const string EnvironmentSuffix = ".postman_environment.json";
    public const string GlobalsSuffix = ".postman_globals.json";

    private static readonly string[] Order = ["id", "name", "values", "_postman_variable_scope", "_postman_exported_at", "_postman_exported_using"];
    private static readonly TextFileInfo NewYamlFile = new(false, true, false);

    private YamlFile? _yaml;
    private JsonObject? _v3;

    private PostmanEnvironment(JsonObject root, string? filePath, TextFileInfo fileInfo)
    {
        Root = root;
        FilePath = filePath;
        FileInfo = fileInfo;
        Values = PostmanRequestMapper.ReadKeyValues(root["values"], environmentStyle: true);
    }

    public JsonObject Root { get; }

    public string? FilePath { get; set; }

    public TextFileInfo FileInfo { get; private set; }

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

    /// <summary>True for a v3 YAML environment/globals file.</summary>
    public bool IsYaml => FilePath is not null && IsYamlPath(FilePath);

    public bool IsDirty { get; private set; }

    public static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    public static PostmanEnvironment Load(string path)
    {
        var (text, info) = TextFile.Read(path);
        if (IsYamlPath(path))
        {
            YamlFile yaml;
            try
            {
                yaml = YamlFile.Parse(text);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"'{path}' is not a valid Postman environment: {ex.Message}", ex);
            }

            var fileName = Path.GetFileName(path);
            var globals = fileName.Contains(".globals.", StringComparison.OrdinalIgnoreCase);
            var fallbackName = globals ? "Globals" : fileName.Split('.')[0];
            var converted = V3Convert.EnvironmentToV21(yaml.Json, fallbackName, globals);
            return new PostmanEnvironment(converted, path, info) { _yaml = yaml, _v3 = yaml.Json };
        }

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

    /// <summary>A new v3 YAML environment (saved to <paramref name="filePath"/>, a <c>.yaml</c> file).</summary>
    public static PostmanEnvironment CreateYaml(string name, string filePath, bool globals = false)
    {
        var root = new JsonObject
        {
            ["name"] = name,
            ["values"] = new JsonArray(),
            ["_postman_variable_scope"] = globals ? "globals" : "environment",
        };
        return new PostmanEnvironment(root, filePath, NewYamlFile) { IsDirty = true };
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

        // Converting between JSON and YAML writes the target format's defaults, not the source's BOM and line breaks.
        var info = FilePath is not null && IsYamlPath(FilePath) != IsYamlPath(path)
            ? IsYamlPath(path) ? NewYamlFile : TextFileInfo.PostmanDefault
            : FileInfo;
        if (IsYamlPath(path))
        {
            // Only the changed values are rewritten; the rest of the file keeps its exact text.
            var v3 = V3Convert.EnvironmentToV3(Root, _v3);
            if (_yaml is null || !JsonNode.DeepEquals(v3, _v3) || !string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var style = _yaml is null ? YamlStyle.Default : YamlStyle.Detect([_yaml.Tree]);
                var text = _yaml is null ? YamlFile.Write(v3, style) : _yaml.Render(v3, style);
                TextFile.WriteAtomic(path, info.HasTrailingNewline ? text[..^1] : text, info);
                _yaml = YamlFile.Parse(text);
                _v3 = v3;
            }
        }
        else
        {
            TextFile.WriteAtomic(path, PostmanJson.Serialize(Root), info);
        }

        FilePath = path;
        FileInfo = info;
        IsDirty = false;
    }

    public override string ToString() => Name;
}
