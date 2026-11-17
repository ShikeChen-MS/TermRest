using System.Buffers;
using System.Text;
using System.Text.Json;
using TermRest.Core.Json;

namespace TermRest.Core.Http;

/// <summary>The values a JSONPath query selects in a body, written as a JSON array.</summary>
/// <param name="Text">The array: all the values, or the first <paramref name="Shown"/> when they would not fit the length asked.</param>
/// <param name="Count">How many values the query selects.</param>
/// <param name="Shown">How many of them the array holds.</param>
public sealed record FilteredBody(string Text, int Count, int Shown);

/// <summary>Applies a JSONPath query to a response body.</summary>
public static class BodyFilter
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };

    private static readonly JsonWriterOptions Indented = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = MinimalJsonEncoder.Instance,
        MaxDepth = 1000,
    };

    private static readonly JsonWriterOptions Compact = new() { Encoder = MinimalJsonEncoder.Instance, MaxDepth = 1000 };

    /// <summary>
    /// The values <paramref name="query"/> selects in <paramref name="body"/> (decoded as it is shown: see
    /// <see cref="BodyFormatter.Decode"/>; NDJSON and JSON Lines as an array of their lines), as a JSON array, two-space
    /// indented or on one line. Values that would take it beyond <paramref name="maxBytes"/> of UTF-8 are left out. A
    /// <see cref="JsonPathException"/> says why a body cannot be filtered: it is empty, binary or not JSON.
    /// </summary>
    public static FilteredBody Apply(JsonPath query, byte[] body, string? contentType, bool indented = true, long maxBytes = long.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (body.Length == 0)
        {
            throw new JsonPathException("The body is empty.");
        }

        if (!BodyFormatter.LooksLikeText(body, contentType))
        {
            throw new JsonPathException("The body is binary, not JSON.");
        }

        var text = BodyFormatter.Decode(body, contentType);
        try
        {
            using var document = Parse(text, contentType);
            var nodes = query.Select(document.RootElement, cancellationToken);
            return Write(nodes, indented, maxBytes, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            // System.Text.Json reads "\ud83d" (half of an emoji, cut by JavaScript), then fails on first access to it.
            throw new JsonPathException("The body has a string holding half of a character (an unpaired surrogate such as \\ud83d).");
        }
    }

    private static JsonDocument Parse(string text, string? contentType)
    {
        if (BodyFormatter.SubtypeHasWord(contentType, "ndjson", "jsonl", "jsonlines") && Lines(text) is { } lines)
        {
            return lines;
        }

        try
        {
            return JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonPathException(ex.LineNumber is { } line
                ? $"The body is not JSON: line {line + 1}, byte {ex.BytePositionInLine + 1}."
                : "The body is not JSON.");
        }
    }

    /// <summary>JSON Lines: one array of the values on its lines; null when that is not what the body holds.</summary>
    private static JsonDocument? Lines(string text)
    {
        var builder = new StringBuilder(text.Length + 16).Append('[');
        var first = true;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (!line.IsWhiteSpace())
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(line);
                first = false;
            }

            builder.Append('\n');
        }

        try
        {
            return JsonDocument.Parse(builder.Append(']').ToString(), DocumentOptions);
        }
        catch (JsonException)
        {
            return null; // one value written over several lines, say: read as a whole
        }
    }

    private static FilteredBody Write(IReadOnlyList<JsonElement> nodes, bool indented, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var shown = 0;
        using (var writer = new Utf8JsonWriter(buffer, indented ? Indented : Compact))
        {
            writer.WriteStartArray();
            foreach (var node in nodes)
            {
                if (shown > 0 && writer.BytesCommitted + writer.BytesPending > maxBytes)
                {
                    break;
                }

                node.WriteTo(writer);
                if ((++shown & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            writer.WriteEndArray();
        }

        return new FilteredBody(Encoding.UTF8.GetString(buffer.WrittenSpan), nodes.Count, shown);
    }
}
