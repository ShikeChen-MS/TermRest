using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace TermRest.Core.Yaml;

public sealed class YamlFormatException(string message, Exception? inner = null) : FormatException(message, inner);

internal enum YamlScalarStyle
{
    Plain,
    SingleQuoted,
    DoubleQuoted,
    Literal,
    Folded,
}

/// <summary>A parsed YAML node that remembers where its text is, so unchanged parts can be written back verbatim.</summary>
internal abstract class YamlNode
{
    /// <summary>Index of the node's first character in the (LF-normalized) source.</summary>
    public int Start { get; init; }

    /// <summary>Index just after the node's last character (for block scalars: after the last content line).</summary>
    public int End { get; set; }

    /// <summary>The value in JSON terms (YAML 1.2 core schema for plain scalars).</summary>
    public abstract JsonNode? ToJson();
}

internal sealed class YamlScalarNode : YamlNode
{
    public required string Value { get; init; }

    public YamlScalarStyle Style { get; init; }

    public string? Tag { get; init; }

    public override JsonNode? ToJson() =>
        Style == YamlScalarStyle.Plain && Tag is null or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:null"
            ? YamlScalars.Resolve(Value)
            : JsonValue.Create(Value);
}

internal sealed class YamlEntry
{
    public required YamlScalarNode Key { get; init; }

    public required YamlNode Value { get; init; }

    /// <summary>0-based line of the key.</summary>
    public int FirstLine { get; set; }

    /// <summary>0-based line of the value's last character.</summary>
    public int LastLine { get; set; }
}

internal sealed class YamlItem
{
    public required YamlNode Value { get; init; }

    /// <summary>0-based line of the item's "-" indicator.</summary>
    public int FirstLine { get; set; }

    public int LastLine { get; set; }

    /// <summary>Column of the "-" indicator (-1 for flow sequences).</summary>
    public int DashColumn { get; set; } = -1;
}

internal sealed class YamlMappingNode : YamlNode
{
    public bool IsFlow { get; init; }

    public List<YamlEntry> Entries { get; } = [];

    public YamlEntry? Find(string key)
    {
        YamlEntry? found = null;
        foreach (var entry in Entries)
        {
            if (entry.Key.Value == key)
            {
                found = entry; // duplicate keys: the last one wins, like JSON.parse
            }
        }

        return found;
    }

    public override JsonNode? ToJson()
    {
        var obj = new JsonObject();
        foreach (var entry in Entries)
        {
            obj[entry.Key.Value] = entry.Value.ToJson();
        }

        return obj;
    }
}

internal sealed class YamlSequenceNode : YamlNode
{
    public bool IsFlow { get; init; }

    public List<YamlItem> Items { get; } = [];

    public override JsonNode? ToJson()
    {
        var array = new JsonArray();
        foreach (var item in Items)
        {
            array.Add(item.Value.ToJson());
        }

        return array;
    }
}

/// <summary>A parsed YAML document: LF-normalized source text, its line table and the root node.</summary>
internal sealed class YamlTree
{
    /// <summary>Collections nested deeper are refused, as in JSON input: reading them would overflow the stack.</summary>
    private const int MaxDepth = 256;

    private readonly int[] _lineStarts;

    private YamlTree(string text, YamlNode? root)
    {
        Text = text;
        Root = root;
        _lineStarts = LineStarts(text);
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            starts.Add(i + 1);
        }

