using System.Text;

namespace TermRest.Core.Http;

/// <summary>
/// Watches a response that streams (see <see cref="ResponseStreams"/>) while it arrives, and can stop it: what arrived is
/// then its body, as a whole response's would be. Set it on <see cref="PreparedRequest.StreamWatcher"/> before the request
/// goes out; responses that don't stream are read whole, as always. Called on the thread that sends.
/// </summary>
public interface IStreamWatcher
{
    /// <summary>Cancelled to stop the stream: what arrived is kept as its body (the request is not cancelled).</summary>
    CancellationToken Stopping { get; }

    /// <summary>The response's status and headers arrived, and its body streams: it comes next, part by part.</summary>
    void Started(ResponseSnapshot head);

    /// <summary>A part of the body as it arrived, decoded (gzip …), within the maximum response size.</summary>
    void Received(ReadOnlySpan<byte> data);
}

/// <summary>
/// The responses whose body is read as it arrives, part by part: server-sent events and JSON lines. A watcher sees them
/// arrive and can stop them; a request timeout that ends one keeps what arrived too.
/// </summary>
public static class ResponseStreams
{
    private static readonly string[] MediaTypes =
    [
        "text/event-stream",
        "application/x-ndjson", "application/ndjson", "application/jsonl", "application/x-jsonlines", "application/json-seq", "application/stream+json",
    ];

    /// <summary>Whether a response with this Content-Type streams.</summary>
    public static bool Streams(string? contentType) => MediaType(contentType) is { } type && MediaTypes.Contains(type);

    /// <summary>Whether a response with this Content-Type is server-sent events.</summary>
    public static bool IsEventStream(string? contentType) => MediaType(contentType) == "text/event-stream";

    private static string? MediaType(string? contentType) =>
        contentType is null ? null : contentType.AsSpan(0, contentType.IndexOf(';') is >= 0 and var end ? end : contentType.Length).Trim().ToString().ToLowerInvariant();
}

/// <summary>An event of a server-sent events body: where it is in the body, its type, and where its last event id is.</summary>
/// <param name="Start">Where its lines start in the body (bytes).</param>
/// <param name="Length">How long its lines are, without the blank line that ends it (bytes).</param>
/// <param name="Type">Its <c>event:</c>, or <c>message</c>.</param>
/// <param name="IdLine">
/// Where the last <c>id:</c> line so far starts in the body (an earlier event's, maybe: the id carries over to the events
/// that follow, as browsers have it); -1 for none.
/// </param>
public readonly record struct ServerSentEvent(int Start, int Length, string Type, int IdLine)
{
    /// <summary>Its last event id, read from the body; null for none.</summary>
    public string? Id(byte[] body) => IdLine < 0 ? null : ServerSentEvents.FieldAt(body, IdLine).Value;

    /// <summary>Its data: the values of its <c>data:</c> lines, joined with line breaks, read from the body (within its first <paramref name="maxBytes"/>).</summary>
    public string Data(byte[] body, int maxBytes = int.MaxValue)
    {
        var data = new StringBuilder();
        var first = true;
        foreach (var (name, value) in ServerSentEvents.Fields(body.AsSpan(Start, Math.Min(Length, maxBytes))))
        {
            if (name == "data")
            {
                data.Append(first ? "" : "\n").Append(value);
                first = false;
            }
        }

        return data.ToString();
    }
}

