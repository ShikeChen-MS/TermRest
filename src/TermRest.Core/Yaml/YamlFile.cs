using System.Text.Json.Nodes;
using TermRest.Core.IO;

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
    /// <param name="rewriteEndBlock">Write anew what ends this document, which has no final line break, with a block scalar
    /// that keeps its last line break (<c>|</c>), as copied before more lines its value would change.</param>
    public string Render(JsonObject root, YamlStyle style, bool rewriteEndBlock = false)
    {
        if (Tree.Root is not YamlMappingNode { IsFlow: false, Entries.Count: > 0 } mapping)
        {
            return Write(root, style, header: Tree.Root is null ? Tree.Text : null);
        }

        var writer = new YamlWriter(YamlStyle.Detect([Tree], style), Tree, rewriteEndBlock);
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
                writer.AppendTrivia(trivia, 0);
            }

            writer.WriteEntry(key, value, 0, entry);
        }

        writer.AppendTrivia(Tree.Lines(entries[^1].LastLine + 1, Tree.LineCount - 1), 0);
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

    /// <summary>
    /// The text to write to <paramref name="path"/> for <paramref name="root"/>, rendered over <paramref name="source"/>
    /// (null for a new file) and read back first exactly as it will be written, so that TermRest never writes a file it
    /// cannot read, or would read differently. Long values are written unwrapped when wrapping them would not read back;
    /// when the text still would not, an <see cref="IOException"/> names the file, and nothing must be written.
    /// </summary>
    /// <param name="info">The format of the file (that of a new file for one that doesn't exist yet).</param>
    /// <returns>
    /// The text without its final line break, and the format to write it in (see <see cref="TextFile.WriteAtomic(string, string, TextFileInfo?, UnixFileMode?)"/>):
    /// <paramref name="info"/>, so a file without a final line break stays without one, unless its last value (a block,
    /// <c>|</c> or <c>|+</c>) ends with a line break, which then needs one.
    /// </returns>
    public static (string Text, TextFileInfo Info) WriteChecked(YamlFile? source, JsonObject root, YamlStyle style, string path, TextFileInfo info, out YamlFile written)
    {
        // Unchanged parts are copied first. A block scalar that ends a source without a final line break can't be copied
        // before new lines (its value would gain one): the last attempts write it anew.
        List<(YamlStyle Style, bool RewriteEndBlock)> attempts = [(style, false), (style.WithLineWidth(0), false)];
        if (source is not null && !source.Tree.Text.EndsWith('\n'))
        {
            attempts.AddRange([(style, true), (style.WithLineWidth(0), true)]);
        }

        var tried = new HashSet<string>(StringComparer.Ordinal);
        var problem = "";
        foreach (var (attempt, rewriteEndBlock) in attempts)
        {
            var text = source is null ? Write(root, attempt) : source.Render(root, attempt, rewriteEndBlock);
            if (!tried.Add(text))
            {
                continue;
            }

            if (HasLoneSurrogate(text))
            {
                // UTF-8 can't hold half of a character (a value wrapped inside an emoji): it would be written as U+FFFD.
                problem = "it would hold half of a character";
                continue;
            }

            var full = text.EndsWith('\n') ? text : text + "\n";
            var trimmed = full[..^1];
            if (!info.HasTrailingNewline && ReadsBack(trimmed, root, out written, ref problem))
            {
                return (trimmed, info);
            }

            if (ReadsBack(full, root, out written, ref problem))
            {
                return (trimmed, info.HasTrailingNewline ? info : info with { HasTrailingNewline = true });
            }
        }

        throw new IOException($"'{path}' was not saved: TermRest cannot write it as YAML that reads back unchanged ({problem}).");
    }

    private static bool ReadsBack(string text, JsonObject root, out YamlFile written, ref string problem)
    {
        try
        {
            written = Parse(text);
            if (JsonNode.DeepEquals(written.Json, root))
            {
                return true;
            }

            problem = "it would read back changed";
        }
        catch (YamlFormatException ex)
        {
            problem = ex.Message;
        }

        written = null!;
        return false;
    }

    private static bool HasLoneSurrogate(ReadOnlySpan<char> text)
    {
        for (var at = text.IndexOfAnyInRange('\uD800', '\uDFFF'); at >= 0;)
        {
            if (!char.IsHighSurrogate(text[at]) || at + 1 >= text.Length || !char.IsLowSurrogate(text[at + 1]))
            {
                return true;
            }

            var next = text[(at + 2)..].IndexOfAnyInRange('\uD800', '\uDFFF');
            at = next < 0 ? -1 : at + 2 + next;
        }

        return false;
    }

    private static string EnsureFinalNewline(string text) => text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
}
