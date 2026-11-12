using System.Collections.Frozen;
using TermRest.Core.Scripting;

namespace TermRest.Tui.Syntax;

/// <summary>What a character of a script is, for coloring (each kind has its color in <see cref="Theming.ThemePalette"/>).</summary>
internal enum SyntaxKind : byte
{
    Plain,
    Comment,
    Keyword,
    String,
    Number,
    Variable,
    Function,
    Type,
    Parameter,
}

/// <summary>What a line leaves open for the next one.</summary>
internal enum LexMode : byte
{
    Code,
    Comment,
    String,
}

/// <summary>The kind of string a line leaves open.</summary>
[Flags]
internal enum StringStyle : byte
{
    None = 0,

    /// <summary>No escape character (C# @"…" and raw strings, PowerShell '…').</summary>
    Raw = 1,

    /// <summary>Two quotes stand for one (C# @"…", PowerShell '…' and "…").</summary>
    Doubled = 2,

    /// <summary>With interpolations, colored as code: Python f"{…}", JavaScript `${…}`, C# $"{…}", PowerShell "$(…)".</summary>
    Interpolated = 4,

    /// <summary>Goes on over lines; other strings continue only past an escaped line end.</summary>
    MultiLine = 8,

    /// <summary>A PowerShell here-string, which ends with "@ or '@ at the start of a line.</summary>
    Here = 16,
}

/// <summary>
/// What a line leaves open for the next one: a block comment, or a string (its closing quote, how many of them, its style,
/// and how many brackets deep into an interpolation the line ends).
/// </summary>
internal readonly record struct LexState(LexMode Mode, char Quote = '\0', byte Quotes = 0, StringStyle Style = StringStyle.None, byte Depth = 0)
{
    public static LexState Code => default;

    public static LexState Comment => new(LexMode.Comment);
}

/// <summary>
/// Basic syntax highlighting of scripts: comments, strings, numbers, keywords, function calls, types, and PowerShell's
/// variables and parameters. Each line is colored on its own, knowing only what the lines above left open
/// (<see cref="LexState"/>), so an editor colors only the lines down to the last one in view. Not a parser: rare
/// constructs may get the wrong color, never different text.
/// </summary>
internal abstract class SyntaxLexer
{
    /// <summary>The lexer of a script language (<see cref="ScriptLanguage"/>), or null for plain text.</summary>
    public static SyntaxLexer? For(string? language) => language switch
    {
        ScriptLanguage.Python => PythonLexer.Instance,
        ScriptLanguage.JavaScript => JavaScriptLexer.Instance,
        ScriptLanguage.CSharp => CSharpLexer.Instance,
        ScriptLanguage.PowerShell => PowerShellLexer.Instance,
        _ => null,
    };

    /// <summary>
    /// Colors <paramref name="line"/> (without its line break), which starts in <paramref name="state"/>: sets the kind of
    /// each of its characters in <paramref name="kinds"/> (all <see cref="SyntaxKind.Plain"/> on entry). Returns the
    /// state at its end.
    /// </summary>
    public abstract LexState Lex(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, LexState state);

