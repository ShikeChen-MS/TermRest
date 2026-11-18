using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermRest.Core.Workspace;

/// <summary>
/// The data file of a collection run (Newman's <c>--iteration-data</c>): CSV with a header row, or JSON, an array of
/// objects. Each row is the data of one iteration: data variables for its requests (<c>{{name}}</c>) and scripts
/// (<c>pm.iterationData</c>). Rows are read one at a time, as they are needed, so a large file takes no more memory than a row.
/// </summary>
public sealed class IterationDataFile
{
    private IterationDataFile(string path, bool isJson)
    {
        Path = path;
        IsJson = isJson;
    }

    public string Path { get; }

    /// <summary>JSON (values keep their types: numbers, true and false, objects …), else CSV (every value is text).</summary>
    public bool IsJson { get; }

    private string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Opens a data file: JSON when it starts with '[' (after white space and a byte order mark), else CSV.</summary>
    public static IterationDataFile Open(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int c;
        while ((c = reader.Read()) >= 0 && char.IsWhiteSpace((char)c))
        {
        }

        return new IterationDataFile(path, c == '[');
    }

    /// <summary>The rows, read as they are needed.</summary>
    /// <exception cref="InvalidDataException">The file, or a row, is not valid (the message says where).</exception>
    public IAsyncEnumerable<JsonObject> ReadAsync(CancellationToken cancellationToken = default) =>
        IsJson ? ReadJsonAsync(cancellationToken) : ReadCsv().ToAsyncEnumerable();

    /// <summary>How many rows the file has: read through once, a row at a time.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        await foreach (var _ in ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private async IAsyncEnumerable<JsonObject> ReadJsonAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(Path);
        await using (stream.ConfigureAwait(false))
        {
            var items = JsonSerializer.DeserializeAsyncEnumerable(stream, CoreJsonContext.Default.JsonElement, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (items.ConfigureAwait(false))
            {
                for (var number = 1; ; number++)
                {
                    try
                    {
                        if (!await items.MoveNextAsync().ConfigureAwait(false))
                        {
                            yield break;
                        }
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException($"{Name} is not a JSON array of objects: {ex.Message}", ex);
                    }

                    if (items.Current.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"{Name}: item {number} of the array is not an object ({{\"name\": value, …}}) but {items.Current.ValueKind.ToString().ToLowerInvariant()}.");
                    }

                    // Found here, when the file is read through before the run, rather than when the row is used, in the middle of it.
                    if (RepeatedName(items.Current) is { } repeated)
                    {
                        throw new InvalidDataException($"{Name}: item {number} of the array has \"{repeated}\" twice in an object: a name can be there once.");
                    }

                    yield return JsonObject.Create(items.Current)!;
                }
            }
        }
    }

    /// <summary>A name an object in <paramref name="element"/> (or in one it holds) has twice, if any.</summary>
    private static string? RepeatedName(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        return property.Name;
                    }

                    if (RepeatedName(property.Value) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (RepeatedName(item) is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private IEnumerable<JsonObject> ReadCsv()
    {
        using var reader = new StreamReader(Path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string[]? keys = null;
        var row = 0;
        while (ReadRecord(reader) is { } fields)
        {
            // Lines without a value (an empty line, or ",,," from a spreadsheet) are not rows.
            if (fields.All(f => f.Length == 0))
            {
                continue;
            }

            if (keys is null)
            {
                keys = fields;
                continue;
            }

            row++;
            if (fields.Length > keys.Length && fields.Skip(keys.Length).Any(f => f.Length > 0))
            {
                throw new InvalidDataException($"{Name}: row {row} has {fields.Length} values, but the header row names {keys.Length}. Put a value with a comma in double quotes.");
            }

            var values = new JsonObject();
            for (var i = 0; i < keys.Length; i++)
            {
                values[keys[i]] = i < fields.Length ? fields[i] : "";
            }

            yield return values;
        }
    }

    /// <summary>
    /// One CSV record (RFC 4180): fields separated by commas, a field in double quotes may hold commas and line breaks, and
    /// <c>""</c> in it is a quote. Null at the end of the file.
    /// </summary>
    private static string[]? ReadRecord(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        while (true)
        {
            var c = reader.Read();
            if (c < 0)
            {
                if (!any)
                {
                    return null;
                }

                fields.Add(field.ToString());
                return [.. fields];
            }

            any = true;
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append((char)c);
                }
                else if (reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true; // a quote inside a value that doesn't start with one is a character
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' or '\n':
                    if (c == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    fields.Add(field.ToString());
                    return [.. fields];
                default:
                    field.Append((char)c);
                    break;
            }
        }
    }
}
