using System.Collections.Frozen;

namespace TermRest.Tui.Syntax;

/// <summary>
/// PowerShell: comments, strings (their $variables and $(…) colored as code), here-strings, variables, parameters,
/// operators (-eq …), commands, [types], numbers, keywords. Keywords and operators are case-insensitive.
/// </summary>
internal sealed class PowerShellLexer : SyntaxLexer
{
    public static readonly PowerShellLexer Instance = new();

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = Words(
        "begin break catch class continue data default do dynamicparam else elseif end enum exit filter finally for foreach function hidden if in param process return static switch throw trap try until using while",
        ignoreCase: true);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Operators = Words(
        "and as band bnot bor bxor f is isnot join not or shl shr xor", ignoreCase: true);

    /// <summary>The operators that also come case-sensitive or not: -ceq, -ilike …</summary>
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Comparisons = Words(
        "contains eq ge gt in le like lt match ne notcontains notin notlike notmatch replace split", ignoreCase: true);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Constants = Words("true false null", ignoreCase: true);

    public override LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state)
    {
        var i = state.Mode switch
        {
            LexMode.Comment => BlockComment(line, kinds, 0, "#>", ref state),
            LexMode.String => StringText(line, kinds, 0, ref state),
            _ => 0,
        };
        var name = SyntaxKind.Plain; // the next name: a function after function, a type after class
        while (i < line.Length)
        {
            var c = line[i];
            var next = At(line, i + 1);
            var tokenStart = i == 0 || char.IsWhiteSpace(line[i - 1]) || line[i - 1] is '(' or '{' or '}' or ';' or '|' or ',' or '=';
            if (c == '<' && next == '#')
            {
                kinds[i] = kinds[i + 1] = SyntaxKind.Comment;
                i = BlockComment(line, kinds, i + 2, "#>", ref state);
                continue;
            }

            if (c == '#' && tokenStart)
            {
                kinds[i..].Fill(SyntaxKind.Comment);
                break;
            }

            if (c == '`')
            {
                i = Math.Min(i + 2, line.Length); // an escaped character, or a line continuation
                continue;
            }

            if (c == '@' && next is '"' or '\'' && line[(i + 2)..].IsWhiteSpace())
            {
                // A here-string: its text starts on the next line.
                kinds[i..].Fill(SyntaxKind.String);
                state = new LexState(LexMode.String, next, 1, StringStyle.Here | StringStyle.MultiLine | (next == '"' ? StringStyle.Interpolated : StringStyle.Raw));
                break;
            }

            if (c is '"' or '\'')
            {
                kinds[i] = SyntaxKind.String;
                state = new LexState(LexMode.String, c, 1, StringStyle.Doubled | StringStyle.MultiLine | (c == '"' ? StringStyle.Interpolated : StringStyle.Raw));
                i = StringText(line, kinds, i + 1, ref state);
                name = SyntaxKind.Plain;
                continue;
            }

            if (c == '$' && VariableEnd(line, i) is var variableEnd and > 0)
            {
                kinds[i..variableEnd].Fill(Constants.Contains(line[(i + 1)..variableEnd]) ? SyntaxKind.Number : SyntaxKind.Variable);
                i = variableEnd;
                continue;
            }

            if (c == '@' && IsWordStart(next))
            {
                var end = WordEnd(line, i + 1); // splatting: @parameters
                kinds[i..end].Fill(SyntaxKind.Variable);
                i = end;
                continue;
            }

            if (c == '[' && !(i > 0 && (IsWordPart(line[i - 1]) || line[i - 1] is ']' or ')')) && TypeEnd(line, i) is var typeEnd and > 0)
            {
                kinds[i..typeEnd].Fill(SyntaxKind.Type);
                i = typeEnd;
                continue;
            }

            if (c == '-' && tokenStart && IsWordStart(next))
            {
                var end = WordEnd(line, i + 1);
                kinds[i..end].Fill(IsOperator(line[(i + 1)..end]) ? SyntaxKind.Keyword : SyntaxKind.Parameter);
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

            if (IsWordStart(c))
            {
                // A bareword: a keyword, a command (Get-Item), a member, or an argument.
                var end = i;
                while (end < line.Length && (IsWordPart(line[end]) || (line[end] == '-' && IsWordPart(At(line, end + 1)))))
                {
                    end++;
                }

                var word = line[i..end];
                var member = IsMember(line, i) || (i > 1 && line[i - 1] == ':' && line[i - 2] == ':');
                var keyword = !member && Keywords.Contains(word);
                kinds[i..end].Fill(
                    keyword ? SyntaxKind.Keyword
                    : name != SyntaxKind.Plain ? name
                    : At(line, end) == '(' || (!member && (word.Contains('-') || IsCommand(line, i, end))) ? SyntaxKind.Function
                    : SyntaxKind.Plain);
                name = !keyword ? SyntaxKind.Plain
                    : word.Equals("function", StringComparison.OrdinalIgnoreCase) || word.Equals("filter", StringComparison.OrdinalIgnoreCase) ? SyntaxKind.Function
                    : word.Equals("class", StringComparison.OrdinalIgnoreCase) || word.Equals("enum", StringComparison.OrdinalIgnoreCase) ? SyntaxKind.Type
                    : SyntaxKind.Plain;
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

    /// <summary>
    /// The text of a string from <paramref name="i"/> to its closing quote; returns the index after it. In "…" and @"…"@,
    /// $variables and $(…) are colored as code.
    /// </summary>
    private int StringText(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, ref LexState state)
    {
        if (state.Depth > 0)
        {
            i = Interpolation(line, kinds, i, state.Depth, '(', ')', '`', ref state);
        }
        else if (state.Style.HasFlag(StringStyle.Here) && i == 0 && At(line, 0) == state.Quote && At(line, 1) == '@')
        {
            kinds[0] = kinds[1] = SyntaxKind.String;
            state = LexState.Code;
            return 2;
        }

        var expands = state.Style.HasFlag(StringStyle.Interpolated);
        while (i < line.Length && state.Depth == 0)
        {
            var c = line[i];
            kinds[i] = SyntaxKind.String;
            if (c == '`' && expands)
            {
                if (i + 1 < line.Length)
                {
                    kinds[i + 1] = SyntaxKind.String;
                }

                i = Math.Min(i + 2, line.Length);
                continue;
            }

            if (c == state.Quote && !state.Style.HasFlag(StringStyle.Here))
            {
                if (At(line, i + 1) == c)
                {
                    kinds[i + 1] = SyntaxKind.String; // '' or "": a quote
                    i += 2;
                    continue;
                }

                state = LexState.Code;
                return i + 1;
            }

            if (c == '$' && expands)
            {
                if (At(line, i + 1) == '(')
                {
                    kinds[i + 1] = SyntaxKind.String;
                    i = Interpolation(line, kinds, i + 2, 1, '(', ')', '`', ref state);
                    continue;
                }

                if (VariableEnd(line, i) is var end and > 0)
                {
                    kinds[i..end].Fill(SyntaxKind.Variable);
                    i = end;
                    continue;
                }
            }

            i++;
        }

        return i;
    }

    /// <summary>The end of the variable at <paramref name="i"/> ($name, $env:Path, ${any name}, $_, $$, $?, $^), or -1 if there is none.</summary>
    private static int VariableEnd(ReadOnlySpan<char> line, int i)
    {
        var c = At(line, i + 1);
        if (c == '{')
        {
            var close = line[(i + 2)..].IndexOf('}');
            return close < 0 ? -1 : i + 3 + close;
        }

        if (c is '$' or '?' or '^')
        {
            return i + 2;
        }

        if (!IsWordPart(c))
        {
            return -1;
        }

        var end = WordEnd(line, i + 1);
        return At(line, end) == ':' && IsWordStart(At(line, end + 1)) ? WordEnd(line, end + 1) : end; // $env:Path, $script:count
    }

    /// <summary>The end of the type at <paramref name="i"/> ([int], [System.IO.File], [string[]]), or -1 if it is not one.</summary>
    private static int TypeEnd(ReadOnlySpan<char> line, int i)
    {
        if (!IsWordStart(At(line, i + 1)))
        {
            return -1;
        }

        var depth = 0;
        for (var j = i; j < line.Length; j++)
        {
            var c = line[j];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                if (--depth == 0)
                {
                    return j + 1;
                }
            }
            else if (!(IsWordPart(c) || c is '.' or ',' or ' ' or '`' or '+'))
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool IsOperator(ReadOnlySpan<char> word) =>
        Operators.Contains(word) || Comparisons.Contains(word)
        || (word.Length > 2 && char.ToLowerInvariant(word[0]) is 'c' or 'i' && Comparisons.Contains(word[1..]));

    /// <summary>Whether the bareword at <paramref name="i"/> is a command: first on the line, in a pipeline or a block, or assigned; not a key (Name = …).</summary>
    private static bool IsCommand(ReadOnlySpan<char> line, int i, int end)
    {
        var before = line[..i].TrimEnd();
        return (before.IsEmpty || before[^1] is '|' or ';' or '{' or '&' or '=') && line[end..].TrimStart() is not ['=', ..];
    }
}
