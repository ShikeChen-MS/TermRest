using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using TermRest.Core.Http;
using TermRest.Core.Json;

namespace TermRest.Core.Recording;

/// <summary>How a recorded body is written: as text (redacted with JSON or form rules where they apply) or as base64.</summary>
public enum BodyFormat
{
    Json,
    Form,
    Text,
    Binary,
}

/// <summary>Something removed from a recorded exchange: where, which header, parameter or field, why, and how often.</summary>
public sealed class RedactionNote
{
    /// <summary>The credential of an Authorization header (its scheme, such as Bearer, is kept).</summary>
    public const string CredentialReason = "credential";

    /// <summary>Cookie values (names and attributes are kept).</summary>
    public const string CookieReason = "cookie value";

    /// <summary>A header, parameter or field named like a credential (password, token, api key …).</summary>
    public const string NameReason = "secret name";

    /// <summary>A known secret: an auth credential, a secret variable, or a value already redacted by name.</summary>
    public const string ValueReason = "secret value";

    public const string UrlPasswordReason = "URL password";

    public required string Location { get; init; }

    public string? Name { get; init; }

    public required string Reason { get; init; }

    public int Count { get; set; } = 1;
}

/// <summary>A part of a body replaced by the placeholder: byte offsets, and whether it was a whole JSON value (quoted).</summary>
internal readonly record struct RedactedSpan(int Start, int End, bool Quoted);

/// <summary>
/// The secrets of one recording. Values are redacted by name: headers, URL parameters and body fields named like
/// credentials (see <see cref="SecretNames"/>), and all cookie values. They are also redacted by value: auth
/// credentials, secret variables, variables named like secrets, and every value already redacted by name are replaced
/// wherever they appear (URLs, headers, bodies, error messages), also in later requests of the recording.
/// </summary>
public sealed class Redactor
{
    public const string Placeholder = "[REDACTED]";

    /// <summary>Auth credentials and variables typed "secret" (as in the history).</summary>
    internal const int MinSecretLength = 4;

    /// <summary>Variables that are only named like a secret.</summary>
    internal const int MinNamedVariableLength = 6;

    /// <summary>Values redacted by name that are looked for everywhere afterwards (shorter ones would match ordinary words).</summary>
    internal const int MinLearnedLength = 8;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _learned = new(StringComparer.Ordinal);

    public Redactor(IEnumerable<string>? extraNames = null)
    {
        ExtraNames = SecretNames.NormalizeAll(extraNames);
    }

    /// <summary>Additional names to redact (normalized), from the workspace settings.</summary>
    public IReadOnlySet<string> ExtraNames { get; }

    /// <summary>Redaction for one exchange: its own secrets plus every value learned earlier in the recording.</summary>
    public ExchangeRedactor For(PreparedRequest request)
    {
        string[] learned;
        lock (_gate)
        {
            learned = [.. _learned];
        }

        return new ExchangeRedactor(this, request, learned);
    }

    internal void Learn(string value)
    {
        lock (_gate)
        {
            _learned.Add(value);
        }
    }
}

/// <summary>Redacts the parts of one exchange and notes what was removed (see <see cref="Redactor"/>).</summary>
public sealed class ExchangeRedactor
{
    internal static readonly byte[] PlaceholderBytes = Encoding.UTF8.GetBytes(Redactor.Placeholder);
    internal static readonly byte[] QuotedPlaceholderBytes = Encoding.UTF8.GetBytes($"\"{Redactor.Placeholder}\"");

    private readonly Redactor _recording;
    private readonly HashSet<string> _values = new(StringComparer.Ordinal);
    private byte[][]? _patterns;

    internal ExchangeRedactor(Redactor recording, PreparedRequest request, IEnumerable<string> learned)
    {
        _recording = recording;
        Add(request.SecretValues, Redactor.MinSecretLength);
        Add(request.SensitiveValues, Redactor.MinNamedVariableLength);
        Add(learned, Redactor.MinLearnedLength);
    }

