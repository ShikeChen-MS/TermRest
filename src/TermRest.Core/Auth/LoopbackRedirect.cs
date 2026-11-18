using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TermRest.Core.Auth;

/// <summary>
/// Receives the authorization response of a sign-in on a loopback redirect URI (<c>http://localhost:port/path</c>,
/// <c>http://127.0.0.1…</c>, <c>http://[::1]…</c>), as RFC 8252 has native apps do: the browser is redirected there with
/// the code, and shows a page that says the sign-in is done.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    /// <summary>The longest request head read: a redirect's URL with its code and state is far shorter.</summary>
    private const int MaxHead = 32 * 1024;

    private readonly List<TcpListener> _listeners;
    private readonly string _path;
    private readonly string _origin;

    private LoopbackRedirect(List<TcpListener> listeners, string redirectUri, string origin, string path)
    {
        _listeners = listeners;
        RedirectUri = redirectUri;
        _origin = origin;
        _path = path;
    }

    /// <summary>The redirect URI it listens on (for an empty one, <c>http://127.0.0.1:port/</c> with the port it chose).</summary>
    public string RedirectUri { get; }

    /// <summary>
    /// Starts listening on <paramref name="redirectUri"/>: null when it is not an http URL of a loopback host, which the
    /// browser goes to elsewhere (the user pastes the URL it ends on instead). An empty one listens on a free port of
    /// 127.0.0.1. Throws <see cref="OAuth2Exception"/> when the port cannot be listened on (another program has it).
    /// </summary>
    public static LoopbackRedirect? Start(string redirectUri)
    {
        IPAddress[] addresses;
        int port;
        string path;
        if (redirectUri.Length == 0)
        {
            (addresses, port, path) = ([IPAddress.Loopback], 0, "/");
        }
        else
        {
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length > 0)
            {
                return null;
            }

            // localhost may be either address to the browser.
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                addresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
            }
            else if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))
            {
                addresses = [address];
            }
            else
            {
                return null;
            }

            (port, path) = (uri.Port, uri.AbsolutePath);
        }

        var listeners = new List<TcpListener>();
        SocketException? failure = null;
        foreach (var address in addresses)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                listeners.Add(listener);
                port = ((IPEndPoint)listener.LocalEndpoint).Port; // the other address listens on the same port
            }
            catch (SocketException ex)
            {
                listener.Dispose();
                failure ??= ex;
            }
        }

        if (listeners.Count == 0)
        {
            throw new OAuth2Exception($"OAuth 2.0: TermRest could not listen on {(redirectUri.Length == 0 ? "a loopback port" : redirectUri)} for the sign-in's response: {failure!.Message}");
        }

        var origin = redirectUri.Length == 0 ? $"http://127.0.0.1:{port}" : new Uri(redirectUri).GetLeftPart(UriPartial.Authority);
        return new LoopbackRedirect(listeners, redirectUri.Length == 0 ? origin + "/" : redirectUri, origin, path);
    }

    /// <summary>
    /// The URL the browser was redirected to (its query holds the response's code or error), once a request for the
    /// redirect URI's path comes; requests for other paths (a favicon) get a 404. Throws
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var accepting = _listeners.Select(listener => AcceptAsync(listener, received, stop.Token)).ToList();
        try
        {
            return await received.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(accepting).ConfigureAwait(false);
        }
    }

    private async Task AcceptAsync(TcpListener listener, TaskCompletionSource<string> received, CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            // Each on its own: a browser opens connections it may send nothing on.
            connections.Add(HandleAsync(client, received, cancellationToken));
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private async Task HandleAsync(TcpClient client, TaskCompletionSource<string> received, CancellationToken cancellationToken)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                if (await RespondAsync(client.GetStream(), timeout.Token).ConfigureAwait(false) is { } url)
                {
                    received.TrySetResult(url);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The browser went away, or it is over.
            }
        }
    }

    /// <summary>Reads a request and answers it; the URL it was for when it brings the authorization response.</summary>
    private async Task<string?> RespondAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        int end;
        while ((end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (length == buffer.Length)
            {
                await WriteAsync(stream, "431 Request Header Fields Too Large", "Request too large", null, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        var lineEnd = buffer.AsSpan(0, end).IndexOf("\r\n"u8);
        var parts = Encoding.ASCII.GetString(buffer, 0, lineEnd < 0 ? end : lineEnd).Split(' ');
        var target = parts.Length == 3 ? parts[1] : "";
        var query = target.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? target : target[..query];
        if (parts.Length != 3 || parts[0] != "GET" || !string.Equals(path.TrimEnd('/'), _path.TrimEnd('/'), StringComparison.Ordinal))
        {
            await WriteAsync(stream, "404 Not Found", "Not found", null, cancellationToken).ConfigureAwait(false);
            return null;
        }

        // A request without a response in it (the page loaded again) is not the one waited for.
        var fields = OAuth2Response.ParseForm(query < 0 ? "" : target[(query + 1)..]);
        if (!fields.ContainsKey("code") && !fields.ContainsKey("error"))
        {
            await WriteAsync(stream, "400 Bad Request", "No authorization response", "This address has no authorization code or error.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (fields.TryGetValue("error", out var error))
        {
            var description = fields.GetValueOrDefault("error_description");
            await WriteAsync(stream, "200 OK", "Sign-in failed", $"{error}{(string.IsNullOrEmpty(description) ? "" : ": " + description)}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(stream, "200 OK", "Signed in", "TermRest received the sign-in. You can close this tab and go back to TermRest.", cancellationToken).ConfigureAwait(false);
        }

        return _origin + target;
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string title, string? text, CancellationToken cancellationToken)
    {
        var page = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>TermRest: {WebUtility.HtmlEncode(title)}</title></head>" +
            $"<body style=\"font-family: system-ui, sans-serif; margin: 3em\"><h1>{WebUtility.HtmlEncode(title)}</h1>" +
            $"{(text is null ? "" : $"<p>{WebUtility.HtmlEncode(text)}</p>")}</body></html>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {page.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
    }
}
