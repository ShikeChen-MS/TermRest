using System.Text.Json.Nodes;

namespace TermRest.Core.Yaml;

/// <summary>
/// A YAML file whose top level is a mapping (every Postman v3 file). <see cref="Render"/> writes new content while
/// keeping the exact text of everything that did not change: comments, unchanged entries, quoting and line folding.
/// </summary>
internal sealed class YamlFile
{
    private YamlFile(YamlTree tree, JsonObject json)
    {
        Tree = tree;
        Json = json;
    }

    public YamlTree Tree { get; }

    /// <summary>The content as read. Treat as read-only (clone before changing).</summary>
    public JsonObject Json { get; }

    /// <summary>Parses a file's text (any line endings). Throws <see cref="YamlFormatException"/>.</summary>
    public static YamlFile Parse(string text)
    {
        var tree = YamlTree.Parse(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        var json = tree.Root switch
        {
            null => new JsonObject(),
            YamlMappingNode mapping => (JsonObject)mapping.ToJson()!,
            YamlScalarNode { Value.Length: 0, Style: YamlScalarStyle.Plain } => new JsonObject(),
            _ => throw new YamlFormatException("Expected a mapping (key: value lines) at the top level."),
        };
        return new YamlFile(tree, json);
    }

    /// <summary>
    /// The text for <paramref name="root"/> (LF line breaks, ending with one). New scalars are quoted like the ones
    /// already in this document; <paramref name="style"/> applies when it has none (or as many of each).
    /// </summary>
    public string Render(JsonObject root, YamlStyle style)
    {
        if (Tree.Root is not YamlMappingNode { IsFlow: false, Entries.Count: > 0 } mapping)
        {
            return Write(root, style, header: Tree.Root is null ? Tree.Text : null);
        }

        var writer = new YamlWriter(YamlStyle.Detect([Tree], style), Tree);
        var entries = mapping.Entries;
        writer.Append(Tree.Lines(0, entries[0].FirstLine - 1));

        // Comments and blank lines above an entry move with it.
        var leading = new Dictionary<YamlEntry, string>(ReferenceEqualityComparer.Instance);
        for (var i = 1; i < entries.Count; i++)
        {
            leading[entries[i]] = Tree.Lines(entries[i - 1].LastLine + 1, entries[i].FirstLine - 1);
        }

        foreach (var (key, value) in root)
        {
            var entry = mapping.Find(key);
            if (entry is not null && leading.TryGetValue(entry, out var trivia))
            {
                writer.Append(trivia);
            }

            writer.WriteEntry(key, value, 0, entry);
        }

        writer.Append(Tree.Lines(entries[^1].LastLine + 1, Tree.LineCount - 1));
        return EnsureFinalNewline(writer.ToString());
    }

    /// <summary>A new file for <paramref name="root"/>.</summary>
    public static string Write(JsonObject root, YamlStyle style, string? header = null)
    {
        var writer = new YamlWriter(style, null);
        if (!string.IsNullOrWhiteSpace(header))
        {
            // Only comments: keep them above the content.
            writer.Append(EnsureFinalNewline(header));
        }

        writer.WriteMapping(root, 0, null);
        return writer.ToString();
    }

    private static string EnsureFinalNewline(string text) => text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
}
