using System.Text.Json.Nodes;

namespace TermRest.Core.Model;

/// <summary>
/// A key/value row (query parameter, header, form field, path variable or environment value).
/// <see cref="Source"/> keeps the original JSON object so unknown properties survive a save.
/// </summary>
public sealed class KeyValueEntry
{
    public string Key { get; set; } = "";

    /// <summary>Null means "no value" (for example <c>?flag</c> without <c>=</c>).</summary>
    public string? Value { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    /// <summary>Postman item type: <c>text</c>/<c>file</c> for form data, <c>default</c>/<c>secret</c> for environments.</summary>
    public string? Type { get; set; }

    /// <summary>Form-data file source path(s), newline separated when there are several.</summary>
    public string? Src { get; set; }

    /// <summary>Form-data part content type.</summary>
    public string? ContentType { get; set; }

    public JsonObject? Source { get; set; }

    public bool IsFile => string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);

    public bool IsSecret => string.Equals(Type, "secret", StringComparison.OrdinalIgnoreCase);

    public KeyValueEntry Clone() => new()
    {
        Key = Key,
        Value = Value,
        Enabled = Enabled,
        Description = Description,
        Type = Type,
        Src = Src,
        ContentType = ContentType,
        Source = Source,
    };

    public bool ContentEquals(KeyValueEntry other) =>
        Key == other.Key && Value == other.Value && Enabled == other.Enabled && Description == other.Description
        && Type == other.Type && Src == other.Src && ContentType == other.ContentType;

    public static bool ListEquals(IReadOnlyList<KeyValueEntry> a, IReadOnlyList<KeyValueEntry> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].ContentEquals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static List<KeyValueEntry> CloneList(IEnumerable<KeyValueEntry> entries) => entries.Select(e => e.Clone()).ToList();

    public override string ToString() => $"{(Enabled ? "" : "// ")}{Key}: {Value}";
}
