using System.Globalization;
using System.Text;

namespace TermRest.Core.Import;

public enum ShellDialect
{
    /// <summary>bash/zsh/sh quoting: '…', "…", $'…', backslash escapes and line continuations.</summary>
    Posix,

    /// <summary>cmd.exe: ^ escapes and line continuations, then Windows (MSVCRT) argument rules.</summary>
    Cmd,

    /// <summary>PowerShell: '…', "…" with backtick escapes, backtick line continuations.</summary>
    PowerShell,
}

/// <summary>
/// Splits a pasted command line into arguments the way the shell it was written for would, so commands copied from
/// docs, terminals and browser developer tools ("Copy as cURL" for bash or cmd) arrive with the same values.
/// </summary>
public static class ShellWords
{
    /// <summary>
    /// Guesses the shell from continuation characters and Windows-only escapes outside quoted values (see
    /// <see cref="FindMarks"/> and <see cref="HasPowerShellContinuation"/>); defaults to POSIX.
    /// </summary>
    public static ShellDialect Detect(string text)
    {
        var marks = FindMarks(text);
        if (marks.Backtick)
        {
            return ShellDialect.PowerShell;
        }

        if (marks.CaretQuote || marks.Caret)
        {
            return ShellDialect.Cmd;
        }

        if (marks.Backslash)
        {
            return ShellDialect.Posix;
        }

        if (HasPowerShellContinuation(text))
        {
            return ShellDialect.PowerShell;
        }

        var start = text.TrimStart();
        if (start.StartsWith('&'))
        {
            return ShellDialect.PowerShell; // the call operator: & curl.exe …
        }

        if (start.StartsWith("curl.exe", StringComparison.OrdinalIgnoreCase))
        {
            return start.Contains('\'', StringComparison.Ordinal) ? ShellDialect.PowerShell : ShellDialect.Cmd;
        }

        return ShellDialect.Posix;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into words. Only the first command is read: an unquoted <c>;</c>, <c>|</c>,
    /// <c>&amp;&amp;</c> or redirection at the start of a word ends it (a note is added to <paramref name="notes"/>).
    /// </summary>
    public static List<string> Split(string text, ShellDialect dialect, ICollection<string>? notes = null) => dialect switch
    {
        // Chrome's "Copy as cURL (cmd)" (recognizable by ^"…^") doubles every backslash, although the C runtime only
        // halves those before a quote; halving the rest restores the browser's request.
        ShellDialect.Cmd => SplitWindowsArguments(UnescapeCmd(text), notes, halveBackslashes: text.Contains("^\"", StringComparison.Ordinal)),
        ShellDialect.PowerShell => SplitPowerShell(text, notes),
        _ => SplitPosix(text, notes),
    };

    /// <summary>
    /// Which of <c>`</c>, <c>^</c> and <c>\</c> end a line, and whether a word starts with <c>^"</c> (Chrome's cmd quoting,
    /// <c>curl ^"https://…^"</c>), outside quoted values, which say nothing about the shell: '…' (PowerShell's '' inside one
    /// ends it and starts another), $'…' (where \ escapes the next character) and "…" (where \ and ` do). Outside them, \, ^
    /// and ` escape the next character, so \' and ^" start no quoted value, and a # that starts a word starts a comment.
    /// </summary>
    private static (bool Backtick, bool Caret, bool Backslash, bool CaretQuote) FindMarks(string text)
    {
        bool backtick = false, caret = false, backslash = false, caretQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var wordStart = i == 0 || IsBlank(text[i - 1]);
            switch (text[i])
            {
                case '`' or '^' or '\\':
                    if (NewlineLength(text, i + 1) > 0)
                    {
                        backtick |= text[i] == '`';
                        caret |= text[i] == '^';
                        backslash |= text[i] == '\\';
                    }

                    caretQuote |= text[i] == '^' && wordStart && i + 1 < text.Length && text[i + 1] == '"';
                    i++;
                    break;
                case '\'':
                    i = QuotedEnd(text, i + 1, '\'', "");
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '\'':
                    i = QuotedEnd(text, i + 2, '\'', "\\");
                    break;
                case '"':
                    i = QuotedEnd(text, i + 1, '"', "\\`");
                    break;
                case '#' when wordStart:
                    while (i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        i++;
                    }

                    break;
            }
        }

        return (backtick, caret, backslash, caretQuote);
    }

    /// <summary>Where the quoted value whose text starts at <paramref name="i"/> ends: its closing quote, or the end of the text.</summary>
    private static int QuotedEnd(string text, int i, char quote, string escapes)
    {
        for (; i < text.Length && text[i] != quote; i++)
        {
            if (escapes.Contains(text[i], StringComparison.Ordinal))
            {
                i++;
            }
        }

        return i;
    }

