using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using TermRest.Core.Certificates;
using TermRest.Core.Http;
using TermRest.Core.Scripting;

namespace TermRest.Core.Recording;

/// <summary>An exchange as it was when it completed (the lists are copied: the UI may still show and change the exchange).</summary>
internal sealed record RecordedExchange(HttpExchange Exchange, string? Environment, KeyValuePair<string, string>[] SentHeaders, string[] Warnings)
{
    public static RecordedExchange Capture(HttpExchange exchange, string? environment) =>
        new(exchange, environment, [.. exchange.SentHeaders.Count > 0 ? exchange.SentHeaders : exchange.Request.Headers], [.. exchange.Request.Warnings]);
}

/// <summary>
/// Writes one HAR 1.2 entry for an exchange, with TermRest's details (source, TLS, settings, warnings, what was
/// redacted) under <c>_termrest</c>. Secrets are redacted by an <see cref="ExchangeRedactor"/>; bodies are written in
/// pieces, flushing in between, so writing never needs a second copy of a large body.
/// </summary>
internal static class HarEntryWriter
{
    private const int TextChunk = 64 * 1024;

    // A multiple of 3: base64 pieces then need no padding in between.
    private const int Base64Chunk = 48 * 1024;

    public static void Write(Utf8JsonWriter writer, RecordedExchange recorded, ExchangeRedactor redactor)
    {
        var exchange = recorded.Exchange;
        var request = exchange.Request;

        // Headers and body first: values redacted there by name are then also found in the URL and the response.
        var headers = recorded.SentHeaders.Select(h => new KeyValuePair<string, string>(h.Key, redactor.Header(h.Key, h.Value, "request.headers"))).ToList();
        var postData = PostData.Prepare(request, recorded.SentHeaders, redactor);
        var url = redactor.Url(request.Uri?.AbsoluteUri ?? request.Url, "request.url");

        writer.WriteStartObject();
        writer.WriteString("startedDateTime", FormatTime(exchange.StartedAt));
        writer.WriteNumber("time", Ms(exchange.Timing.Total) ?? 0);
        WriteRequest(writer, recorded, url, headers, postData);
        WriteResponse(writer, exchange, redactor);
        writer.WriteStartObject("cache");
        writer.WriteEndObject();
        WriteTimings(writer, exchange.Timing);
        if (IPEndPoint.TryParse(exchange.Timing.RemoteEndPoint ?? "", out var remote))
        {
            // Dual-mode sockets report IPv4 servers as ::ffff:a.b.c.d.
            var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            writer.WriteString("serverIPAddress", address.ToString());
            writer.WriteString("connection", remote.Port.ToString(CultureInfo.InvariantCulture));
        }

        WriteDetails(writer, recorded, redactor);
        writer.WriteEndObject();
    }

