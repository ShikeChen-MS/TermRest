using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml;

namespace TermRest.Core.Http;

/// <summary>
/// Request bodies sent from a file. The file is read when the request is sent and streamed, never held in memory.
/// A file body can have a type (JSON, XML, text…): the file is then checked first, through a small buffer, so that a
/// broken file is not sent. Without a type it is binary: sent as it is.
/// </summary>
public static class BodyFile
{
    private const int BufferSize = 64 * 1024;

    /// <summary>File body types: Postman's raw body languages, stored as <c>body.file.language</c>.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["json", "xml", "text", "html", "javascript"];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>A known type in lower case, or null (binary).</summary>
    public static string? Normalize(string? language) =>
        language is null ? null : Languages.FirstOrDefault(l => l.Equals(language, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why <paramref name="path"/> cannot be sent as a <paramref name="language"/> body, or null when it can.</summary>
    public static string? Check(string path, string? language, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return $"{path} does not exist.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{name} cannot be read: {ex.Message}";
        }

        using (stream)
        {
            try
            {
                return Normalize(language) switch
                {
                    null => null,
                    "json" => CheckJson(stream, name, cancellationToken),
                    "xml" => CheckXml(stream, name, cancellationToken),
                    _ => CheckText(stream, name),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"{name} cannot be read: {ex.Message}";
            }
        }
    }

    private static string? CheckJson(FileStream stream, string name, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 1000 });
            var length = 0;
            var final = false;
            var first = true;
            long offset = 0; // file position of buffer[0]
            long line = 1;
            long lineStart = 0; // file position where that line starts
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (!final && length < buffer.Length)
                {
                    var read = stream.Read(buffer, length, buffer.Length - length);
                    final = read == 0;
                    length += read;
                }

                var start = 0;
                if (first)
                {
                    first = false;
                    if (WideEncoding(buffer.AsSpan(0, length)) is { } encoding)
                    {
                        return $"{name} is {encoding} text, but JSON must be UTF-8: save it as UTF-8.";
                    }

                    start = buffer.AsSpan(0, length).StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
                    lineStart = start;
                    if (final && buffer.AsSpan(start, length - start).Trim(" \t\r\n"u8).IsEmpty)
                    {
                        return $"{name} is empty.";
                    }
                }

                var data = buffer.AsSpan(start, length - start);
                var reader = new Utf8JsonReader(data, final, state);
                try
                {
                    while (reader.Read())
                    {
                    }
                }
                catch (JsonException ex)
                {
                    return $"{name} is not valid JSON: {Describe(ex.Message, ex.LineNumber + 1, ex.BytePositionInLine + 1)}";
                }

                // The reader does not check the UTF-8 inside strings. Chunks end after whole values, never inside one.
                var consumed = data[..(int)reader.BytesConsumed];
                var position = offset + start;
                if (!Utf8.IsValid(consumed))
                {
                    var at = InvalidUtf8Index(consumed);
                    var before = consumed[..at];
                    var newline = before.LastIndexOf((byte)'\n');
                    var column = newline >= 0 ? at - newline : position + at - lineStart + 1;
                    return $"{name} is not valid JSON: it contains bytes that are not UTF-8 text (line {line + before.Count((byte)'\n')}, column {column}).";
                }

                if (final)
                {
                    return null;
                }

                line += consumed.Count((byte)'\n');
                if (consumed.LastIndexOf((byte)'\n') is var lastNewline and >= 0)
                {
                    lineStart = position + lastNewline + 1;
                }

                state = reader.CurrentState;
                offset = position + consumed.Length;
                data[consumed.Length..].CopyTo(buffer);
                length = data.Length - consumed.Length;
                if (length == buffer.Length)
                {
                    // A single value (a long string) fills the whole buffer: give it more room.
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? CheckXml(FileStream stream, string name, CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
        {
            return $"{name} is empty.";
        }

        // No DTD processing and no resolver: entities and external references are never followed.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CloseInput = false };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            for (var nodes = 1; reader.Read(); nodes++)
            {
                if (nodes % 4096 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return null;
        }
        catch (XmlException ex)
        {
            var message = ex.Message;
            var position = message.LastIndexOf(" Line ", StringComparison.Ordinal);
            return $"{name} is not well-formed XML: {Describe(position > 0 ? message[..position] : message, ex.LineNumber > 0 ? ex.LineNumber : null, ex.LinePosition)}";
        }
    }

    /// <summary>Text, HTML and JavaScript: the start of the file must look like text (no UTF-16, no NUL bytes).</summary>
    private static string? CheckText(FileStream stream, string name)
    {
        Span<byte> head = stackalloc byte[8192];
        head = head[..stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
        if (WideEncoding(head) is { } encoding)
        {
            return $"{name} is {encoding} text: save it as UTF-8, or choose Binary to send it as it is.";
        }

        return head.Contains((byte)0) ? $"{name} looks like a binary file (it contains NUL bytes): choose Binary to send it as it is." : null;
    }

    /// <summary>"UTF-16" or "UTF-32" when the start of the file shows it: a byte order mark, or the zero bytes of ASCII text.</summary>
    private static string? WideEncoding(ReadOnlySpan<byte> head) => head switch
    {
        [0xFF, 0xFE, 0, 0, ..] or [0, 0, 0xFE, 0xFF, ..] => "UTF-32",
        [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] => "UTF-16",
        [0, 0, 0, not 0, ..] or [not 0, 0, 0, 0, ..] => "UTF-32",
        [0, not 0, ..] or [not 0, 0, ..] => "UTF-16",
        _ => null,
    };

    private static int InvalidUtf8Index(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length && Rune.DecodeFromUtf8(bytes[index..], out _, out var used) == OperationStatus.Done)
        {
            index += used;
        }

        return index;
    }

    /// <summary>A parser message without its own position details, followed by the line and column.</summary>
    private static string Describe(string message, long? line, long? column)
    {
        var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        var text = (cut > 0 ? message[..cut] : message).Trim().TrimEnd('.');
        return line is null ? text + "." : $"{text} (line {line}, column {column}).";
    }
}