    /// <summary>What was redacted so far.</summary>
    public List<RedactionNote> Notes { get; } = [];

    /// <summary>Number of values redacted so far.</summary>
    public int Count => Notes.Sum(n => n.Count);

    /// <summary>UTF-8 forms of the known secret values as they may appear in text: plain, URL-encoded, JSON-escaped; longest first.</summary>
    private byte[][] Patterns => _patterns ??= BuildPatterns();

    public bool IsSecretName(string? name, NameKind kind) => SecretNames.IsSecret(name, kind, _recording.ExtraNames);

    /// <summary>Replaces the known secret values in <paramref name="text"/>.</summary>
    public string Text(string? text, string location, string? name = null)
    {
        if (string.IsNullOrEmpty(text) || Patterns.Length == 0)
        {
            return text ?? "";
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var spans = new List<RedactedSpan>();
        AddValueSpans(bytes, spans);
        if (spans.Count == 0)
        {
            return text;
        }

        var merged = Merge(spans);
        Note(location, name, RedactionNote.ValueReason, merged.Count);
        return Encoding.UTF8.GetString(Apply(bytes, merged));
    }

    /// <summary>Redacts a URL: a password in the user info, parameters named like secrets (query and fragment), and known secret values.</summary>
    public string Url(string? url, string location)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url ?? "";
        }

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        var builder = new StringBuilder(url.Length + 16);
        var authority = url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        var colon = at < 0 ? -1 : authority.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && colon < at - 1)
        {
            // https://user:password@host keeps the user name.
            Learn(Decode(authority[(colon + 1)..at]));
            builder.Append(url, 0, authorityStart + colon + 1).Append(Redactor.Placeholder).Append(authority, at, authority.Length - at);
            Note(location, null, RedactionNote.UrlPasswordReason);
        }
        else
        {
            builder.Append(url, 0, authorityEnd);
        }

        var rest = url[authorityEnd..];
        var fragment = rest.IndexOf('#', StringComparison.Ordinal);
        var query = rest.IndexOf('?', StringComparison.Ordinal);
        if (fragment >= 0 && query > fragment)
        {
            query = -1; // a '?' after '#' is part of the fragment
        }

        var pathEnd = query >= 0 ? query : fragment >= 0 ? fragment : rest.Length;
        builder.Append(rest, 0, pathEnd);
        if (query >= 0)
        {
            var queryEnd = fragment >= 0 ? fragment : rest.Length;
            builder.Append('?').Append(Parameters(rest[(query + 1)..queryEnd], location));
        }

        if (fragment >= 0)
        {
            // OAuth implicit flows return tokens in the fragment: #access_token=…&token_type=Bearer
            var text = rest[(fragment + 1)..];
            builder.Append('#').Append(text.Contains('=', StringComparison.Ordinal) ? Parameters(text, location) : text);
        }

        return Text(builder.ToString(), location);
    }

    /// <summary>
    /// Redacts a header value: credentials keep their scheme (<c>Bearer [REDACTED]</c>), cookies keep their names and
    /// attributes, URL headers (Location …) keep everything but secrets, and headers named like secrets are replaced.
    /// </summary>
    public string Header(string name, string? value, string location)
    {
        value ??= "";
        switch (SecretNames.Normalize(name))
        {
            case "authorization" or "proxyauthorization":
                return Credential(name, value, location);
            case "cookie":
                return Cookies(value, location, name);
            case "setcookie":
                return SetCookie(value, location, name);
            case "location" or "contentlocation" or "referer":
                return Url(value, location);
        }

        if (value.Length > 0 && IsSecretName(name, NameKind.Header))
        {
            Learn(value);
            Note(location, name, RedactionNote.NameReason);
            return Redactor.Placeholder;
        }

        return Text(value, location, name);
    }

    /// <summary>
    /// Parts of a body to replace: JSON values and form fields named like secrets (all values inside a secret JSON
    /// object or array), then known secret values anywhere. The result is sorted and does not overlap.
    /// </summary>
    internal List<RedactedSpan> BodySpans(ReadOnlySpan<byte> body, BodyFormat format, string location)
    {
        var structural = new List<RedactedSpan>();
        if (format == BodyFormat.Json)
        {
            JsonSpans(body, structural, location);
        }
        else if (format == BodyFormat.Form)
        {
            FormSpans(body, structural, location);
        }

        var values = new List<RedactedSpan>();
        AddValueSpans(body, values);
        var outside = Subtract(Merge(values), structural);
        if (outside.Count > 0)
        {
            Note(location, null, RedactionNote.ValueReason, outside.Count);
        }

        if (structural.Count == 0)
        {
            return outside;
        }

        structural.AddRange(outside);
        structural.Sort((a, b) => a.Start.CompareTo(b.Start));
        return structural;
    }

    /// <summary>Records a redaction made by the caller (for example a form-data field named like a secret).</summary>
    internal string RedactNamed(string name, string value, string location)
    {
        if (value.Length == 0)
        {
            return value;
        }

        Learn(value);
        Note(location, name, RedactionNote.NameReason);
        return Redactor.Placeholder;
    }

    /// <summary><paramref name="data"/> with every span replaced by the placeholder.</summary>
    internal static byte[] Apply(ReadOnlySpan<byte> data, IReadOnlyList<RedactedSpan> spans)
    {
        using var output = new MemoryStream(data.Length + (spans.Count * 12));
        var position = 0;
        foreach (var span in spans)
        {
            output.Write(data[position..span.Start]);
            output.Write(span.Quoted ? QuotedPlaceholderBytes : PlaceholderBytes);
            position = span.End;
        }

        output.Write(data[position..]);
        return output.ToArray();
    }

    /// <summary>Detects how a body is written: JSON, form and text bodies as text (UTF-8), anything else as base64.</summary>
    public static BodyFormat DetectFormat(string? contentType, ReadOnlySpan<byte> body)
    {
        var media = MediaType(contentType);
        var text = Utf8.IsValid(body) && !body[..Math.Min(body.Length, 8192)].Contains((byte)0);
        if (!text)
        {
            return BodyFormat.Binary;
        }

        if (media.Contains("json", StringComparison.Ordinal))
        {
            return BodyFormat.Json;
        }

        if (media == "application/x-www-form-urlencoded")
        {
            return BodyFormat.Form;
        }

        if (media.Length == 0 || media == "application/octet-stream")
        {
            // No (useful) content type: JSON if it looks like JSON, otherwise readable text stays text.
            var start = body.TrimStart(" \t\r\n"u8);
            return start.Length > 0 && start[0] is (byte)'{' or (byte)'[' ? BodyFormat.Json : BodyFormat.Text;
        }

        return IsTextual(media) ? BodyFormat.Text : BodyFormat.Binary;
    }

    internal static bool IsTextual(string media) =>
        media.StartsWith("text/", StringComparison.Ordinal)
        || media.Contains("json", StringComparison.Ordinal) || media.Contains("xml", StringComparison.Ordinal)
        || media.Contains("javascript", StringComparison.Ordinal) || media.Contains("ecmascript", StringComparison.Ordinal)
        || media.Contains("yaml", StringComparison.Ordinal) || media.Contains("graphql", StringComparison.Ordinal)
        || media.Contains("csv", StringComparison.Ordinal) || media.Contains("html", StringComparison.Ordinal)
        || media is "application/x-www-form-urlencoded" or "application/x-sh" or "application/sql";

    /// <summary>The media type of a Content-Type value, lowercase, without parameters.</summary>
    internal static string MediaType(string? contentType)
    {
        var value = contentType ?? "";
        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        return (semicolon < 0 ? value : value[..semicolon]).Trim().ToLowerInvariant();
    }

    internal static List<RedactedSpan> Merge(List<RedactedSpan> spans)
    {
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var merged = new List<RedactedSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, span.End) };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>The parts of <paramref name="spans"/> that <paramref name="covered"/> does not cover (both sorted, not overlapping).</summary>
    private static List<RedactedSpan> Subtract(List<RedactedSpan> spans, List<RedactedSpan> covered)
    {
        if (covered.Count == 0)
        {
            return spans;
        }

        var result = new List<RedactedSpan>();
        var first = 0;
        foreach (var span in spans)
        {
            var start = span.Start;
            while (first < covered.Count && covered[first].End <= start)
            {
                first++;
            }

            for (var i = first; i < covered.Count && covered[i].Start < span.End; i++)
            {
                if (covered[i].Start > start)
                {
                    result.Add(new RedactedSpan(start, covered[i].Start, false));
                }

                start = Math.Max(start, covered[i].End);
            }

            if (start < span.End)
            {
                result.Add(new RedactedSpan(start, span.End, false));
            }
        }

        return result;
    }

    private string Credential(string name, string value, string location)
    {
        if (value.Length == 0)
        {
            return value;
        }

        // "Bearer <token>", "Basic <base64>", "AWS4-HMAC-SHA256 Credential=…": the scheme says what kind of credential was sent.
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        var scheme = space is > 0 and <= 32 && value[..space].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? value[..space] : null;
        Learn(scheme is null ? value : value[(space + 1)..]);
        Note(location, name, RedactionNote.CredentialReason);
        return scheme is null ? Redactor.Placeholder : $"{scheme} {Redactor.Placeholder}";
    }

    private string Cookies(string value, string location, string headerName)
    {
        var parts = value.Split(';');
        var count = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || parts[i][(equals + 1)..].Trim().Length == 0)
            {
                continue;
            }

            Learn(parts[i][(equals + 1)..]);
            parts[i] = parts[i][..(equals + 1)] + Redactor.Placeholder;
            count++;
        }

        if (count > 0)
        {
            Note(location, headerName, RedactionNote.CookieReason, count);
        }

        return string.Join(';', parts);
    }

    private string SetCookie(string value, string location, string headerName)
    {
        var end = value.IndexOf(';', StringComparison.Ordinal);
        var pair = end < 0 ? value : value[..end];
        var equals = pair.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0 || pair[(equals + 1)..].Trim().Length == 0)
        {
            return Text(value, location, headerName);
        }

        Learn(pair[(equals + 1)..]);
        Note(location, headerName, RedactionNote.CookieReason);
        return pair[..(equals + 1)] + Redactor.Placeholder + (end < 0 ? "" : Text(value[end..], location, headerName));
    }

    private string Parameters(string text, string location)
    {
        var parts = text.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == parts[i].Length - 1)
            {
                continue;
            }

            var name = Decode(parts[i][..equals]);
            if (IsSecretName(name, NameKind.QueryParameter))
            {
                Learn(Decode(parts[i][(equals + 1)..]));
                parts[i] = parts[i][..(equals + 1)] + Redactor.Placeholder;
                Note(location, name, RedactionNote.NameReason);
            }
        }

        return string.Join('&', parts);
    }

    private void JsonSpans(ReadOnlySpan<byte> body, List<RedactedSpan> spans, string location)
    {
        var offset = body.StartsWith("\xEF\xBB\xBF"u8) ? 3 : 0;
        var reader = new Utf8JsonReader(body[offset..], new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            AllowMultipleValues = true, // JSON Lines responses
            MaxDepth = 512,
        });

        // For each open object or array: whether everything in it is secret, and the name it has (for notes).
        var containers = new List<(bool Secret, string? Name)>();
        string? property = null;
        var propertySecret = false;
        try
        {
            while (reader.Read())
            {
                var parent = containers.Count > 0 ? containers[^1] : (Secret: false, Name: null);
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        property = reader.GetString();
                        propertySecret = parent.Secret || IsSecretName(property, NameKind.Field);
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        containers.Add(property is null ? parent : (propertySecret, property));
                        property = null;
                        propertySecret = false;
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        if (containers.Count > 0)
                        {
                            containers.RemoveAt(containers.Count - 1);
                        }

                        property = null;
                        propertySecret = false;
                        break;
                    case JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False:
                        var secret = property is null ? parent.Secret : propertySecret;
                        if (secret && !(reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length == 0))
                        {
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                Learn(reader.GetString() ?? "");
                            }

                            spans.Add(new RedactedSpan(offset + (int)reader.TokenStartIndex, offset + (int)reader.BytesConsumed, true));
                            Note(location, parent.Secret ? parent.Name : property, RedactionNote.NameReason);
                        }

                        property = null;
                        propertySecret = false;
                        break;
                    default:
                        property = null;
                        propertySecret = false;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not (entirely) JSON: the values found before the problem are still exact.
        }
    }

    private void FormSpans(ReadOnlySpan<byte> body, List<RedactedSpan> spans, string location)
    {
        var start = 0;
        while (start < body.Length)
        {
            var length = body[start..].IndexOf((byte)'&');
            var end = length < 0 ? body.Length : start + length;
            var pair = body[start..end];
            var equals = pair.IndexOf((byte)'=');
            if (equals > 0 && equals < pair.Length - 1)
            {
                var name = WebUtility.UrlDecode(Encoding.UTF8.GetString(pair[..equals]));
                if (IsSecretName(name, NameKind.Field))
                {
                    Learn(WebUtility.UrlDecode(Encoding.UTF8.GetString(pair[(equals + 1)..])));
                    spans.Add(new RedactedSpan(start + equals + 1, end, false));
                    Note(location, name, RedactionNote.NameReason);
                }
            }

            start = end + 1;
        }
    }

    private void AddValueSpans(ReadOnlySpan<byte> data, List<RedactedSpan> spans)
    {
        foreach (var pattern in Patterns)
        {
            var offset = 0;
            while (offset + pattern.Length <= data.Length)
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
    }

    private byte[][] BuildPatterns()
    {
        var variants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in _values)
        {
            variants.Add(value);
            variants.Add(Uri.EscapeDataString(value));
            variants.Add(WebUtility.UrlEncode(value));
            variants.Add(JavaScriptEncoder.Default.Encode(value));
            variants.Add(MinimalJsonEncoder.Instance.Encode(value));
            if (value.Contains('/', StringComparison.Ordinal))
            {
                variants.Add(value.Replace("/", "\\/", StringComparison.Ordinal));
            }
        }

        return [.. variants.Where(v => v.Length > 0).OrderByDescending(v => v.Length).Select(Encoding.UTF8.GetBytes)];
    }

    private void Add(IEnumerable<string> values, int minLength)
    {
        foreach (var value in values)
        {
            if (value.Length >= minLength && !string.IsNullOrWhiteSpace(value) && _values.Add(value))
            {
                _patterns = null;
            }
        }
    }

    /// <summary>A value redacted by name is also redacted wherever it appears later (in this exchange and the rest of the recording).</summary>
    private void Learn(string value)
    {
        value = value.Trim();
        if (value.Length < Redactor.MinLearnedLength || value.Contains(Redactor.Placeholder, StringComparison.Ordinal))
        {
            return;
        }

        if (_values.Add(value))
        {
            _patterns = null;
        }

        _recording.Learn(value);
    }

    private void Note(string location, string? name, string reason, int count = 1)
    {
        var existing = Notes.FirstOrDefault(n => n.Location == location && n.Name == name && n.Reason == reason);
        if (existing is not null)
        {
            existing.Count += count;
        }
        else
        {
            Notes.Add(new RedactionNote { Location = location, Name = name, Reason = reason, Count = count });
        }
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
