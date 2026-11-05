using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;

namespace TermRest.Core.History;

/// <summary>
/// One sent request as recorded in the history: the request as edited (a Postman v2.1 item with variables unresolved),
/// what was actually requested (resolved URL and sent headers with secrets masked) and the response.
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>Header names whose values are always masked in the recorded request headers.</summary>
    private static readonly string[] SensitiveHeaderParts = ["authorization", "cookie", "token", "secret", "password", "api-key", "apikey", "x-api-key"];

    public required string Id { get; init; }

    public required DateTimeOffset Time { get; init; }

    public required string Method { get; init; }

    /// <summary>The resolved URL that was requested, with secret values masked as <c>***</c>.</summary>
    public required string Url { get; init; }

    /// <summary>The request as edited when it was sent: a Postman v2.1 item (<c>name</c>, <c>request</c>, <c>event</c>).</summary>
    public required JsonObject Item { get; init; }

    public string? Collection { get; init; }

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
            ["name"] = source.RequestName ?? $"{request.Method} {PostmanUrl.StripQueryAndHash(source.Spec.Url)}",
            ["request"] = new JsonObject { ["method"] = source.Spec.Method, ["header"] = new JsonArray() },
        };
        PostmanRequestMapper.Apply(item, source.Spec, PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21), PostmanSchemaVersion.V21);

        body = null;
        var truncated = response?.BodyTruncated == true;
        if (response is { Body.Length: > 0 } && maxBodyBytes > 0)
        {
            body = response.Body.Length > maxBodyBytes ? response.Body[..maxBodyBytes] : response.Body;
            truncated |= response.Body.Length > maxBodyBytes;

            // Servers that echo the request (like postman-echo) would otherwise keep credentials in the history.
            if (request.SecretValues.Count > 0 && System.Text.Unicode.Utf8.IsValid(body))
            {
                var text = System.Text.Encoding.UTF8.GetString(body);
                var redacted = request.Redact(text);
                if (redacted != text)
                {
                    body = System.Text.Encoding.UTF8.GetBytes(redacted);
                }
            }
        }

        var sent = exchange.SentHeaders.Count > 0 ? exchange.SentHeaders : request.Headers;
        var finalUrl = response?.FinalUri?.ToString();
        return new HistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = exchange.StartedAt,
            Method = request.Method,
            Url = request.Redact(request.Url),
            Item = item,
            Collection = source.CollectionName,
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
            FinalUrl = finalUrl is not null && finalUrl != request.Uri?.ToString() ? request.Redact(finalUrl) : null,
            ClientCertificate = exchange.Tls.Select(t => t.ClientCertificate).FirstOrDefault(c => c is not null)?.ToString()
                                ?? request.ClientCertificate?.Summary.ToString(),
            Timing = exchange.Timing,
        };
    }

    /// <summary>Rebuilds an exchange for display in the response pane (no TLS details; body from the store).</summary>
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
        var exchange = new HttpExchange(prepared) { StartedAt = Time, Timing = Timing, ErrorMessage = Error };
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

    private static string RedactHeader(string name, string value, PreparedRequest request)
    {
        var lower = name.ToLowerInvariant();
        if (SensitiveHeaderParts.Any(part => lower.Contains(part, StringComparison.Ordinal)))
        {
            // Keep the scheme ("Bearer", "Basic") so the entry still says what kind of credential was sent.
            var space = value.IndexOf(' ', StringComparison.Ordinal);
            return lower.Contains("authorization", StringComparison.Ordinal) && space > 0 ? value[..space] + " ***" : "***";
        }

        return request.Redact(value);
    }

    /// <summary>Session cookies keep their name and attributes; the value is masked.</summary>
    private static string RedactResponseHeader(string name, string value, PreparedRequest request)
    {
        if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
        {
            var equals = value.IndexOf('=', StringComparison.Ordinal);
            var end = value.IndexOf(';', StringComparison.Ordinal);
            return equals < 0 ? "***" : value[..(equals + 1)] + "***" + (end > equals ? value[end..] : "");
        }

        return request.Redact(value);
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
        AddIfNotNull(obj, "requestPath", RequestPath);
        AddIfNotNull(obj, "environment", Environment);
        obj["item"] = Item.DeepClone();
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
                Collection = obj["collection"].GetString(),
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
