using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using TermRest.Core.Http;

namespace TermRest.Core.Recording;

/// <summary>How a recorded body is written: as text (redacted with JSON, form or form-data rules where they apply) or as base64.</summary>
public enum BodyFormat
{
    Json,
    Form,

    /// <summary>multipart/form-data that is entirely UTF-8 (one with binary files is <see cref="Binary"/>).</summary>
    Multipart,
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

    /// <summary>The user name of a URL without a password: it is then the credential (an API key, as Stripe takes it).</summary>
    public const string UrlUserReason = "URL user name";

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
/// wherever they appear (URLs, headers, bodies, error messages), also in later requests of the recording (a cookie's
/// value until the cookie is set again). The values are looked for in one pass however many there are (see
/// <see cref="SecretPatterns"/>), so the work for a request does not grow with the length of the recording.
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

    /// <summary>The forms of every value redacted by name so far (encoded once, as they are learned).</summary>
    private readonly SecretPatterns _learned = new();

    /// <summary>The forms of each cookie's latest value.</summary>
    private readonly Dictionary<(string Host, string Name), byte[][]> _cookies = [];

    /// <summary>The normalized extra names: replaced, never changed, when names are added (see <see cref="AddExtraNames"/>).</summary>
    private HashSet<string> _extraNames;

    public Redactor(IEnumerable<string>? extraNames = null)
    {
        _extraNames = SecretNames.NormalizeAll(extraNames);
    }

    /// <summary>Additional names to redact (normalized), from the workspace settings.</summary>
    public IReadOnlySet<string> ExtraNames
    {
        get
        {
            lock (_gate)
            {
                return _extraNames;
            }
        }
    }

    /// <summary>Whether a value was redacted by name so far (else <see cref="FindLearned"/> finds nothing).</summary>
    internal bool HasLearned
    {
        get
        {
            lock (_gate)
            {
                return _learned.Count > 0;
            }
        }
    }

    /// <summary>Redaction for one exchange: its own secrets plus every value learned in the recording (earlier, and while it runs).</summary>
    public ExchangeRedactor For(PreparedRequest request)
    {
        byte[][] cookies;
        HashSet<string> extraNames;
        lock (_gate)
        {
            cookies = [.. _cookies.Values.SelectMany(forms => forms)];
            extraNames = _extraNames;
        }

        return new ExchangeRedactor(this, request, cookies, extraNames);
    }

    /// <summary>
    /// Adds extra names to redact, as when the workspace settings change while recording: the exchanges redacted from now
    /// on (see <see cref="For(PreparedRequest)"/>) redact them too. Names are never removed.
    /// </summary>
    internal void AddExtraNames(IEnumerable<string> names)
    {
        var extended = SecretNames.NormalizeAll(names);
        lock (_gate)
        {
            // A new set: an exchange being redacted keeps the one it started with.
            extended.UnionWith(_extraNames);
            _extraNames = extended;
        }
    }

