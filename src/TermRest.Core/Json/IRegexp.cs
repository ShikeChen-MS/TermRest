using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TermRest.Core.Json;

/// <summary>
/// Turns I-Regexp (RFC 9485), the regular expressions of JSONPath's match and search, into .NET ones that match code
/// points as I-Regexp does, not UTF-16 code units: "." and [^a] match an emoji whole, \p{L} matches 𝒜.
/// </summary>
internal static class IRegexp
{
    /// <summary>How long a regular expression may take on one string.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<(string Category, bool Negated), List<(int Low, int High)>> SupplementaryCategories = new();

    private const string Surrogates = @"\uD800-\uDFFF";

    /// <summary>"." of I-Regexp: any code point but a line feed or a carriage return.</summary>
    private const string Dot = @"(?:[^\n\r\uD800-\uDFFF]|[\uD800-\uDBFF][\uDC00-\uDFFF])";

    /// <summary>
    /// A regular expression that matches what <paramref name="pattern"/> matches: the whole string (match) or a part of it
    /// (search). Null when the pattern is not an I-Regexp, which then matches nothing.
    /// </summary>
    public static Regex? Create(string pattern, bool whole)
    {
        if (Translate(pattern) is not { } translated)
        {
            return null;
        }

        var expression = whole ? $@"\A(?:{translated})\z" : translated;
        try
        {
            // Matches in a time proportional to the string's length: a pattern taken from a response cannot hang a filter.
            return new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout);
        }
        catch (NotSupportedException)
        {
            try
            {
                return new Regex(expression, RegexOptions.CultureInvariant, Timeout); // too large for that engine: a{1000}{1000}
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null; // a count .NET cannot take: a{99999999999}
        }
    }

