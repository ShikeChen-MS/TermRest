using System.Text;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Responses that stream (server-sent events, JSON lines): read as they arrive, and stopped keeping what arrived.</summary>
public class StreamingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static PreparedRequest Prepare(string url, TimeSpan? timeout = null) =>
        new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { Timeout = timeout }).Prepare(new HttpRequestSpec { Url = url }, null, AppContext.BaseDirectory);

    /// <summary>Keeps what it sees; stops the stream once it has seen <paramref name="stopAfter"/> events (or lines).</summary>
    private sealed class Watcher(int stopAfter = 0, string separator = "\n\n") : IStreamWatcher, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly StringBuilder _text = new();

        public CancellationToken Stopping => _stop.Token;

        public ResponseSnapshot? Head { get; private set; }

        public int Parts { get; private set; }

        public string Text => _text.ToString();

        public void Started(ResponseSnapshot head) => Head = head;

        public void Dispose() => _stop.Dispose();

        public void Received(ReadOnlySpan<byte> data)
        {
            Parts++;
            _text.Append(Encoding.UTF8.GetString(data));
            if (stopAfter > 0 && Text.Split(separator).Length > stopAfter)
            {
                _stop.Cancel();
            }
        }
    }

    [Theory]
    [InlineData("events?count=3&delay=30")]
    [InlineData("events?count=3&delay=30&gzip")] // decoded as it arrives
    public async Task A_stream_is_read_as_it_arrives_and_its_watcher_sees_each_part(string path)
    {
        await using var server = await EchoServer.StartAsync();
        var request = Prepare(server.Url(path));
        using var watcher = new Watcher();
        request.StreamWatcher = watcher;

        var exchange = await new HttpExecutor().SendAsync(request, _ct);

        var response = exchange.Response!;
        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        Assert.Equal((200, "text/event-stream; charset=utf-8"), (watcher.Head?.StatusCode, watcher.Head?.ContentType));
        Assert.Empty(watcher.Head!.Body);
        Assert.True(watcher.Parts >= 3, $"{watcher.Parts} parts");
        var body = Encoding.UTF8.GetString(response.Body);
        Assert.Equal(watcher.Text, body);
        Assert.Equal(string.Concat(Enumerable.Range(1, 3).Select(n => $"id: {n}\nevent: tick\ndata: {{\"n\":{n}}}\n\n")), body);
        Assert.False(response.Stopped);
        Assert.Empty(request.Warnings);
        if (path.EndsWith("gzip", StringComparison.Ordinal))
        {
            Assert.NotEqual(response.Body.LongLength, response.ReceivedBodySize);
        }

        var events = ServerSentEvents.Parse(response.Body, _ct);
        Assert.Equal(["1", "2", "3"], events.Select(e => e.Id(response.Body)));
        Assert.All(events, e => Assert.Equal("tick", e.Type));
        Assert.Equal("{\"n\":2}", events[1].Data(response.Body));
    }

    [Fact]
    public async Task A_stream_its_watcher_stops_keeps_what_arrived()
    {
        await using var server = await EchoServer.StartAsync();
        var request = Prepare(server.Url("events?count=0&delay=10&ndjson"));
        using var watcher = new Watcher(stopAfter: 3, separator: "\n");
        request.StreamWatcher = watcher;

        var exchange = await new HttpExecutor().SendAsync(request, _ct);

        Assert.False(exchange.Cancelled);
        var response = exchange.Response!;
        Assert.True(response.Stopped);
        Assert.StartsWith("{\"n\":1}\n{\"n\":2}\n{\"n\":3}\n", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Equal(["The stream was stopped: its body is what arrived until then."], request.Warnings);
    }

    [Fact]
    public async Task The_request_timeout_stops_a_stream_and_keeps_what_arrived()
    {
        await using var server = await EchoServer.StartAsync();
        var request = Prepare(server.Url("events?count=0&delay=20"), timeout: TimeSpan.FromMilliseconds(400));

        var exchange = await new HttpExecutor().SendAsync(request, _ct);

        var response = exchange.Response!;
        Assert.True(response.Stopped);
        Assert.StartsWith("id: 1\nevent: tick\n", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Equal(["The stream was stopped at the request timeout (400 ms): its body is what arrived until then."], request.Warnings);
    }

    [Fact]
    public async Task Cancelling_the_request_while_it_streams_keeps_nothing()
    {
        await using var server = await EchoServer.StartAsync();
        var request = Prepare(server.Url("events?count=0&delay=10"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cancel.CancelAfter(300);

        var exchange = await new HttpExecutor().SendAsync(request, cancel.Token);

        Assert.True(exchange.Cancelled);
        Assert.Null(exchange.Response);
    }

    [Fact]
    public void Server_sent_events_are_read_as_the_html_standard_reads_them()
    {
        var body = Encoding.UTF8.GetBytes(
            "\uFEFF: a comment\r\n" +
            "data: first\r\ndata:  two lines\r\n\r\n" +
            "event: update\nid: 7\ndata: {\"a\":1}\n\n" +
            "event: no data\n\n" +
            "data: after\rid\r\r" + // an id with no value clears it
            "data: the last, never ended\n");

        var events = ServerSentEvents.Parse(body, _ct);

        Assert.Equal([("message", null), ("update", "7"), ("message", "")], events.Select(e => (e.Type, e.Id(body))));
        Assert.Equal(["first\n two lines", "{\"a\":1}", "after"], events.Select(e => e.Data(body)));
        Assert.StartsWith(": a comment", Encoding.UTF8.GetString(body, events[0].Start, events[0].Length), StringComparison.Ordinal);
    }
}