        return [.. starts];
    }

    public string Text { get; }

    public YamlNode? Root { get; }

    /// <summary>Number of lines, not counting the empty "line" after a final line break.</summary>
    public int LineCount => Text.Length > 0 && Text[^1] == '\n' ? _lineStarts.Length - 1 : _lineStarts.Length;

    public int LineOf(int index)
    {
        var line = Array.BinarySearch(_lineStarts, index);
        return line >= 0 ? line : ~line - 1;
    }

    public int LineStart(int line) => line < _lineStarts.Length ? _lineStarts[line] : Text.Length;

    /// <summary>Index just after the line's line break (or the end of the text).</summary>
    public int LineEndInclusive(int line) => line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;

    public int ColumnOf(int index) => index - LineStart(LineOf(index));

    /// <summary>The text of lines [first, last], including the final line break.</summary>
    public string Lines(int first, int last) => first > last ? "" : Text[LineStart(first)..LineEndInclusive(last)];

    /// <summary>True when only spaces precede <paramref name="index"/> on its line.</summary>
    public bool StartsLine(int index)
    {
        for (var i = LineStart(LineOf(index)); i < index; i++)
        {
            if (Text[i] != ' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses one YAML document. The text must use LF line breaks.</summary>
    public static YamlTree Parse(string text)
    {
        var parsed = text;
        int[]? removed = null; // per line: the spaces left out of parsed (see BlankLeadingLines)
        while (true)
        {
            try
            {
                var parser = new Parser(new Scanner(new StringReader(parsed), skipComments: true));
                Expect<StreamStart>(parser);
                if (parser.Current is StreamEnd)
                {
                    return new YamlTree(text, null);
                }

                Expect<DocumentStart>(parser);
                var root = ReadNode(parser, 0, removed is null ? m => (int)m.Index : PositionsIn(text, parsed, removed));
                Expect<DocumentEnd>(parser);
                if (parser.Current is not StreamEnd)
                {
                    throw new YamlFormatException("Only one YAML document per file is supported.");
                }

                var tree = new YamlTree(text, root);
                tree.AssignLines(root);
                return tree;
            }
            catch (YamlException ex)
            {
                if (ex is SemanticErrorException && ex.Message.Contains("extra spaces in first line", StringComparison.Ordinal)
                    && BlankLeadingLines(ref parsed, ref removed, (int)ex.Start.Index))
                {
                    continue;
                }

                throw new YamlFormatException($"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// YamlDotNet refuses a literal block scalar without an indentation indicator whose first line holds only spaces, as
    /// Postman's YAML library writes a value that starts with an empty line (<c>code: |-</c>, then a line with just the
    /// indentation). YAML allows it: the spaces of the empty lines before a block's first line of content are no part of
    /// the value. So those lines are parsed without their spaces. <paramref name="stop"/> is where the scanner stopped:
    /// the start of the block's first non-empty line; the lines above it, up to the block's header, hold spaces only.
    /// Returns false when there is nothing to leave out.
    /// </summary>
    private static bool BlankLeadingLines(ref string parsed, ref int[]? removed, int stop)
    {
        if (stop <= 0 || stop > parsed.Length || parsed[stop - 1] != '\n')
        {
            return false;
        }

        var line = parsed.AsSpan(0, stop).Count('\n'); // the stop line's number
        var blank = new List<(int Line, int Start, int Length)>();
        for (var end = stop - 1; end > 0;)
        {
            var start = parsed.LastIndexOf('\n', end - 1) + 1;
            line--;
            if (parsed.AsSpan(start, end - start).ContainsAnyExcept(' '))
            {
                break; // the block's header
            }

            if (end > start)
            {
                blank.Add((line, start, end - start));
            }

            end = start - 1;
        }

        if (blank.Count == 0)
        {
            return false;
        }

        removed ??= new int[parsed.AsSpan().Count('\n') + 1];
        var builder = new System.Text.StringBuilder(parsed.Length);
        var copied = 0;
        foreach (var (number, start, length) in Enumerable.Reverse(blank))
        {
            builder.Append(parsed, copied, start - copied);
            copied = start + length;
            removed[number] += length;
        }

        parsed = builder.Append(parsed, copied, parsed.Length - copied).ToString();
        return true;
    }

    /// <summary>
    /// Where a position in <paramref name="parsed"/> is in <paramref name="text"/>: the same lines, but some started with
    /// spaces parsed leaves out (<paramref name="removed"/>, per line).
    /// </summary>
    private static Func<Mark, int> PositionsIn(string text, string parsed, int[] removed)
    {
        var parsedStarts = LineStarts(parsed);
        var textStarts = LineStarts(text);
        return mark =>
        {
            var index = (int)mark.Index;
            var line = Array.BinarySearch(parsedStarts, index);
            line = line >= 0 ? line : ~line - 1;
            return textStarts[line] + removed[line] + (index - parsedStarts[line]);
        };
    }

    private static void Expect<T>(Parser parser)
        where T : ParsingEvent
    {
        if (parser.Current is null && !parser.MoveNext())
        {
            throw new YamlFormatException("Unexpected end of YAML.");
        }

        if (parser.Current is not T)
        {
            throw new YamlFormatException($"Unexpected YAML content at line {parser.Current?.Start.Line}.");
        }

        parser.MoveNext();
    }

    private static YamlNode ReadNode(Parser parser, int depth, Func<Mark, int> at)
    {
        switch (parser.Current)
        {
            case Scalar scalar:
            {
                parser.MoveNext();
                return new YamlScalarNode
                {
                    Value = scalar.Value,
                    Style = scalar.Style switch
                    {
                        ScalarStyle.SingleQuoted => YamlScalarStyle.SingleQuoted,
                        ScalarStyle.DoubleQuoted => YamlScalarStyle.DoubleQuoted,
                        ScalarStyle.Literal => YamlScalarStyle.Literal,
                        ScalarStyle.Folded => YamlScalarStyle.Folded,
                        _ => YamlScalarStyle.Plain,
                    },
                    Tag = scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific ? null : NormalizeTag(scalar.Tag.Value),
                    Start = at(scalar.Start),
                    End = at(scalar.End),
                };
            }

            case MappingStart start:
            {
                CheckDepth(depth, start);
                var mapping = new YamlMappingNode { IsFlow = start.Style == MappingStyle.Flow, Start = at(start.Start), End = at(start.End) };
                parser.MoveNext();
                while (parser.Current is not MappingEnd)
                {
                    if (ReadNode(parser, depth + 1, at) is not YamlScalarNode key)
                    {
                        throw new YamlFormatException("Only plain text mapping keys are supported.");
                    }

                    var value = ReadNode(parser, depth + 1, at);
                    mapping.Entries.Add(new YamlEntry { Key = key, Value = value });
                    mapping.End = Math.Max(mapping.End, ContentEnd(key, value));
                }

                if (mapping.IsFlow)
                {
                    mapping.End = at(parser.Current!.End);
                }

                parser.MoveNext();
                return mapping;
            }

            case SequenceStart start:
            {
                CheckDepth(depth, start);
                var sequence = new YamlSequenceNode { IsFlow = start.Style == SequenceStyle.Flow, Start = at(start.Start), End = at(start.End) };
                parser.MoveNext();
                while (parser.Current is not SequenceEnd)
                {
                    var value = ReadNode(parser, depth + 1, at);
                    sequence.Items.Add(new YamlItem { Value = value });
                    sequence.End = Math.Max(sequence.End, value.End);
                }

                if (sequence.IsFlow)
                {
                    sequence.End = at(parser.Current!.End);
                }

                parser.MoveNext();
                return sequence;
            }

            case AnchorAlias alias:
                throw new YamlFormatException($"YAML aliases (*{alias.Value}) are not supported (line {alias.Start.Line}).");

            default:
                throw new YamlFormatException($"Unexpected YAML content at line {parser.Current?.Start.Line}.");
        }
    }

    private static void CheckDepth(int depth, ParsingEvent start)
    {
        if (depth >= MaxDepth)
        {
            throw new YamlFormatException($"YAML nested more than {MaxDepth} levels deep is not supported (line {start.Start.Line}).");
        }
    }

    private static string NormalizeTag(string tag) => tag switch
    {
        "!!str" => "tag:yaml.org,2002:str",
        "!!int" => "tag:yaml.org,2002:int",
        "!!float" => "tag:yaml.org,2002:float",
        "!!bool" => "tag:yaml.org,2002:bool",
        "!!null" => "tag:yaml.org,2002:null",
        _ => tag,
    };

    /// <summary>End of an entry's text: an empty value (<c>key:</c>) ends with the key.</summary>
    private static int ContentEnd(YamlScalarNode key, YamlNode value) =>
        value is YamlScalarNode { Value.Length: 0, Style: YamlScalarStyle.Plain } ? key.End : Math.Max(key.End, value.End);

    private void AssignLines(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var entry in mapping.Entries)
                {
                    entry.FirstLine = LineOf(entry.Key.Start);
                    entry.LastLine = Math.Max(entry.FirstLine, LineOf(Math.Max(entry.Key.Start, ContentEnd(entry.Key, entry.Value) - 1)));
                    AssignLines(entry.Value);
                }

                break;
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Items)
                {
                    var dash = sequence.IsFlow ? -1 : FindDash(item.Value.Start);
                    item.DashColumn = dash < 0 ? -1 : ColumnOf(dash);
                    item.FirstLine = LineOf(dash < 0 ? item.Value.Start : dash);
                    var end = item.Value is YamlScalarNode { Value.Length: 0, Style: YamlScalarStyle.Plain } ? item.Value.Start + 1 : item.Value.End;
                    item.LastLine = Math.Max(item.FirstLine, LineOf(Math.Max(item.Value.Start, end - 1)));
                    AssignLines(item.Value);
                }

                break;
        }
    }

    /// <summary>Finds the "-" indicator of a block sequence item whose value starts at <paramref name="valueStart"/>.</summary>
    private int FindDash(int valueStart)
    {
        for (var i = Math.Min(valueStart, Text.Length) - 1; i >= 0; i--)
        {
            var c = Text[i];
            if (c == '-')
            {
                return i;
            }

            if (c is not (' ' or '\n' or '\t'))
            {
                return -1;
            }
        }

        return -1;
    }
}

/// <summary>Plain scalar resolution and quoting rules (YAML 1.2 core schema, like Postman's YAML library).</summary>
internal static class YamlScalars
{
    public static JsonNode? Resolve(string plain)
    {
        switch (plain)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (IsDecimalInteger(plain))
        {
            return long.TryParse(plain, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : ExactInteger(plain);
        }

        if (plain.Length > 2 && plain[0] == '0' && plain[1] is 'x' or 'o')
        {
            var digits = plain[2..];
            try
            {
                if (plain[1] == 'x' && digits.All(char.IsAsciiHexDigit))
                {
                    return JsonValue.Create(Convert.ToInt64(digits, 16));
                }

                if (plain[1] == 'o' && digits.All(c => c is >= '0' and <= '7'))
                {
                    return JsonValue.Create(Convert.ToInt64(digits, 8));
                }
            }
            catch (OverflowException)
            {
                return JsonValue.Create(plain);
            }
        }

        if (IsFloat(plain) && double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
        {
            return JsonValue.Create(real);
        }

        // .inf/.nan have no JSON form; they are kept as text.
        return JsonValue.Create(plain);
    }

    /// <summary>True when a plain scalar with this text would not read back as the same string.</summary>
    public static bool IsAmbiguous(string text)
    {
        if (Resolve(text) is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return true;
        }

        // Kept as text by Resolve, but numbers to YAML 1.2 (and to Postman's library): .inf and .nan, which JSON cannot hold,
        // and numbers too big for a long or a double (an Ethereum address, 0x and 40 hex digits; 1e400).
        return text is ".inf" or "-.inf" or "+.inf" or ".Inf" or "-.Inf" or "+.Inf" or ".INF" or "-.INF" or "+.INF" or ".nan" or ".NaN" or ".NAN"
            || IsFloat(text) || IsPrefixedInteger(text);
    }

    /// <summary>0x[0-9a-fA-F]+ or 0o[0-7]+, of any size.</summary>
    private static bool IsPrefixedInteger(string s)
    {
        if (s.Length <= 2 || s[0] != '0' || s[1] is not ('x' or 'o'))
        {
            return false;
        }

        foreach (var c in s.AsSpan(2))
        {
            if (s[1] == 'x' ? !char.IsAsciiHexDigit(c) : c is < '0' or > '7')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalInteger(string s)
    {
        var start = s.Length > 0 && s[0] is '-' or '+' ? 1 : 0;
        return s.Length > start && s.AsSpan(start).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>
    /// An integer too big for a long, as a JSON number with exactly its digits (as JSON files keep them): a double would
    /// round it, or be infinite, which JSON cannot hold.
    /// </summary>
    private static JsonNode ExactInteger(string plain)
    {
        var digits = plain.AsSpan(plain[0] is '-' or '+' ? 1 : 0).TrimStart('0');
        return JsonNode.Parse((plain[0] == '-' ? "-" : "") + (digits.IsEmpty ? "0" : digits.ToString()))!;
    }

    /// <summary>[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?</summary>
    private static bool IsFloat(string s)
    {
        var i = s.Length > 0 && s[0] is '-' or '+' ? 1 : 0;
        var digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
            digits++;
        }

        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < s.Length && s[i] is 'e' or 'E')
        {
            i++;
            if (i < s.Length && s[i] is '-' or '+')
            {
                i++;
            }

            var exponent = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
                exponent++;
            }

            if (exponent == 0)
            {
                return false;
            }
        }

        return i == s.Length;
    }
}
