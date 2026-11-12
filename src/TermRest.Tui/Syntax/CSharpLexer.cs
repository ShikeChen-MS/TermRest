using System.Collections.Frozen;

namespace TermRest.Tui.Syntax;

/// <summary>
/// C#: comments, directives (#if, #:package …), strings (regular, verbatim, interpolated with their {…} as code, raw),
/// characters, numbers, keywords.
/// </summary>
internal sealed class CSharpLexer : SyntaxLexer
{
    public static readonly CSharpLexer Instance = new();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = Words(
        "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new nint nuint object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw try typeof uint ulong unchecked unsafe ushort using virtual void volatile while " +
        "and async await dynamic get global init nameof not or partial record required set var when where with yield");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Constants = Words("true false null");

    /// <summary>The keywords followed by the name of a type.</summary>
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> BeforeType = Words("class enum interface new record struct");

    public override LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state)
    {
        if (state.Mode == LexMode.Code && line.TrimStart() is ['#', ..] directive)
        {
            Directive(line, kinds, line.Length - directive.Length);
            return state;
        }

        var i = state.Mode switch
        {
            LexMode.Comment => BlockComment(line, kinds, 0, "*/", ref state),
            LexMode.String => StringText(line, kinds, 0, ref state),
            _ => 0,
        };
        var name = SyntaxKind.Plain; // the next name: a type after class, new, record …
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

            if (QuoteOfString(line, i) is var quote and >= 0)
            {
                i = OpenString(line, kinds, i, quote, ref state);
                name = SyntaxKind.Plain;
                continue;
            }

            if (c == '\'')
            {
                var end = CharEnd(line, i);
                kinds[i..end].Fill(SyntaxKind.String);
                i = end;
                continue;
            }

            if (IsNumberStart(line, i))
            {
                var end = NumberEnd(line, i);
                kinds[i..end].Fill(SyntaxKind.Number);
                i = end;
                continue;
            }

            if (IsWordStart(c) || (c == '@' && IsWordStart(next)))
            {
                var start = c == '@' ? i + 1 : i;
                var end = WordEnd(line, start);
                var word = line[start..end];
                var plainName = c == '@' || IsMember(line, i); // @class is a name
                var keyword = !plainName && Keywords.Contains(word);
                kinds[i..end].Fill(
                    !plainName && Constants.Contains(word) ? SyntaxKind.Number
                    : keyword ? SyntaxKind.Keyword
                    : name != SyntaxKind.Plain ? name
                    : At(line, end) == '(' ? SyntaxKind.Function
                    : SyntaxKind.Plain);
                name = keyword && BeforeType.Contains(word) ? SyntaxKind.Type : SyntaxKind.Plain;
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

    /// <summary>Where the quotes are if a string starts at <paramref name="i"/> ("…", @"…", $"…", $@"…", """…""", $$"""…"""), else -1.</summary>
    private static int QuoteOfString(ReadOnlySpan<char> line, int i)
    {
        var j = i;
        while (j < line.Length && line[j] is '$' or '@')
        {
            j++;
        }

        return At(line, j) == '"' ? j : -1;
    }

    /// <summary>A string that starts at <paramref name="start"/> (its $ and @) with its quotes at <paramref name="quote"/>.</summary>
    private int OpenString(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int start, int quote, ref LexState state)
    {
        var prefix = line[start..quote];
        var verbatim = prefix.Contains('@');
        var quotes = Run(line, quote, '"');
        StringStyle style;
        if (quotes >= 3 && !verbatim)
        {
            style = StringStyle.Raw | StringStyle.MultiLine; // """…""": its interpolations stay string-colored
        }
        else
        {
            quotes = 1;
            style = (verbatim ? StringStyle.Raw | StringStyle.Doubled | StringStyle.MultiLine : StringStyle.None)
                    | (prefix.Contains('$') ? StringStyle.Interpolated : StringStyle.None);
        }

        kinds[start..(quote + quotes)].Fill(SyntaxKind.String);
        state = new LexState(LexMode.String, '"', (byte)Math.Min(quotes, byte.MaxValue), style);
        return StringText(line, kinds, quote + quotes, ref state);
    }

    /// <summary>The text of a string from <paramref name="i"/> to its closing quotes; returns the index after them.</summary>
    private int StringText(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, ref LexState state)
    {
        if (state.Depth > 0)
        {
            i = Interpolation(line, kinds, i, state.Depth, '{', '}', '\\', ref state);
        }

        var style = state.Style;
        while (i < line.Length && state.Depth == 0)
        {
            var c = line[i];
            kinds[i] = SyntaxKind.String;
            if (c == '\\' && !style.HasFlag(StringStyle.Raw))
            {
                if (i + 1 < line.Length)
                {
                    kinds[i + 1] = SyntaxKind.String;
                }

                i = Math.Min(i + 2, line.Length);
                continue;
            }

            if (c == '"')
            {
                if (style.HasFlag(StringStyle.Doubled) && At(line, i + 1) == '"')
                {
                    kinds[i + 1] = SyntaxKind.String; // "" in a verbatim string
                    i += 2;
                    continue;
                }

                var run = Run(line, i, '"');
                if (run >= state.Quotes)
                {
                    var end = i + state.Quotes;
                    kinds[i..end].Fill(SyntaxKind.String);
                    state = LexState.Code;
                    return end;
                }

                kinds[i..(i + run)].Fill(SyntaxKind.String); // fewer quotes than close a raw string
                i += run;
                continue;
            }

            if (c is '{' or '}' && style.HasFlag(StringStyle.Interpolated))
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

        if (state.Depth == 0 && !style.HasFlag(StringStyle.MultiLine))
        {
            state = LexState.Code; // not closed on its line
        }

        return i;
    }

    /// <summary>The end of the character at <paramref name="i"/> ('a', '\n', '\''); just its quote if it does not close.</summary>
    private static int CharEnd(ReadOnlySpan<char> line, int i)
    {
        for (var j = i + 1; j < line.Length; j++)
        {
            if (line[j] == '\\')
            {
                j++;
            }
            else if (line[j] == '\'')
            {
                return j + 1;
            }
        }

        return i + 1;
    }

    /// <summary>A directive at <paramref name="i"/>: #if, #region …, #:package and the like of file-based programs, or a #! line.</summary>
    private static void Directive(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i)
    {
        if (At(line, i + 1) == '!')
        {
            kinds[i..].Fill(SyntaxKind.Comment);
            return;
        }

        var fileBased = At(line, i + 1) == ':';
        var end = WordEnd(line, fileBased ? i + 2 : i + 1);
        kinds[i..end].Fill(SyntaxKind.Keyword);
        if (fileBased)
        {
            kinds[(line.Length - line[end..].TrimStart().Length)..].Fill(SyntaxKind.String); // #:package Name@1.0
        }
        else if (line[end..].IndexOf("//".AsSpan()) is var comment and >= 0)
        {
            kinds[(end + comment)..].Fill(SyntaxKind.Comment);
        }
    }
}