    /// <summary>
    /// Whether a ` ends a line outside quoted values as PowerShell reads them: '…' ('' inside one ends it and starts another)
    /// and "…", where only ` escapes. A \ before the closing quote is a character there ("C:\temp\"), where
    /// <see cref="FindMarks"/> takes the value for one that goes on, past the ` that ends its line.
    /// </summary>
    private static bool HasPowerShellContinuation(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '`':
                    if (NewlineLength(text, i + 1) > 0)
                    {
                        return true;
                    }

                    i++;
                    break;
                case '\'':
                    i = QuotedEnd(text, i + 1, '\'', "");
                    break;
                case '"':
                    i = QuotedEnd(text, i + 1, '"', "`");
                    break;
                case '#' when i == 0 || IsBlank(text[i - 1]):
                    while (i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        i++;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>Length of the line break starting at <paramref name="index"/> (LF or CRLF), or 0.</summary>
    private static int NewlineLength(string text, int index) =>
        index >= text.Length ? 0
        : text[index] == '\n' ? 1
        : text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2
        : 0;

    private static bool IsBlank(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static bool EndsCommand(string text, int index, ICollection<string>? notes)
    {
        if (text[index] is not (';' or '|' or '&' or '>' or '<'))
        {
            return false;
        }

        notes?.Add($"Only the first command was imported; '{text[index..].Split('\n')[0].Trim()}' and anything after it was ignored.");
        return true;
    }

    // ----- POSIX shells ------------------------------------------------------------------------

    private static List<string> SplitPosix(string s, ICollection<string>? notes)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && NewlineLength(s, i + 1) is var continuation and > 0)
            {
                i += 1 + continuation;
                continue;
            }

            if (IsBlank(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                i++;
                continue;
            }

            if (!inWord)
            {
                if (c == '#')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                // Control operators only count at the start of a word, so an unquoted URL such as
                // https://host/?a=1&b=2 (a common copy-paste slip) keeps its query string.
                if (EndsCommand(s, i, notes))
                {
                    break;
                }
            }

            inWord = true;
            switch (c)
            {
                case '\\':
                    if (i + 1 < s.Length)
                    {
                        word.Append(s[i + 1]);
                    }

                    i += 2;
                    break;
                case '\'':
                {
                    var end = s.IndexOf('\'', i + 1);
                    if (end < 0)
                    {
                        notes?.Add("A single-quoted string is not closed.");
                        end = s.Length;
                    }

                    word.Append(s, i + 1, end - i - 1);
                    i = end + 1;
                    break;
                }

                case '"':
                    i = ReadPosixDoubleQuoted(s, i + 1, word, notes);
                    break;
                case '$' when i + 1 < s.Length && s[i + 1] == '\'':
                    i = ReadAnsiC(s, i + 2, word, notes);
                    break;
                case '$' when i + 1 < s.Length && s[i + 1] == '"':
                    i = ReadPosixDoubleQuoted(s, i + 2, word, notes);
                    break;
                default:
                    word.Append(c);
                    i++;
                    break;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static int ReadPosixDoubleQuoted(string s, int i, StringBuilder word, ICollection<string>? notes)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"')
            {
                return i + 1;
            }

            if (c == '\\' && i + 1 < s.Length)
            {
                if (NewlineLength(s, i + 1) is var continuation and > 0)
                {
                    i += 1 + continuation;
                    continue;
                }

                if (s[i + 1] is '"' or '\\' or '$' or '`')
                {
                    word.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
            }

            word.Append(c);
            i++;
        }

        notes?.Add("A double-quoted string is not closed.");
        return i;
    }

    /// <summary>bash <c>$'…'</c> quoting, used by browsers' "Copy as cURL (bash)" for values with quotes or control characters.</summary>
    private static int ReadAnsiC(string s, int i, StringBuilder word, ICollection<string>? notes)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\'')
            {
                return i + 1;
            }

            if (c != '\\' || i + 1 >= s.Length)
            {
                word.Append(c);
                i++;
                continue;
            }

            var e = s[i + 1];
            i += 2;
            switch (e)
            {
                case 'a': word.Append('\a'); break;
                case 'b': word.Append('\b'); break;
                case 'e' or 'E': word.Append('\u001b'); break;
                case 'f': word.Append('\f'); break;
                case 'n': word.Append('\n'); break;
                case 'r': word.Append('\r'); break;
                case 't': word.Append('\t'); break;
                case 'v': word.Append('\v'); break;
                case '\\' or '\'' or '"' or '?': word.Append(e); break;
                case 'x': i = AppendCodePoint(s, i, 2, 16, word, "\\x"); break;
                case 'u': i = AppendCodePoint(s, i, 4, 16, word, "\\u"); break;
                case 'U': i = AppendCodePoint(s, i, 8, 16, word, "\\U"); break;
                case >= '0' and <= '7': i = AppendCodePoint(s, i - 1, 3, 8, word, "\\"); break;
                case 'c' when i < s.Length:
                    word.Append((char)(s[i] & 0x1f));
                    i++;
                    break;
                default:
                    word.Append('\\').Append(e);
                    break;
            }
        }

        notes?.Add("A $'…' string is not closed.");
        return i;
    }

