using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Variables;

namespace TermRest.Core.History;

/// <summary>
/// One sent request as recorded in the history: the request as edited (a Postman v2.1 item with variables unresolved),
/// what was actually requested (resolved URL and sent headers with secrets masked) and the response.
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>
    /// Header names whose values are always masked in the recorded request and response headers, besides the names that
    /// recordings redact (<see cref="SecretNames"/>: Ocp-Apim-Subscription-Key, X-Functions-Key, api_key …).
    /// </summary>
    private static readonly string[] SensitiveHeaderParts = ["authorization", "cookie", "token", "secret", "password", "api-key", "apikey", "x-api-key"];

    /// <summary>What may stand between <c>{{variable}}</c> references without being a typed secret (<c>{{user}}:{{password}}</c>).</summary>
    private const string ReferenceSeparators = "=:;,&";

    // UTF-16 and UTF-32 in either byte order, with their byte order marks (see WideEncodings).
    private static readonly Encoding[] Utf16 = [Encoding.Unicode, Encoding.BigEndianUnicode];
    private static readonly Encoding[] Utf32 = [Encoding.UTF32, new UTF32Encoding(bigEndian: true, byteOrderMark: true)];

    public required string Id { get; init; }

    public required DateTimeOffset Time { get; init; }

    public required string Method { get; init; }

    /// <summary>The resolved URL that was requested, with secret values masked as <c>***</c>.</summary>
    public required string Url { get; init; }

    /// <summary>The request as edited when it was sent: a Postman v2.1 item (<c>name</c>, <c>request</c>, <c>event</c>).</summary>
    public required JsonObject Item { get; init; }

    /// <summary>
    /// Whether credentials typed into the request were masked as <c>***</c> in <see cref="Item"/>: secret auth
    /// parameters (and a Basic user name sent without a password, then the credential), headers named like secrets (the
    /// values of a Cookie header, keeping the cookie names), the password in the URL (https://user:password@host, or the
    /// user name of a URL without one) and the client certificate's passphrase (<c>{{variable}}</c> references are kept).
    /// They must be entered again before the request is sent again.
    /// </summary>
    public bool CredentialsMasked { get; init; }

    /// <summary>
    /// Whether the request inherited its auth from its folders or collection: <see cref="Item"/> holds a copy, masked like
    /// typed credentials. Re-sending can take the auth from the original request again (see <see cref="RequestPath"/>).
    /// </summary>
    public bool AuthInherited { get; init; }

    /// <summary>
    /// Whether <paramref name="spec"/> still holds a credential masked in the history (see <see cref="CredentialsMasked"/>):
    /// sent as it is, the server would get <c>***</c> in its place.
    /// </summary>
    public static bool HasMaskedCredentials(HttpRequestSpec spec) =>
        spec.Auth?.Params.Any(p => p.ValueAsString == "***") == true
        || spec.Headers.Any(h => IsSensitiveHeader(h.Key) && h.Value is { } value && IsMasked(h.Key, value))
        || spec.Certificate?.Passphrase == "***"
        || PostmanUrl.Parse(spec.Url) is { AuthPassword: "***" } or { AuthUser: "***" };

    public string? Collection { get; init; }

    /// <summary>
    /// The file of <see cref="Collection"/> (a v3 collection's folder) in the workspace, relative with "/" (see
    /// <see cref="Workspace.ApiWorkspace.FileOf"/>): it tells collections of the same name apart. Entries of earlier
    /// versions have none.
    /// </summary>
    public string? CollectionFile { get; init; }

    public string? RequestPath { get; init; }

    public string? Environment { get; init; }

    public List<KeyValuePair<string, string>> SentHeaders { get; init; } = [];

    public int? Status { get; init; }

    public string? Reason { get; init; }

    public string? HttpVersion { get; init; }

    public long? Size { get; init; }

    public string? ContentType { get; init; }

    public List<KeyValuePair<string, string>> ResponseHeaders { get; init; } = [];

    /// <summary>Whether (part of) the response body is stored next to the history file.</summary>
    public bool BodySaved { get; init; }

    /// <summary>The stored body is incomplete (the response or the history size limit cut it).</summary>
    public bool BodyTruncated { get; init; }

    public string? Error { get; init; }

    public string? FinalUrl { get; init; }

    public string? ClientCertificate { get; init; }

    public TimingBreakdown Timing { get; init; } = new();

    public string Name => Item["name"].GetString() is { Length: > 0 } name ? name : $"{Method} {Url}";

    public bool Succeeded => Status is not null;

    /// <summary>The request as it was edited, ready to load into an editor (a fresh copy every call).</summary>
    public HttpRequestSpec ReadSpec() => PostmanRequestMapper.Read((JsonObject)Item.DeepClone(), PostmanSchemaVersion.V21);

    /// <summary>Builds the entry for a finished exchange. <paramref name="body"/> receives the response bytes to store, if any.</summary>
    public static HistoryEntry Create(HttpExchange exchange, RequestSource source, string? environment, int maxBodyBytes, out byte[]? body)
    {
        var request = exchange.Request;
        var response = exchange.Response;
        var item = new JsonObject
        {
            ["name"] = source.RequestName ?? $"{request.Method} {MaskUrlPassword(PostmanUrl.StripQueryAndHash(source.Spec.Url))}",
            ["request"] = new JsonObject { ["method"] = source.Spec.Method, ["header"] = new JsonArray() },
        };
        PostmanRequestMapper.Apply(item, source.Spec, PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21), PostmanSchemaVersion.V21);
        var credentialsMasked = MaskCredentials(item);

        body = null;
        var truncated = response?.BodyTruncated == true;
        if (response is { Body.Length: > 0 } && maxBodyBytes > 0)
        {
            body = StoredBody(response.Body, response.ContentType, maxBodyBytes, request, out var cut);
            truncated |= cut;
        }

        var sent = exchange.SentHeaders.Count > 0 ? exchange.SentHeaders : request.Headers;
        var finalUri = response?.FinalUri;
        return new HistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = exchange.StartedAt,
            Method = request.Method,
            Url = MaskUrlPassword(request.Redact(request.Url), keepReferences: false),
            Item = item,
            CredentialsMasked = credentialsMasked,
            AuthInherited = source.AuthInherited,
            Collection = source.CollectionName,
            CollectionFile = source.CollectionFile,
            RequestPath = source.RequestPath,
            Environment = environment,
            SentHeaders = sent.Select(h => new KeyValuePair<string, string>(h.Key, RedactHeader(h.Key, h.Value, request))).ToList(),
            Status = response?.StatusCode,
            Reason = response?.ReasonPhrase,
            HttpVersion = response is null ? null : $"{response.Version.Major}.{response.Version.Minor}",
            Size = response?.Body.LongLength,
            ContentType = response?.ContentType,
            ResponseHeaders = response?.Headers.Select(h => new KeyValuePair<string, string>(h.Key, RedactResponseHeader(h.Key, h.Value, request))).ToList() ?? [],
            BodySaved = body is not null,
            BodyTruncated = truncated,
            Error = response is null ? request.Redact(exchange.ErrorMessage ?? "Request failed.") : null,
            FinalUrl = finalUri is not null && finalUri != request.Uri ? MaskUri(finalUri, request) : null,
            ClientCertificate = exchange.Tls.Select(t => t.ClientCertificate).FirstOrDefault(c => c is not null)?.ToString()
                                ?? request.ClientCertificate?.Summary.ToString(),
            Timing = exchange.Timing,
        };
    }

    /// <summary>
    /// Rebuilds an exchange for display in the response pane: no TLS details or script results, the request's settings
    /// left at their defaults, and the body from the store (maybe only its start). Its
    /// <see cref="HttpExchange.HistoryEntry"/> is this entry, which has the size of the body and the client certificate.
    /// </summary>
    public HttpExchange ToExchange(byte[]? body)
    {
        var prepared = new PreparedRequest
        {
            Method = Method,
            Url = Url,
            Uri = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri : null,
            AuthDescription = "(not recorded)",
        };
        prepared.Headers.AddRange(SentHeaders);
        var exchange = new HttpExchange(prepared) { StartedAt = Time, Timing = Timing, ErrorMessage = Error, HistoryEntry = this };
        exchange.SentHeaders.AddRange(SentHeaders);
        if (Status is { } status)
        {
            exchange.Response = new ResponseSnapshot
            {
                StatusCode = status,
                ReasonPhrase = Reason ?? "",
                Version = System.Version.TryParse(HttpVersion, out var version) ? version : System.Net.HttpVersion.Version11,
                Headers = ResponseHeaders,
                Body = body ?? [],
                BodyTruncated = BodyTruncated,
                ContentType = ContentType,
                FinalUri = FinalUrl is not null && Uri.TryCreate(FinalUrl, UriKind.Absolute, out var final) ? final : null,
            };
        }

        return exchange;
    }

    private static string RedactHeader(string name, string value, PreparedRequest request) =>
        IsSensitiveHeader(name) ? MaskHeader(name, value) : request.Redact(value);

    /// <summary>Session cookies keep their name and attributes; the value is masked. Other headers are masked like request headers.</summary>
    private static string RedactResponseHeader(string name, string value, PreparedRequest request)
    {
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
        {
            var equals = value.IndexOf('=', StringComparison.Ordinal);
            var end = value.IndexOf(';', StringComparison.Ordinal);
            return equals < 0 ? "***" : value[..(equals + 1)] + "***" + (end > equals ? value[end..] : "");
        }

        return RedactHeader(name, value, request);
    }

    private static bool IsSensitiveHeader(string name)
    {
        var lower = name.ToLowerInvariant();
        return SensitiveHeaderParts.Any(part => lower.Contains(part, StringComparison.Ordinal)) || SecretNames.IsSecret(name, NameKind.Header);
    }

    /// <summary>Keeps the scheme of an Authorization header ("Bearer ***") so the entry still says what kind of credential was sent.</summary>
    private static string MaskHeader(string name, string value) => CredentialStart(name, value) is > 0 and var start ? value[..start] + "***" : "***";

    /// <summary>Where the credential starts in a header value: after the scheme of an Authorization header ("Bearer …").</summary>
    private static int CredentialStart(string name, string value)
    {
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 && name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ? space + 1 : 0;
    }

    /// <summary>
    /// <paramref name="value"/> with the credential typed into it masked, or null when it has none (only <c>{{variable}}</c>
    /// references). A Cookie header is masked cookie by cookie, keeping their names: <c>sid=***; theme={{theme}}</c>.
    /// </summary>
    private static string? MaskTyped(string name, string value)
    {
        if (!IsCookie(name))
        {
            return IsLiteral(value[CredentialStart(name, value)..]) ? MaskHeader(name, value) : null;
        }

        var pairs = value.Split(';');
        var masked = false;
        for (var i = 0; i < pairs.Length; i++)
        {
            var start = CookieValueStart(pairs[i]);
            if (IsLiteral(pairs[i][start..]))
            {
                pairs[i] = pairs[i][..start] + "***";
                masked = true;
            }
        }

        return masked ? string.Join(';', pairs) : null;
    }

    /// <summary>Whether a header still has a credential masked in the history (in a Cookie header: one of its cookies).</summary>
    private static bool IsMasked(string name, string value) =>
        IsCookie(name)
            ? value.Split(';').Any(pair => pair[CookieValueStart(pair)..].Trim() == "***")
            : value[CredentialStart(name, value)..] == "***";

    private static bool IsCookie(string name) => name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the value starts in a cookie's <c>name=value</c> pair (a pair without a name is all value).</summary>
    private static int CookieValueStart(string pair)
    {
        var equals = pair.IndexOf('=', StringComparison.Ordinal);
        return equals >= 0 ? equals + 1 : pair.Length - pair.TrimStart().Length;
    }

    /// <summary>
    /// Masks the credentials typed into the request (see <see cref="CredentialsMasked"/>); values that are only
    /// <c>{{variable}}</c> references (or <c>env:NAME</c> for the passphrase) hold no secret and are kept, so the request
    /// can be sent again. Returns whether anything was masked.
    /// </summary>
    private static bool MaskCredentials(JsonObject item)
    {
        if (item["request"] is not JsonObject request)
        {
            return false;
        }

        var masked = false;
        if (request["auth"] is JsonObject auth)
        {
            foreach (var (type, parameters) in auth)
            {
                // Basic auth without a password (curl -u sk_test_…:) sends the user name as the credential.
                var userIsCredential = type == "basic" && string.IsNullOrEmpty(AuthParameter(parameters, "password"));

                // Postman v2.1 lists the parameters ([{"key": "password", "value": …}]); v2.0 maps them ({"password": …}).
                if (parameters is JsonArray list)
                {
                    foreach (var parameter in list.OfType<JsonObject>())
                    {
                        masked |= MaskAuthParameter(type, parameter["key"].GetString(), parameter, "value", userIsCredential);
                    }
                }
                else if (parameters is JsonObject map)
                {
                    foreach (var key in map.Select(p => p.Key).ToList())
                    {
                        masked |= MaskAuthParameter(type, key, map, key, userIsCredential);
                    }
                }
            }
        }

        foreach (var header in (request["header"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (header["key"].GetString() is { } name && IsSensitiveHeader(name) && header["value"].GetString() is { } value
                && MaskTyped(name, value) is { } typed)
            {
                header["value"] = typed;
                masked = true;
            }
        }

        if (request["certificate"] is JsonObject certificate && certificate["passphrase"].GetString() is { } passphrase
            && !passphrase.StartsWith("env:", StringComparison.OrdinalIgnoreCase) && IsLiteral(passphrase))
        {
            certificate["passphrase"] = "***";
            masked = true;
        }

        // Credentials in the URL (https://user:password@host, or a user name alone, then the credential) are sent as Basic
        // auth: the URL's text, and its parts in a URL object.
        var url = request["url"] as JsonObject;
        foreach (var (owner, property) in (IEnumerable<(JsonObject?, string)>)[(request, "url"), (url, "raw")])
        {
            if (owner?[property] is JsonValue text && text.GetValueKind() == JsonValueKind.String
                && MaskUrlPassword(text.GetValue<string>()) is var maskedUrl && maskedUrl != text.GetValue<string>())
            {
                owner[property] = maskedUrl;
                masked = true;
            }
        }

        if (url?["auth"] is JsonObject urlAuth)
        {
            var part = string.IsNullOrEmpty(urlAuth["password"].GetString()) ? "user" : "password";
            if (urlAuth[part].GetString() is { } credential && credential != "***" && IsLiteral(credential))
            {
                urlAuth[part] = "***";
                masked = true;
            }
        }

        return masked;
    }

    /// <summary>
    /// <paramref name="url"/> with the password of its user info (https://user:password@host) masked as <c>***</c>, or its
    /// user name when it has no password (https://user@host, https://user:@host: it is then the credential); one that is
    /// only <c>{{variable}}</c> references is kept if <paramref name="keepReferences"/>.
    /// </summary>
    private static string MaskUrlPassword(string url, bool keepReferences = true) =>
        PostmanUrl.MaskCredentials(url, keepReferences ? IsLiteral : null);

    /// <summary>
    /// A URL System.Uri gives (the one a redirect led to), masked like <see cref="Url"/>: its password, and the secret
    /// values in the forms a URL holds them, as typed, as System.Uri leaves them and URL-encoded. It is kept as
    /// <see cref="Uri.AbsoluteUri"/>, as it was sent: ToString() unescapes part of a value (%20 …), which then matches none.
    /// </summary>
    private static string MaskUri(Uri uri, PreparedRequest request)
    {
        var url = uri.AbsoluteUri;
        var forms = request.SecretValues.Where(s => s.Length >= 4).SelectMany(s => (string[])[s, SecretPatterns.InUrl(s), Uri.EscapeDataString(s)]);
        foreach (var form in forms.Distinct(StringComparer.Ordinal).OrderByDescending(f => f.Length))
        {
            url = url.Replace(form, "***", StringComparison.Ordinal);
        }

        return MaskUrlPassword(url, keepReferences: false);
    }

    /// <summary>
    /// Masks an auth parameter that holds a credential: passwords, tokens, secrets, private keys, access keys and auth keys
    /// (named so, see <see cref="SecretNames"/>), the value of an API key, and a Basic user name that is the credential.
    /// A number (a hand-written collection's <c>"value": 123456</c>) is sent as its text and masked too; a flag such as
    /// JWT's <c>isSecretBase64Encoded</c> holds no credential.
    /// </summary>
    private static bool MaskAuthParameter(string type, string? key, JsonObject owner, string property, bool userIsCredential)
    {
        if (!((type == "apikey" && key == "value") || (userIsCredential && key == "username") || SecretNames.IsSecret(key, NameKind.Field))
            || owner[property] is not JsonValue value || value.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number)
            || !IsLiteral(value.GetString()!))
        {
            return false;
        }

        owner[property] = "***";
        return true;
    }

    /// <summary>The value of an auth parameter, listed (v2.1) or mapped (v2.0); null without one.</summary>
    private static string? AuthParameter(JsonNode? parameters, string key) => parameters switch
    {
        JsonArray list => list.OfType<JsonObject>().FirstOrDefault(p => p["key"].GetString() == key)?["value"].GetString(),
        JsonObject map => map[key].GetString(),
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="value"/> has text of its own besides <c>{{variable}}</c> references. Spaces and separators
    /// between references are not (<c>{{user}}:{{password}}</c>).
    /// </summary>
    private static bool IsLiteral(string value)
    {
        var rest = value;
        foreach (var name in VariableResolver.FindReferences(value))
        {
            rest = rest.Replace("{{" + name + "}}", "", StringComparison.Ordinal);
        }

        return rest.Length == value.Length
            ? rest.Trim().Length > 0
            : rest.Any(c => !char.IsWhiteSpace(c) && !ReferenceSeparators.Contains(c, StringComparison.Ordinal));
    }

    /// <summary>
    /// The part of a response body kept in the history (at most <paramref name="maxBytes"/>) with every secret value
    /// replaced by <c>***</c>: servers that echo the request (like postman-echo) would otherwise keep credentials in the
    /// history. The bytes are searched, in every form a secret takes in text (also URL-encoded or JSON-escaped, see
    /// <see cref="SecretPatterns.Forms(string, Encoding)"/>), in UTF-8 and in the UTF-16 or UTF-32 the body says it is in
    /// (see <see cref="WideEncodings"/>), so it does not matter which encoding the body has or where the limit cuts it;
    /// a secret that the limit would cut is left out entirely. The placeholder is written in the secret's encoding.
    /// </summary>
    private static byte[] StoredBody(byte[] body, string? contentType, int maxBytes, PreparedRequest request, out bool cut)
    {
        // UTF-8 whatever the body says, as a label can be wrong.
        Encoding[] encodings = [Encoding.UTF8, .. WideEncodings(body, contentType)];
        var units = Array.ConvertAll(encodings, encoding => encoding.GetByteCount("*"));
        var placeholders = Array.ConvertAll(encodings, encoding => encoding.GetBytes("***"));
        var secrets = new List<(byte[] Bytes, int Encoding)>();
        foreach (var value in request.SecretValues.Where(s => s.Length >= 4))
        {
            for (var e = 0; e < encodings.Length; e++)
            {
                foreach (var form in SecretPatterns.Forms(value, encodings[e]))
                {
                    secrets.Add((form, e));
                }
            }
        }

        var window = body.AsSpan(0, (int)Math.Min(body.Length, (long)maxBytes + secrets.Select(s => s.Bytes.Length).DefaultIfEmpty(1).Max() - 1));
        var spans = new List<(int Start, int End, int Encoding)>();
        foreach (var (secret, encoding) in secrets)
        {
            for (var offset = 0; ;)
            {
                var index = window[offset..].IndexOf(secret);
                if (index < 0)
                {
                    break;
                }

                index += offset;
                if (index % units[encoding] != 0)
                {
                    offset = index + 1; // across UTF-16 or UTF-32 characters: other text
                    continue;
                }

                spans.Add((index, index + secret.Length, encoding));
                offset = index + secret.Length;
            }
        }

        spans.Sort();
        var limit = Math.Min(body.Length, maxBytes);
        var replaced = new List<(int Start, int End, int Encoding)>();
        foreach (var (start, end, encoding) in spans)
        {
            var position = replaced.Count > 0 ? replaced[^1].End : 0;
            if (end <= position)
            {
                continue; // within a secret already replaced
            }

            if (Math.Max(start, position) >= limit || end > limit)
            {
                limit = Math.Clamp(start, position, limit);
                break;
            }

            if (start < position)
            {
                replaced[^1] = replaced[^1] with { End = end }; // overlapping secrets are replaced together
            }
            else
            {
                replaced.Add((start, end, encoding));
            }
        }

        cut = limit < body.Length;
        if (replaced.Count == 0)
        {
            return cut ? body[..limit] : body;
        }

        using var output = new MemoryStream(limit);
        var copied = 0;
        foreach (var (start, end, encoding) in replaced)
        {
            output.Write(body, copied, start - copied);
            output.Write(placeholders[encoding]);
            copied = end;
        }

        output.Write(body, copied, limit - copied);
        return output.ToArray();
    }

    /// <summary>
    /// The encodings besides UTF-8 that a body may be in, so that the secrets echoed in it are found: UTF-16 or UTF-32 as
    /// its byte order mark says, else as its charset says, in either byte order (without a mark, a "utf-16" body may be
    /// either). None for other bodies.
    /// </summary>
    private static Encoding[] WideEncodings(byte[] body, string? contentType)
    {
        // The UTF-32 little-endian mark starts with the UTF-16 one: such a body is searched as both.
        var marked = Utf32.Concat(Utf16).Where(encoding => body.AsSpan().StartsWith(encoding.Preamble)).ToArray();
        if (marked.Length > 0)
        {
            return marked;
        }

        if (Http.BodyFormatter.GetCharset(contentType) is not { Length: > 0 } charset)
        {
            return [];
        }

        try
        {
            return Encoding.GetEncoding(charset).CodePage switch
            {
                1200 or 1201 => Utf16,
                12000 or 12001 => Utf32,
                _ => [],
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return []; // a charset .NET does not know (or support, like UTF-7)
        }
    }

    // ----- JSON Lines ------------------------------------------------------------------------------

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["id"] = Id,
            ["time"] = Time.ToString("O", CultureInfo.InvariantCulture),
            ["method"] = Method,
            ["url"] = Url,
        };
        AddIfNotNull(obj, "collection", Collection);
        AddIfNotNull(obj, "collectionFile", CollectionFile);
        AddIfNotNull(obj, "requestPath", RequestPath);
        AddIfNotNull(obj, "environment", Environment);
        obj["item"] = Item.DeepClone();
        if (CredentialsMasked)
        {
            obj["credentialsMasked"] = true;
        }

        if (AuthInherited)
        {
            obj["authInherited"] = true;
        }

        obj["sentHeaders"] = WritePairs(SentHeaders);
        if (Status is { } status)
        {
            obj["status"] = status;
            obj["reason"] = Reason;
            obj["httpVersion"] = HttpVersion;
            obj["size"] = Size;
            AddIfNotNull(obj, "contentType", ContentType);
            obj["responseHeaders"] = WritePairs(ResponseHeaders);
            obj["body"] = BodySaved ? BodyTruncated ? "truncated" : "saved" : "none";
        }

        AddIfNotNull(obj, "error", Error);
        AddIfNotNull(obj, "finalUrl", FinalUrl);
        AddIfNotNull(obj, "clientCertificate", ClientCertificate);
        var timing = new JsonObject();
        AddMs(timing, "dns", Timing.Dns);
        AddMs(timing, "connect", Timing.Connect);
        AddMs(timing, "tls", Timing.Tls);
        AddMs(timing, "waiting", Timing.Waiting);
        AddMs(timing, "download", Timing.Download);
        AddMs(timing, "total", Timing.Total);
        AddIfNotNull(timing, "remote", Timing.RemoteEndPoint);
        obj["timing"] = timing;
        return obj;
    }

    /// <summary>Parses one history line; returns false for damaged or foreign lines.</summary>
    public static bool TryParse(string line, out HistoryEntry entry)
    {
        entry = null!;
        try
        {
            if (string.IsNullOrWhiteSpace(line) || PostmanJson.Parse(line) is not JsonObject obj
                || obj["id"].GetString() is not { Length: > 0 } id || obj["item"] is not JsonObject item
                || !DateTimeOffset.TryParse(obj["time"].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
            {
                return false;
            }

            var timing = obj["timing"] as JsonObject;
            var bodyState = obj["body"].GetString();
            entry = new HistoryEntry
            {
                Id = id,
                Time = time,
                Method = obj["method"].GetString() ?? "GET",
                Url = obj["url"].GetString() ?? "",
                Item = (JsonObject)item.DeepClone(),
                CredentialsMasked = obj["credentialsMasked"].GetBool() == true,
                AuthInherited = obj["authInherited"].GetBool() == true,
                Collection = obj["collection"].GetString(),
                CollectionFile = obj["collectionFile"].GetString(),
                RequestPath = obj["requestPath"].GetString(),
                Environment = obj["environment"].GetString(),
                SentHeaders = ReadPairs(obj["sentHeaders"]),
                Status = ReadInt(obj["status"]),
                Reason = obj["reason"].GetString(),
                HttpVersion = obj["httpVersion"].GetString(),
                Size = ReadLong(obj["size"]),
                ContentType = obj["contentType"].GetString(),
                ResponseHeaders = ReadPairs(obj["responseHeaders"]),
                BodySaved = bodyState is "saved" or "truncated",
                BodyTruncated = bodyState == "truncated",
                Error = obj["error"].GetString(),
                FinalUrl = obj["finalUrl"].GetString(),
                ClientCertificate = obj["clientCertificate"].GetString(),
                Timing = new TimingBreakdown
                {
                    Dns = ReadMs(timing?["dns"]),
                    Connect = ReadMs(timing?["connect"]),
                    Tls = ReadMs(timing?["tls"]),
                    Waiting = ReadMs(timing?["waiting"]),
                    Download = ReadMs(timing?["download"]),
                    Total = ReadMs(timing?["total"]),
                    RemoteEndPoint = timing?["remote"].GetString(),
                },
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Reads just the id of a history line (for compaction and deletion).</summary>
    internal static string? ReadId(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null : (PostmanJson.Parse(line) as JsonObject)?["id"].GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddIfNotNull(JsonObject obj, string key, string? value)
    {
        if (value is not null)
        {
            obj[key] = value;
        }
    }

    private static void AddMs(JsonObject obj, string key, TimeSpan? value)
    {
        if (value is { } v)
        {
            obj[key] = Math.Round(v.TotalMilliseconds, 3);
        }
    }

    private static JsonArray WritePairs(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var array = new JsonArray();
        foreach (var (key, value) in pairs)
        {
            array.Add((JsonNode)new JsonArray(key, value));
        }

        return array;
    }

    private static List<KeyValuePair<string, string>> ReadPairs(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonArray>().Where(p => p.Count == 2).Select(p => new KeyValuePair<string, string>(p[0].GetString() ?? "", p[1].GetString() ?? "")).ToList()
            : [];

    private static int? ReadInt(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static long? ReadLong(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static TimeSpan? ReadMs(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
}
