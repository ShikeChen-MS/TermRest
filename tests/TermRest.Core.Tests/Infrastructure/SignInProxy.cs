using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>
/// An HTTP proxy that asks for Basic sign-in (407 Proxy Authentication Required) until a request brings the
/// Proxy-Authorization of its user and password, and then answers the request itself (200, "through the proxy"). A
/// tunnel (CONNECT) is only ever refused. Each connection serves one request, so every sign-in is a new connection.
/// </summary>
internal sealed class SignInProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _expected;
    private readonly Task _serving;

    public SignInProxy(string user, string password)
    {
        _expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Each request: its first line and its Proxy-Authorization ("-" without one).</summary>
    public List<string> Requests { get; } = [];

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    var lines = head.Split("\r\n");
                    var authorization = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))?["Proxy-Authorization:".Length..].Trim();
                    lock (Requests)
                    {
                        Requests.Add($"{lines[0]} | {authorization ?? "-"}");
                    }

                    var response = authorization != _expected
                        ? "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"corp\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : lines[0].StartsWith("CONNECT ", StringComparison.Ordinal)
                            ? "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 17\r\nConnection: close\r\n\r\nthrough the proxy";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                    // The client went away.
                }
            }
        }
    }

    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[16384];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), _stop.Token);
            if (count == 0)
            {
                break;
            }

            read += count;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
    }
}