    private static int AppendCodePoint(string s, int i, int maxDigits, int radix, StringBuilder word, string literalIfEmpty)
    {
        var start = i;
        var value = 0;
        while (i < s.Length && i - start < maxDigits && DigitValue(s[i], radix) is var digit and >= 0)
        {
            value = (value * radix) + digit;
            i++;
        }

        if (i == start)
        {
            word.Append(literalIfEmpty);
        }
        else if (value is >= 0xD800 and <= 0xDBFF && maxDigits == 4 && i + 6 <= s.Length && s[i] == '\\' && s[i + 1] == 'u'
                 && int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low) && low is >= 0xDC00 and <= 0xDFFF)
        {
            // Browsers escape text one UTF-16 unit at a time, so an emoji arrives as a surrogate pair: \ud83d\ude00.
            word.Append((char)value).Append((char)low);
            i += 6;
        }
        else if (value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
        {
            word.Append(char.ConvertFromUtf32(value));
        }

        return i;
    }

    private static int DigitValue(char c, int radix) => c switch
    {
        >= '0' and <= '7' => c - '0',
        '8' or '9' when radix == 16 => c - '0',
        >= 'a' and <= 'f' when radix == 16 => c - 'a' + 10,
        >= 'A' and <= 'F' when radix == 16 => c - 'A' + 10,
        _ => -1,
    };

    // ----- cmd.exe -----------------------------------------------------------------------------

    /// <summary>
    /// The cmd.exe pass: outside double quotes, <c>^x</c> is a literal x and <c>^</c> + line break continues the line
    /// (<c>^</c> + two line breaks is a literal line break, as emitted by Chrome's "Copy as cURL (cmd)").
    /// </summary>
    private static string UnescapeCmd(string s)
    {
        var result = new StringBuilder(s.Length);
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                quoted = !quoted;
                result.Append(c);
            }
            else if (c == '^' && !quoted && i + 1 < s.Length)
            {
                if (NewlineLength(s, i + 1) is var continuation and > 0)
                {
                    i += continuation;
                    if (NewlineLength(s, i + 1) is var literal and > 0)
                    {
                        result.Append('\n');
                        i += literal;
                    }
                }
                else
                {
                    result.Append(s[i + 1]);
                    i++;
                }
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>Windows C runtime argument rules: "…" groups, backslashes only escape quotes.</summary>
    private static List<string> SplitWindowsArguments(string s, ICollection<string>? notes, bool halveBackslashes)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!quoted && IsBlank(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                continue;
            }

            if (!quoted && !inWord && EndsCommand(s, i, notes))
            {
                break;
            }

            inWord = true;
            if (c == '\\')
            {
                var start = i;
                while (i < s.Length && s[i] == '\\')
                {
                    i++;
                }

                var count = i - start;
                if (i < s.Length && s[i] == '"')
                {
                    word.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        word.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else
                {
                    word.Append('\\', halveBackslashes ? (count + 1) / 2 : count);
                    i--;
                }
            }
            else if (c == '"')
            {
                if (quoted && i + 1 < s.Length && s[i + 1] == '"')
                {
                    word.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else
            {
                word.Append(c);
            }
        }

        if (quoted)
        {
            notes?.Add("A double-quoted string is not closed.");
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    // ----- PowerShell --------------------------------------------------------------------------

    private static List<string> SplitPowerShell(string s, ICollection<string>? notes)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '`' && NewlineLength(s, i + 1) is var continuation and > 0)
            {
                i += 1 + continuation;
                continue;
            }

            if (IsBlank(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                i++;
                continue;
            }

            if (!inWord)
            {
                if (c == '#')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '&' && words.Count == 0)
                {
                    i++; // the call operator: & curl.exe …
                    continue;
                }

                if (EndsCommand(s, i, notes))
                {
                    break;
                }
            }

            inWord = true;
            switch (c)
            {
                case '`':
                    if (i + 1 < s.Length)
                    {
                        word.Append(PowerShellEscape(s[i + 1]));
                    }

                    i += 2;
                    break;
                case '\'':
                    i = ReadPowerShellSingleQuoted(s, i + 1, word, notes);
                    break;
                case '"':
                    i = ReadPowerShellDoubleQuoted(s, i + 1, word, notes);
                    break;
                default:
                    word.Append(c);
                    i++;
                    break;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static int ReadPowerShellSingleQuoted(string s, int i, StringBuilder word, ICollection<string>? notes)
    {
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    word.Append('\'');
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            word.Append(s[i]);
            i++;
        }

        notes?.Add("A single-quoted string is not closed.");
        return i;
    }

    private static int ReadPowerShellDoubleQuoted(string s, int i, StringBuilder word, ICollection<string>? notes)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '`' && i + 1 < s.Length)
            {
                if (NewlineLength(s, i + 1) is var continuation and > 0)
                {
                    i += 1 + continuation;
                    continue;
                }

                word.Append(PowerShellEscape(s[i + 1]));
                i += 2;
                continue;
            }

            if (c == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"')
                {
                    word.Append('"');
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            word.Append(c);
            i++;
        }

        notes?.Add("A double-quoted string is not closed.");
        return i;
    }

    private static char PowerShellEscape(char c) => c switch
    {
        '0' => '\0',
        'a' => '\a',
        'b' => '\b',
        'e' => '\u001b',
        'f' => '\f',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        'v' => '\v',
        _ => c,
    };
}
