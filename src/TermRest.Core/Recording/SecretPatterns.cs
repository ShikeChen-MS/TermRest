using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using TermRest.Core.Json;

namespace TermRest.Core.Recording;

/// <summary>
/// Secret values in the forms they take in text (see <see cref="Forms(string)"/>), found in one pass over the data however many
/// there are. A few are looked for one by one (that search is vectorized). Beyond that, each is indexed by an 8-byte
/// piece of it that few others share: a scan reads the 8 bytes at each position of the data once, a bit filter lets few
/// of them through, and only the patterns indexed by those are compared. The work for some data then hardly grows with
/// the number of values.
/// </summary>
internal sealed class SecretPatterns
{
    private const int PieceLength = 8;

    // Up to this many patterns, looking for each on its own is faster than a scan.
    private const int FewPatterns = 16;

    private const ulong Multiplier = 0x9E3779B97F4A7C15; // spreads pieces over the filter (Fibonacci hashing)
    private const int MinFilterBits = 12;
    private const int MaxFilterBits = 23;

    private readonly HashSet<byte[]> _seen = new(ContentComparer.Instance);
    private readonly List<byte[]> _patterns = [];

    // Shorter than a piece (secrets of 4 to 7 characters): always looked for one by one.
    private readonly List<byte[]> _short = [];

    // The patterns indexed by each piece, chained: the latest entry, then each entry's Next (-1 ends the chain).
    private readonly Dictionary<ulong, int> _latest = [];
    private readonly List<Entry> _entries = [];
    private ulong[] _filter = [];
    private int _filterBits;
    private int _indexed;

    public int Count => _patterns.Count;

    /// <summary>
    /// The forms <paramref name="value"/> may take in text, as UTF-8: as it is, URL-encoded, as a URL holds it, JSON-escaped
    /// (as .NET, JavaScript, Python, PHP and Go escape it), with "/" escaped as "\/".
    /// </summary>
    public static IEnumerable<byte[]> Forms(string value) => Forms(value, Encoding.UTF8);

    /// <summary>The forms <paramref name="value"/> may take in text (see <see cref="Forms(string)"/>), in <paramref name="encoding"/>.</summary>
    public static IEnumerable<byte[]> Forms(string value, Encoding encoding) =>
        ((string[])
        [
            value, Uri.EscapeDataString(value), WebUtility.UrlEncode(value), InUrl(value), JavaScriptEncoder.Default.Encode(value),
            MinimalJsonEncoder.Instance.Encode(value), value.Replace("/", "\\/", StringComparison.Ordinal),
            LowerHexJson(value, asciiOnly: true), LowerHexJson(value, asciiOnly: true, slash: true), LowerHexJson(value, asciiOnly: false),
        ]).Where(form => form.Length > 0).Distinct(StringComparer.Ordinal).Select(encoding.GetBytes);

