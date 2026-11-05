using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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

    public async Task<HttpExchange> SendAsync(PreparedRequest request, CancellationToken cancellationToken = default)
    {
        var exchange = new HttpExchange(request) { StartedAt = DateTimeOffset.Now };
        if (request.Errors.Count > 0 || request.Uri is null)
        {
            exchange.ErrorMessage = request.Errors.Count > 0 ? string.Join(" ", request.Errors) : "The request URL is not valid.";
            return exchange;
        }

        var clock = Stopwatch.StartNew();
        var capture = new ConnectionCapture(clock);
        using var handler = CreateHandler(request, capture);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var timeoutSource = request.Timeout is { } timeout && timeout > TimeSpan.Zero ? new CancellationTokenSource(timeout) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource?.Token ?? CancellationToken.None);
        TimeSpan? headersAt = null;
        try
        {
            using var message = CreateMessage(request, exchange);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            headersAt = clock.Elapsed;
            var (body, truncated) = await ReadBodyAsync(response, request.MaxResponseBytes, linked.Token).ConfigureAwait(false);
            exchange.Response = Snapshot(response, body, truncated);
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException or SocketException
                                       or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            exchange.Exception = ex;
            exchange.ErrorMessage = DescribeError(ex, capture);
        }
        finally
        {
            exchange.Timing = capture.BuildTiming(headersAt, clock.Elapsed);
            exchange.Tls.AddRange(capture.CollectTls(request));
        }

        return exchange;
    }

    private SocketsHttpHandler CreateHandler(PreparedRequest request, ConnectionCapture capture)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = request.FollowRedirects,
            MaxAutomaticRedirections = Math.Max(1, request.MaxRedirects),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = request.UseCookies,
            CookieContainer = Cookies,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            Credentials = request.Credential,
            ConnectCallback = capture.ConnectAsync,
        };

        switch (request.Proxy?.Trim().ToLowerInvariant())
        {
            case null or "" or "system":
                handler.UseProxy = true;
                break;
            case "none":
                handler.UseProxy = false;
                break;
            default:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(request.Proxy!.Trim());
                break;
        }

        var selection = request.ClientCertificate;
        var options = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => capture.Validate(sender, certificate, chain, errors, request),

            // Resumed sessions can carry the client identity of an earlier handshake, which would hide certificate
            // changes while testing mTLS. A full handshake per request also gives honest timing.
            AllowTlsResume = false,
        };

        if (selection is not null && selection.Certificate.Chain.Count > 0)
        {
            // An explicit context also sends the intermediates loaded with the certificate.
            options.ClientCertificateContext = SslStreamCertificateContext.Create(
                selection.Certificate.Certificate, selection.Certificate.Chain, offline: true);
            capture.RecordContextCertificate(selection);
        }
        else
        {
            // Always answer the selection explicitly: the configured certificate regardless of the server's list of
            // acceptable issuers (like curl/Postman), or none at all. Without a callback the platform may fall back to
            // a credential cached from an earlier connection to the same host.
            options.LocalCertificateSelectionCallback = (_, _, _, remoteCertificate, acceptableIssuers) =>
                capture.SelectClientCertificate(selection, remoteCertificate, acceptableIssuers)!;
        }

        handler.SslOptions = options;
        return handler;
    }

    private HttpRequestMessage CreateMessage(PreparedRequest request, HttpExchange exchange)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Uri)
        {
            Version = request.HttpVersion == "2" ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var content = request.Body.CreateContent();
        message.Content = content;
        var hasUserAgent = false;
        var hasAccept = false;
        foreach (var (key, value) in request.Headers)
        {
            hasUserAgent |= key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase);
            hasAccept |= key.Equals("Accept", StringComparison.OrdinalIgnoreCase);
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue; // always computed from the body
            }

            if (IsContentHeader(key))
            {
                if (content is null)
                {
                    request.Warnings.Add($"'{key}' was not sent because the request has no body.");
                    continue;
                }

                content.Headers.Remove(key);
                if (!content.Headers.TryAddWithoutValidation(key, value))
                {
                    request.Warnings.Add($"Header '{key}' has an invalid value and was skipped.");
                }
            }
            else if (!message.Headers.TryAddWithoutValidation(key, value))
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

    private static bool IsContentHeader(string name) => name.ToLowerInvariant() switch
    {
        "content-type" or "content-encoding" or "content-language" or "content-location" or "content-md5"
            or "content-range" or "content-disposition" or "expires" or "last-modified" or "allow" => true,
        _ => false,
    };

    private static async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var room = maxBytes - buffer.Length;
                if (read > room)
                {
                    buffer.Write(chunk, 0, (int)Math.Max(0, room));
                    return (buffer.ToArray(), true);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return (buffer.ToArray(), false);
    }

    private static ResponseSnapshot Snapshot(HttpResponseMessage response, byte[] body, bool truncated)
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
            BodyTruncated = truncated,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            FinalUri = response.RequestMessage?.RequestUri,
        };
    }

    private static string DescribeError(Exception ex, ConnectionCapture capture)
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
            var text = e.Message.Trim();
            if (text.Length > 0 && !messages.Contains(text))
            {
                messages.Add(text);
            }
        }

        var summary = string.Join(" → ", messages);
        return ex is HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } hre ? $"{hre.HttpRequestError}: {summary}" : summary;
    }

    /// <summary>Collects connection timing and TLS details from handler callbacks (which run on pool threads).</summary>
    private sealed class ConnectionCapture(Stopwatch clock)
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

        public X509Certificate2? SelectClientCertificate(ClientCertificateSelection? selection, X509Certificate? remoteCertificate, string[] acceptableIssuers)
        {
            // The platform calls this once before the handshake (no remote certificate yet) and again when the
            // server actually requests a client certificate.
            if (remoteCertificate is not null || acceptableIssuers.Length > 0)
            {
                lock (_gate)
                {
                    _clientCertificateRequested = true;
                    _acceptableIssuers = acceptableIssuers;
                    _presented = selection;
                }
            }

            return selection?.Certificate.Certificate;
        }

        public void RecordContextCertificate(ClientCertificateSelection selection)
        {
            lock (_gate)
            {
                _presented = selection;
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
                Host = stream?.TargetHostName is { Length: > 0 } host ? host : request.Uri?.Host ?? "",
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

        public List<TlsConnectionInfo> CollectTls(PreparedRequest request)
        {
            lock (_gate)
            {
                foreach (var (info, stream) in _tls)
                {
                    info.ClientCertificateRequested = _clientCertificateRequested;
                    info.AcceptableIssuers.AddRange(_acceptableIssuers);
                    try
                    {
                        if (stream is { IsAuthenticated: true })
                        {
                            info.Protocol = stream.SslProtocol;
                            info.CipherSuite = stream.NegotiatedCipherSuite;
                            var alpn = stream.NegotiatedApplicationProtocol;
                            info.ApplicationProtocol = alpn.Protocol.IsEmpty ? null : alpn.ToString();
                            if (stream.LocalCertificate is { } local)
                            {
                                using var copy = X509CertificateLoader.LoadCertificate(local.GetRawCertData());
                                info.ClientCertificate = CertificateSummary.From(copy);
                                info.ClientCertificateOrigin = request.ClientCertificate?.Origin ?? "selected by the OS";
                            }
                        }
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                    {
                        // The connection was torn down; keep what the callbacks recorded.
                    }

                    if (info.ClientCertificate is null && _presented is { } presented && (_clientCertificateRequested || _presentedViaContext))
                    {
                        info.ClientCertificate = presented.Certificate.Summary;
                        info.ClientCertificateOrigin = info.Protocol == SslProtocols.None
                            ? $"{presented.Origin}; handshake did not complete"
                            : presented.Origin;
                    }
                }

                return _tls.Select(t => t.Info).ToList();
            }
        }

        private static bool ValidateWithExtraTrust(X509Certificate certificate, X509Chain chain, X509Certificate2Collection extra)
        {
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
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