/// <summary>
/// Reads the events of a server-sent events body (<c>text/event-stream</c>) as the HTML standard does: lines of fields
/// (<c>event:</c>, <c>data:</c>, <c>id:</c>, <c>retry:</c>; a line that starts with a colon is a comment), and an event at
/// each blank line that follows data. An event keeps only where it is in the body, so that a long stream's thousands of
/// events take little memory; its data is read from the body when it is shown.
/// </summary>
public static class ServerSentEvents
{
    public static List<ServerSentEvent> Parse(byte[] body, CancellationToken cancellationToken = default)
    {
        var events = new List<ServerSentEvent>();
        var types = new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = "message" };
        Span<char> typeBuffer = stackalloc char[128];
        var idLine = -1;
        string? type = null;
        var hasData = false;
        var start = body.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
        var eventStart = start;
        var eventEnd = start;
        for (var at = start; at < body.Length;)
        {
            var next = Line(body, at, out var end);
            var line = body.AsSpan(at, end - at);
            if (line.Length == 0)
            {
                if (hasData)
                {
                    events.Add(new ServerSentEvent(eventStart, eventEnd - eventStart, type ?? "message", idLine));
                    if ((events.Count & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                (type, hasData, eventStart) = (null, false, next);
            }
            else if (line[0] != (byte)':')
            {
                var colon = line.IndexOf((byte)':');
                var field = colon < 0 ? line : line[..colon];
                if (field.SequenceEqual("data"u8))
                {
                    hasData = true;
                }
                else if (field.SequenceEqual("event"u8))
                {
                    type = Intern(Value(line, colon), types, typeBuffer);
                }
                else if (field.SequenceEqual("id"u8) && !Value(line, colon).Contains((byte)0))
                {
                    idLine = at;
                }

                eventEnd = at + line.Length;
            }
            else
            {
                eventEnd = at + line.Length; // a comment, kept with its event
            }

            at = next;
        }

        return events;
    }

    /// <summary>An event type as one string for all the events of that type: a long stream's events don't take one each.</summary>
    private static string Intern(ReadOnlySpan<byte> value, Dictionary<string, string> types, Span<char> buffer)
    {
        if (value.Length <= buffer.Length)
        {
            var length = Encoding.UTF8.GetChars(value, buffer);
            if (types.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(buffer[..length], out var known))
            {
                return known;
            }
        }

        var text = Encoding.UTF8.GetString(value);
        return types.TryGetValue(text, out var existing) ? existing : types[text] = text;
    }

    /// <summary>The field of the line at <paramref name="at"/> in <paramref name="body"/>.</summary>
    internal static (string Name, string Value) FieldAt(byte[] body, int at)
    {
        Line(body, at, out var end);
        return Field(body.AsSpan(at, end - at));
    }

    /// <summary>A field's value: what follows its colon, a space after it left out; empty without one.</summary>
    private static ReadOnlySpan<byte> Value(ReadOnlySpan<byte> line, int colon)
    {
        if (colon < 0)
        {
            return [];
        }

        var value = line[(colon + 1)..];
        return value.Length > 0 && value[0] == ' ' ? value[1..] : value;
    }

    /// <summary>The fields of an event's lines, in order (comments left out).</summary>
    internal static IEnumerable<(string Name, string Value)> Fields(ReadOnlySpan<byte> lines)
    {
        var fields = new List<(string, string)>();
        for (var at = 0; at < lines.Length;)
        {
            var next = Line(lines, at, out var end);
            if (end > at && lines[at] != (byte)':')
            {
                fields.Add(Field(lines[at..end]));
            }

            at = next;
        }

        return fields;
    }

    /// <summary>Where the next line starts; <paramref name="end"/>: where the line at <paramref name="at"/> ends, before its CRLF, LF or CR.</summary>
    private static int Line(ReadOnlySpan<byte> text, int at, out int end)
    {
        var length = text[at..].IndexOfAny((byte)'\n', (byte)'\r');
        if (length < 0)
        {
            end = text.Length;
            return text.Length;
        }

        end = at + length;
        return end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
    }

    /// <summary>A field's name and value (a space after the colon is left out); a comment's name is empty.</summary>
    private static (string Name, string Value) Field(ReadOnlySpan<byte> line)
    {
        var colon = line.IndexOf((byte)':');
        return (Encoding.UTF8.GetString(colon < 0 ? line : line[..colon]), Encoding.UTF8.GetString(Value(line, colon)));
    }
}