    /// <summary>
    /// <paramref name="value"/> JSON-escaped as Python, PHP and Go write it, with lower-case hex digits. With
    /// <paramref name="asciiOnly"/>, as Python's json.dumps does by default (Flask, Django, httpbin, TermRest's Python
    /// scripts): every character outside printable ASCII is a \u escape of its UTF-16 code unit; PHP's json_encode does the
    /// same and also escapes "/" (<paramref name="slash"/>). Otherwise as Go's encoding/json does: besides what JSON
    /// requires, only &lt; &gt; &amp; U+2028 and U+2029 are escaped.
    /// </summary>
    private static string LowerHexJson(string value, bool asciiOnly, bool slash = false)
    {
        const string Hex = "0123456789abcdef";
        var text = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '/' when slash: text.Append("\\/"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < ' ' || (asciiOnly ? c > '~' : c is '<' or '>' or '&' or '\u2028' or '\u2029'))
                    {
                        text.Append("\\u").Append(Hex[c >> 12]).Append(Hex[(c >> 8) & 0xF]).Append(Hex[(c >> 4) & 0xF]).Append(Hex[c & 0xF]);
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> as <see cref="Uri"/> leaves it in a URL, and so as it is sent: only what a URL can't hold is
    /// escaped (spaces, control and non-ASCII characters, <c>"&lt;&gt;\^`{|}</c> and a % that starts no escape), so
    /// <c>P@ss^word</c> is <c>P@ss%5Eword</c>, which neither of the URL encodings gives. A '#' is escaped too, as TermRest
    /// sends one in a path or a query (see <see cref="Http.RequestPreparer"/>).
    /// </summary>
    internal static string InUrl(string value)
    {
        const string Hex = "0123456789ABCDEF";
        var text = new StringBuilder(value.Length + 8);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is > ' ' and < '\x7F' and not ('"' or '#' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}')
                && (c != '%' || (i + 2 < value.Length && char.IsAsciiHexDigit(value[i + 1]) && char.IsAsciiHexDigit(value[i + 2]))))
            {
                text.Append(c);
                continue;
            }

            Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var length); // a lone surrogate is U+FFFD
            i += length - 1;
            foreach (var b in utf8[..rune.EncodeToUtf8(utf8)])
            {
                text.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 0xF]);
            }
        }

        return text.ToString();
    }

    /// <summary>Adds <paramref name="pattern"/> unless it is empty or already there.</summary>
    public void Add(byte[] pattern)
    {
        if (pattern.Length > 0 && _seen.Add(pattern))
        {
            _patterns.Add(pattern);
        }
    }

    /// <summary>Adds where the patterns are in <paramref name="data"/>: unsorted, possibly overlapping.</summary>
    public void Find(ReadOnlySpan<byte> data, List<RedactedSpan> spans)
    {
        if (_patterns.Count <= FewPatterns)
        {
            foreach (var pattern in _patterns)
            {
                FindEach(data, pattern, spans);
            }

            return;
        }

        IndexNew();
        foreach (var pattern in _short)
        {
            FindEach(data, pattern, spans);
        }

        if (_latest.Count == 0)
        {
            return;
        }

        var filter = _filter;
        var shift = 64 - _filterBits;
        ulong? notIndexed = null; // runs of the same bytes (zeros, indentation) are looked up once
        ref var first = ref MemoryMarshal.GetReference(data);
        for (var i = 0; i <= data.Length - PieceLength; i++)
        {
            var piece = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, i));
            var bit = (piece * Multiplier) >> shift;
            if ((filter[(int)(bit >> 6)] & (1UL << (int)bit)) != 0 && piece != notIndexed && !Compare(data, i, piece, spans))
            {
                notIndexed = piece;
            }
        }
    }

    /// <summary>
    /// Compares the patterns indexed by <paramref name="piece"/>, which is at <paramref name="position"/> in
    /// <paramref name="data"/>; false when it indexes none.
    /// </summary>
    private bool Compare(ReadOnlySpan<byte> data, int position, ulong piece, List<RedactedSpan> spans)
    {
        if (!_latest.TryGetValue(piece, out var latest))
        {
            return false;
        }

        for (var e = latest; e >= 0; e = _entries[e].Next)
        {
            var (pattern, offset, _) = _entries[e];
            var start = position - offset;
            if (start >= 0 && data.Length - start >= pattern.Length && data.Slice(start, pattern.Length).SequenceEqual(pattern))
            {
                spans.Add(new RedactedSpan(start, start + pattern.Length, false));
            }
        }

        return true;
    }

    private static void FindEach(ReadOnlySpan<byte> data, byte[] pattern, List<RedactedSpan> spans)
    {
        for (var offset = 0; offset + pattern.Length <= data.Length;)
        {
            var index = data[offset..].IndexOf(pattern);
            if (index < 0)
            {
                break;
            }

            spans.Add(new RedactedSpan(offset + index, offset + index + pattern.Length, false));
            offset += index + pattern.Length;
        }
    }

    private static ulong Piece(byte[] pattern, int offset) => MemoryMarshal.Read<ulong>(pattern.AsSpan(offset));

    /// <summary>Indexes the patterns added since the last scan.</summary>
    private void IndexNew()
    {
        for (; _indexed < _patterns.Count; _indexed++)
        {
            var pattern = _patterns[_indexed];
            if (pattern.Length < PieceLength)
            {
                _short.Add(pattern);
                continue;
            }

            // The piece fewest patterns are indexed by, looked for from the end: secrets often share their start (eyJhbGciOi…).
            var best = 0;
            var fewest = int.MaxValue;
            for (var offset = pattern.Length - PieceLength; offset >= 0 && fewest > 0; offset--)
            {
                var count = 0;
                for (var e = _latest.GetValueOrDefault(Piece(pattern, offset), -1); e >= 0 && count < fewest; e = _entries[e].Next)
                {
                    count++;
                }

                if (count < fewest)
                {
                    (best, fewest) = (offset, count);
                }
            }

            var piece = Piece(pattern, best);
            _entries.Add(new Entry(pattern, best, _latest.GetValueOrDefault(piece, -1)));
            _latest[piece] = _entries.Count - 1;
            if (_filterBits < MaxFilterBits && (long)_latest.Count * 16 > 1L << _filterBits)
            {
                // At most one bit in 16 is set, so few positions that index nothing get past the filter.
                _filterBits = Math.Max(MinFilterBits, _filterBits + 1);
                _filter = new ulong[(1 << _filterBits) / 64];
                foreach (var key in _latest.Keys)
                {
                    Mark(key);
                }
            }

            Mark(piece);
        }
    }

    private void Mark(ulong piece)
    {
        var bit = (piece * Multiplier) >> (64 - _filterBits);
        _filter[(int)(bit >> 6)] |= 1UL << (int)bit;
    }

    private readonly record struct Entry(byte[] Pattern, int Offset, int Next);

    private sealed class ContentComparer : IEqualityComparer<byte[]>
    {
        public static readonly ContentComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
