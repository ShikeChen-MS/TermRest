using System.Collections.Frozen;

namespace TermRest.Tui.Syntax;

/// <summary>Python: comments, strings and their prefixes (the fields of f-strings as code), numbers, keywords, decorators.</summary>
internal sealed class PythonLexer : SyntaxLexer
{
    public static readonly PythonLexer Instance = new();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = Words(
        "and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Constants = Words("True False None");

    public override LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state)
    {
        var i = state.Mode == LexMode.String ? StringText(line, kinds, 0, ref state) : 0;
        var name = SyntaxKind.Plain; // the next name: a function after def, a type after class
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '#')
            {
                kinds[i..].Fill(SyntaxKind.Comment);
                break;
            }

            if (c is '"' or '\'')
            {
                i = OpenString(line, kinds, i, i, StringStyle.None, ref state);
                name = SyntaxKind.Plain;
                continue;
            }

            if (IsNumberStart(line, i))
            {
                var end = NumberEnd(line, i);
                kinds[i..end].Fill(SyntaxKind.Number);
                i = end;
                continue;
            }

            if (IsWordStart(c))
            {
                var end = WordEnd(line, i);
                var word = line[i..end];
                if (At(line, end) is '"' or '\'' && Prefix(word) is { } style)
                {
                    i = OpenString(line, kinds, i, end, style, ref state);
                    name = SyntaxKind.Plain;
                    continue;
                }

                var member = IsMember(line, i);
                var keyword = !member && (Keywords.Contains(word) || IsSoftKeyword(line, i, word));
                kinds[i..end].Fill(
                    keyword ? SyntaxKind.Keyword
                    : name != SyntaxKind.Plain ? name
                    : !member && Constants.Contains(word) ? SyntaxKind.Number
                    : At(line, end) == '(' ? SyntaxKind.Function
                    : SyntaxKind.Plain);
                name = !keyword ? SyntaxKind.Plain : word is "def" ? SyntaxKind.Function : word is "class" ? SyntaxKind.Type : SyntaxKind.Plain;
                i = end;
                continue;
            }

            if (c == '@' && line[..i].IsWhiteSpace())
            {
                // A decorator: @name, @tr.test('…')
                var end = i + 1;
                while (end < line.Length && (IsWordPart(line[end]) || line[end] == '.'))
                {
                    end++;
                }

                kinds[i..end].Fill(SyntaxKind.Function);
                i = end;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                name = SyntaxKind.Plain;
            }

            i++;
        }

        return state;
    }

    /// <summary>A string that starts at <paramref name="start"/> (its prefix) with its quotes at <paramref name="quote"/>.</summary>
    private int OpenString(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int start, int quote, StringStyle style, ref LexState state)
    {
        var quotes = Run(line, quote, line[quote]) >= 3 ? 3 : 1;
        kinds[start..(quote + quotes)].Fill(SyntaxKind.String);
        state = new LexState(LexMode.String, line[quote], (byte)quotes, quotes == 3 ? style | StringStyle.MultiLine : style);
        return StringText(line, kinds, quote + quotes, ref state);
    }

    /// <summary>The text of a string from <paramref name="i"/> to its closing quotes; returns the index after them.</summary>
    private int StringText(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, ref LexState state)
    {
        if (state.Depth > 0)
        {
            i = Interpolation(line, kinds, i, state.Depth, '{', '}', '\\', ref state);
        }

        while (i < line.Length && state.Depth == 0)
        {
            var c = line[i];
            kinds[i] = SyntaxKind.String;
            if (c == '\\')
            {
                if (i + 1 == line.Length)
                {
                    return line.Length; // an escaped line end: the string goes on
                }

                kinds[i + 1] = SyntaxKind.String;
                i += 2;
                continue;
            }

            if (c == state.Quote && Run(line, i, c) >= state.Quotes)
            {
                kinds[i..(i + state.Quotes)].Fill(SyntaxKind.String);
                i += state.Quotes;
                state = LexState.Code;
                return i;
            }

            if (c is '{' or '}' && state.Style.HasFlag(StringStyle.Interpolated))
            {
                if (At(line, i + 1) == c)
                {
                    kinds[i + 1] = SyntaxKind.String; // {{ or }}
                    i += 2;
                    continue;
                }

                if (c == '{')
                {
                    i = Interpolation(line, kinds, i + 1, 1, '{', '}', '\\', ref state);
                    continue;
                }
            }

            i++;
        }

        if (state.Depth == 0 && !state.Style.HasFlag(StringStyle.MultiLine))
        {
            state = LexState.Code; // not closed on its line
        }

        return i;
    }

    /// <summary>The style of a string prefix (r, b, u, f, t, or two of them such as rb and fr), or null if it is not one.</summary>
    private static StringStyle? Prefix(ReadOnlySpan<char> word)
    {
        if (word.Length > 2)
        {
            return null;
        }

        var style = StringStyle.None;
        foreach (var c in word)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'f' or 't':
                    style |= StringStyle.Interpolated;
                    break;
                case 'r' or 'b' or 'u':
                    break;
                default:
                    return null;
            }
        }

        return style;
    }

    /// <summary>match and case are keywords only where they start a statement: first on a line that ends with a colon.</summary>
    private static bool IsSoftKeyword(ReadOnlySpan<char> line, int i, ReadOnlySpan<char> word) =>
        word is "match" or "case" && line[..i].IsWhiteSpace() && line.TrimEnd() is [.., ':'];
}
