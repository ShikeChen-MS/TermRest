namespace TermRest.Tui.Syntax;

/// <summary>
/// XML and HTML: tag names as keywords, attribute names as variables, their values as strings, comments, entities
/// (&amp;amp;) as numbers, and the declarations (&lt;?xml …?&gt;, &lt;!DOCTYPE …&gt;, CDATA sections) as types. In HTML,
/// the content of script and style elements is left plain.
/// </summary>
/// <remarks>
/// What a line leaves open, in <see cref="LexState"/>: a tag (Code, Depth 1), an attribute value in one (String, its
/// quote, Depth 1), the content of a script or style element (Code, Depth 2), a comment (Comment, Quote '-') or a CDATA
/// section (Comment, Quote ']'). In a tag and its values, Quotes says which raw text element it starts: 1 script, 2 style.
/// </remarks>
internal sealed class MarkupLexer : SyntaxLexer
{
    public static readonly MarkupLexer Xml = new(html: false);

    public static readonly MarkupLexer Html = new(html: true);

    private const byte InTag = 1;
    private const byte InRawText = 2;

    private static readonly string[] RawTextElements = ["", "script", "style"];

    private static readonly string[] RawTextEnds = ["", "</script", "</style"];

    private readonly bool _html;

    private MarkupLexer(bool html) => _html = html;