    /// <summary>
    /// Redaction for a report about <paramref name="exchange"/> that is not a recording, such as the collection runner's
    /// JSON report: <see cref="For(PreparedRequest)"/>, once it has learned what a recording learns while it writes the
    /// exchange, in the same order (see <see cref="HarEntryWriter.Write"/>): credentials, cookies, and the values of
    /// headers, URL parameters and body fields named like secrets. The report's text (test names and errors …) then
    /// loses them as the recording does, and so do the reports of the exchanges that follow. The exchange is not kept.
    /// </summary>
    public ExchangeRedactor ForReport(HttpExchange exchange)
    {
        const string PostData = "request.postData";
        var request = exchange.Request;
        var redactor = For(request);
        var headers = exchange.SentHeaders.Count > 0 ? exchange.SentHeaders : request.Headers;
        foreach (var (name, value) in headers)
        {
            redactor.Header(name, value, "request.headers");
        }

        var body = request.Body;
        switch (body.Kind)
        {
            case PreparedBodyKind.Text:
            {
                var text = Encoding.UTF8.GetBytes(body.Text ?? "");
                var type = headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? body.ContentType;
                redactor.BodySpans(text, ExchangeRedactor.DetectFormat(type, text), PostData);
                break;
            }

            case PreparedBodyKind.FormUrlEncoded:
                redactor.BodySpans(Encoding.UTF8.GetBytes(PreparedBody.EncodeForm(body.Fields)), BodyFormat.Form, PostData);
                break;
            case PreparedBodyKind.Multipart:
                foreach (var part in body.Parts.Where(p => p.FilePath is null && redactor.IsSecretName(p.Name, NameKind.Field)))
                {
                    redactor.RedactNamed(part.Name, part.Value ?? "", PostData);
                }

                break;
        }

        redactor.Url(request.Uri?.AbsoluteUri ?? request.Url, "request.url");
        if (exchange.Response is { } response)
        {
            foreach (var (name, value) in response.Headers)
            {
                redactor.Header(name, value, "response.headers");
            }

            if (response.Body.Length > 0)
            {
                var (data, format) = HarEntryWriter.Classify(response.Body, response.ContentType);
                redactor.BodySpans(data, format, "response.content");
            }

            if (response.FinalUri is { } final && request.Uri is { } requested && final != requested)
            {
                redactor.Url(final.AbsoluteUri, "_termrest.finalUrl");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Proxy))
        {
            redactor.Url(request.Proxy, "_termrest.settings");
        }

        return redactor;
    }

    internal void Learn(string value)
    {
        var forms = SecretPatterns.Forms(value).ToList();
        lock (_gate)
        {
            foreach (var form in forms)
            {
                _learned.Add(form);
            }
        }
    }

    /// <summary>
    /// Learns the value of a cookie of <paramref name="host"/> (its <paramref name="forms"/>). A cookie that is set again
    /// replaces its earlier value, which is no longer looked for: load balancer and session cookies (AWSALB, XSRF-TOKEN …)
    /// change with every response. Cookie values in Cookie and Set-Cookie headers are always redacted.
    /// </summary>
    internal void LearnCookie(string host, string name, byte[][] forms)
    {
        lock (_gate)
        {
            _cookies[(host, name)] = forms;
        }
    }

    /// <summary>Adds where the values redacted by name so far are in <paramref name="data"/>.</summary>
    internal void FindLearned(ReadOnlySpan<byte> data, List<RedactedSpan> spans)
    {
        lock (_gate)
        {
            _learned.Find(data, spans);
        }
    }
}

/// <summary>Redacts the parts of one exchange and notes what was removed (see <see cref="Redactor"/>).</summary>
public sealed partial class ExchangeRedactor
{
    internal static readonly byte[] PlaceholderBytes = Encoding.UTF8.GetBytes(Redactor.Placeholder);
    internal static readonly byte[] QuotedPlaceholderBytes = Encoding.UTF8.GetBytes($"\"{Redactor.Placeholder}\"");

    // The bytes EF BB BF: a "\xEF\xBB\xBF"u8 literal would be the UTF-8 of U+00EF U+00BB U+00BF, six other bytes.
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private readonly Redactor _recording;
    private readonly string _host;

    /// <summary>The recording's extra names when this exchange started to be redacted.</summary>
    private readonly IReadOnlySet<string> _extraNames;

    /// <summary>The forms of this exchange's secrets and of cookie values (values redacted by name are the recording's).</summary>
    private readonly SecretPatterns _patterns = new();

    internal ExchangeRedactor(Redactor recording, PreparedRequest request, IEnumerable<byte[]> cookies, IReadOnlySet<string> extraNames)
    {
        _recording = recording;
        _host = request.Uri?.Host ?? "";
        _extraNames = extraNames;
        Add(request.SecretValues, Redactor.MinSecretLength);
        Add(request.SensitiveValues, Redactor.MinNamedVariableLength);
        foreach (var form in cookies)
        {
            _patterns.Add(form);
        }
    }

    /// <summary>What was redacted so far.</summary>
    public List<RedactionNote> Notes { get; } = [];

    /// <summary>Number of values redacted so far.</summary>
    public int Count => Notes.Sum(n => n.Count);

    public bool IsSecretName(string? name, NameKind kind) => SecretNames.IsSecret(name, kind, _extraNames);

