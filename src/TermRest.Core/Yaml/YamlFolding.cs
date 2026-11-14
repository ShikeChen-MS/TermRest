using System.Text;
using System.Text.RegularExpressions;

namespace TermRest.Core.Yaml;

/// <summary>
/// Wraps ("folds") long values the way the <c>yaml</c> npm library that Postman uses for v3 files does. Plain and
/// quoted text breaks at single spaces; double-quoted text without one breaks at an escaped line break; multi-line text
/// with a long line becomes a folded (<c>&gt;</c>) block, unless folding cannot shorten that line. The details follow
/// the library, so wrapped output matches files written by the Postman versions that wrap at 80 columns. For example,
/// a value's first line is measured from the start of its key, and text that fits in one indented line is never broken.
/// </summary>
internal static partial class YamlFolding
{
    /// <summary>The library's default width; Postman versions that wrap long lines wrap at 80 columns.</summary>
    public const int PostmanLineWidth = 80;

    /// <summary>The library's minimum number of characters on a line (when the width allows it).</summary>
    private const int MinContentWidth = 20;

    /// <summary>Double-quoted text at least this long (quotes and escapes included) gets real line breaks for its <c>\n</c>s.</summary>
    private const int DoubleQuotedMinMultiLineLength = 40;

    private enum Mode
    {
        /// <summary>Plain and single-quoted scalars: break at spaces only.</summary>
        Flow,

        /// <summary>Folded block scalars: break at spaces, never inside more-indented lines.</summary>
        Block,

        /// <summary>Double-quoted scalars: break at spaces, else at an escaped line break.</summary>
        Quoted,
    }

    /// <summary>
    /// Wraps a plain or single-quoted scalar (<paramref name="text"/> as written, quotes included) written after a key
    /// (<paramref name="indentAtStart"/>: key length + 2) or a <c>- </c> (null). Continuation lines are indented by
    /// <paramref name="indent"/>. A width of 0 or less never wraps.
    /// </summary>
    public static string FoldFlow(string text, int indent, int? indentAtStart, int lineWidth) =>
        FoldLines(text, indent, Mode.Flow, indentAtStart, lineWidth, out _);

    /// <summary>
    /// Wraps a double-quoted scalar (<paramref name="quoted"/>: the single-line escaped form, quotes included). When
    /// wrapping, a long text's escaped line breaks become real ones, as the library writes them.
    /// </summary>
    public static string FoldDoubleQuoted(string quoted, int indent, int? indentAtStart, int lineWidth)
    {
        if (lineWidth <= 0)
        {
            return quoted;
        }

        var builder = new StringBuilder(quoted.Length + 16);
        var start = 0;
        var escapedLeadingSpace = -1;
        for (var i = 0; i < quoted.Length; i++)
        {
            var ch = quoted[i];
            if (ch == ' ' && At(quoted, i + 1) == '\\' && At(quoted, i + 2) == 'n')
            {
                // A space before a line break would be lost when the break is folded: escape it, unless it is a line's
                // only character, which is escaped already as its leading space (the library escapes it twice).
                builder.Append(quoted, start, i - start).Append(i == escapedLeadingSpace ? " " : "\\ ");
                i += 1;
                start = i;
                ch = '\\';
            }

            if (ch != '\\')
            {
                continue;
            }

            if (At(quoted, i + 1) != 'n' || At(quoted, i + 2) == '"' || quoted.Length < DoubleQuotedMinMultiLineLength)
            {
                i += 1; // keep the escape as it is
                continue;
            }

            // "\n" becomes an empty line (a single line break would fold into a space).
            builder.Append(quoted, start, i - start).Append("\n\n");
            while (At(quoted, i + 2) == '\\' && At(quoted, i + 3) == 'n' && At(quoted, i + 4) != '"')
            {
                builder.Append('\n');
                i += 2;
            }

            builder.Append(' ', indent);
            if (At(quoted, i + 2) == ' ')
            {
                builder.Append('\\'); // leading spaces of a continuation line would be dropped
                escapedLeadingSpace = i + 2;
            }

            i += 1;
            start = i + 1;
        }

        var text = start > 0 ? builder.Append(quoted, start, quoted.Length - start).ToString() : quoted;
        return FoldLines(text, indent, Mode.Quoted, indentAtStart, lineWidth, out _);
    }