    /// <summary>ISO 8601 with milliseconds and the UTC offset, as in HAR files written by browsers.</summary>
    internal static string FormatTime(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes a body as the string property <paramref name="property"/>: as text with the redacted parts replaced, or as
    /// base64 (marking it in <paramref name="encodingProperty"/>) with secret bytes overwritten by <c>*</c>.
    /// </summary>
    internal static void WriteBody(Utf8JsonWriter writer, string property, byte[] data, BodyFormat format, IReadOnlyList<RedactedSpan> spans, string encodingProperty)
    {
        if (format == BodyFormat.Binary)
        {
            WriteBase64(writer, property, data, spans);
            writer.WriteString(encodingProperty, "base64");
        }
        else
        {
            WriteText(writer, property, data, spans);
        }
    }

    /// <summary>
    /// The bytes to write and how: text bodies in another charset (Latin-1, UTF-16 …) are converted to UTF-8, so that the
    /// secrets in them are found. A UTF-16 or UTF-32 byte order mark says which, also when the Content-Type names no charset.
    /// </summary>
    internal static (byte[] Data, BodyFormat Format) Classify(byte[] body, string? contentType)
    {
        var format = ExchangeRedactor.DetectFormat(contentType, body);
        if (body.Length == 0 || !ExchangeRedactor.IsTextual(ExchangeRedactor.MediaType(contentType)))
        {
            return (body, format);
        }

        if (WideMark(body) is { } marked)
        {
            var text = marked.GetString(body, marked.Preamble.Length, body.Length - marked.Preamble.Length);
            var bytes = Encoding.UTF8.GetBytes(text);
            return (bytes, ExchangeRedactor.DetectFormat(contentType, bytes));
        }

        if (Charset(contentType) is not { } charset || (format != BodyFormat.Binary && System.Text.Unicode.Utf8.IsValid(body)))
        {
            return (body, format);
        }

        // As the body is shown (legacy code pages such as windows-1252 included); a charset this platform does not know,
        // or does not support (UTF-7), leaves the bytes as they are.
        if (Http.BodyFormatter.GetEncoding(charset) is not { } encoding || encoding.CodePage == Encoding.UTF8.CodePage)
        {
            return (body, format);
        }

        // UTF-16 without a byte order mark in the byte order its text shows, as the body is shown.
        var converted = Encoding.UTF8.GetBytes(Http.BodyFormatter.InByteOrder(encoding, body).GetString(body));
        return (converted, ExchangeRedactor.DetectFormat(contentType, converted));
    }

    /// <summary>The UTF-16 or UTF-32 encoding whose byte order mark <paramref name="body"/> starts with; null for none.</summary>
    private static Encoding? WideMark(byte[] body) => WideEncodings.FirstOrDefault(encoding => body.AsSpan().StartsWith(encoding.Preamble));

    // The UTF-32 little-endian mark starts with the UTF-16 one: it is checked first.
    private static readonly Encoding[] WideEncodings =
        [Encoding.UTF32, new UTF32Encoding(bigEndian: true, byteOrderMark: true), Encoding.Unicode, Encoding.BigEndianUnicode];

    private static void WriteRequest(Utf8JsonWriter writer, RecordedExchange recorded, string url, List<KeyValuePair<string, string>> headers, PostData? postData)
    {
        var exchange = recorded.Exchange;
        var request = exchange.Request;
        writer.WriteStartObject("request");
        writer.WriteString("method", request.Method);
        writer.WriteString("url", url);
        writer.WriteString("httpVersion", VersionText(exchange.Response?.Version ?? (request.HttpVersion == "2" ? HttpVersion.Version20 : HttpVersion.Version11)));
        writer.WriteStartArray("cookies");
        foreach (var header in recorded.SentHeaders.Where(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var part in header.Value.Split(';'))
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", part[..equals].Trim());
                    writer.WriteString("value", part[(equals + 1)..].Trim().Length == 0 ? "" : Redactor.Placeholder);
                    writer.WriteEndObject();
                }
            }
        }

