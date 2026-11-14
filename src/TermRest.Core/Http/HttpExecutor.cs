using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TermRest.Core.Certificates;

namespace TermRest.Core.Http;

/// <summary>
/// Sends <see cref="PreparedRequest"/>s. A fresh <see cref="SocketsHttpHandler"/> is used per request so every send
/// reports full timing (DNS, connect, TLS) and the exact TLS session details, which matters more than connection
/// reuse for a dev/testing tool. Cookies persist across requests through <see cref="Cookies"/>.
/// </summary>
public sealed class HttpExecutor
{
    public HttpExecutor(CookieContainer? cookies = null)
    {
        Cookies = cookies ?? new CookieContainer();
    }

    public static string UserAgent { get; } =
        $"TermRest/{typeof(HttpExecutor).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    public CookieContainer Cookies { get; }

    /// <summary>
    /// Limit for establishing a connection (TCP connect plus TLS handshake), so an unreachable host fails even when
    /// the request itself has no timeout. A shorter request timeout still applies.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<HttpExchange> SendAsync(PreparedRequest request, CancellationToken cancellationToken = default)
    {
        var exchange = new HttpExchange(request) { StartedAt = DateTimeOffset.Now };
        if (request.Errors.Count > 0 || request.Uri is null)
        {
            exchange.ErrorMessage = request.Errors.Count > 0 ? string.Join(" ", request.Errors) : "The request URL is not valid.";
            return exchange;
        }

        if (request.Body is { Kind: PreparedBodyKind.File, FileLanguage: not null } fileBody)
        {
            // Checked as it is now (it may have changed since it was chosen), off the caller's thread: it may be large.
            string? problem;
            try
            {
                problem = await Task.Run(() => BodyFile.Check(fileBody.FilePath!, fileBody.FileLanguage, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                exchange.Cancelled = true;
                exchange.ErrorMessage = "Request cancelled.";
                return exchange;
            }

            if (problem is not null)
            {
                request.Errors.Add(problem); // nothing was sent: not kept in history or recordings
                exchange.ErrorMessage = "Not sent: " + problem;
                return exchange;
            }
        }

        var clock = Stopwatch.StartNew();
        var target = request.Uri;
        var capture = new ConnectionCapture(clock, target, request.ClientCertificate);
        var captures = new List<ConnectionCapture> { capture };
        var clients = new List<HttpClient>();
        using var timeoutSource = request.Timeout is { } timeout && timeout > TimeSpan.Zero ? new CancellationTokenSource(timeout) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource?.Token ?? CancellationToken.None);
        TimeSpan? headersAt = null;
        HttpRequestMessage? message = null;
        HttpResponseMessage? response = null;
        try
        {
            // Redirects are followed here rather than by the handler: credentials only go to the request's own origin,
            // and each URL gets the client certificate selected for it, on a handler of its own.
            var credentials = CreateCredentials(request);
            var method = request.Method;
            var withBody = true;
            HttpClient? client = null;
            message = CreateMessage(request, target, method, withBody, exchange);
            for (var redirects = 0; ; redirects++)
            {
                if (client is null)
                {
                    client = new HttpClient(CreateHandler(request, capture, credentials)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                    clients.Add(client);
                }

                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (GetRedirectTarget(request, response, target, redirects) is not { } next)
                {
                    break;
                }

                // As browsers and .NET do: 301 and 302 turn a POST into a GET, 303 anything but GET and HEAD; without the body.
                var status = (int)response.StatusCode;
                if ((status is 300 or 301 or 302 && method == "POST") || (status == 303 && method is not ("GET" or "HEAD")))
                {
                    (method, withBody) = ("GET", false);
                }

                HttpRequestMessage nextMessage;
                try
                {
                    nextMessage = CreateMessage(request, next, method, withBody, exchange: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    request.Warnings.Add($"The redirect to {next.AbsoluteUri} was not followed: the body could not be read again ({ex.Message}).");
                    break;
                }

                response.Dispose();
                message.Dispose();
                (message, response, target) = (nextMessage, null, next);
                var certificate = SelectClientCertificate(request, next);
                if (!capture.Serves(next, certificate))
                {
                    capture = new ConnectionCapture(clock, next, certificate);
                    captures.Add(capture);
                    client = null;
                }
            }

            headersAt = clock.Elapsed;
            var (body, truncated, received, warning) = await ReadBodyAsync(response, request.MaxResponseBytes, linked.Token).ConfigureAwait(false);
            exchange.Response = Snapshot(response, body, truncated, received);
            if (warning is not null)
            {
                request.Warnings.Add(warning);
            }

            if (truncated)
            {
                request.Warnings.Add($"The response body was truncated at {HttpExchange.FormatSize(request.MaxResponseBytes)}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exchange.Cancelled = true;
            exchange.ErrorMessage = "Request cancelled.";
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true)
        {
            exchange.ErrorMessage = $"Request timed out after {HttpExchange.Format(request.Timeout)}.";
        }
        catch (OperationCanceledException ex)
        {
            // Neither the caller nor the request timeout cancelled: the handler gave up establishing the connection.
            exchange.Exception = ex;
            exchange.ErrorMessage = ex.InnerException is TimeoutException
                ? capture.IsConnected
                    ? $"The TLS handshake with {target.Authority} did not complete within {HttpExchange.Format(ConnectTimeout)}."
                    : $"Could not connect to {target.Authority} within {HttpExchange.Format(ConnectTimeout)}."
                : DescribeError(ex, capture, target);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException or SocketException
                                       or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
                                       or FormatException or ArgumentException or CryptographicException or InvalidDataException)
        {
            exchange.Exception = ex;
            exchange.ErrorMessage = DescribeError(ex, capture, target);
        }
        finally
        {
            response?.Dispose();
            message?.Dispose();
            exchange.Timing = captures[0].BuildTiming(headersAt, clock.Elapsed);
            foreach (var hop in captures)
            {
                exchange.Tls.AddRange(hop.CollectTls());
            }

            // Disposed last (with their handlers): closing the connections first would hide the TLS details read above.
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        return exchange;
    }

    private SocketsHttpHandler CreateHandler(PreparedRequest request, ConnectionCapture capture, ICredentials? credentials)
    {
        var connectTimeout = ConnectTimeout;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // followed in SendAsync

            // Decoded in ReadBodyAsync: .NET would drop Content-Encoding and Content-Length from the response, and add its
            // codings to an Accept-Encoding typed in the request.
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = request.UseCookies,
            CookieContainer = Cookies,
            ConnectTimeout = connectTimeout,
            Credentials = credentials,
            ConnectCallback = capture.ConnectAsync,
            PlaintextStreamFilter = capture.RecordSession,

            RequestHeaderEncodingSelector = HeaderEncoding,
        };

        switch (request.Proxy?.Trim().ToLowerInvariant())
        {
            case null or "" or "system":
                handler.UseProxy = true;
                capture.Proxy = HttpClient.DefaultProxy; // the one the handler uses
                break;
            case "none":
                handler.UseProxy = false;
                break;
            default:
                handler.UseProxy = true;
                handler.Proxy = capture.Proxy = new WebProxy(request.Proxy!.Trim());
                break;
        }

        var selection = capture.Selection;
        var options = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => capture.Validate(sender, certificate, chain, errors, request),

            // Resumed sessions can carry the client identity of an earlier handshake, which would hide certificate
            // changes while testing mTLS. A full handshake per request also gives honest timing.
            AllowTlsResume = false,
        };

        if (selection is not null && selection.Certificate.Chain.Count > 0 && !OperatingSystem.IsWindows())
        {
            // An explicit context also sends the intermediates loaded with the certificate.
            options.ClientCertificateContext = SslStreamCertificateContext.Create(
                selection.Certificate.Certificate, selection.Certificate.Chain, offline: true);
            capture.RecordContextCertificate();
        }
        else
        {
            // Windows gets no context: there .NET (up to at least 10.0.15, dotnet/runtime#131992) can cache the credential
            // of a connection given one, which holds its certificate, as the process-wide credential of connections
            // WITHOUT a certificate. Every later request without one would then present that certificate to any server
            // that asks, and this callback would not even be asked.
            if (selection is not null && selection.Certificate.Chain.Count > 0)
            {
                AddChainToWindowsStores(selection.Certificate);
            }

            // Always answer the selection explicitly: the configured certificate regardless of the server's list of
            // acceptable issuers (like curl/Postman), or none at all.
            options.LocalCertificateSelectionCallback = (_, _, _, remoteCertificate, acceptableIssuers) =>
                capture.SelectClientCertificate(remoteCertificate, acceptableIssuers)!;
        }

        handler.SslOptions = options;
        return handler;
    }

    /// <summary>
    /// SChannel sends the chain it builds from the certificate stores. Creating a context adds the chain's CA certificates
    /// that Windows can't find to the intermediate store (CurrentUser\CA), which is how a context's chain gets sent on
    /// Windows; the context itself is not used.
    /// </summary>
    private static void AddChainToWindowsStores(LoadedCertificate certificate)
    {
        var context = SslStreamCertificateContext.Create(certificate.Certificate, certificate.Chain, offline: true);
        foreach (var copy in context.IntermediateCertificates)
        {
            copy.Dispose();
        }
    }

    /// <param name="exchange">Records the headers sent and warns about headers left out; null for a redirect.</param>
    private HttpRequestMessage CreateMessage(PreparedRequest request, Uri target, string method, bool withBody, HttpExchange? exchange)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), target)
        {
            Version = request.HttpVersion == "2" ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var content = withBody ? request.Body.CreateContent() : null;
        message.Content = content;
        var otherOrigin = !IsSameOrigin(target, request.Uri!);
        var hasUserAgent = false;
        var hasAccept = false;
        var hasAcceptEncoding = false;
        foreach (var (key, value) in request.Headers)
        {
            hasUserAgent |= key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase);
            hasAccept |= key.Equals("Accept", StringComparison.OrdinalIgnoreCase);
            hasAcceptEncoding |= key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase);
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue; // always computed from the body
            }

            if (otherOrigin && IsOriginBound(request, key))
            {
                continue;
            }

            if (IsContentHeader(key) || (content is null && key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            {
                // Without a body there is nothing to describe, nor to send in chunks (.NET refuses chunked encoding
                // without content).
                if (content is null)
                {
                    if (exchange is not null)
                    {
                        request.Warnings.Add($"'{key}' was not sent because the request has no body.");
                    }

                    continue;
                }

                content.Headers.Remove(key);
                if (!content.Headers.TryAddWithoutValidation(key, value) && exchange is not null)
                {
                    request.Warnings.Add($"Header '{key}' has an invalid value and was skipped.");
                }
            }
            else if (!message.Headers.TryAddWithoutValidation(key, value) && exchange is not null)
            {
                request.Warnings.Add($"Header '{key}' is not valid and was skipped.");
            }
        }

        if (!hasUserAgent)
        {
            message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        if (!hasAccept)
        {
            message.Headers.TryAddWithoutValidation("Accept", "*/*");
        }

        if (!hasAcceptEncoding)
        {
            // A typed one (identity …) is sent as it is: ReadBodyAsync decodes whatever comes back.
            message.Headers.TryAddWithoutValidation("Accept-Encoding", DecodedCodings);
        }

        if (exchange is null)
        {
            return message;
        }

        foreach (var header in message.Headers.NonValidated)
        {
            foreach (var value in header.Value)
            {
                exchange.SentHeaders.Add(new(header.Key, value));
            }
        }

        if (content is not null)
        {
            foreach (var header in content.Headers.NonValidated)
            {
                foreach (var value in header.Value)
                {
                    exchange.SentHeaders.Add(new(header.Key, value));
                }
            }
        }

        if (request.UseCookies && Cookies.GetCookieHeader(request.Uri!) is { Length: > 0 } cookie)
        {
            exchange.SentHeaders.Add(new("Cookie", cookie));
        }

        return message;
    }

    /// <summary>Headers that describe a body (sent with it, not on their own); the cURL export follows the same rule.</summary>
    internal static bool IsContentHeader(string name) => name.ToLowerInvariant() switch
    {
        "content-type" or "content-encoding" or "content-language" or "content-location" or "content-md5"
            or "content-range" or "content-disposition" or "expires" or "last-modified" or "allow" => true,
        _ => false,
    };

    /// <summary>
    /// Headers sent only to the request's own origin, as with curl -L: credentials (including cookies typed in and the
    /// auth helper's headers, such as an API key) and Host. Other headers follow redirects.
    /// </summary>
    private static bool IsOriginBound(PreparedRequest request, string name) =>
        name.ToLowerInvariant() is "authorization" or "proxy-authorization" or "cookie" or "host" || request.AuthHeaderNames.Contains(name);

    private static bool IsSameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The auth helper's challenge credentials, for the request's own origin and the helper's schemes only: neither a
    /// Basic challenge nor another origin gets the password.
    /// </summary>
    private static CredentialCache? CreateCredentials(PreparedRequest request)
    {
        if (request.Credential is not { } credential)
        {
            return null;
        }

        var credentials = new CredentialCache();
        var origin = new Uri(request.Uri!, "/");
        foreach (var scheme in request.CredentialSchemes)
        {
            credentials.Add(origin, scheme, credential);
        }

        return credentials;
    }

    /// <summary>
    /// Where a response redirects to, if TermRest follows it (never from https to http, as .NET); otherwise null. An http(s)
    /// target has no user info: credentials in the request's URL are sent as an Authorization header, to its own origin
    /// only (see <see cref="IsOriginBound"/>), and .NET sends none from a URL. A relative target would otherwise take them
    /// from the request's URL, and show its password where the redirect is reported, kept in the history or recorded.
    /// </summary>
    private static Uri? GetRedirectTarget(PreparedRequest request, HttpResponseMessage response, Uri from, int redirects)
    {
        if (!request.FollowRedirects || (int)response.StatusCode is not (300 or 301 or 302 or 303 or 307 or 308)
            || response.Headers.Location is not { } location)
        {
            return null;
        }

        var target = WithoutUserInfo(location.IsAbsoluteUri ? location : new Uri(from, location));
        var refusal = target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps ? "TermRest follows only http and https URLs"
            : from.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp ? "it leads from https to http"
            : redirects >= request.MaxRedirects ? $"it would exceed the maximum number of redirects ({request.MaxRedirects})"
            : null;
        if (refusal is null)
        {
            return target;
        }

        // As it is sent (ToString() would unescape part of a value, which recordings then would not find to redact), its
        // password masked: a URL that is not http(s) keeps its user info (see WithoutUserInfo).
        request.Warnings.Add($"The redirect to {MaskUrlPasswords(target.AbsoluteUri)} was not followed: {refusal}.");
        return null;
    }

    /// <summary>An http(s) URL without its user info (in other URLs, such as mailto:, it can be more than credentials).</summary>
    private static Uri WithoutUserInfo(Uri uri) =>
        uri.UserInfo.Length == 0 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ? uri
        : new Uri(uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped));

    /// <summary>The client certificate for a URL a redirect leads to: selected for that URL, never one chosen for another origin.</summary>
    private static ClientCertificateSelection? SelectClientCertificate(PreparedRequest request, Uri target) =>
        target.Scheme != Uri.UriSchemeHttps ? null
        : request.ClientCertificateSelector is { } select ? select(target)
        : IsSameOrigin(target, request.Uri!) ? request.ClientCertificate
        : null;

    /// <summary>The content codings <see cref="ReadBodyAsync"/> removes, sent as Accept-Encoding unless the request has its own.</summary>
    private const string DecodedCodings = "gzip, deflate, br";

    /// <summary>
    /// Reads the body and removes the content codings its Content-Encoding lists. At most <paramref name="maxBytes"/> are
    /// read, and as many kept once decoded: a small body that decodes to a huge one stays within the limit. A body that
    /// cannot be decoded is kept as received, with a warning saying why.
    /// </summary>
    internal static async Task<(byte[] Body, bool Truncated, long Received, string? Warning)> ReadBodyAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        // HEAD, 204 and 304 responses may declare a length without having a body.
        var length = response.RequestMessage?.Method != HttpMethod.Head && response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotModified)
            ? response.Content.Headers.ContentLength ?? 0 : 0;
        byte[] received;
        bool truncated;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            (received, truncated) = await ReadAsync(stream, length, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        var codings = ContentCodings(response.Content.Headers);
        if (received.Length == 0 || codings.Count == 0)
        {
            return (received, truncated, received.Length, null);
        }

        if (codings.Find(c => c is not ("gzip" or "x-gzip" or "deflate" or "br")) is { } unknown)
        {
            return (received, truncated, received.Length, $"The body is kept as received: TermRest cannot decode Content-Encoding '{unknown}'.");
        }

        // Listed in the order they were applied: the last one is removed first.
        var body = received;
        var cut = truncated;
        for (var i = codings.Count - 1; i >= 0 && body.Length > 0; i--)
        {
            try
            {
                // A gzip member ends with the size of its data (modulo 4 GB): a hint for the buffer, as a declared length is.
                var size = codings[i] is "gzip" or "x-gzip" && body.Length >= 18 ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(^4)) : 0;
                await using var decoder = CreateDecoder(codings[i], body);
                (body, var more) = await ReadAsync(decoder, size, maxBytes, cancellationToken).ConfigureAwait(false);
                cut |= more;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                // Such as a plain body labelled gzip by a misconfigured server or proxy (.NET's messages tell no more).
                return (received, truncated, received.Length,
                    $"The body could not be decoded (Content-Encoding: {string.Join(", ", codings)}): it is not valid {codings[i]} data. It is kept as received.");
            }
        }

