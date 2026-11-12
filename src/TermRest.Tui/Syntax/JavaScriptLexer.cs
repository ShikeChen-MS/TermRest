using System.Collections.Frozen;

namespace TermRest.Tui.Syntax;

/// <summary>JavaScript: comments, strings and template literals (their ${…} as code), regular expressions, numbers, keywords.</summary>
internal sealed class JavaScriptLexer : SyntaxLexer
{
    public static readonly JavaScriptLexer Instance = new();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = Words(
        "async await break case catch class const continue debugger default delete do else export extends finally for function if import in instanceof let new of return static super switch this throw try typeof var void while with yield");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Constants = Words("true false null undefined NaN Infinity");

    /// <summary>The keywords after which a slash starts a regular expression rather than a division.</summary>
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> BeforeExpression = Words(
        "await case delete do else in instanceof new of return throw typeof void yield");

    public override LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state)
    {
        var i = state.Mode switch
        {
            LexMode.Comment => BlockComment(line, kinds, 0, "*/", ref state),
            LexMode.String => StringText(line, kinds, 0, ref state),
            _ => 0,
        };
        var regex = true; // whether a slash here starts a regular expression
        var name = SyntaxKind.Plain; // the next name: a function after function, a type after class or new
        while (i < line.Length)
        {
            var c = line[i];
            var next = At(line, i + 1);
            if (c == '/' && next == '/')
            {
                kinds[i..].Fill(SyntaxKind.Comment);
                break;
            }

            if (c == '/' && next == '*')
            {
                kinds[i] = kinds[i + 1] = SyntaxKind.Comment;
                i = BlockComment(line, kinds, i + 2, "*/", ref state);
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                kinds[i] = SyntaxKind.String;
                state = new LexState(LexMode.String, c, 1, c == '`' ? StringStyle.Interpolated | StringStyle.MultiLine : StringStyle.None);
                i = StringText(line, kinds, i + 1, ref state);
                regex = false;
                name = SyntaxKind.Plain;
                continue;
            }

            if (c == '/' && regex && RegexEnd(line, i) is var regexEnd and > 0)
            {
                kinds[i..regexEnd].Fill(SyntaxKind.String);
                i = regexEnd;
                regex = false;
                continue;
            }

            if (IsNumberStart(line, i))
            {
                var end = NumberEnd(line, i);
                kinds[i..end].Fill(SyntaxKind.Number);
                i = end;
                regex = false;
                continue;
            }

            if (IsWordStart(c) || c == '$')
            {
                var end = i + 1;
                while (end < line.Length && (IsWordPart(line[end]) || line[end] == '$'))
                {
                    end++;
                }

                var word = line[i..end];
                var member = IsMember(line, i);
                var keyword = !member && Keywords.Contains(word);
                kinds[i..end].Fill(
                    keyword ? SyntaxKind.Keyword
                    : name != SyntaxKind.Plain ? name
                    : !member && Constants.Contains(word) ? SyntaxKind.Number
                    : At(line, end) == '(' ? SyntaxKind.Function
                    : SyntaxKind.Plain);
                regex = keyword && BeforeExpression.Contains(word);
                name = !keyword ? SyntaxKind.Plain
                    : word is "function" ? SyntaxKind.Function
                    : word is "class" or "new" or "extends" ? SyntaxKind.Type
                    : SyntaxKind.Plain;
                i = end;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                regex = c is not (')' or ']' or '}');
                if (c != '*')
                {
                    name = SyntaxKind.Plain; // (function* name)
                }
            }

            i++;
        }

        return state;
    }

    /// <summary>The text of a string from <paramref name="i"/> to its closing quote; returns the index after it.</summary>
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

            if (c == state.Quote)
            {
                state = LexState.Code;
                return i + 1;
            }

            if (c == '$' && At(line, i + 1) == '{' && state.Style.HasFlag(StringStyle.Interpolated))
            {
                kinds[i + 1] = SyntaxKind.String;
                i = Interpolation(line, kinds, i + 2, 1, '{', '}', '\\', ref state);
                continue;
            }

            i++;
        }

        if (state.Depth == 0 && !state.Style.HasFlag(StringStyle.MultiLine))
        {
            state = LexState.Code; // not closed on its line
        }

        return i;
    }

    /// <summary>The end of the regular expression at <paramref name="i"/> (with its flags), or -1 if it does not close on the line.</summary>
    private static int RegexEnd(ReadOnlySpan<char> line, int i)
    {
        var inClass = false;
        for (var j = i + 1; j < line.Length; j++)
        {
            switch (line[j])
            {
                case '\\':
                    j++;
                    break;
                case '[':
                    inClass = true;
                    break;
                case ']':
                    inClass = false;
                    break;
                case '/' when !inClass:
                    return WordEnd(line, j + 1);
            }
        }

        return -1;
    }
}
