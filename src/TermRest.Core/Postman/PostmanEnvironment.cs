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

    /// <summary>What <see cref="FilePath"/> held when last read or written (see <see cref="Save"/>).</summary>
    private FileStamp? _stamp;

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

    /// <summary>
    /// For YAML files: the column at which new and changed values are wrapped when saving (0: never); null (the default)
    /// follows the file. See <see cref="Workspace.WorkspaceSettings.YamlLineWidth"/>.
    /// </summary>
    public int? YamlLineWidth { get; set; }

    /// <summary>For YAML files: the column new and changed values are wrapped at (0: not wrapped); null for JSON.</summary>
    public int? EffectiveYamlLineWidth => IsYaml
        ? (_yaml is null ? YamlStyle.Default : YamlStyle.Detect([_yaml.Tree])).WithLineWidth(YamlLineWidth).LineWidth
        : null;

    public bool IsDirty { get; private set; }

    public static bool IsYamlPath(string path) =>
        path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    public static PostmanEnvironment Load(string path)
    {
        var (text, info) = TextFile.Read(path, out var stamp);
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
            return new PostmanEnvironment(converted, path, info) { _yaml = yaml, _v3 = yaml.Json, _stamp = stamp };
        }

        JsonNode? parsed;
        try
        {
            parsed = PostmanJson.Parse(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not a valid Postman environment: {ex.Message}", ex);
        }

        if (parsed is not JsonObject root)
        {
            throw new InvalidDataException($"'{path}' is not a Postman environment file.");
        }

        return new PostmanEnvironment(root, path, info) { _stamp = stamp };
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

    /// <param name="overwriteChanged">Write <see cref="FilePath"/> even when it changed on disk since it was read (see <see cref="IO.FileChangedException"/>).</param>
    public void Save(string? path = null, bool overwriteChanged = false)
    {
        path ??= FilePath ?? throw new InvalidOperationException("The environment has no file path yet.");
        var samePath = FilePath is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase);

        // Converting between JSON and YAML writes the target format's defaults, not the source's BOM and line breaks.
        var info = FilePath is not null && IsYamlPath(FilePath) != IsYamlPath(path)
            ? IsYamlPath(path) ? NewYamlFile : TextFileInfo.PostmanDefault
            : FileInfo;

        // A copy saved elsewhere (an imported or converted environment) is as private as the file it was read from.
        var newFileMode = samePath || FilePath is null ? null : TextFile.CopyMode(FilePath);
        if (IsYamlPath(path))
        {
            // Only the changed values are rewritten; the rest of the file keeps its exact text.
            var v3 = V3Convert.EnvironmentToV3(Root, _v3);
            if (_yaml is null || !JsonNode.DeepEquals(v3, _v3) || !samePath)
            {
                ThrowIfChanged(path, samePath, overwriteChanged);
                var style = (_yaml is null ? YamlStyle.Default : YamlStyle.Detect([_yaml.Tree])).WithLineWidth(YamlLineWidth);
                (var text, info) = YamlFile.WriteChecked(_yaml, v3, style, path, info, out var written);
                TextFile.WriteAtomic(path, text, info, out var stamp, newFileMode);
                _stamp = stamp;
                _yaml = written;
                _v3 = v3;
            }
        }
        else
        {
            ThrowIfChanged(path, samePath, overwriteChanged);
            TextFile.WriteAtomic(path, PostmanJson.Serialize(Root), info, out var stamp, newFileMode);
            _stamp = stamp;
        }

        FilePath = path;
        FileInfo = info;
        IsDirty = false;
    }

    /// <summary>Refuses to save over the file when another program changed it since it was read: that would undo its changes.</summary>
    private void ThrowIfChanged(string path, bool samePath, bool overwriteChanged)
    {
        if (!overwriteChanged && samePath && _stamp is not null && File.Exists(path) && _stamp.HasChanged(path))
        {
            throw new FileChangedException([Path.GetFullPath(path)]);
        }
    }

    public override string ToString() => Name;
}