    /// <summary>Whether some line of <paramref name="text"/> is longer than the width allows at <paramref name="indent"/>.</summary>
    public static bool LineLengthOverLimit(string text, int lineWidth, int indent)
    {
        if (lineWidth <= 0)
        {
            return false;
        }

        var limit = lineWidth - indent;
        if (text.Length <= limit)
        {
            return false;
        }

        for (int i = 0, start = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i - start > limit)
                {
                    return true;
                }

                start = i + 1;
                if (text.Length - start <= limit)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="text"/> (which must be writable as a block scalar) as a folded block: the <c>&gt;</c> header line and the
    /// content lines indented by <paramref name="indent"/>, without a final line break. Returns null for text that
    /// cannot be folded without changing it (its first line starts with white space, or a line of only white space
    /// follows a more-indented one), and, with <paramref name="literalWhenUnfoldable"/>, when a line cannot be broken to
    /// fit; a literal block is written then.
    /// </summary>
    public static string? FoldedBlock(string text, int indent, int lineWidth, bool literalWhenUnfoldable)
    {
        // The library would break such a first line like any other, but a line starting with white space keeps its
        // line breaks when read back.
        var firstLine = text.TrimStart('\n');
        if (firstLine.Length > 0 && firstLine[0] is ' ' or '\t')
        {
            return null;
        }

        var indentText = new string(' ', indent);
        var value = text;

        // Chomping from the white space at the end.
        var endStart = value.Length;
        while (endStart > 0 && value[endStart - 1] is '\n' or '\t' or ' ')
        {
            endStart--;
        }

        var end = value[endStart..];
        var endBreak = end.IndexOf('\n', StringComparison.Ordinal);
        var chomp = endBreak < 0 ? "-" : value.Length == end.Length || endBreak != end.Length - 1 ? "+" : "";
        if (end.Length > 0)
        {
            value = value[..endStart];
            if (end[^1] == '\n')
            {
                end = end[..^1];
            }

            end = InnerLineBreaks().Replace(end, m => m.Value + indentText);
        }

        // Indentation indicator from the white space at the start.
        var startsWithSpace = false;
        var startBreak = -1;
        var startEnd = 0;
        for (; startEnd < value.Length; startEnd++)
        {
            var ch = value[startEnd];
            if (ch == ' ')
            {
                startsWithSpace = true;
            }
            else if (ch == '\n')
            {
                startBreak = startEnd;
            }
            else
            {
                break;
            }
        }

        var start = value[..(startBreak + 1)];
        if (start.Length > 0)
        {
            value = value[start.Length..];
            start = LineBreaks().Replace(start, m => m.Value + indentText);
        }

        if (HasWhiteLineAfterMoreIndented(value))
        {
            return null;
        }

        var header = (startsWithSpace ? indent > 0 ? "2" : "1" : "") + chomp;

        // A single line break folds into a space, so every break is doubled, except around more-indented lines, which are
        // never folded.
        var folded = LineBreaks().Replace(value, m => "\n" + m.Value);
        folded = MoreIndentedLine().Replace(folded, m => m.Groups[1].Value + m.Groups[2].Value);
        folded = LineBreaks().Replace(folded, m => m.Value + indentText);

        var body = FoldLines(start + folded + end, indent, Mode.Block, indent, lineWidth, out var overflow);
        return overflow && literalWhenUnfoldable ? null : ">" + header + "\n" + indentText + body;
    }

    /// <summary>
    /// The library's line folding: breaks <paramref name="text"/> at the last suitable point before the width is reached.
    /// <paramref name="overflow"/> tells whether some line is still too long.
    /// </summary>
    private static string FoldLines(string text, int indent, Mode mode, int? indentAtStart, int lineWidth, out bool overflow)
    {
        overflow = false;
        if (lineWidth <= 0)
        {
            return text;
        }

        var minContentWidth = lineWidth < MinContentWidth ? 0 : MinContentWidth;
        var endStep = Math.Max(1 + minContentWidth, 1 + lineWidth - indent);
        if (text.Length <= endStep)
        {
            return text;
        }

        var folds = new List<int>();
        var escapedFolds = new HashSet<int>();
        var end = lineWidth - indent;
        if (indentAtStart is int atStart)
        {
            // The library also does this for blocks (indented deeper than the width allows), which adds an empty first
            // line to the value; a block's first line already has a line of its own.
            if (mode != Mode.Block && atStart > lineWidth - Math.Max(2, minContentWidth))
            {
                folds.Add(0); // the key is too long: the value starts on the next line
            }
            else
            {
                end = lineWidth - atStart;
            }
        }

        var split = -1;
        var prev = -1;
        var i = -1;
        var escapeStart = -1;
        var escapeEnd = -1;
        var escapeAt = mode == Mode.Quoted ? EscapeStarts(text) : null;
        if (mode == Mode.Block)
        {
            i = ConsumeMoreIndentedLines(text, i, indent);
            if (i != -1)
            {
                end = i + endStep;
            }
        }

        while (++i < text.Length)
        {
            int ch = text[i];
            if (mode == Mode.Quoted && ch == '\\')
            {
                escapeStart = i;
                i += At(text, i + 1) switch
                {
                    'x' => 3,
                    'u' => 5,
                    'U' => 9,
                    _ => 1,
                };
                escapeEnd = i;
            }

            if (ch == '\n')
            {
                if (mode == Mode.Block)
                {
                    i = ConsumeMoreIndentedLines(text, i, indent);
                }

                end = i + indent + endStep;
                split = -1;
            }
            else
            {
                // A space between two other characters can become a line break.
                if (ch == ' ' && prev >= 0 && prev is not (' ' or '\n' or '\t'))
                {
                    var next = At(text, i + 1);
                    if (next >= 0 && next is not (' ' or '\n' or '\t'))
                    {
                        split = i;
                    }
                }

                if (i >= end)
                {
                    if (split > 0)
                    {
                        folds.Add(split);
                        end = split + endStep;
                        split = -1;
                    }
                    else if (mode == Mode.Quoted)
                    {
                        // White space at the end may run past the width.
                        while (prev is ' ' or '\t')
                        {
                            prev = ch;
                            ch = At(text, ++i);
                            overflow = true;
                        }

                        // Break with an escaped line break, but not inside an escape sequence.
                        var j = i > escapeEnd + 1 ? i - 2 : escapeStart - 1;
                        if (j >= 0 && j + 1 < text.Length && escapeAt![j + 1] is var escape and >= 0 && escape <= j)
                        {
                            j = escape - 1; // after skipped white space the library can land inside one
                        }

                        if (j >= 1 && char.IsHighSurrogate(text[j]))
                        {
                            j--; // nor between the two halves of a character (an emoji): apart, neither can be written
                        }

                        if (j < 1 || (folds.Count > 0 && j <= folds[^1]))
                        {
                            // Only when the indentation leaves no room at all: keep the line long.
                            overflow = true;
                        }
                        else
                        {
                            if (!escapedFolds.Add(j))
                            {
                                return text; // the width is shorter than an escape sequence
                            }

                            folds.Add(j);
                            end = j + endStep;
                            split = -1;
                        }
                    }
                    else
                    {
                        overflow = true;
                    }
                }
            }

            prev = ch;
        }

        if (folds.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + (folds.Count * (indent + 2)));
        result.Append(text, 0, Math.Max(0, folds[0]));
        for (var k = 0; k < folds.Count; k++)
        {
            var fold = folds[k];
            var stop = k + 1 < folds.Count && folds[k + 1] != 0 ? folds[k + 1] : text.Length;
            if (fold == 0)
            {
                result.Clear().Append('\n').Append(' ', indent).Append(text, 0, stop);
            }
            else
            {
                if (mode == Mode.Quoted && escapedFolds.Contains(fold))
                {
                    result.Append(text[fold]).Append('\\');
                }

                result.Append('\n').Append(' ', indent).Append(text, fold + 1, Math.Max(0, stop - fold - 1));
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a line of only white space follows a more-indented line (empty lines between them or not). The library
    /// takes it for an empty line and keeps the doubled line break before it, which adds an empty line to the text.
    /// </summary>
    private static bool HasWhiteLineAfterMoreIndented(string text)
    {
        var moreIndented = false;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty)
            {
                continue;
            }

            if (moreIndented && line.TrimStart(" \t").IsEmpty)
            {
                return true;
            }

            moreIndented = line[0] is ' ' or '\t';
        }

        return false;
    }

    /// <summary>
    /// Skips the more-indented lines (white space after the indentation) that follow the line break at
    /// <paramref name="i"/>; returns the index of the last line break skipped.
    /// </summary>
    private static int ConsumeMoreIndentedLines(string text, int i, int indent)
    {
        var end = i;
        var start = i + 1;
        var ch = At(text, start);
        while (ch is ' ' or '\t')
        {
            if (i < start + indent)
            {
                ch = At(text, ++i);
            }
            else
            {
                do
                {
                    ch = At(text, ++i);
                }
                while (ch >= 0 && ch != '\n');

                end = i;
                start = i + 1;
                ch = At(text, start);
            }
        }

        return end;
    }

    /// <summary>For each character of a double-quoted scalar, the index of the escape sequence it belongs to (or -1).</summary>
    private static int[] EscapeStarts(string text)
    {
        var starts = new int[text.Length];
        Array.Fill(starts, -1);
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] != '\\')
            {
                continue;
            }

            var length = At(text, k + 1) switch
            {
                'x' => 4,
                'u' => 6,
                'U' => 10,
                _ => 2,
            };
            var stop = Math.Min(text.Length, k + length);
            for (var m = k; m < stop; m++)
            {
                starts[m] = k;
            }

            k = stop - 1;
        }

        return starts;
    }

    private static int At(string text, int index) => index >= 0 && index < text.Length ? text[index] : -1;

    [GeneratedRegex("\n+")]
    private static partial Regex LineBreaks();

    /// <summary>Line breaks followed by more (white space) content.</summary>
    [GeneratedRegex("\n+(?!\n|\\z)")]
    private static partial Regex InnerLineBreaks();

    /// <summary>A more-indented line with the empty lines after it, and the doubled line breaks around them.</summary>
    [GeneratedRegex("(?:^|\n)([\t ].*)(?:([\n\t ]*)\n(?![\n\t ]))?")]
    private static partial Regex MoreIndentedLine();
}