        return (body, cut, received.Length, null);
    }

    /// <summary>A body's content codings in the order they were applied, leaving out identity (and "none", which some servers send).</summary>
    private static List<string> ContentCodings(HttpContentHeaders headers)
    {
        var codings = new List<string>();
        if (headers.NonValidated.TryGetValues("Content-Encoding", out var values))
        {
            foreach (var value in values)
            {
                foreach (var coding in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!coding.Equals("identity", StringComparison.OrdinalIgnoreCase) && !coding.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        codings.Add(coding.ToLowerInvariant());
                    }
                }
            }
        }

        return codings;
    }

    private static Stream CreateDecoder(string coding, byte[] data)
    {
        var input = new MemoryStream(data, writable: false);
        return coding switch
        {
            "br" => new BrotliStream(input, CompressionMode.Decompress),

            // The standard deflate coding is zlib data, but some servers send raw deflate data. As .NET does, the first byte
            // tells them apart: 8 in its low bits for zlib.
            "deflate" when (data[0] & 0x0F) == 8 => new ZLibStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            _ => new GZipStream(input, CompressionMode.Decompress),
        };
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from <paramref name="stream"/>. A body of known length goes into one buffer
    /// of that size: growing one allocates about twice the body. The length is only declared, so the buffer takes that size
    /// once the first megabyte has actually arrived.
    /// </summary>
    private static async Task<(byte[] Data, bool Truncated)> ReadAsync(Stream stream, long length, long maxBytes, CancellationToken cancellationToken)
    {
        var expected = (int)Math.Min(Math.Min(length, maxBytes), Array.MaxLength);
        using var buffer = new MemoryStream(Math.Min(expected, 1024 * 1024));
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var count = (int)Math.Clamp(maxBytes - buffer.Length, 0, read);
                if (buffer.Length + count > buffer.Capacity && expected > buffer.Capacity)
                {
                    buffer.Capacity = expected;
                }

                buffer.Write(chunk, 0, count);
                if (count < read)
                {
                    return (ToArray(buffer), true);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return (ToArray(buffer), false);
    }

    /// <summary>The buffer itself when the content fills it exactly, otherwise a copy of the content.</summary>
    private static byte[] ToArray(MemoryStream buffer) => buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();

    private static ResponseSnapshot Snapshot(HttpResponseMessage response, byte[] body, bool truncated, long received)
    {
        var headers = new List<KeyValuePair<string, string>>();
        void AddAll(HttpHeaders source)
        {
            foreach (var header in source.NonValidated)
            {
                foreach (var value in header.Value)
                {
                    headers.Add(new(header.Key, value));
                }
            }
        }

        AddAll(response.Headers);
        AddAll(response.Content.Headers);
        AddAll(response.TrailingHeaders);
        return new ResponseSnapshot
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase ?? "",
            Version = response.Version,
            Headers = headers,
            Body = body,
            ReceivedBodySize = received,
            BodyTruncated = truncated,
            // .NET reads no type from a Content-Type it can't parse (a trailing ";", an empty charset …), which servers send:
            // the header as the server sent it then, as Postman reads it (the charset and media type are read leniently).
            ContentType = response.Content.Headers.ContentType?.ToString() ?? RawContentType(response.Content.Headers),
            FinalUri = response.RequestMessage?.RequestUri,
        };
    }

    /// <summary>The Content-Type as the server sent it (null for none).</summary>
    private static string? RawContentType(HttpContentHeaders headers)
    {
        if (headers.NonValidated.TryGetValues("Content-Type", out var values))
        {
            foreach (var value in values)
            {
                return value.Trim() is { Length: > 0 } type ? type : null;
            }
        }

        return null;
    }

    private static string DescribeError(Exception ex, ConnectionCapture capture, Uri target)
    {
        if (capture.FirstRejected() is { } rejected)
        {
            var details = rejected.ChainStatus.Count > 0 ? $" ({string.Join("; ", rejected.ChainStatus)})" : "";
            return $"Server certificate for {rejected.Host} was rejected: {rejected.AcceptReason}{details}. " +
                   "Add its CA to the workspace CA bundle (Tools > Settings) or turn off SSL verification for this request.";
        }

        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // An outer message often repeats the inner one ("Connection refused (host:port)" around "Connection refused").
            var text = e.Message.Trim();
            if (text.Length > 0 && !messages.Any(m => m.Contains(text, StringComparison.Ordinal)))
            {
                messages.Add(text);
            }
        }

        // .NET writes the proxy's URL as Uri.ToString() does, which unescapes a space in its password, where
        // MaskUrlPasswords stops: that URL is masked whole first (looked up only for a message with a URL in it).
        var summary = string.Join(" → ", messages);
        if (summary.Contains("://", StringComparison.Ordinal) && capture.ProxyUri(target) is { } proxy)
        {
            summary = MaskProxyPassword(summary, proxy);
        }

        summary = MaskUrlPasswords(summary);
        return ex is HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } hre ? $"{hre.HttpRequestError}: {summary}" : summary;
    }

    /// <summary>
    /// <paramref name="text"/> with the password of <paramref name="proxy"/>'s URL masked as <c>***</c> wherever the URL is
    /// in it as .NET writes it (<see cref="Uri.ToString"/>: a space or a letter that is not ASCII unescaped), as it is sent,
    /// or as it was given. The password goes from the first ':' after "://" to the authority's last '@', white space and all.
    /// </summary>
    private static string MaskProxyPassword(string text, Uri proxy)
    {
        foreach (var url in (string[])[proxy.ToString(), proxy.AbsoluteUri, proxy.OriginalString])
        {
            var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
            if (start < 3 || !text.Contains(url, StringComparison.Ordinal))
            {
                continue;
            }

            var end = url.IndexOf('/', start);
            var authority = url.AsSpan(start, (end < 0 ? url.Length : end) - start);
            var at = authority.LastIndexOf('@');
            var colon = at < 0 ? -1 : authority[..at].IndexOf(':');
            if (colon >= 0 && at > colon + 1)
            {
                text = text.Replace(url, string.Concat(url.AsSpan(0, start + colon + 1), "***", url.AsSpan(start + at)), StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    /// <paramref name="text"/> with the password of each URL in it masked as <c>***</c>. .NET puts a proxy's URL in some
    /// messages (a tunnel the proxy refused), with the user and password the proxy setting or HTTPS_PROXY gives.
    /// </summary>
    internal static string MaskUrlPasswords(string text)
    {
        StringBuilder? masked = null;
        var copied = 0;
        for (var scheme = text.IndexOf("://", StringComparison.Ordinal); scheme >= 0; scheme = text.IndexOf("://", scheme + 3, StringComparison.Ordinal))
        {
            var start = scheme + 3;
            var end = start;
            while (end < text.Length && text[end] is not ('/' or '?' or '#') && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            // The user info ends at the authority's last '@' (System.Uri escapes any other); the password follows its first ':'.
            var authority = text.AsSpan(start, end - start);
            var at = authority.LastIndexOf('@');
            var colon = at < 0 ? -1 : authority[..at].IndexOf(':');
            if (colon >= 0 && at > colon + 1)
            {
                masked ??= new StringBuilder(text.Length);
                masked.Append(text, copied, start + colon + 1 - copied).Append("***");
                copied = start + at;
            }
        }

        return masked is null ? text : masked.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// How a header value is sent: as typed, in UTF-8, as curl sends it (.NET would refuse any value that is not ASCII).
    /// A Cookie header typed in ASCII, or not typed, carries the jar's cookies as they came: .NET reads response headers,
    /// Set-Cookie included, as Latin-1, so Latin-1 gives the server its bytes back.
    /// </summary>
    private static Encoding HeaderEncoding(string name, HttpRequestMessage message) =>
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        && (!message.Headers.NonValidated.TryGetValues("Cookie", out var typed) || typed.All(value => Ascii.IsValid(value)))
            ? Encoding.Latin1
            : Encoding.UTF8;

    /// <summary>
    /// Collects connection timing and TLS details from the callbacks of one handler (they run on pool threads). A handler
    /// serves one origin with one client certificate: redirects elsewhere get handlers (and captures) of their own.
    /// </summary>
    private sealed class ConnectionCapture(Stopwatch clock, Uri target, ClientCertificateSelection? selection)
    {
        private readonly Lock _gate = new();
        private readonly List<(TlsConnectionInfo Info, SslStream? Stream)> _tls = [];
        private int _connections;
        private TimeSpan? _dnsStart;
        private TimeSpan? _dnsEnd;
        private TimeSpan? _connectStart;
        private TimeSpan? _connectEnd;
        private TimeSpan? _tlsValidated;
        private string? _remoteEndPoint;
        private bool _clientCertificateRequested;
        private string[] _acceptableIssuers = [];
        private ClientCertificateSelection? _presented;
        private bool _presentedViaContext;

        /// <summary>The client certificate this handler presents (null: none).</summary>
        public ClientCertificateSelection? Selection { get; } = selection;

        /// <summary>The proxy the handler sends through (the system's unless the request names one); null for none.</summary>
        public IWebProxy? Proxy { get; set; }

        /// <summary>
        /// The URL of the proxy that a request to <paramref name="uri"/> goes through, or null. Only for an error: the system's
        /// can take long to find (Windows). A lookup that throws means no proxy, as for the handler.
        /// </summary>
        public Uri? ProxyUri(Uri uri)
        {
            try
            {
                return Proxy is { } proxy && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is { IsAbsoluteUri: true } address ? address : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _connectEnd is not null;
                }
            }
        }

        /// <summary>Whether a redirect to <paramref name="next"/> with <paramref name="certificate"/> can reuse this handler.</summary>
        public bool Serves(Uri next, ClientCertificateSelection? certificate) =>
            IsSameOrigin(next, target) && ReferenceEquals(certificate?.Certificate, Selection?.Certificate);

        public X509Certificate2? SelectClientCertificate(X509Certificate? remoteCertificate, string[] acceptableIssuers)
        {
            // Windows and macOS call this once before the handshake (no remote certificate yet) and again when the
            // server actually requests a client certificate, unless (Windows) .NET reuses the credential of an earlier
            // connection with the same certificate, which answers itself. Linux calls it only before the handshake and
            // reuses its answer. Then the request shows only in the certificate sent (see CollectTls).
            if (remoteCertificate is not null || acceptableIssuers.Length > 0)
            {
                lock (_gate)
                {
                    _clientCertificateRequested = true;
                    _acceptableIssuers = acceptableIssuers;
                    _presented = Selection;
                }
            }

            return Selection?.Certificate.Certificate;
        }

        public void RecordContextCertificate()
        {
            lock (_gate)
            {
                _presented = Selection;
                _presentedViaContext = true;
            }
        }

        public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var endpoint = context.DnsEndPoint;
            var first = Interlocked.Increment(ref _connections) == 1;
            var dnsStart = clock.Elapsed;
            var addresses = IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
            var dnsEnd = clock.Elapsed;

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                var connectStart = clock.Elapsed;
                await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
                if (first)
                {
                    lock (_gate)
                    {
                        _dnsStart = dnsStart;
                        _dnsEnd = dnsEnd;
                        _connectStart = connectStart;
                        _connectEnd = clock.Elapsed;
                        _remoteEndPoint = socket.RemoteEndPoint?.ToString();
                    }
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, PreparedRequest request)
        {
            var stream = sender as SslStream;
            var info = new TlsConnectionInfo
            {
                Host = stream?.TargetHostName is { Length: > 0 } host ? host : target.Host,
                PolicyErrors = errors,
            };

            if (chain is not null && chain.ChainElements.Count > 0)
            {
                foreach (var element in chain.ChainElements)
                {
                    info.ServerChain.Add(CertificateSummary.From(element.Certificate));
                }

                foreach (var status in chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError))
                {
                    info.ChainStatus.Add($"{status.Status}: {status.StatusInformation.Trim()}");
                }
            }
            else if (certificate is not null)
            {
                using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                info.ServerChain.Add(CertificateSummary.From(copy));
            }

            if (errors == SslPolicyErrors.None)
            {
                (info.Accepted, info.AcceptReason) = (true, "trusted by the operating system");
            }
            else if (!request.StrictSsl)
            {
                (info.Accepted, info.AcceptReason) = (true, "SSL certificate verification is turned off");
            }
            else if (errors == SslPolicyErrors.RemoteCertificateChainErrors && certificate is not null && chain is not null
                     && request.ExtraTrustedCertificates is { Count: > 0 } extra && ValidateWithExtraTrust(certificate, chain, extra))
            {
                (info.Accepted, info.AcceptReason) = (true, "trusted through the workspace CA bundle");
            }
            else
            {
                info.Accepted = false;
                info.AcceptReason = errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "the host name does not match the certificate"
                    : errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) ? "the server sent no certificate"
                    : "the certificate chain is not trusted";
            }

            lock (_gate)
            {
                _tlsValidated ??= clock.Elapsed;
                _tls.Add((info, stream));
            }

            return info.Accepted;
        }

        public TlsConnectionInfo? FirstRejected()
        {
            lock (_gate)
            {
                return _tls.Select(t => t.Info).FirstOrDefault(i => !i.Accepted);
            }
        }

        public TimingBreakdown BuildTiming(TimeSpan? headersAt, TimeSpan total)
        {
            lock (_gate)
            {
                var timing = new TimingBreakdown { Total = total, RemoteEndPoint = _remoteEndPoint };
                if (_dnsStart is { } dnsStart && _dnsEnd is { } dnsEnd)
                {
                    timing.Dns = dnsEnd - dnsStart;
                }

                if (_connectStart is { } connectStart && _connectEnd is { } connectEnd)
                {
                    timing.Connect = connectEnd - connectStart;
                    if (_tlsValidated is { } validated && validated >= connectEnd)
                    {
                        timing.Tls = validated - connectEnd;
                    }
                }

                if (headersAt is { } headers)
                {
                    var ready = _tlsValidated ?? _connectEnd ?? TimeSpan.Zero;
                    timing.Waiting = headers - ready;
                    timing.Download = total - headers;
                }

                return timing;
            }
        }

        public List<TlsConnectionInfo> CollectTls()
        {
            lock (_gate)
            {
                foreach (var (info, stream) in _tls)
                {
                    info.ClientCertificateRequested |= _clientCertificateRequested;
                    info.AcceptableIssuers.AddRange(_acceptableIssuers);

                    // Read again while the connection is open: a server may ask for the certificate after the handshake (a
                    // TLS 1.2 renegotiation), which the selection callback may not see. One that is closed keeps the record.
                    ReadSession(info, stream);

                    // The certificate chosen is shown when the server asked for it, or (sent with its chain, through a
                    // context) when the handshake did not complete.
                    if (info.ClientCertificate is null && _presented is { } presented
                        && (_clientCertificateRequested || (_presentedViaContext && info.Protocol == SslProtocols.None)))
                    {
                        info.ClientCertificate = presented.Summary;
                        info.ClientCertificateOrigin = info.Protocol == SslProtocols.None ? $"{presented.Origin}; handshake did not complete" : presented.Origin;
                    }
                }

                return _tls.Select(t => t.Info).ToList();
            }
        }

        /// <summary>
        /// The handler's <see cref="SocketsHttpHandler.PlaintextStreamFilter"/>: records the TLS session of a new connection
        /// as soon as its handshake has completed. A connection the server closes (<c>Connection: close</c>, HTTP/1.0) is
        /// disposed once its response has been read, and its session with it.
        /// </summary>
        public ValueTask<Stream> RecordSession(SocketsHttpPlaintextStreamFilterContext context, CancellationToken cancellationToken)
        {
            if (context.PlaintextStream is SslStream stream)
            {
                lock (_gate)
                {
                    foreach (var (info, tracked) in _tls)
                    {
                        if (ReferenceEquals(tracked, stream))
                        {
                            ReadSession(info, stream);
                        }
                    }
                }
            }

            return ValueTask.FromResult(context.PlaintextStream);
        }

        /// <summary>The protocol, cipher, ALPN and client certificate of a session whose connection is still open.</summary>
        private void ReadSession(TlsConnectionInfo info, SslStream? stream)
        {
            try
            {
                if (stream is not { IsAuthenticated: true })
                {
                    return;
                }

                info.Protocol = stream.SslProtocol;
                info.CipherSuite = stream.NegotiatedCipherSuite;
                var alpn = stream.NegotiatedApplicationProtocol;
                info.ApplicationProtocol = alpn.Protocol.IsEmpty ? null : alpn.ToString();
                if (stream.LocalCertificate is { } local)
                {
                    using var copy = X509CertificateLoader.LoadCertificate(local.GetRawCertData());
                    info.ClientCertificate = CertificateSummary.From(copy);
                    info.ClientCertificateOrigin = Selection?.Origin ?? "selected by the OS";

                    // A client sends its certificate only when the server asks for one, and except on macOS
                    // LocalCertificate is the certificate sent (there, the one chosen, sent or not). This also tells when
                    // the selection callback did not see the request.
                    if (!OperatingSystem.IsMacOS())
                    {
                        info.ClientCertificateRequested = true;
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or CryptographicException)
            {
                // The connection (or a certificate reloaded meanwhile) was torn down; keep what the callbacks recorded.
            }
        }

        private static bool ValidateWithExtraTrust(X509Certificate certificate, X509Chain chain, X509Certificate2Collection extra)
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;

            // Like the OS check: a certificate issued for other purposes does not authenticate a server.
            custom.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            foreach (var ca in extra)
            {
                if (ca.SubjectName.RawData.AsSpan().SequenceEqual(ca.IssuerName.RawData))
                {
                    custom.ChainPolicy.CustomTrustStore.Add(ca);
                }
                else
                {
                    custom.ChainPolicy.ExtraStore.Add(ca);
                }
            }

            foreach (var element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }

            return custom.Build(leaf);
        }
    }
}