    /// <summary>Replaces the known secret values in <paramref name="text"/>.</summary>
    public string Text(string? text, string location, string? name = null)
    {
        if (string.IsNullOrEmpty(text) || (_patterns.Count == 0 && !_recording.HasLearned))
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

    /// <summary>
    /// Redacts a URL: the password of its user info, or the user name when it has no password (it is then the credential),
    /// parameters named like secrets (query and fragment), and known secret values.
    /// </summary>
    public string Url(string? url, string location)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url ?? "";
        }

        var schemeEnd = SchemeEnd(url);
        var authorityStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        var builder = new StringBuilder(url.Length + 16);
        var authority = url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        var colon = at < 0 ? -1 : authority.IndexOf(':', 0, at);
        if (colon >= 0 && colon < at - 1)
        {
            // https://user:password@host keeps the user name.
            Learn(Decode(authority[(colon + 1)..at]));
            builder.Append(url, 0, authorityStart + colon + 1).Append(Redactor.Placeholder).Append(authority, at, authority.Length - at);
            Note(location, null, RedactionNote.UrlPasswordReason);
        }
        else if (at > 0 && colon != 0)
        {
            // https://user@host and https://user:@host send the user name as the credential, with an empty password (as curl
            // and Postman do): an API key, as Stripe takes it.
            var userEnd = colon < 0 ? at : colon;
            Learn(Decode(authority[..userEnd]));
            builder.Append(url, 0, authorityStart).Append(Redactor.Placeholder).Append(authority, userEnd, authority.Length - userEnd);
            Note(location, null, RedactionNote.UrlUserReason);
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
    /// Parts of a body to replace: JSON values, form fields and form-data parts named like secrets (all values inside a
    /// secret JSON object or array), then known secret values anywhere. The result is sorted and does not overlap.
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
        else if (format is BodyFormat.Multipart or BodyFormat.Binary)
        {
            // Form-data with binary files is Binary (written as base64): the bytes of its secret parts are overwritten.
            MultipartSpans(body, structural, location);
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

    /// <summary>
    /// Detects how a body is written: JSON, form, form-data and text bodies as text (UTF-8), anything else as base64.
    /// Bodies labelled as JSON, form or text stay text even where they are not UTF-8 (a body cut at the size limit inside
    /// a character, a stray Latin-1 byte): such bytes are written as U+FFFD, and the JSON and form rules still apply.
    /// Form-data is text only where it is UTF-8, as its files may be binary.
    /// </summary>
    public static BodyFormat DetectFormat(string? contentType, ReadOnlySpan<byte> body)
    {
        var media = MediaType(contentType);
        var unlabelled = media.Length == 0 || media == "application/octet-stream";
        var multipart = media == "multipart/form-data";
        if (body[..Math.Min(body.Length, 8192)].Contains((byte)0) || (unlabelled || multipart ? !IsUtf8(body) : !IsTextual(media)))
        {
            return BodyFormat.Binary;
        }

        if (multipart)
        {
            return BodyFormat.Multipart;
        }

        if (Http.BodyFormatter.IsJson(media))
        {
            return BodyFormat.Json;
        }

        if (media == "application/x-www-form-urlencoded")
        {
            return BodyFormat.Form;
        }

        // JSON also comes without a (useful) content type, or as text/plain or text/html: it is recognized by its start, after
        // a UTF-8 byte order mark (a PHP file saved with one puts it before what it echoes).
        var start = (body.StartsWith(Utf8Bom) ? body[Utf8Bom.Length..] : body).TrimStart(" \t\r\n"u8);
        return (unlabelled || media.StartsWith("text/", StringComparison.Ordinal)) && start.Length > 0 && start[0] is (byte)'{' or (byte)'['
            ? BodyFormat.Json
            : BodyFormat.Text;
    }

    /// <summary>
    /// Whether a media type is text: text/*, form data, or a word of its subtype names a text format, as the body is shown
    /// (application/problem+json, image/svg+xml; not application/ubjson or an Office document's openxmlformats).
    /// </summary>
    internal static bool IsTextual(string media) =>
        media.StartsWith("text/", StringComparison.Ordinal)
        || Http.BodyFormatter.SubtypeHasWord(media, "json", "ndjson", "jsonl", "jsonlines", "xml", "javascript", "ecmascript", "yaml", "graphql", "csv", "html", "xhtml")
        || media is "application/x-www-form-urlencoded" or "application/x-sh" or "application/sql";

    /// <summary>The media type of a Content-Type value, lowercase, without parameters.</summary>
    internal static string MediaType(string? contentType)
    {
        var value = contentType ?? "";
        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        return (semicolon < 0 ? value : value[..semicolon]).Trim().ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="body"/> is UTF-8, allowing a character cut off at the end (a body cut at the size limit).</summary>
    private static bool IsUtf8(ReadOnlySpan<byte> body)
    {
        if (Utf8.IsValid(body))
        {
            return true;
        }

        // The first byte of the last character says how many bytes it has.
        var lead = body.Length - 1;
        while (lead > 0 && lead > body.Length - 4 && (body[lead] & 0xC0) == 0x80)
        {
            lead--;
        }

        var length = body[lead] switch { >= 0xF0 => 4, >= 0xE0 => 3, >= 0xC0 => 2, _ => 1 };
        return body.Length - lead < length && Utf8.IsValid(body[..lead]);
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
        var credential = scheme is null ? value : value[(space + 1)..];
        Learn(credential);
        if (string.Equals(scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            LearnBasic(credential);
        }

        Note(location, name, RedactionNote.CredentialReason);
        return scheme is null ? Redactor.Placeholder : $"{scheme} {Redactor.Placeholder}";
    }

    /// <summary>
    /// Learns the user name and the password of a Basic credential (<c>user:password</c> in base64, RFC 7617): the user name
    /// can be the credential itself, an API key sent with an empty or dummy password (as Stripe and Freshdesk take theirs).
    /// </summary>
    private void LearnBasic(string credential)
    {
        var base64 = credential.Trim();
        var bytes = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64, bytes, out var length))
        {
            return;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, length);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        Learn(colon < 0 ? text : text[..colon]);
        if (colon >= 0)
        {
            Learn(text[(colon + 1)..]);
        }
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

            LearnCookie(parts[i][..equals], parts[i][(equals + 1)..]);
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

        LearnCookie(pair[..equals], pair[(equals + 1)..]);
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
        var offset = body.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
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
                        property = ReadString(ref reader);
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
                                Learn(ReadString(ref reader));
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

    /// <summary>The text of a string token; bytes that are not UTF-8 are read as U+FFFD (as they are written).</summary>
    private static string ReadString(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            return Encoding.UTF8.GetString(reader.ValueSpan);
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

    /// <summary>
    /// The values of form-data parts named like secrets; files (parts with a file name) are kept. The body's first line is
    /// the delimiter (<c>--boundary</c>), a part's headers end at a blank line and its Content-Disposition gives its name.
    /// Lines may end with CRLF or LF (a body typed in the editor). A body of another shape has no such parts; in one cut
    /// off at the size limit, a value that runs to the end is redacted to the end.
    /// </summary>
    private void MultipartSpans(ReadOnlySpan<byte> body, List<RedactedSpan> spans, string location)
    {
        // A boundary has at most 70 characters: a longer first line (or none, as in most binary data) is no delimiter.
        var firstLine = body.StartsWith("--"u8) ? body[..Math.Min(body.Length, 256)].IndexOf((byte)'\n') : -1;
        var boundary = firstLine < 0 ? [] : body[2..firstLine].TrimEnd(" \t\r"u8);
        if (boundary.IsEmpty)
        {
            return;
        }

        byte[] delimiter = [.. "\n--"u8, .. boundary];
        for (var start = firstLine + 1; start < body.Length;)
        {
            var length = body[start..].IndexOf(delimiter);
            var end = length < 0 ? body.Length : start + length;
            var (name, file, content) = ReadPart(body[start..end]);
            if (content >= 0 && !file && IsSecretName(name, NameKind.Field))
            {
                // The line break before the delimiter belongs to the delimiter.
                var valueEnd = length >= 0 && body[end - 1] == '\r' ? end - 1 : end;
                if (valueEnd > start + content)
                {
                    Learn(Encoding.UTF8.GetString(body[(start + content)..valueEnd]));
                    spans.Add(new RedactedSpan(start + content, valueEnd, false));
                    Note(location, name, RedactionNote.NameReason);
                }
            }

            // The next part starts on the line after the delimiter; "--" right after it ends the body.
            var after = end + delimiter.Length;
            var lineEnd = length < 0 || body[after..].StartsWith("--"u8) ? -1 : body[after..].IndexOf((byte)'\n');
            if (lineEnd < 0)
            {
                break;
            }

            start = after + lineEnd + 1;
        }
    }

    /// <summary>
    /// The name a form-data part's Content-Disposition gives it, whether it has a file name, and where its content starts:
    /// after the blank line that ends its headers (-1 without one).
    /// </summary>
    private static (string? Name, bool File, int Content) ReadPart(ReadOnlySpan<byte> part)
    {
        var header = "content-disposition:"u8;
        string? name = null;
        var file = false;
        for (var start = 0; part[start..].IndexOf((byte)'\n') is var length and >= 0; start += length + 1)
        {
            var line = part.Slice(start, length).TrimEnd((byte)'\r');
            if (line.IsEmpty)
            {
                return (name, file, start + length + 1);
            }

            if (line.Length > header.Length && Ascii.EqualsIgnoreCase(line[..header.Length], header))
            {
                foreach (Match parameter in DispositionParameter().Matches(Encoding.UTF8.GetString(line[header.Length..])))
                {
                    var key = parameter.Groups[1].Value;
                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        name ??= parameter.Groups[2].Success ? parameter.Groups[2].Value : parameter.Groups[3].Value.Trim();
                    }
                    else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase) || key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
                    {
                        file = true;
                    }
                }
            }
        }

        return (null, false, -1);
    }

    /// <summary>A parameter of a Content-Disposition value: <c>; name="value"</c> or <c>; name=value</c>.</summary>
    [GeneratedRegex("""
        ;[ \t]*([^ \t=;]+)[ \t]*=[ \t]*(?:"([^"]*)"|([^;]*))
        """)]
    private static partial Regex DispositionParameter();

    private void AddValueSpans(ReadOnlySpan<byte> data, List<RedactedSpan> spans)
    {
        _patterns.Find(data, spans);
        _recording.FindLearned(data, spans);
    }

    private void Add(IEnumerable<string> values, int minLength)
    {
        foreach (var value in values)
        {
            if (value.Length >= minLength && !string.IsNullOrWhiteSpace(value))
            {
                foreach (var form in SecretPatterns.Forms(value))
                {
                    _patterns.Add(form);
                }
            }
        }
    }

    /// <summary>A value redacted by name is also redacted wherever it appears later (in this exchange and the rest of the recording).</summary>
    private void Learn(string value)
    {
        value = value.Trim();
        if (IsLearnable(value))
        {
            _recording.Learn(value);
        }
    }

    /// <summary>A cookie's value is redacted wherever it appears later, until the cookie is set again (see <see cref="Redactor.LearnCookie"/>).</summary>
    private void LearnCookie(string name, string value)
    {
        value = value.Trim();
        if (IsLearnable(value))
        {
            byte[][] forms = [.. SecretPatterns.Forms(value)];
            foreach (var form in forms)
            {
                _patterns.Add(form);
            }

            _recording.LearnCookie(_host, name.Trim(), forms);
        }
    }

    private static bool IsLearnable(string value) =>
        value.Length >= Redactor.MinLearnedLength && !value.Contains(Redactor.Placeholder, StringComparison.Ordinal);

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

    /// <summary>Where the "://" after a URL's scheme is, or -1 for a relative URL (whose query may hold one: /login?next=https://…).</summary>
    private static int SchemeEnd(string url)
    {
        var end = url.IndexOf("://", StringComparison.Ordinal);
        if (end <= 0 || !char.IsAsciiLetter(url[0]))
        {
            return -1;
        }

        for (var i = 1; i < end; i++)
        {
            if (!char.IsAsciiLetterOrDigit(url[i]) && url[i] is not ('+' or '-' or '.'))
            {
                return -1;
            }
        }

        return end;
    }
}