    public static bool IsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new JsonPathException($"A regular expression takes more than {Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds on a string of {text.Length.ToString(CultureInfo.InvariantCulture)} characters.");
        }
    }

    /// <summary>The .NET pattern for an I-Regexp; null when the text is not one.</summary>
    internal static string? Translate(string pattern) => new Translator(pattern).Run();

    private sealed class Translator(string pattern)
    {
        private const int MaxDepth = 64;

        private readonly StringBuilder _out = new();
        private int _pos;
        private int _depth;

        public string? Run()
        {
            try
            {
                Regexp();
                return _pos == pattern.Length ? _out.ToString() : null; // a ) that closes nothing
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // i-regexp = branch *( "|" branch ); branch = *piece; piece = atom [ quantifier ]
        private void Regexp()
        {
            while (true)
            {
                while (_pos < pattern.Length && pattern[_pos] is not ('|' or ')'))
                {
                    Atom();
                    Quantifier();
                }

                if (!Take('|'))
                {
                    return;
                }

                _out.Append('|');
            }
        }

        private void Atom()
        {
            switch (pattern[_pos])
            {
                case '(':
                    _pos++;
                    if (++_depth > MaxDepth)
                    {
                        throw new FormatException();
                    }

                    _out.Append("(?:");
                    Regexp();
                    if (!Take(')'))
                    {
                        throw new FormatException();
                    }

                    _out.Append(')');
                    _depth--;
                    break;
                case '.':
                    _pos++;
                    _out.Append(Dot);
                    break;
                case '[':
                    _pos++;
                    var negated = Take('^');
                    var set = new CodePointSet();
                    ClassItems(set);
                    Append(set, negated);
                    break;
                case '\\' when Peek(1) is 'p' or 'P':
                    var category = new CodePointSet();
                    Category(category);
                    Append(category, negated: false);
                    break;
                case '\\':
                    AppendLiteral(SingleCharEscape());
                    break;

                // Anchors, as ECMAScript (and the JSONPath compliance tests) take them.
                case '^':
                    _pos++;
                    _out.Append(@"(?:\A)");
                    break;
                case '$':
                    _pos++;
                    _out.Append(@"(?:\z)");
                    break;
                case '*' or '+' or '?' or '{' or '}' or ']':
                    throw new FormatException();
                default:
                    AppendLiteral(CodePoint());
                    break;
            }
        }

        // quantifier = "*" / "+" / "?" / "{" n [ "," [ m ] ] "}"
        private void Quantifier()
        {
            if (_pos >= pattern.Length)
            {
                return;
            }

            switch (pattern[_pos])
            {
                case '*' or '+' or '?':
                    _out.Append(pattern[_pos++]);
                    break;
                case '{':
                    var start = _pos++;
                    var min = Count() ?? throw new FormatException();
                    var max = min;
                    if (Take(','))
                    {
                        max = Count() ?? long.MaxValue;
                    }

                    if (!Take('}') || max < min)
                    {
                        throw new FormatException();
                    }

                    _out.Append(pattern, start, _pos - start);
                    break;
            }
        }

        private long? Count()
        {
            var start = _pos;
            long value = 0;
            while (_pos < pattern.Length && char.IsAsciiDigit(pattern[_pos]))
            {
                value = Math.Min(value * 10 + (pattern[_pos++] - '0'), int.MaxValue);
            }

            return _pos == start ? null : value;
        }

        // charClassExpr = "[" [ "^" ] ( "-" / CCE1 ) *CCE1 [ "-" ] "]"; CCE1 = ( CCchar [ "-" CCchar ] ) / charClassEsc
        private void ClassItems(CodePointSet set)
        {
            var first = true;
            while (true)
            {
                if (_pos >= pattern.Length)
                {
                    throw new FormatException();
                }

                var c = pattern[_pos];
                if (c == ']' && !first)
                {
                    _pos++;
                    return;
                }

                if (c == '-')
                {
                    if (!first && Peek(1) != ']')
                    {
                        throw new FormatException(); // a - only comes first or last
                    }

                    _pos++;
                    set.Ranges.Add(('-', '-'));
                }
                else if (c == '\\' && Peek(1) is 'p' or 'P')
                {
                    Category(set);
                }
                else
                {
                    var low = ClassChar();
                    var high = low;
                    if (Peek(0) == '-' && Peek(1) is not (']' or -1))
                    {
                        _pos++;
                        if (Peek(0) == '\\' && Peek(1) is 'p' or 'P')
                        {
                            throw new FormatException();
                        }

                        high = ClassChar();
                        if (high < low)
                        {
                            throw new FormatException();
                        }
                    }

                    set.Ranges.Add((low, high));
                }

                first = false;
            }
        }

        // CCchar = ( %x00-2C / %x2E-5A / %x5E-D7FF / %xE000-10FFFF ) / SingleCharEsc
        private int ClassChar() => pattern[_pos] switch
        {
            '\\' => SingleCharEscape(),
            '-' or '[' or ']' => throw new FormatException(),
            _ => CodePoint(),
        };

        // SingleCharEsc = "\" ( %x28-2B / "-" / "." / "?" / %x5B-5E / "n" / "r" / "t" / %x7B-7D )
        private int SingleCharEscape()
        {
            var c = Peek(1);
            _pos += 2;
            return c switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}' => c,
                _ => throw new FormatException(),
            };
        }

        // catEsc = "\p{" charProp "}"; complEsc = "\P{" charProp "}"
        private void Category(CodePointSet set)
        {
            var negated = pattern[_pos + 1] == 'P';
            _pos += 2;
            if (!Take('{'))
            {
                throw new FormatException();
            }

            var end = pattern.IndexOf('}', _pos);
            if (end < 0 || CategoryMembers(pattern[_pos..end]) is null)
            {
                throw new FormatException();
            }

            set.Categories.Add((pattern[_pos..end], negated));
            _pos = end + 1;
        }

        private int CodePoint()
        {
            var c = pattern[_pos];
            if (char.IsHighSurrogate(c) && _pos + 1 < pattern.Length && char.IsLowSurrogate(pattern[_pos + 1]))
            {
                _pos += 2;
                return char.ConvertToUtf32(c, pattern[_pos - 1]);
            }

            if (char.IsSurrogate(c))
            {
                throw new FormatException();
            }

            _pos++;
            return c;
        }

        private void AppendLiteral(int codePoint)
        {
            if (codePoint > 0xFFFF)
            {
                // A pair, grouped so that a quantifier repeats the character.
                var text = char.ConvertFromUtf32(codePoint);
                _out.Append("(?:");
                AppendUnit(_out, text[0]);
                AppendUnit(_out, text[1]);
                _out.Append(')');
            }
            else
            {
                AppendUnit(_out, (char)codePoint);
            }
        }

        /// <summary>
        /// A character class: its BMP part as a .NET class without surrogates, and its other code points as the surrogate
        /// pairs that write them.
        /// </summary>
        private void Append(CodePointSet set, bool negated)
        {
            var bmp = new StringBuilder();
            var supplementary = new List<(int Low, int High)>();
            foreach (var (low, high) in set.Ranges)
            {
                if (low <= 0xFFFF)
                {
                    AppendUnit(bmp, (char)low);
                    if (high > low)
                    {
                        bmp.Append('-');
                        AppendUnit(bmp, (char)Math.Min(high, 0xFFFF));
                    }
                }

                if (high > 0xFFFF)
                {
                    supplementary.Add((Math.Max(low, 0x10000), high));
                }
            }

            foreach (var (name, categoryNegated) in set.Categories)
            {
                bmp.Append(categoryNegated ? @"\P{" : @"\p{").Append(name).Append('}');
                supplementary.AddRange(SupplementaryRanges(name, categoryNegated));
            }

            supplementary = Merge(supplementary);
            var alternatives = new List<string>();
            if (negated)
            {
                alternatives.Add($"[^{bmp}{Surrogates}]");
                alternatives.AddRange(Pairs(Complement(supplementary)));
            }
            else
            {
                if (bmp.Length > 0)
                {
                    alternatives.Add($"[{bmp}-[{Surrogates}]]");
                }

                alternatives.AddRange(Pairs(supplementary));
            }

            _out.Append("(?:").AppendJoin('|', alternatives.Count > 0 ? alternatives : [@"[^\u0000-\uFFFF]"]).Append(')');
        }

        private int Peek(int offset) => _pos + offset < pattern.Length ? pattern[_pos + offset] : -1;

        private bool Take(char c)
        {
            if (Peek(0) != c)
            {
                return false;
            }

            _pos++;
            return true;
        }
    }

    private sealed class CodePointSet
    {
        public List<(int Low, int High)> Ranges { get; } = [];

        public List<(string Name, bool Negated)> Categories { get; } = [];
    }

    private static void AppendUnit(StringBuilder builder, char c)
    {
        if (char.IsAsciiLetterOrDigit(c))
        {
            builder.Append(c);
        }
        else
        {
            builder.Append(@"\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The general categories in an I-Regexp category name (Lu, or L for all letters); null for another name.</summary>
    private static UnicodeCategory[]? CategoryMembers(string name) => name switch
    {
        "L" => [UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter],
        "Lu" => [UnicodeCategory.UppercaseLetter],
        "Ll" => [UnicodeCategory.LowercaseLetter],
        "Lt" => [UnicodeCategory.TitlecaseLetter],
        "Lm" => [UnicodeCategory.ModifierLetter],
        "Lo" => [UnicodeCategory.OtherLetter],
        "M" => [UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark],
        "Mn" => [UnicodeCategory.NonSpacingMark],
        "Mc" => [UnicodeCategory.SpacingCombiningMark],
        "Me" => [UnicodeCategory.EnclosingMark],
        "N" => [UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber],
        "Nd" => [UnicodeCategory.DecimalDigitNumber],
        "Nl" => [UnicodeCategory.LetterNumber],
        "No" => [UnicodeCategory.OtherNumber],
        "P" =>
        [
            UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation,
            UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation,
        ],
        "Pc" => [UnicodeCategory.ConnectorPunctuation],
        "Pd" => [UnicodeCategory.DashPunctuation],
        "Ps" => [UnicodeCategory.OpenPunctuation],
        "Pe" => [UnicodeCategory.ClosePunctuation],
        "Pi" => [UnicodeCategory.InitialQuotePunctuation],
        "Pf" => [UnicodeCategory.FinalQuotePunctuation],
        "Po" => [UnicodeCategory.OtherPunctuation],
        "Z" => [UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator],
        "Zs" => [UnicodeCategory.SpaceSeparator],
        "Zl" => [UnicodeCategory.LineSeparator],
        "Zp" => [UnicodeCategory.ParagraphSeparator],
        "S" => [UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol],
        "Sm" => [UnicodeCategory.MathSymbol],
        "Sc" => [UnicodeCategory.CurrencySymbol],
        "Sk" => [UnicodeCategory.ModifierSymbol],
        "So" => [UnicodeCategory.OtherSymbol],
        "C" => [UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned],
        "Cc" => [UnicodeCategory.Control],
        "Cf" => [UnicodeCategory.Format],
        "Co" => [UnicodeCategory.PrivateUse],
        "Cn" => [UnicodeCategory.OtherNotAssigned],
        _ => null,
    };

    /// <summary>The code points from U+10000 on in a category (or out of it), worked out once per category.</summary>
    private static List<(int Low, int High)> SupplementaryRanges(string category, bool negated) =>
        SupplementaryCategories.GetOrAdd((category, negated), static key =>
        {
            var members = new bool[32];
            foreach (var member in CategoryMembers(key.Category)!)
            {
                members[(int)member] = true;
            }

            var ranges = new List<(int Low, int High)>();
            var start = -1;
            for (var codePoint = 0x10000; codePoint <= 0x110000; codePoint++)
            {
                var inSet = codePoint <= 0x10FFFF && members[(int)Rune.GetUnicodeCategory(new Rune(codePoint))] != key.Negated;
                if (inSet && start < 0)
                {
                    start = codePoint;
                }
                else if (!inSet && start >= 0)
                {
                    ranges.Add((start, codePoint - 1));
                    start = -1;
                }
            }

            return ranges;
        });

    private static List<(int Low, int High)> Merge(List<(int Low, int High)> ranges)
    {
        ranges.Sort();
        var merged = new List<(int Low, int High)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Low <= merged[^1].High + 1)
            {
                merged[^1] = (merged[^1].Low, Math.Max(merged[^1].High, range.High));
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>The code points from U+10000 to U+10FFFF outside the (merged) ranges.</summary>
    private static List<(int Low, int High)> Complement(List<(int Low, int High)> ranges)
    {
        var complement = new List<(int Low, int High)>();
        var next = 0x10000;
        foreach (var (low, high) in ranges)
        {
            if (low > next)
            {
                complement.Add((next, low - 1));
            }

            next = high + 1;
        }

        if (next <= 0x10FFFF)
        {
            complement.Add((next, 0x10FFFF));
        }

        return complement;
    }

    /// <summary>
    /// The surrogate pairs that write code points of the (merged) ranges, grouped: for each run of high surrogates followed
    /// by the same low surrogates, one alternative.
    /// </summary>
    private static List<string> Pairs(List<(int Low, int High)> ranges)
    {
        var lows = new List<(int Low, int High)>?[0x400];
        foreach (var (low, high) in ranges)
        {
            for (var h = (low - 0x10000) >> 10; h <= (high - 0x10000) >> 10; h++)
            {
                var first = Math.Max(low, 0x10000 + (h << 10));
                var last = Math.Min(high, 0x10000 + (h << 10) + 0x3FF);
                (lows[h] ??= []).Add((0xDC00 + ((first - 0x10000) & 0x3FF), 0xDC00 + ((last - 0x10000) & 0x3FF)));
            }
        }

        var alternatives = new List<string>();
        for (var h = 0; h < lows.Length;)
        {
            if (lows[h] is not { } same)
            {
                h++;
                continue;
            }

            var end = h;
            while (end + 1 < lows.Length && lows[end + 1] is { } next && next.SequenceEqual(same))
            {
                end++;
            }

            var builder = new StringBuilder();
            AppendRange(builder, 0xD800 + h, 0xD800 + end);
            if (same.Count == 1)
            {
                AppendRange(builder, same[0].Low, same[0].High);
            }
            else
            {
                builder.Append('[');
                foreach (var (low, high) in same)
                {
                    AppendUnit(builder, (char)low);
                    if (high > low)
                    {
                        builder.Append('-');
                        AppendUnit(builder, (char)high);
                    }
                }

                builder.Append(']');
            }

            alternatives.Add(builder.ToString());
            h = end + 1;
        }

        return alternatives;
    }

    private static void AppendRange(StringBuilder builder, int low, int high)
    {
        if (low == high)
        {
            AppendUnit(builder, (char)low);
            return;
        }

        builder.Append('[');
        AppendUnit(builder, (char)low);
        builder.Append('-');
        AppendUnit(builder, (char)high);
        builder.Append(']');
    }
}
