using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TermRest.Core.Json;

namespace TermRest.Core.Http;

/// <summary>Turns response bytes into displayable text: charset decoding, JSON/XML pretty printing, hex dumps.</summary>
public static class BodyFormatter
{
    public static bool IsJson(string? contentType) =>
        contentType is not null && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase));

    public static bool IsXml(string? contentType) =>
        contentType is not null && (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase));

    public static bool IsHtml(string? contentType) =>
        contentType is not null && contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

    /// <summary>A short language label for the body (json, xml, html, text, binary).</summary>
    public static string Language(byte[] body, string? contentType) =>
        IsJson(contentType) ? "json" : IsHtml(contentType) ? "html" : IsXml(contentType) ? "xml" : LooksLikeText(body, contentType) ? "text" : "binary";

    public static bool LooksLikeText(byte[] body, string? contentType)
    {
        if (contentType is not null && (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || IsJson(contentType)
            || IsXml(contentType) || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var sample = body.AsSpan(0, Math.Min(body.Length, 8192));
        return !sample.Contains((byte)0);
    }

    public static string Decode(byte[] body, string? contentType)
    {
        var encoding = Encoding.UTF8;
        var charset = GetCharset(contentType);
        if (charset is not null)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        var text = encoding.GetString(body);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
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
                return XDocument.Parse(text, LoadOptions.None).ToString();
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

    private static string? GetCharset(string? contentType)
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
