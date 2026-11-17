using System.Collections.Frozen;

namespace TermRest.Tui.Syntax;

/// <summary>
/// JSON (and JSON with comments, as request bodies may have): keys as variables, other strings, numbers, true, false and
/// null. A string followed by a colon on its line is a key.
/// </summary>
internal sealed class JsonLexer : SyntaxLexer
{
    public static readonly JsonLexer Instance = new();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Constants = Words("true false null");

    public override LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state)
    {
        var i = state.Mode == LexMode.Comment ? BlockComment(line, kinds, 0, "*/", ref state) : 0;
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

            if (c == '"')
            {
                // A string ends on its line (JSON writes a line break in one as \n).
                var end = StringEnd(line, i + 1);
                var after = end;
                while (after < line.Length && line[after] is ' ' or '\t')
                {
                    after++;
                }

                kinds[i..end].Fill(At(line, after) == ':' ? SyntaxKind.Variable : SyntaxKind.String);
                i = end;
                continue;
            }

            if ((c == '-' && char.IsAsciiDigit(next) && !(i > 0 && IsWordPart(line[i - 1]))) || IsNumberStart(line, i))
            {
                var end = NumberEnd(line, c == '-' ? i + 1 : i);
                kinds[i..end].Fill(SyntaxKind.Number);
                i = end;
                continue;
            }

            if (IsWordStart(c))
            {
                var end = WordEnd(line, i);
                if (Constants.Contains(line[i..end]))
                {
                    kinds[i..end].Fill(SyntaxKind.Keyword);
                }

                i = end;
                continue;
            }

            i++;
        }

        return state;
    }

    /// <summary>The index after the closing quote of the string whose text starts at <paramref name="i"/>, or the line's end.</summary>
    private static int StringEnd(ReadOnlySpan<char> line, int i)
    {
        for (; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == '"')
            {
                return i + 1;
            }
        }

        return line.Length;
    }
}