    /// <summary>A set of words (separated by spaces) to look up without allocating.</summary>
    protected static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Words(string words, bool ignoreCase = false) =>
        words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToFrozenSet(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .GetAlternateLookup<ReadOnlySpan<char>>();

    protected static char At(ReadOnlySpan<char> line, int i) => i < line.Length ? line[i] : '\0';

    protected static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    protected static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The end of the word (letters, digits, _) that goes on at <paramref name="i"/>.</summary>
    protected static int WordEnd(ReadOnlySpan<char> line, int i)
    {
        while (i < line.Length && IsWordPart(line[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>How many <paramref name="c"/> follow each other from <paramref name="i"/>.</summary>
    protected static int Run(ReadOnlySpan<char> line, int i, char c)
    {
        var end = i;
        while (end < line.Length && line[end] == c)
        {
            end++;
        }

        return end - i;
    }

    /// <summary>Whether the word at <paramref name="i"/> follows a dot: a member (<c>pm.test</c>, <c>x.default</c>), never a keyword.</summary>
    protected static bool IsMember(ReadOnlySpan<char> line, int i) => i > 0 && line[i - 1] == '.';

    /// <summary>Whether a number starts at <paramref name="i"/>: a digit, or a decimal point and a digit, not inside a word.</summary>
    protected static bool IsNumberStart(ReadOnlySpan<char> line, int i)
    {
        if (i > 0 && IsWordPart(line[i - 1]))
        {
            return false;
        }

        return char.IsAsciiDigit(line[i])
               || (line[i] == '.' && char.IsAsciiDigit(At(line, i + 1)) && !(i > 0 && line[i - 1] is '.' or ')' or ']'));
    }

    /// <summary>The end of the number at <paramref name="i"/>: digits, letters (0x1F, 1e-3, 10kb, 2UL, 1n, 3j), _ and decimal points.</summary>
    protected static int NumberEnd(ReadOnlySpan<char> line, int i)
    {
        var hex = line[i] == '0' && char.ToLowerInvariant(At(line, i + 1)) == 'x';
        for (i++; i < line.Length; i++)
        {
            var c = line[i];
            var exponentSign = c is '+' or '-' && !hex && char.ToLowerInvariant(line[i - 1]) == 'e';
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || ((c == '.' || exponentSign) && char.IsAsciiDigit(At(line, i + 1)))))
            {
                break;
            }
        }

        return i;
    }

    /// <summary>
    /// Colors a block comment from <paramref name="i"/> up to and including <paramref name="close"/>, and returns the index
    /// after it. When it goes on past the line, returns the line's length with <paramref name="state"/> in a comment.
    /// </summary>
    protected static int BlockComment(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, string close, ref LexState state)
    {
        var found = line[i..].IndexOf(close.AsSpan());
        var end = found < 0 ? line.Length : i + found + close.Length;
        kinds[i..end].Fill(SyntaxKind.Comment);
        state = found < 0 ? LexState.Comment : LexState.Code;
        return end;
    }

    /// <summary>
    /// Colors an interpolation from <paramref name="i"/>, which is <paramref name="depth"/> brackets deep there: its code
    /// as code, its closing bracket as string. Returns the index after it; when it goes on past the line, returns the
    /// line's length with <paramref name="state"/> keeping how deep.
    /// </summary>
    protected int Interpolation(ReadOnlySpan<char> line, Span<SyntaxKind> kinds, int i, int depth, char open, char close, char escape, ref LexState state)
    {
        var end = ClosingBracket(line, i, open, close, escape, ref depth);
        var codeEnd = end < 0 ? line.Length : end;
        kinds[i..codeEnd].Clear();
        Lex(line[i..codeEnd], kinds[i..codeEnd], LexState.Code);
        if (end < 0)
        {
            state = state with { Depth = (byte)Math.Min(depth, byte.MaxValue) };
            return line.Length;
        }

        kinds[end] = SyntaxKind.String;
        state = state with { Depth = 0 };
        return end + 1;
    }

    /// <summary>
    /// The closing bracket of an interpolation that is <paramref name="depth"/> brackets deep at <paramref name="i"/>, or -1
    /// if it is not on the line (<paramref name="depth"/> then says how deep the line ends). Strings inside are skipped.
    /// </summary>
    private static int ClosingBracket(ReadOnlySpan<char> line, int i, char open, char close, char escape, ref int depth)
    {
        for (; i < line.Length; i++)
        {
            var c = line[i];
            if (c == escape)
            {
                i++;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                if (--depth == 0)
                {
                    return i;
                }
            }
            else if (c is '"' or '\'' or '`')
            {
                for (i++; i < line.Length && line[i] != c; i++)
                {
                    if (line[i] == escape)
                    {
                        i++;
                    }
                }
            }
        }

        return -1;
    }
}