        writer.WriteEndArray();
        WriteNameValues(writer, "headers", headers);
        writer.WriteStartArray("queryString");
        foreach (var (name, value) in QueryParameters(url))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        postData?.Write(writer);
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", postData?.Size ?? 0);
        writer.WriteEndObject();
    }

    private static void WriteResponse(Utf8JsonWriter writer, HttpExchange exchange, ExchangeRedactor redactor)
    {
        writer.WriteStartObject("response");
        if (exchange.Response is not { } response)
        {
            // No response (connection error, timeout, cancelled): HAR viewers show status 0; the error is in _termrest.error.
            writer.WriteNumber("status", 0);
            writer.WriteString("statusText", "");
            writer.WriteString("httpVersion", "");
            writer.WriteStartArray("cookies");
            writer.WriteEndArray();
            writer.WriteStartArray("headers");
            writer.WriteEndArray();
            writer.WriteStartObject("content");
            writer.WriteNumber("size", 0);
            writer.WriteString("mimeType", "x-unknown");
            writer.WriteEndObject();
            writer.WriteString("redirectURL", "");
            writer.WriteNumber("headersSize", -1);
            writer.WriteNumber("bodySize", -1);
            writer.WriteEndObject();
            return;
        }

        var headers = response.Headers.Select(h => new KeyValuePair<string, string>(h.Key, redactor.Header(h.Key, h.Value, "response.headers"))).ToList();
        writer.WriteNumber("status", response.StatusCode);
        writer.WriteString("statusText", response.ReasonPhrase);
        writer.WriteString("httpVersion", VersionText(response.Version));
        writer.WriteStartArray("cookies");
        foreach (var header in response.Headers.Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            WriteSetCookie(writer, header.Value);
        }

        writer.WriteEndArray();
        WriteNameValues(writer, "headers", headers);

        writer.WriteStartObject("content");
        writer.WriteNumber("size", response.Body.LongLength);
        if (response.ReceivedBodySize is { } received && received != response.Body.LongLength && !response.BodyTruncated)
        {
            // Decoded (gzip …): the bytes saved, so that size = bodySize + compression.
            writer.WriteNumber("compression", response.Body.LongLength - received);
        }

        writer.WriteString("mimeType", response.ContentType ?? "x-unknown");
        if (response.Body.Length > 0)
        {
            var (data, format) = Classify(response.Body, response.ContentType);
            WriteBody(writer, "text", data, format, redactor.BodySpans(data, format, "response.content"), "encoding");
        }

        if (response.BodyTruncated)
        {
            writer.WriteBoolean("_truncated", true);
            writer.WriteString("comment", "The body was cut at the workspace's maximum response size (Tools > Settings).");
        }

        writer.WriteEndObject();
        writer.WriteString("redirectURL", headers.LastOrDefault(h => h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value ?? "");
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", response.ReceivedBodySize ?? -1);
        writer.WriteEndObject();
    }

    /// <summary>A Set-Cookie header as a HAR cookie: name and attributes, the value redacted.</summary>
    private static void WriteSetCookie(Utf8JsonWriter writer, string header)
    {
        var parts = header.Split(';');
        var equals = parts[0].IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("name", parts[0][..equals].Trim());
        writer.WriteString("value", parts[0][(equals + 1)..].Trim().Length == 0 ? "" : Redactor.Placeholder);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in parts.Skip(1))
        {
            var separator = attribute.IndexOf('=', StringComparison.Ordinal);
            var key = (separator < 0 ? attribute : attribute[..separator]).Trim().ToLowerInvariant();
            var value = separator < 0 ? "" : attribute[(separator + 1)..].Trim();
            switch (key)
            {
                case "path" when written.Add(key):
                    writer.WriteString("path", value);
                    break;
                case "domain" when written.Add(key):
                    writer.WriteString("domain", value);
                    break;
                case "expires" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) && written.Add(key):
                    writer.WriteString("expires", FormatTime(expires));
                    break;
                case "httponly" when written.Add(key):
                    writer.WriteBoolean("httpOnly", true);
                    break;
                case "secure" when written.Add(key):
                    writer.WriteBoolean("secure", true);
                    break;
                case "samesite" when written.Add(key):
                    writer.WriteString("sameSite", value);
                    break;
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteTimings(Utf8JsonWriter writer, TimingBreakdown timing)
    {
        writer.WriteStartObject("timings");
        writer.WriteNumber("blocked", -1);
        writer.WriteNumber("dns", Ms(timing.Dns) ?? -1);

        // In HAR the connect time includes the TLS handshake (ssl).
        writer.WriteNumber("connect", Ms(timing.Connect is { } connect ? connect + (timing.Tls ?? TimeSpan.Zero) : null) ?? -1);
        writer.WriteNumber("ssl", Ms(timing.Tls) ?? -1);
        writer.WriteNumber("send", 0);
        writer.WriteNumber("wait", Ms(timing.Waiting) ?? 0);
        writer.WriteNumber("receive", Ms(timing.Download) ?? 0);
        writer.WriteEndObject();
    }

    private static void WriteDetails(Utf8JsonWriter writer, RecordedExchange recorded, ExchangeRedactor redactor)
    {
        var exchange = recorded.Exchange;
        var request = exchange.Request;
        writer.WriteStartObject("_termrest");
        if (request.Source?.CollectionName is { } collection)
        {
            writer.WriteString("collection", collection);
        }

        if (request.Source?.RequestPath is { } path)
        {
            writer.WriteString("request", path);
        }

        if (recorded.Environment is { } environment)
        {
            writer.WriteString("environment", environment);
        }

        writer.WriteString("auth", redactor.Text(request.AuthDescription, "_termrest.auth"));
        if (exchange.Response?.FinalUri is { } final && request.Uri is { } requested && final != requested)
        {
            writer.WriteString("finalUrl", redactor.Url(final.AbsoluteUri, "_termrest.finalUrl"));
        }

        if (exchange.Cancelled)
        {
            writer.WriteBoolean("cancelled", true);
        }

        if (exchange.Response is null)
        {
            writer.WriteString("error", redactor.Text(exchange.ErrorMessage ?? "The request failed.", "_termrest.error"));
        }

        if (recorded.Warnings.Length > 0)
        {
            writer.WriteStartArray("warnings");
            foreach (var warning in recorded.Warnings)
            {
                writer.WriteStringValue(redactor.Text(warning, "_termrest.warnings"));
            }

            writer.WriteEndArray();
        }

        if (exchange.Scripts is { } scripts)
        {
            WriteScripts(writer, scripts, redactor);
        }

        var timing = exchange.Timing;
        writer.WriteStartObject("timing");
        WriteMs(writer, "dns", timing.Dns);
        WriteMs(writer, "connect", timing.Connect);
        WriteMs(writer, "tls", timing.Tls);
        WriteMs(writer, "waiting", timing.Waiting);
        WriteMs(writer, "download", timing.Download);
        WriteMs(writer, "total", timing.Total);
        writer.WriteBoolean("reusedConnection", timing.ReusedConnection);
        if (timing.RemoteEndPoint is { } endPoint)
        {
            writer.WriteString("remoteEndPoint", endPoint);
        }

        writer.WriteEndObject();

        writer.WriteStartObject("settings");
        writer.WriteBoolean("sslVerification", request.StrictSsl);
        writer.WriteBoolean("followRedirects", request.FollowRedirects);
        writer.WriteNumber("maxRedirects", request.MaxRedirects);
        if (request.Timeout is { } timeout)
        {
            writer.WriteNumber("timeoutMs", timeout.TotalMilliseconds);
        }
        else
        {
            writer.WriteNull("timeoutMs");
        }

        writer.WriteString("proxy", redactor.Url(string.IsNullOrWhiteSpace(request.Proxy) ? "system" : request.Proxy, "_termrest.settings"));
        writer.WriteString("httpVersion", request.HttpVersion);
        writer.WriteBoolean("cookies", request.UseCookies);
        writer.WriteNumber("maxResponseBytes", request.MaxResponseBytes);
        writer.WriteEndObject();

        if (request.ClientCertificate is { } selection)
        {
            writer.WriteStartObject("clientCertificate");
            writer.WriteString("name", selection.Entry.Name);
            writer.WriteString("origin", selection.Origin);
            WriteCertificateFields(writer, selection.Summary);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("tls");
        foreach (var tls in exchange.Tls)
        {
            WriteTls(writer, tls);
        }

        writer.WriteEndArray();

        // Last: everything above may have redacted something.
        writer.WriteStartArray("redacted");
        foreach (var note in redactor.Notes)
        {
            writer.WriteStartObject();
            writer.WriteString("in", note.Location);
            if (note.Name is not null)
            {
                writer.WriteString("name", note.Name);
            }

            writer.WriteString("reason", note.Reason);
            writer.WriteNumber("count", note.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// What the scripts did: tests, the error, the names of the variables they set (not the values) and what they
    /// printed (the first lines), all redacted like the rest of the entry.
    /// </summary>
    private static void WriteScripts(Utf8JsonWriter writer, ScriptResults scripts, ExchangeRedactor redactor)
    {
        const string Location = "_termrest.scripts";
        const int MaxOutputLines = 200;
        writer.WriteStartObject("scripts");
        writer.WriteNumber("ran", scripts.Ran);
        writer.WriteStartArray("tests");
        foreach (var test in scripts.Tests)
        {
            writer.WriteStartObject();
            writer.WriteString("name", redactor.Text(test.Name, Location));
            writer.WriteBoolean("passed", test.Passed);
            if (test.Error is { } error)
            {
                writer.WriteString("error", redactor.Text(error, Location));
            }

            writer.WriteString("script", test.Script);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (scripts.Error is { } failure)
        {
            writer.WriteString("error", redactor.Text(failure.ToString(), Location));
            if (failure.Traceback is { Length: > 0 } traceback)
            {
                writer.WriteString("traceback", redactor.Text(traceback, Location));
            }
        }

        WriteStrings(writer, "variablesSet", scripts.VariablesSet.Distinct(StringComparer.Ordinal));
        WriteStrings(writer, "output", scripts.Logs.Take(MaxOutputLines).Select(l => redactor.Text($"[{l.Phase} · {l.Script}] {l.Text}", Location)));
        if (scripts.Logs.Count > MaxOutputLines)
        {
            writer.WriteNumber("outputLinesNotRecorded", scripts.Logs.Count - MaxOutputLines);
        }

        if (scripts.Notes.Count > 0)
        {
            WriteStrings(writer, "notes", scripts.Notes);
        }

        if (scripts.NotRun.Count > 0)
        {
            WriteStrings(writer, "notRun", scripts.NotRun.Select(s => $"{s.Script} ({ScriptLanguage.Label(s.Language)}: no runtime chosen)"));
        }

        writer.WriteEndObject();
    }

    private static void WriteTls(Utf8JsonWriter writer, TlsConnectionInfo tls)
    {
        writer.WriteStartObject();
        writer.WriteString("host", tls.Host);
        writer.WriteString("protocol", tls.Protocol == SslProtocols.None ? null : tls.Protocol.ToString());
        writer.WriteString("cipherSuite", tls.CipherSuite?.ToString());
        writer.WriteString("alpn", tls.ApplicationProtocol);
        writer.WriteBoolean("serverCertificateAccepted", tls.Accepted);
        writer.WriteString("validation", tls.AcceptReason);
        if (tls.PolicyErrors != System.Net.Security.SslPolicyErrors.None)
        {
            writer.WriteString("policyErrors", tls.PolicyErrors.ToString());
        }

        WriteStrings(writer, "chainStatus", tls.ChainStatus);
        writer.WriteStartArray("serverCertificates");
        foreach (var certificate in tls.ServerChain)
        {
            writer.WriteStartObject();
            WriteCertificateFields(writer, certificate);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("clientCertificateRequested", tls.ClientCertificateRequested);
        WriteStrings(writer, "acceptableIssuers", tls.AcceptableIssuers);
        if (tls.ClientCertificate is { } client)
        {
            writer.WriteStartObject("clientCertificate");
            WriteCertificateFields(writer, client);
            writer.WriteString("origin", tls.ClientCertificateOrigin);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("clientCertificate");
        }

        writer.WriteEndObject();
    }

    private static void WriteCertificateFields(Utf8JsonWriter writer, CertificateSummary certificate)
    {
        writer.WriteString("subject", certificate.Subject);
        writer.WriteString("issuer", certificate.Issuer);
        writer.WriteString("notBefore", certificate.NotBefore.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        writer.WriteString("notAfter", certificate.NotAfter.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        writer.WriteString("thumbprint", certificate.Thumbprint);
        writer.WriteBoolean("expired", certificate.IsExpired);
        if (certificate.Source is { } source)
        {
            writer.WriteString("source", source);
        }
    }

    private static void WriteNameValues(Utf8JsonWriter writer, string property, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        writer.WriteStartArray(property);
        foreach (var (name, value) in pairs)
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string property, IEnumerable<string> values)
    {
        writer.WriteStartArray(property);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteText(Utf8JsonWriter writer, string property, byte[] data, IReadOnlyList<RedactedSpan> spans)
    {
        writer.WritePropertyName(property);
        var position = 0;
        foreach (var span in spans)
        {
            WriteTextSegments(writer, data.AsSpan(position, span.Start - position));
            writer.WriteStringValueSegment(span.Quoted ? ExchangeRedactor.QuotedPlaceholderBytes : ExchangeRedactor.PlaceholderBytes, false);
            position = span.End;
        }

        WriteTextSegments(writer, data.AsSpan(position));
        writer.WriteStringValueSegment(ReadOnlySpan<byte>.Empty, true);
        writer.Flush();
    }

    private static void WriteTextSegments(Utf8JsonWriter writer, ReadOnlySpan<byte> data)
    {
        while (data.Length > TextChunk)
        {
            // Cut before a UTF-8 continuation byte, never inside a character (a character has at most three).
            var cut = TextChunk;
            while (cut > TextChunk - 3 && (data[cut] & 0xC0) == 0x80)
            {
                cut--;
            }

            writer.WriteStringValueSegment(data[..cut], false);
            writer.Flush();
            data = data[cut..];
        }

        if (data.Length > 0)
        {
            writer.WriteStringValueSegment(data, false);
        }
    }

    private static void WriteBase64(Utf8JsonWriter writer, string property, byte[] data, IReadOnlyList<RedactedSpan> spans)
    {
        if (spans.Count > 0)
        {
            // A secret inside binary data (protobuf, a gzip member …): overwrite it, keeping the length.
            data = (byte[])data.Clone();
            foreach (var span in spans)
            {
                data.AsSpan(span.Start, span.End - span.Start).Fill((byte)'*');
            }
        }

        writer.WritePropertyName(property);
        var remaining = data.AsSpan();
        while (remaining.Length > Base64Chunk)
        {
            writer.WriteBase64StringSegment(remaining[..Base64Chunk], false);
            writer.Flush();
            remaining = remaining[Base64Chunk..];
        }

        writer.WriteBase64StringSegment(remaining, true);
        writer.Flush();
    }

    private static IEnumerable<(string Name, string Value)> QueryParameters(string url)
    {
        var query = url.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
        {
            yield break;
        }

        var end = url.IndexOf('#', query);
        var text = end < 0 ? url[(query + 1)..] : url[(query + 1)..end];
        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            yield return equals < 0
                ? (Uri.UnescapeDataString(part), "")
                : (Uri.UnescapeDataString(part[..equals]), Uri.UnescapeDataString(part[(equals + 1)..]));
        }
    }

    private static string? Charset(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        foreach (var parameter in contentType.Split(';').Skip(1))
        {
            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && parameter[..equals].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                return parameter[(equals + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    private static string VersionText(Version version) => $"HTTP/{version.Major}.{version.Minor}";

    private static double? Ms(TimeSpan? value) => value is { } v ? Math.Round(v.TotalMilliseconds, 3) : null;

    private static void WriteMs(Utf8JsonWriter writer, string property, TimeSpan? value)
    {
        if (Ms(value) is { } ms)
        {
            writer.WriteNumber(property, ms);
        }
    }

    /// <summary>
    /// The request body, prepared (and redacted) before anything else is written: fields named like secrets in the
    /// body are then also redacted where their values appear in the URL and the response.
    /// </summary>
    private sealed class PostData
    {
        private const string Location = "request.postData";

        public required string MimeType { get; init; }

        public byte[]? Data { get; init; }

        public BodyFormat Format { get; init; }

        public List<RedactedSpan> Spans { get; init; } = [];

        public List<(string Name, string Value)>? FormParameters { get; init; }

        public List<Part>? Parts { get; init; }

        public FileReference? File { get; init; }

        public string? Comment { get; init; }

        public long Size { get; init; }

        public static PostData? Prepare(PreparedRequest request, KeyValuePair<string, string>[] sentHeaders, ExchangeRedactor redactor)
        {
            var body = request.Body;
            var mimeType = sentHeaders.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? body.ContentType ?? "";
            switch (body.Kind)
            {
                case PreparedBodyKind.Text:
                {
                    var data = Encoding.UTF8.GetBytes(body.Text ?? "");
                    var format = ExchangeRedactor.DetectFormat(mimeType, data);
                    return new PostData { MimeType = mimeType, Data = data, Format = format, Spans = redactor.BodySpans(data, format, Location), Size = data.Length };
                }

                case PreparedBodyKind.FormUrlEncoded:
                {
                    var form = Encoding.UTF8.GetBytes(PreparedBody.EncodeForm(body.Fields));
                    var redacted = ExchangeRedactor.Apply(form, redactor.BodySpans(form, BodyFormat.Form, Location));
                    var parameters = Encoding.UTF8.GetString(redacted).Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
                    {
                        var equals = p.IndexOf('=', StringComparison.Ordinal);
                        return equals < 0 ? (WebUtility.UrlDecode(p), "") : (WebUtility.UrlDecode(p[..equals]), WebUtility.UrlDecode(p[(equals + 1)..]));
                    }).ToList();
                    return new PostData { MimeType = mimeType, Data = redacted, Format = BodyFormat.Text, FormParameters = parameters, Size = form.Length };
                }

                case PreparedBodyKind.Multipart:
                    return new PostData
                    {
                        MimeType = mimeType,
                        Parts = body.Parts.Select(p => p.FilePath is null ? TextPart(p, redactor) : FilePart(p, redactor)).ToList(),
                        Size = -1, // depends on the generated boundary
                    };

                case PreparedBodyKind.File:
                {
                    var file = FileReference.Create(body.FilePath!, redactor, Location);
                    return new PostData { MimeType = mimeType, File = file, Comment = FileReference.Comment, Size = file.Size };
                }

                default:
                    return null;
            }
        }

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject("postData");
            writer.WriteString("mimeType", MimeType);
            if (FormParameters is not null || Parts is not null)
            {
                writer.WriteStartArray("params");
                foreach (var (name, value) in FormParameters ?? [])
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteString("value", value);
                    writer.WriteEndObject();
                }

                foreach (var part in Parts ?? [])
                {
                    part.Write(writer);
                }

                writer.WriteEndArray();
            }

            if (Data is not null)
            {
                WriteBody(writer, "text", Data, Format, Spans, "_encoding");
            }

            File?.Write(writer);
            if (Comment is not null)
            {
                writer.WriteString("comment", Comment);
            }

            writer.WriteEndObject();
        }

        private static Part TextPart(MultipartPart part, ExchangeRedactor redactor)
        {
            var value = part.Value ?? "";
            value = redactor.IsSecretName(part.Name, NameKind.Field) ? redactor.RedactNamed(part.Name, value, Location) : redactor.Text(value, Location, part.Name);
            return new Part { Name = part.Name, Value = value, ContentType = part.ContentType };
        }

        private static Part FilePart(MultipartPart part, ExchangeRedactor redactor) => new()
        {
            Name = part.Name,
            FileName = Path.GetFileName(part.FilePath),
            ContentType = part.SentContentType,
            File = FileReference.Create(part.FilePath!, redactor, Location),
            Comment = FileReference.Comment,
        };
    }

    /// <summary>
    /// A file sent with a request: the body, or a form-data field. Recordings keep a reference to it, never its content
    /// (it can be large, and it stays on disk): <c>_file</c> gives the path, size and modification time and says
    /// <c>"contentRecorded": false</c>, and a comment says the same for HAR viewers.
    /// </summary>
    private sealed class FileReference
    {
        public const string Comment = "Sent from a file. The recording keeps the file's path, not its content.";

        public required string Path { get; init; }

        public long Size { get; init; } = -1;

        public DateTimeOffset? Modified { get; init; }

        public static FileReference Create(string path, ExchangeRedactor redactor, string location)
        {
            var info = new FileInfo(path);
            return new FileReference
            {
                Path = redactor.Text(path, location),
                Size = info.Exists ? info.Length : -1,
                Modified = info.Exists ? new DateTimeOffset(info.LastWriteTime) : null,
            };
        }

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject("_file");
            writer.WriteString("path", Path);
            writer.WriteNumber("size", Size);
            if (Modified is { } modified)
            {
                writer.WriteString("modified", FormatTime(modified));
            }

            writer.WriteBoolean("contentRecorded", false);
            writer.WriteEndObject();
        }
    }

    /// <summary>A form-data part: a text field, or a reference to a file.</summary>
    private sealed class Part
    {
        public required string Name { get; init; }

        public string? Value { get; init; }

        public string? FileName { get; init; }

        public string? ContentType { get; init; }

        public FileReference? File { get; init; }

        public string? Comment { get; init; }

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);
            if (Value is not null)
            {
                writer.WriteString("value", Value);
            }

            if (FileName is not null)
            {
                writer.WriteString("fileName", FileName);
            }

            if (ContentType is not null)
            {
                writer.WriteString("contentType", ContentType);
            }

            File?.Write(writer);
            if (Comment is not null)
            {
                writer.WriteString("comment", Comment);
            }

            writer.WriteEndObject();
        }
    }
}
