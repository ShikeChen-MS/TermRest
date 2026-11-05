using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermRest.Core.Yaml;

/// <summary>Formatting choices that differ between Postman versions; detected from existing files.</summary>
internal sealed record YamlStyle(bool PreferSingleQuotes)
{
    /// <summary>Double quotes: what most Postman-written collections use.</summary>
    public static YamlStyle Default { get; } = new(false);

    /// <summary>
    /// Uses whichever quote character the given documents use most; <paramref name="fallback"/> (double quotes by
    /// default) when they use neither, or both equally.
    /// </summary>
    public static YamlStyle Detect(IEnumerable<YamlTree> trees, YamlStyle? fallback = null)
    {
        var single = 0;
        var @double = 0;
        foreach (var tree in trees)
        {
            Count(tree.Root, ref single, ref @double);
        }

        return single > @double ? new YamlStyle(true) : @double > single ? Default : fallback ?? Default;

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
/// literal blocks for multi-line text, quotes only when needed). When the original YAML is given as a hint, unchanged
/// entries, items and scalars are copied verbatim, so an edit only touches the lines it has to.
/// </summary>
internal sealed class YamlWriter(YamlStyle style, YamlTree? source)
{
    private readonly StringBuilder _out = new();

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

        _out.Append(FormatKey(key)).Append(':');
        WriteValue(value, indent, hint?.Value);
    }

    /// <summary>Writes the entries of a mapping at <paramref name="indent"/>.</summary>
    public void WriteMapping(JsonObject obj, int indent, YamlNode? hint, bool firstInline = false)
    {
        var mapping = hint as YamlMappingNode;
        var first = true;
        foreach (var (key, value) in obj)
        {
            var entryHint = mapping is { IsFlow: false } ? mapping.Find(key) : null;
            WriteEntry(key, value, indent, entryHint, inline: first && firstInline);
            first = false;
        }
    }

    /// <summary>The value after <c>key:</c> (scalars on the same line, collections on the following lines).</summary>
    private void WriteValue(JsonNode? value, int indent, YamlNode? hint)
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
                _out.Append(' ').Append(FormatScalar(value, indent, hint as YamlScalarNode, inSequence: false)).Append('\n');
                break;
        }
    }

    private void WriteSequence(JsonArray array, int indent, YamlSequenceNode? hint)
    {
        var items = hint is { IsFlow: false } ? hint.Items : [];
        var used = new bool[items.Count];
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
                if (TryCopyItem(items[equal], indent))
                {
                    continue;
                }
            }

            WriteItem(value, indent, equal >= 0 ? items[equal].Value : SimilarItem(items, used, i, value));
        }
    }

    /// <summary>The original item that most likely is the edited version of <paramref name="value"/> (for formatting hints).</summary>
    private static YamlNode? SimilarItem(List<YamlItem> items, bool[] used, int index, JsonNode? value)
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
                            return m;
                        }
                    }
                }
            }
        }

        return index < items.Count && !used[index] ? items[index].Value : null;
    }

    private void WriteItem(JsonNode? value, int indent, YamlNode? hint)
    {
        _out.Append(' ', indent).Append('-');
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
                _out.Append(' ').Append(FormatScalar(value, indent, hint as YamlScalarNode, inSequence: true)).Append('\n');
                break;
        }
    }

    // ----- copying unchanged source text -----------------------------------------------------------

    private bool TryCopyEntry(YamlEntry hint, JsonNode? value, int indent)
    {
        if (source is null || source.ColumnOf(hint.Key.Start) != indent || !source.StartsLine(hint.Key.Start)
            || !JsonNode.DeepEquals(Json(hint.Value), value))
        {
            return false;
        }

        AppendLines(hint.FirstLine, hint.LastLine);
        return true;
    }

    private bool TryCopyItem(YamlItem hint, int indent)
    {
        if (source is null || hint.DashColumn != indent || !source.StartsLine(source.LineStart(hint.FirstLine) + indent))
        {
            return false;
        }

        AppendLines(hint.FirstLine, hint.LastLine);
        return true;
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
    /// Formats a scalar written after <c>key: </c> or <c>- </c>. <paramref name="indent"/> is the indentation of the
    /// owning key (block content goes two columns deeper). May span several lines (no trailing line break).
    /// </summary>
    private string FormatScalar(JsonNode? value, int indent, YamlScalarNode? hint, bool inSequence)
    {
        if (hint is not null && source is not null && JsonNode.DeepEquals(Json(hint), value) && CanCopyScalar(hint, indent))
        {
            var raw = source.Text[hint.Start..hint.End];
            return raw.EndsWith('\n') ? raw[..^1] : raw;
        }

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
        var quote = hint?.Style is YamlScalarStyle.SingleQuoted or YamlScalarStyle.DoubleQuoted ? hint.Style : (YamlScalarStyle?)null;
        if (text.Contains('\n', StringComparison.Ordinal))
        {
            return LiteralBlock(text, indent + 2, inSequence) ?? DoubleQuoted(text);
        }

        if (quote is null && IsPlainSafe(text))
        {
            return text;
        }

        return Quoted(text, quote);
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

    private string Quoted(string text, YamlScalarStyle? preferred)
    {
        if (!CanSingleQuote(text))
        {
            return DoubleQuoted(text);
        }

        var hasSingle = text.Contains('\'', StringComparison.Ordinal);
        var hasDouble = text.Contains('"', StringComparison.Ordinal);
        var single = preferred switch
        {
            YamlScalarStyle.SingleQuoted => true,
            YamlScalarStyle.DoubleQuoted => false,
            _ => hasDouble && !hasSingle || (!(hasSingle && !hasDouble) && style.PreferSingleQuotes),
        };
        return single ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'" : DoubleQuoted(text);
    }

    /// <summary>JSON number text is also a valid YAML 1.2 number (e.g. 1000, 1500.5, 1E+20).</summary>
    private static string FormatNumber(JsonValue value) => value.ToJsonString();

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
            if (inSequence)
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
