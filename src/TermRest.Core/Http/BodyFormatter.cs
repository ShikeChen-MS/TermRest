using System.Buffers;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TermRest.Core.Json;

namespace TermRest.Core.Http;

/// <summary>Turns response bytes into displayable text: charset decoding, JSON/XML pretty printing, hex dumps.</summary>
public static class BodyFormatter
{
    /// <summary>What separates the words of a media subtype: "problem+json", "x-amz-json-1.1", "vnd.api+json".</summary>
    private static readonly SearchValues<char> WordSeparators = SearchValues.Create("+-.");

    /// <summary>
    /// Whether a Content-Type names JSON: a word of its subtype is json (application/json, application/problem+json,
    /// application/json-seq, application/x-amz-json-1.1), or it names NDJSON or JSON Lines.
    /// </summary>
    public static bool IsJson(string? contentType) => SubtypeHasWord(contentType, "json", "ndjson", "jsonl", "jsonlines");

    /// <summary>
    /// Whether a Content-Type names XML: a word of its subtype is xml (application/xml, image/svg+xml, application/atom+xml).
    /// Not an Office document (application/vnd.openxmlformats-officedocument.spreadsheetml.sheet …), which is a ZIP file.
    /// </summary>
    public static bool IsXml(string? contentType) => SubtypeHasWord(contentType, "xml");

    public static bool IsHtml(string? contentType) => SubtypeHasWord(contentType, "html", "xhtml");

    /// <summary>A short language label for the body (json, xml, html, javascript, text, binary).</summary>
    public static string Language(byte[] body, string? contentType) =>
        IsJson(contentType) ? "json"
        : IsHtml(contentType) ? "html"
        : IsXml(contentType) ? "xml"
        : SubtypeHasWord(contentType, "javascript", "ecmascript") ? "javascript"
        : LooksLikeText(body, contentType) ? "text" : "binary";

    /// <summary>Whether a body is shown as text: its Content-Type declares text, or no NUL byte is near its start.</summary>
    public static bool LooksLikeText(byte[] body, string? contentType) => IsTextType(contentType) || !HasNulNearStart(body);

    /// <summary>
    /// Whether a body is binary, to be passed on as the bytes received: its Content-Type declares a type that is not text
    /// (application/octet-stream, image/png, application/x-protobuf …), or it declares none and a NUL byte is near its
    /// start. Unlike <see cref="LooksLikeText"/>, which decides how a body is shown, it takes a declared type at its word:
    /// binary data need not have a NUL byte.
    /// </summary>
    public static bool IsBinary(byte[] body, string? contentType) =>
        !IsTextType(contentType) && (!string.IsNullOrWhiteSpace(contentType) || HasNulNearStart(body));

