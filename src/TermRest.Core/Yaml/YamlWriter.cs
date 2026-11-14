using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermRest.Core.Yaml;

/// <summary>Formatting choices that differ between Postman versions; detected from existing files.</summary>
/// <param name="PreferSingleQuotes">Quote with single rather than double quotes where either works.</param>
/// <param name="LineWidth">Column at which long values are wrapped (folded), like Postman's YAML library; 0 never wraps.</param>
/// <param name="LineWidthIsFixed">The width is the user's choice: it is not detected from documents.</param>
internal sealed record YamlStyle(bool PreferSingleQuotes, int LineWidth = 0, bool LineWidthIsFixed = false)
{
    /// <summary>Double quotes, no wrapping: what current Postman versions write.</summary>
    public static YamlStyle Default { get; } = new(false);

    /// <summary>This style with the user's line width setting applied; null keeps the detected width.</summary>
    public YamlStyle WithLineWidth(int? lineWidth) =>
        lineWidth is int width ? this with { LineWidth = Math.Max(0, width), LineWidthIsFixed = true } : this;

    /// <summary>
    /// Quotes: whichever quote character the documents use most. Line width: 80 when their long values are wrapped the
    /// way Postman wrapped them, 0 when long values are not wrapped. <paramref name="fallback"/> (double quotes and no
    /// wrapping by default) applies where the documents show neither (or both equally), and a fixed width always wins.
    /// </summary>
    public static YamlStyle Detect(IEnumerable<YamlTree> trees, YamlStyle? fallback = null)
    {
        fallback ??= Default;
        var single = 0;
        var @double = 0;
        var wrapped = 0;
        var unwrapped = 0;
        foreach (var tree in trees)
        {
            Count(tree.Root, ref single, ref @double);
            if (!fallback.LineWidthIsFixed)
            {
                YamlWriter.CountWrapping(tree, tree.Root, ref wrapped, ref unwrapped);
            }
        }

        var preferSingle = single == @double ? fallback.PreferSingleQuotes : single > @double;
        var width = fallback.LineWidthIsFixed || wrapped == unwrapped
            ? fallback.LineWidth
            : wrapped > unwrapped ? YamlFolding.PostmanLineWidth : 0;
        return new YamlStyle(preferSingle, width, fallback.LineWidthIsFixed);

        static void Count(YamlNode? node, ref int single, ref int @double)
        {
            switch (node)
            {
                case YamlScalarNode { Style: YamlScalarStyle.SingleQuoted }:
                    single++;
                    break;
                case YamlScalarNode { Style: YamlScalarStyle.DoubleQuoted }:
                    @double++;
                    break;
                case YamlMappingNode mapping:
                    foreach (var entry in mapping.Entries)
                    {
                        Count(entry.Value, ref single, ref @double);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (var item in sequence.Items)
                    {
                        Count(item.Value, ref single, ref @double);
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// Writes JSON values as block YAML the way Postman does (2-space indentation, sequences indented under their key,
/// literal blocks for multi-line text, quotes only when needed, long values wrapped when the style has a line width).
/// When the original YAML is given as a hint, unchanged entries, items and scalars are copied verbatim, so an edit only
/// touches the lines it has to.
/// </summary>
/// <param name="rewriteEndBlock">Write anew, rather than copy, what ends a source without a final line break with a block
/// scalar that keeps its last line break (see <see cref="EndsWithOpenBlock"/>).</param>
internal sealed class YamlWriter(YamlStyle style, YamlTree? source, bool rewriteEndBlock = false)
{
    private readonly StringBuilder _out = new();

    /// <summary>
    /// Whether the output ends with a block scalar: a line after it that is indented like its text would be part of it, and
    /// so would blank lines when it keeps them (<c>|+</c>, <c>&gt;+</c>).
    /// </summary>
    private BlockEnd _blockEnd;

    private enum BlockEnd
    {
        None,
        Block,
        BlockKeepingBlankLines,
    }

    public override string ToString() => _out.ToString();

    public void Append(string text) => _out.Append(text);

    /// <summary>Writes <c>key: value</c> at <paramref name="indent"/> (after <c>- </c> when <paramref name="inline"/>).</summary>
    public void WriteEntry(string key, JsonNode? value, int indent, YamlEntry? hint, bool inline = false)
    {
        if (!inline && hint is not null && TryCopyEntry(hint, value, indent))
        {
            return;
        }

        if (!inline)
        {
            _out.Append(' ', indent);
        }

        var formattedKey = FormatKey(key);
        _out.Append(formattedKey).Append(':');
        _blockEnd = BlockEnd.None;
        WriteValue(value, indent, hint?.Value, formattedKey.Length);
    }

    /// <summary>Writes the entries of a mapping at <paramref name="indent"/>.</summary>
    public void WriteMapping(JsonObject obj, int indent, YamlNode? hint, bool firstInline = false)
    {
        var mapping = hint as YamlMappingNode;
        var first = true;
        foreach (var (key, value) in obj)
        {
            var entryHint = mapping is { IsFlow: false } ? mapping.Find(key) : null;
            var inline = first && firstInline;
            if (entryHint is not null && !inline && source!.StartsLine(entryHint.Key.Start))
            {
                // Comments and blank lines above an entry move with it, as at the top level.
                var index = mapping!.Entries.IndexOf(entryHint);
                var start = index > 0 ? mapping.Entries[index - 1].LastLine + 1 : TriviaAbove(entryHint.FirstLine);
                AppendTrivia(source.Lines(start, entryHint.FirstLine - 1), indent);
            }

            WriteEntry(key, value, indent, entryHint, inline);
            first = false;
        }
    }

    /// <summary>The value after <c>key:</c> (scalars on the same line, collections on the following lines).</summary>
    private void WriteValue(JsonNode? value, int indent, YamlNode? hint, int keyLength)
    {
        switch (value)
        {
            case JsonObject { Count: 0 }:
                _out.Append(" {}\n");
                break;
            case JsonObject obj:
                _out.Append('\n');
                WriteMapping(obj, indent + 2, hint);
                break;
            case JsonArray { Count: 0 }:
                _out.Append(" []\n");
                break;
            case JsonArray array:
                _out.Append('\n');
                WriteSequence(array, indent + 2, hint as YamlSequenceNode);
                break;
            default:
                // A wrapped value after a very long key starts on the next line.
                var scalar = FormatScalar(value, indent, hint as YamlScalarNode, inSequence: false, keyLength);
                _out.Append(scalar.StartsWith('\n') ? "" : " ").Append(scalar).Append('\n');
                break;
        }
    }

    private void WriteSequence(JsonArray array, int indent, YamlSequenceNode? hint)
    {
        var items = hint is { IsFlow: false } ? hint.Items : [];
        var used = new bool[items.Count];
        var placed = new bool[items.Count];
        for (var i = 0; i < array.Count; i++)
        {
            var value = array[i];

            // An unchanged item is copied as-is, wherever it moved.
            var equal = -1;
            for (var j = 0; j < items.Count && equal < 0; j++)
            {
                if (!used[j] && JsonNode.DeepEquals(Json(items[j].Value), value))
                {
                    equal = j;
                }
            }

            if (equal >= 0)
            {
                used[equal] = true;
            }

            var match = equal >= 0 ? equal : SimilarItem(items, used, i, value);
            if (match >= 0 && !placed[match] && items[match].DashColumn >= 0
                && source!.StartsLine(source.LineStart(items[match].FirstLine) + items[match].DashColumn))
            {
                // Comments and blank lines above an item move with it.
                placed[match] = true;
                var start = match > 0 ? items[match - 1].LastLine + 1 : TriviaAbove(items[match].FirstLine);
                AppendTrivia(source.Lines(start, items[match].FirstLine - 1), indent);
            }

            if (equal >= 0 && TryCopyItem(items[equal], indent))
            {
                continue;
            }

            WriteItem(value, indent, match >= 0 ? items[match].Value : null);
        }
    }

    /// <summary>The index of the original item that most likely is the edited version of <paramref name="value"/> (for formatting hints), or -1.</summary>
    private static int SimilarItem(List<YamlItem> items, bool[] used, int index, JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            foreach (var identity in (string[])["key", "type", "listen"])
            {
                if (obj[identity] is JsonValue id)
                {
                    for (var j = 0; j < items.Count; j++)
                    {
                        if (!used[j] && items[j].Value is YamlMappingNode m && m.Find(identity)?.Value is YamlScalarNode s
                            && JsonNode.DeepEquals(s.ToJson(), id))
                        {
                            return j;
                        }
                    }
                }
            }
        }

        return index < items.Count && !used[index] ? index : -1;
    }

    private void WriteItem(JsonNode? value, int indent, YamlNode? hint)
    {
        _out.Append(' ', indent).Append('-');
        _blockEnd = BlockEnd.None;
        switch (value)
        {
            case JsonObject { Count: 0 }:
                _out.Append(" {}\n");
                break;
            case JsonObject obj:
                _out.Append(' ');
                WriteMapping(obj, indent + 2, hint, firstInline: true);
                break;
            case JsonArray { Count: 0 }:
                _out.Append(" []\n");
                break;
            case JsonArray nested:
                _out.Append('\n');
                WriteSequence(nested, indent + 2, hint as YamlSequenceNode);
                break;
            default:
                _out.Append(' ').Append(FormatScalar(value, indent, hint as YamlScalarNode, inSequence: true, keyLength: null)).Append('\n');
                break;
        }
    }

    // ----- copying unchanged source text -----------------------------------------------------------

    private bool TryCopyEntry(YamlEntry hint, JsonNode? value, int indent)
    {
        if (source is null || source.ColumnOf(hint.Key.Start) != indent || !source.StartsLine(hint.Key.Start)
            || !JsonNode.DeepEquals(Json(hint.Value), value) || EndsWithOpenBlock(hint.Value))
        {
            return false;
        }

        AppendLines(hint.FirstLine, hint.LastLine);
        _blockEnd = BlockEndOf(hint.Value);
        return true;
    }

    private bool TryCopyItem(YamlItem hint, int indent)
    {
        if (source is null || hint.DashColumn != indent || !source.StartsLine(source.LineStart(hint.FirstLine) + indent) || EndsWithOpenBlock(hint.Value))
        {
            return false;
        }

        AppendLines(hint.FirstLine, hint.LastLine);
        _blockEnd = BlockEndOf(hint.Value);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="node"/> ends a source without a final line break with a block scalar that keeps its last line
    /// break (<c>|</c>, <c>&gt;</c>, <c>|+</c>): its value has none there, but copied before more lines it would get one.
    /// Only when <c>rewriteEndBlock</c> is set: copied, it is right while it stays last (written without a final line break).
    /// </summary>
    private bool EndsWithOpenBlock(YamlNode node)
    {
        if (!rewriteEndBlock || source is null || source.Text.EndsWith('\n'))
        {
            return false;
        }

        while (true)
        {
            switch (node)
            {
                case YamlMappingNode { IsFlow: false, Entries.Count: > 0 } mapping:
                    node = mapping.Entries[^1].Value;
                    break;
                case YamlSequenceNode { IsFlow: false, Items.Count: > 0 } sequence:
                    node = sequence.Items[^1].Value;
                    break;
                case YamlScalarNode { Style: YamlScalarStyle.Literal or YamlScalarStyle.Folded } scalar
                    when source.Text.AsSpan(scalar.End).IsWhiteSpace():
                    var header = source.Text.AsSpan(scalar.Start);
                    var end = header.IndexOfAny(' ', '\t', '\n');
                    return !(end < 0 ? header : header[..end]).Contains('-');
                default:
                    return false;
            }
        }
    }

    private void AppendLines(int first, int last)
    {
        var text = source!.Lines(first, last);
        _out.Append(text);
        if (!text.EndsWith('\n'))
        {
            _out.Append('\n');
        }
    }

    /// <summary>
    /// Appends the comment and blank lines that stood above an entry or item now written at <paramref name="indent"/>.
    /// After a block scalar, the first comment moves left to <paramref name="indent"/> if it is further right, which ends
    /// the block scalar; blank lines before it are left out when the block scalar keeps blank lines.
    /// </summary>
    public void AppendTrivia(string lines, int indent)
    {
        if (_blockEnd != BlockEnd.None)
        {
            var hash = lines.IndexOf('#');
            var lineStart = hash < 0 ? lines.Length : lines.LastIndexOf('\n', hash) + 1;
            var blankLines = _blockEnd == BlockEnd.Block ? new string('\n', lines.AsSpan(0, lineStart).Count('\n')) : "";
            lines = hash < 0 ? blankLines : blankLines + new string(' ', Math.Min(hash - lineStart, indent)) + lines[hash..];
        }

        _out.Append(lines);
    }

    /// <summary>The first of the comment and blank lines directly above <paramref name="line"/> (up to the parent's line).</summary>
    private int TriviaAbove(int line)
    {
        while (line > 0 && source!.Lines(line - 1, line - 1).TrimStart(' ') is var text && (text.Length == 0 || text[0] is '\n' or '#'))
        {
            line--;
        }

        return line;
    }

    private BlockEnd BlockEndOf(YamlNode node) => node switch
    {
        YamlScalarNode { Style: YamlScalarStyle.Literal or YamlScalarStyle.Folded } scalar => BlockEndOf(source!.Text.AsSpan(scalar.Start)),
        YamlMappingNode { IsFlow: false, Entries.Count: > 0 } mapping => BlockEndOf(mapping.Entries[^1].Value),
        YamlSequenceNode { IsFlow: false, Items.Count: > 0 } sequence => BlockEndOf(sequence.Items[^1].Value),
        _ => BlockEnd.None,
    };

    /// <summary>For a block scalar's text, which starts with its header (such as <c>|2+</c>).</summary>
    private static BlockEnd BlockEndOf(ReadOnlySpan<char> block)
    {
        var end = block.IndexOfAny(' ', '\t', '\n');
        return (end < 0 ? block : block[..end]).Contains('+') ? BlockEnd.BlockKeepingBlankLines : BlockEnd.Block;
    }

    private readonly Dictionary<YamlNode, JsonNode?> _json = new(ReferenceEqualityComparer.Instance);

    private JsonNode? Json(YamlNode node)
    {
        if (!_json.TryGetValue(node, out var json))
        {
            json = node.ToJson();
            _json[node] = json;
        }

        return json;
    }

    // ----- scalars ---------------------------------------------------------------------------------

    /// <summary>
    /// Formats a scalar written after <c>key: </c> (<paramref name="keyLength"/>: length of the written key) or <c>- </c>
    /// (null). <paramref name="indent"/> is the column of the owning key or dash; continuation lines and block content go
    /// two columns deeper. May span several lines (no trailing line break).
    /// </summary>
    private string FormatScalar(JsonNode? value, int indent, YamlScalarNode? hint, bool inSequence, int? keyLength)
    {
        if (hint is not null && source is not null && JsonNode.DeepEquals(Json(hint), value) && CanCopyScalar(hint, indent) && !EndsWithOpenBlock(hint))
        {
            var raw = source.Text[hint.Start..hint.End];
            _blockEnd = BlockEndOf(hint);
            return raw.EndsWith('\n') ? raw[..^1] : raw;
        }

        _blockEnd = BlockEnd.None;
        if (value is not JsonValue scalar)
        {
            return "null";
        }

        switch (scalar.GetValueKind())
        {
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                return FormatNumber(scalar);
            case JsonValueKind.Null:
                return "null";
        }

        var text = scalar.GetValue<string>();
        var contentIndent = indent + 2;
        var indentAtStart = keyLength + 2; // the library measures a value's first line from the start of its key
        if (text.Contains('\n', StringComparison.Ordinal))
        {
            var keep = hint?.Style is YamlScalarStyle.Literal or YamlScalarStyle.Folded ? hint.Style : (YamlScalarStyle?)null;
            var block = BlockScalar(text, contentIndent, inSequence, style.LineWidth, keep);
            _blockEnd = block is null ? BlockEnd.None : BlockEndOf(block);
            return block ?? YamlFolding.FoldDoubleQuoted(DoubleQuoted(text), contentIndent, indentAtStart, style.LineWidth);
        }

        var quote = hint?.Style is YamlScalarStyle.SingleQuoted or YamlScalarStyle.DoubleQuoted ? hint.Style : (YamlScalarStyle?)null;
        if (quote is null && IsPlainSafe(text))
        {
            return YamlFolding.FoldFlow(text, contentIndent, indentAtStart, style.LineWidth);
        }

        return Quoted(text, quote, contentIndent, indentAtStart);
    }

    /// <summary>A copied multi-line scalar must stay more indented than its (new) owner.</summary>
    private bool CanCopyScalar(YamlScalarNode hint, int indent)
    {
        var first = source!.LineOf(hint.Start);
        var last = source.LineOf(Math.Max(hint.Start, hint.End - 1));
        for (var line = first + 1; line <= last; line++)
        {
            var start = source.LineStart(line);
            var end = source.LineEndInclusive(line);
            var spaces = 0;
            while (start + spaces < end && source.Text[start + spaces] == ' ')
            {
                spaces++;
            }

            if (start + spaces < end && source.Text[start + spaces] != '\n' && spaces <= indent)
            {
                return false;
            }
        }

        return true;
    }

    private string Quoted(string text, YamlScalarStyle? preferred, int contentIndent, int? indentAtStart)
    {
        if (!CanSingleQuote(text))
        {
            return YamlFolding.FoldDoubleQuoted(DoubleQuoted(text), contentIndent, indentAtStart, style.LineWidth);
        }

        var hasSingle = text.Contains('\'', StringComparison.Ordinal);
        var hasDouble = text.Contains('"', StringComparison.Ordinal);
        var single = preferred switch
        {
            YamlScalarStyle.SingleQuoted => true,
            YamlScalarStyle.DoubleQuoted => false,
            _ => hasDouble && !hasSingle || (!(hasSingle && !hasDouble) && style.PreferSingleQuotes),
        };
        return single
            ? YamlFolding.FoldFlow(SingleQuoted(text), contentIndent, indentAtStart, style.LineWidth)
            : YamlFolding.FoldDoubleQuoted(DoubleQuoted(text), contentIndent, indentAtStart, style.LineWidth);
    }

    private static string SingleQuoted(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>JSON number text is also a valid YAML 1.2 number (e.g. 1000, 1500.5, 1E+20).</summary>
    private static string FormatNumber(JsonValue value) => value.ToJsonString();

    /// <summary>
    /// Multi-line text as a block scalar, or null when it needs a quoted form. Literal (<c>|</c>) unless a line is longer
    /// than <paramref name="lineWidth"/> allows: then folded (<c>&gt;</c>) when that shortens the lines, as Postman's YAML
    /// library decides. <paramref name="keep"/> keeps the style of the value being replaced.
    /// </summary>
    private static string? BlockScalar(string text, int contentIndent, bool inSequence, int lineWidth, YamlScalarStyle? keep)
    {
        var literal = LiteralBlock(text, contentIndent, inSequence);
        if (literal is null || lineWidth <= 0 || keep == YamlScalarStyle.Literal)
        {
            return literal;
        }

        if (keep == YamlScalarStyle.Folded)
        {
            return YamlFolding.FoldedBlock(text, contentIndent, lineWidth, literalWhenUnfoldable: false) ?? literal;
        }

        return YamlFolding.LineLengthOverLimit(text, lineWidth, contentIndent)
            ? YamlFolding.FoldedBlock(text, contentIndent, lineWidth, literalWhenUnfoldable: true) ?? literal
            : literal;
    }

    /// <summary>A <c>|</c> block (chomping from the trailing line breaks), or null when the text needs a quoted form.</summary>
    private static string? LiteralBlock(string text, int contentIndent, bool inSequence)
    {
        if (!CanBlock(text))
        {
            return null;
        }

        var body = text.TrimEnd('\n');
        var trailing = text.Length - body.Length;
        var chomp = trailing == 0 ? "-" : trailing == 1 ? "" : "+";
        var lines = body.Split('\n');
        var firstContent = lines.FirstOrDefault(l => l.Length > 0) ?? "";
        var indicator = "";
        if (firstContent.StartsWith(' '))
        {
            // The YAML reader refuses a block whose first line holds only spaces ("found extra spaces in first line").
            if (inSequence || firstContent.AsSpan().TrimStart(" \t").IsEmpty)
            {
                return null;
            }

            indicator = "2";
        }

        var builder = new StringBuilder("|").Append(indicator).Append(chomp);
        foreach (var line in lines)
        {
            builder.Append('\n');
            if (line.Length > 0)
            {
                builder.Append(' ', contentIndent).Append(line);
            }
        }

        for (var i = 1; i < trailing && chomp == "+"; i++)
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool CanBlock(string text)
    {
        if (text.Trim(' ', '\t', '\n').Length == 0)
        {
            return false;
        }

        // A last line of only spaces or tabs would be lost.
        var lastBreak = text.TrimEnd('\n').LastIndexOf('\n');
        var lastLine = text.TrimEnd('\n')[(lastBreak + 1)..];
        if (lastLine.Length > 0 && lastLine.Trim(' ', '\t').Length == 0)
        {
            return false;
        }

        if (HasEscapedChars(text, allowNewline: true, allowTab: true))
        {
            return false;
        }

        return true;
    }

    private static bool CanSingleQuote(string text) => !HasEscapedChars(text, allowNewline: false, allowTab: false);

    /// <summary>Whether the text has characters that only a double-quoted scalar can hold (as escapes).</summary>
    private static bool HasEscapedChars(string text, bool allowNewline, bool allowTab)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '\n' && allowNewline) || (c == '\t' && allowTab))
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (NeedsEscape(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NeedsEscape(char c) =>
        c < 0x20 || c is '\u007f' or '\ufeff' or '\u2028' or '\u2029' || (c >= '\u0080' && c <= '\u009f') || char.IsSurrogate(c);

    public static string DoubleQuoted(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '\r': builder.Append("\\r"); break;
                case '\0': builder.Append("\\0"); break;
                case '\a': builder.Append("\\a"); break;
                case '\b': builder.Append("\\b"); break;
                case '\v': builder.Append("\\v"); break;
                case '\f': builder.Append("\\f"); break;
                case '\u001b': builder.Append("\\e"); break;
                case '\u0085': builder.Append("\\N"); break;
                case '\u2028': builder.Append("\\L"); break;
                case '\u2029': builder.Append("\\P"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        builder.Append(c).Append(text[++i]);
                    }
                    else if (NeedsEscape(c))
                    {
                        builder.Append(c <= '\u00ff' ? $"\\x{(int)c:X2}" : $"\\u{(int)c:X4}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string FormatKey(string key) => IsPlainSafe(key) ? key : DoubleQuoted(key);

    // ----- detecting how a document was wrapped -------------------------------------------------------

    /// <summary>
    /// Counts the values of a document that show how its writer wrapped long values: <paramref name="wrapped"/> are
    /// written exactly as Postman's library wraps at 80 columns, <paramref name="unwrapped"/> exactly as it writes them
    /// without wrapping (values that come out the same either way are not counted).
    /// </summary>
    internal static void CountWrapping(YamlTree tree, YamlNode? node, ref int wrapped, ref int unwrapped)
    {
        switch (node)
        {
            case YamlMappingNode { IsFlow: false } mapping:
                foreach (var entry in mapping.Entries)
                {
                    if (entry.Value is YamlScalarNode value)
                    {
                        var keyLength = entry.Key.End - entry.Key.Start;
                        CompareWrapping(tree, value, tree.ColumnOf(entry.Key.Start) + 2, keyLength + 2, inSequence: false, ref wrapped, ref unwrapped);
                    }
                    else
                    {
                        CountWrapping(tree, entry.Value, ref wrapped, ref unwrapped);
                    }
                }

                break;
            case YamlSequenceNode { IsFlow: false } sequence:
                foreach (var item in sequence.Items)
                {
                    if (item.Value is YamlScalarNode value)
                    {
                        if (item.DashColumn >= 0)
                        {
                            CompareWrapping(tree, value, item.DashColumn + 2, null, inSequence: true, ref wrapped, ref unwrapped);
                        }
                    }
                    else
                    {
                        CountWrapping(tree, item.Value, ref wrapped, ref unwrapped);
                    }
                }

                break;
        }
    }

    private static void CompareWrapping(YamlTree tree, YamlScalarNode node, int contentIndent, int? indentAtStart, bool inSequence, ref int wrapped, ref int unwrapped)
    {
        var wide = Render(node, contentIndent, indentAtStart, inSequence, YamlFolding.PostmanLineWidth);
        var narrow = Render(node, contentIndent, indentAtStart, inSequence, 0);
        if (wide is null || narrow is null || wide == narrow)
        {
            return;
        }

        var text = tree.Text[node.Start..node.End];
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        if (text == (wide.StartsWith('\n') ? wide.TrimStart('\n').TrimStart(' ') : wide))
        {
            wrapped++;
        }
        else if (text == narrow)
        {
            unwrapped++;
        }
    }

    /// <summary>How the node's text is written in the node's own style at <paramref name="lineWidth"/>; null for forms TermRest does not write.</summary>
    private static string? Render(YamlScalarNode node, int contentIndent, int? indentAtStart, bool inSequence, int lineWidth)
    {
        var text = node.Value;
        return node.Style switch
        {
            YamlScalarStyle.Plain when IsPlainSafe(text) => YamlFolding.FoldFlow(text, contentIndent, indentAtStart, lineWidth),
            YamlScalarStyle.SingleQuoted when CanSingleQuote(text) => YamlFolding.FoldFlow(SingleQuoted(text), contentIndent, indentAtStart, lineWidth),
            YamlScalarStyle.DoubleQuoted => YamlFolding.FoldDoubleQuoted(DoubleQuoted(text), contentIndent, indentAtStart, lineWidth),
            YamlScalarStyle.Literal or YamlScalarStyle.Folded when text.Contains('\n', StringComparison.Ordinal) =>
                BlockScalar(text, contentIndent, inSequence, lineWidth, keep: null),
            _ => null,
        };
    }

    /// <summary>Whether the text can be written as a plain (unquoted) scalar in block context and read back unchanged.</summary>
    public static bool IsPlainSafe(string text)
    {
        if (text.Length == 0 || text[0] is ' ' or '\t' || text[^1] is ' ' or '\t' or ':')
        {
            return false;
        }

        if ("[]{},#&*!|>'\"%@`".Contains(text[0], StringComparison.Ordinal))
        {
            return false;
        }

        if (text[0] is '-' or '?' or ':' && (text.Length == 1 || text[1] is ' ' or '\t'))
        {
            return false;
        }

        if (text.StartsWith("---", StringComparison.Ordinal) || text.StartsWith("...", StringComparison.Ordinal)
            || HasEscapedChars(text, allowNewline: false, allowTab: false))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ':' && i + 1 < text.Length && text[i + 1] == ' ')
            {
                return false;
            }

            if (c == '#' && i > 0 && text[i - 1] == ' ')
            {
                return false;
            }
        }

        return !YamlScalars.IsAmbiguous(text);
    }
}
