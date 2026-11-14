using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TermRest.Cli.Tests.Infrastructure;

/// <summary>
/// Minimal HTTP/1.1 server for the commands under test (plain sockets: no web framework next to termrest.dll, which
/// must find its own dependencies). <c>/bytes</c> returns a binary body, <c>/octets</c> and <c>/protobuf</c> binary
/// bodies without a NUL byte, <c>/xlsx</c> an Excel workbook, <c>/large</c> 1.5 MB of zeros, <c>/utf8</c>
/// <see cref="Utf8Json"/>, <c>/token</c> a login response with <see cref="Token"/>, <c>/wait</c> no response until the
/// server stops (see <see cref="Waiting"/>); any other path returns the request as JSON.
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    /// <summary>The body of <c>/bytes</c>: every byte value, then line breaks and a NUL that text output would change.</summary>
    public static readonly byte[] Bytes = [.. Enumerable.Range(0, 256).Select(i => (byte)i), .. "\r\n\n\0end"u8];

    /// <summary>The body of <c>/octets</c> (application/octet-stream): bytes that are not UTF-8, line breaks, a byte order mark, an escape sequence.</summary>
    public static readonly byte[] Octets =
        [0xC3, 0x28, 0xA0, 0xA1, 0xE2, 0x28, 0xA1, 0xF0, 0x90, 0x28, 0xBC, 0xFF, 0xFE, 0x0D, 0x0A, 0x0A, 0x7F, 0x80, 0x81, 0x1B, 0x5B, 0x33, 0x31, 0x6D, 0xEF, 0xBB, 0xBF, 0x41, 0x0D, 0x01, 0x02, 0x03];

    /// <summary>The body of <c>/protobuf</c> (application/x-protobuf): field 1 "abc", field 2 150.</summary>
    public static readonly byte[] Protobuf = [0x0A, 0x03, 0x61, 0x62, 0x63, 0x10, 0x96, 0x01];

    /// <summary>The body of <c>/xlsx</c>: the start of an Excel workbook, a ZIP file, though its type names XML formats.</summary>
    public static readonly byte[] Workbook = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00, 0x00, 0x00, 0x21, 0x00, 0x8C, 0x5A, 0xED, 0xC1, 0x6E, 0x01, 0x00, 0x00];

    /// <summary>The body of <c>/utf8</c>: characters that no console code page has all of.</summary>
    public const string Utf8Json = "{\"name\":\"Zoë – 東京 ✓\"}";

    /// <summary>The access token <c>/token</c> returns (40 characters).</summary>
    public const string Token = "tk_live_51HxQ2eKz8Yb4Rf7Lm0Np3Vs6Wc9Ty2A";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private int _requests;

    private TestServer()
    {
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _accepting = AcceptAsync();
    }

    /// <summary>Base URL without a trailing slash, e.g. http://127.0.0.1:5123.</summary>
    public string BaseUrl { get; }

    /// <summary>How many requests it received.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Completes when a request to <c>/wait</c> arrives.</summary>
    public Task Waiting => _waiting.Task;

    public static Task<TestServer> StartAsync() => Task.FromResult(new TestServer());

    private async Task AcceptAsync()
    {
        while (true)
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

            _ = RespondAsync(client);
        }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var received = new MemoryStream();
                var buffer = new byte[4096];
                while (!received.GetBuffer().AsSpan(0, (int)received.Length).EndsWith("\r\n\r\n"u8))
                {
                    var count = await stream.ReadAsync(buffer, _stop.Token);
                    if (count == 0)
                    {
                        return;
                    }

                    received.Write(buffer, 0, count);
                }

                Interlocked.Increment(ref _requests);
                var requestLine = Encoding.ASCII.GetString(received.ToArray()).Split("\r\n")[0].Split(' ');
                var path = requestLine[1].Split('?')[0];
                if (path == "/wait")
                {
                    _waiting.TrySetResult();
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                var (body, type) = path switch
                {
                    "/bytes" => (Bytes, "image/png"),
                    "/octets" => (Octets, "application/octet-stream"),
                    "/protobuf" => (Protobuf, "application/x-protobuf"),
                    "/xlsx" => (Workbook, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
                    "/large" => (new byte[1536 * 1024], "application/octet-stream"),
                    "/utf8" => (Encoding.UTF8.GetBytes(Utf8Json), "application/json; charset=utf-8"),
                    "/token" => (Encoding.UTF8.GetBytes($"{{\"access_token\":\"{Token}\",\"token_type\":\"Bearer\",\"expires_in\":3600}}"), "application/json"),
                    _ => (Encoding.UTF8.GetBytes(new JsonObject { ["method"] = requestLine[0], ["path"] = path }.ToJsonString()), "application/json"),
                };
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // The client went away, or the server is stopping.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