    /// <summary>
    /// Whether a Content-Type declares text: text/*, JSON, XML, JavaScript or form data. An Excel or Word file, whose type
    /// names the XML formats inside it, is binary.
    /// </summary>
    private static bool IsTextType(string? contentType) =>
        MediaType(contentType).StartsWith("text/", StringComparison.OrdinalIgnoreCase) || IsJson(contentType) || IsXml(contentType)
            || SubtypeHasWord(contentType, "javascript") || Subtype(contentType).Equals("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    /// <summary>The media type of a Content-Type, without its parameters: "application/json" in "application/json; charset=utf-8".</summary>
    private static ReadOnlySpan<char> MediaType(string? contentType)
    {
        var value = contentType.AsSpan();
        var semicolon = value.IndexOf(';');
        return (semicolon < 0 ? value : value[..semicolon]).Trim();
    }

    /// <summary>The subtype of a Content-Type's media type: "problem+json" in "application/problem+json; charset=utf-8".</summary>
    private static ReadOnlySpan<char> Subtype(string? contentType)
    {
        var media = MediaType(contentType);
        var slash = media.IndexOf('/');
        return slash < 0 ? [] : media[(slash + 1)..];
    }

    /// <summary>
    /// Whether one of the words of a Content-Type's subtype, between '+', '-' and '.', is one of <paramref name="words"/>:
    /// a word that only contains one (openxmlformats, spreadsheetml, ubjson) names another format.
    /// </summary>
    internal static bool SubtypeHasWord(string? contentType, params ReadOnlySpan<string> words)
    {
        var subtype = Subtype(contentType);
        foreach (var range in subtype.SplitAny(WordSeparators))
        {
            foreach (var word in words)
            {
                if (subtype[range].Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasNulNearStart(byte[] body) => body.AsSpan(0, Math.Min(body.Length, 8192)).Contains((byte)0);

    /// <summary>The body as text in the charset its Content-Type names (see <see cref="GetEncoding"/>), else in UTF-8.</summary>
    public static string Decode(byte[] body, string? contentType)
    {
        var encoding = GetCharset(contentType) is { } charset ? GetEncoding(charset) ?? Encoding.UTF8 : Encoding.UTF8;
        var text = InByteOrder(encoding, body).GetString(body);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// A UTF-16 encoding in the byte order <paramref name="body"/> is written in: the one its byte order mark says, else the
    /// one its text shows (an ASCII character's zero byte comes first in big-endian, last in little-endian), as RFC 2781 lets
    /// an unmarked "UTF-16" be either. Any other encoding, and a body that shows neither, as it is.
    /// </summary>
    internal static Encoding InByteOrder(Encoding encoding, ReadOnlySpan<byte> body)
    {
        if (encoding.CodePage is not (1200 or 1201))
        {
            return encoding;
        }

        if (body.StartsWith(Encoding.Unicode.Preamble) || body.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return body[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
        }

        var (zeroFirst, zeroLast) = (0, 0);
        var sample = body[..(Math.Min(body.Length, 4096) & ~1)];
        for (var i = 0; i < sample.Length; i += 2)
        {
            if (sample[i] == 0 && sample[i + 1] != 0)
            {
                zeroFirst++;
            }
            else if (sample[i] != 0 && sample[i + 1] == 0)
            {
                zeroLast++;
            }
        }

        return zeroFirst > zeroLast * 4 ? Encoding.BigEndianUnicode : zeroLast > zeroFirst * 4 ? Encoding.Unicode : encoding;
    }

    /// <summary>
    /// The encoding a charset names: one built into .NET (UTF-8, UTF-16, ISO-8859-1 …), else a legacy code page
    /// (windows-1252, Shift_JIS, GBK …). The code pages are not registered for the whole application (unless an XML body
    /// file declares one, see <see cref="BodyFile"/>), which would cost memory in every run: only a body in one of them
    /// loads its table. Null for a charset this platform does not know, or does not support (UTF-7).
    /// </summary>
    internal static Encoding? GetEncoding(string charset)
    {
        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(charset);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Pretty text for display; falls back to raw text, then to a hex dump for binary content.</summary>
    public static string Format(byte[] body, string? contentType, bool pretty = true)
    {
        if (body.Length == 0)
        {
            return "";
        }

        if (!LooksLikeText(body, contentType))
        {
            return HexDump(body);
        }

        var text = Decode(body, contentType);
        if (!pretty)
        {
            return text;
        }

        var trimmed = text.TrimStart();
        if (IsJson(contentType) || ((contentType is null || contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)) && trimmed.Length > 0 && trimmed[0] is '{' or '['))
        {
            return PostmanJson.TryPrettyPrint(text) ?? text;
        }

        if ((IsXml(contentType) && !IsHtml(contentType)) || trimmed.StartsWith("<?xml", StringComparison.Ordinal))
        {
            try
            {
                // A document type is not processed (its entities could expand a few hundred bytes into millions of
                // characters, on the UI thread): such a body is shown as received.
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
                return XDocument.Load(reader, LoadOptions.None).ToString();
            }
            catch (XmlException)
            {
                return text;
            }
        }

        return text;
    }

    public static string HexDump(byte[] body, int maxBytes = 4096)
    {
        var builder = new StringBuilder();
        var length = Math.Min(body.Length, maxBytes);
        for (var offset = 0; offset < length; offset += 16)
        {
            builder.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                builder.Append(offset + i < length ? body[offset + i].ToString("x2", CultureInfo.InvariantCulture) + " " : "   ");
            }

            builder.Append(' ');
            for (var i = 0; i < 16 && offset + i < length; i++)
            {
                var b = body[offset + i];
                builder.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            builder.Append('\n');
        }

        if (body.Length > maxBytes)
        {
            builder.Append(CultureInfo.InvariantCulture, $"… {body.Length - maxBytes} more bytes (binary)");
        }

        return builder.ToString();
    }

    /// <summary>The charset a Content-Type names, read leniently (as servers write it: with a trailing ";", say); null for none.</summary>
    internal static string? GetCharset(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        foreach (var part in contentType.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[8..].Trim('"', ' ');
            }
        }

        return null;
    }
}