    public override LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state)
    {
        var i = 0;
        switch (state)
        {
            case { Mode: LexMode.Comment, Quote: '-' }:
                i = Until(line, kinds, 0, "-->", SyntaxKind.Comment, SyntaxKind.Comment, ref state);
                break;
            case { Mode: LexMode.Comment }:
                i = Until(line, kinds, 0, "]]>", SyntaxKind.String, SyntaxKind.Type, ref state);
                break;
            case { Mode: LexMode.String }:
                i = AttributeValue(line, kinds, 0, ref state);
                break;
            case { Depth: InRawText }:
                i = RawText(line, 0, ref state);
                break;
        }

        while (i < line.Length)
        {
            if (state.Mode != LexMode.Code)
            {
                break; // a comment, CDATA section or value goes on past the line
            }

            if (state.Depth == InTag)
            {
                i = TagBody(line, kinds, i, ref state);
                continue;
            }

            if (state.Depth == InRawText)
            {
                i = RawText(line, i, ref state);
                continue;
            }

            var c = line[i];
            if (c == '<')
            {
                i = TagStart(line, kinds, i, ref state);
                continue;
            }

            if (c == '&' && EntityEnd(line, i) is var entityEnd and > 0)
            {
                kinds[i..entityEnd].Fill(SyntaxKind.Number);
                i = entityEnd;
                continue;
            }

            i++;
        }

        return state;
    }

    /// <summary>What starts with a &lt; at <paramref name="i"/>: a tag, a comment, a declaration, or text (a &lt; alone).</summary>
    private int TagStart(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, ref LexState state)
    {
        var rest = line[i..];
        if (rest.StartsWith("<!--"))
        {
            kinds[i..(i + 4)].Fill(SyntaxKind.Comment);
            state = new LexState(LexMode.Comment, '-');
            return Until(line, kinds, i + 4, "-->", SyntaxKind.Comment, SyntaxKind.Comment, ref state);
        }

        if (rest.StartsWith("<![CDATA["))
        {
            kinds[i..(i + 9)].Fill(SyntaxKind.Type);
            state = new LexState(LexMode.Comment, ']');
            return Until(line, kinds, i + 9, "]]>", SyntaxKind.String, SyntaxKind.Type, ref state);
        }

        var next = At(line, i + 1);
        if (next is '!' or '?')
        {
            // <!DOCTYPE html>, <?xml version="1.0"?>: the rest is read as attributes.
            var end = NameEnd(line, i + 2);
            kinds[i..end].Fill(SyntaxKind.Type);
            state = new LexState(LexMode.Code, Depth: InTag);
            return end;
        }

        var closing = next == '/';
        var nameStart = closing ? i + 2 : i + 1;
        if (!IsNameStart(At(line, nameStart)))
        {
            return i + 1; // a < in text
        }

        var nameEnd = NameEnd(line, nameStart);
        kinds[nameStart..nameEnd].Fill(SyntaxKind.Keyword);
        byte raw = 0;
        for (var element = 1; _html && !closing && element < RawTextElements.Length; element++)
        {
            if (line[nameStart..nameEnd].Equals(RawTextElements[element], StringComparison.OrdinalIgnoreCase))
            {
                raw = (byte)element;
            }
        }

        state = new LexState(LexMode.Code, Quotes: raw, Depth: InTag);
        return nameEnd;
    }

    /// <summary>The attributes of a tag from <paramref name="i"/>, up to its end (or the line's).</summary>
    private static int TagBody(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, ref LexState state)
    {
        var value = false; // after =: an unquoted value (HTML)
        while (i < line.Length)
        {
            var c = line[i];
            var next = At(line, i + 1);
            if (c == '>' || (c is '/' or '?' && next == '>'))
            {
                // The end of the tag; after a script or style start tag, the element's content.
                var raw = c == '>' ? state.Quotes : (byte)0;
                state = raw > 0 ? new LexState(LexMode.Code, Quotes: raw, Depth: InRawText) : LexState.Code;
                return c == '>' ? i + 1 : i + 2;
            }

            if (c == '<')
            {
                state = LexState.Code; // a tag left open: what follows is another one
                return i;
            }

            if (c is '"' or '\'')
            {
                kinds[i] = SyntaxKind.String;
                state = state with { Mode = LexMode.String, Quote = c };
                return AttributeValue(line, kinds, i + 1, ref state);
            }

            if (char.IsWhiteSpace(c) || c == '=')
            {
                value = c == '=' || (value && char.IsWhiteSpace(c));
                i++;
                continue;
            }

            var end = i + 1;
            while (end < line.Length && !(char.IsWhiteSpace(line[end]) || line[end] is '"' or '\'' or '=' or '<' or '>' || (line[end] is '/' or '?' && At(line, end + 1) == '>')))
            {
                end++;
            }

            kinds[i..end].Fill(value ? SyntaxKind.String : SyntaxKind.Variable);
            value = false;
            i = end;
        }

        return i;
    }

    /// <summary>A quoted attribute value from <paramref name="i"/> to its closing quote (it may go on over lines).</summary>
    private static int AttributeValue(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, ref LexState state)
    {
        var close = line[i..].IndexOf(state.Quote);
        var end = close < 0 ? line.Length : i + close + 1;
        kinds[i..end].Fill(SyntaxKind.String);
        if (close >= 0)
        {
            state = state with { Mode = LexMode.Code, Quote = '\0' };
        }

        return end;
    }

    /// <summary>The content of a script or style element from <paramref name="i"/>, up to its end tag (or the line's end).</summary>
    private static int RawText(ReadOnlySpan<char> line, int i, ref LexState state)
    {
        var found = line[i..].IndexOf(RawTextEnds[state.Quotes], StringComparison.OrdinalIgnoreCase);
        if (found < 0)
        {
            return line.Length;
        }

        state = LexState.Code;
        return i + found;
    }

    /// <summary>
    /// Colors <paramref name="kind"/> from <paramref name="i"/> up to <paramref name="close"/>, which gets
    /// <paramref name="closeKind"/>; returns the index after it, or the line's end (<paramref name="state"/> unchanged).
    /// </summary>
    private static int Until(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, string close, SyntaxKind kind, SyntaxKind closeKind, ref LexState state)
    {
        var found = line[i..].IndexOf(close.AsSpan());
        if (found < 0)
        {
            kinds[i..].Fill(kind);
            return line.Length;
        }

        kinds[i..(i + found)].Fill(kind);
        kinds[(i + found)..(i + found + close.Length)].Fill(closeKind);
        state = LexState.Code;
        return i + found + close.Length;
    }

    /// <summary>The end of an entity at <paramref name="i"/> (&amp;amp; &amp;#38; &amp;#x26;), or -1.</summary>
    private static int EntityEnd(ReadOnlySpan<char> line, int i)
    {
        var j = i + 1;
        if (At(line, j) == '#')
        {
            j++;
            var hex = At(line, j) is 'x' or 'X';
            if (hex)
            {
                j++;
            }

            var digits = j;
            while (j < line.Length && (hex ? char.IsAsciiHexDigit(line[j]) : char.IsAsciiDigit(line[j])))
            {
                j++;
            }

            return j > digits && At(line, j) == ';' ? j + 1 : -1;
        }

        if (!char.IsAsciiLetter(At(line, j)))
        {
            return -1;
        }

        while (j < line.Length && char.IsAsciiLetterOrDigit(line[j]))
        {
            j++;
        }

        return At(line, j) == ';' ? j + 1 : -1;
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or ':';

    /// <summary>The end of the tag or declaration name at <paramref name="i"/>: letters, digits and _ : . -</summary>
    private static int NameEnd(ReadOnlySpan<char> line, int i)
    {
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or ':' or '.' or '-'))
        {
            i++;
        }

        return i;
    }
}
