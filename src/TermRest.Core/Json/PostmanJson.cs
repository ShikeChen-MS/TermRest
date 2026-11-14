using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermRest.Core.Json;

/// <summary>Parses and writes JSON exactly the way Postman exports it (tab indentation, LF, minimal escaping).</summary>
public static class PostmanJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,

        // A key written twice (a merge conflict resolved by keeping both lines) is a JsonException, reported as a load
        // error, rather than an exception on first access.
        AllowDuplicateProperties = false,
    };

    private static readonly JsonWriterOptions FileWriterOptions = new()
    {
        Indented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        NewLine = "\n",
        Encoder = MinimalJsonEncoder.Instance,
        MaxDepth = 256,
    };

    private static readonly JsonWriterOptions DisplayWriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = MinimalJsonEncoder.Instance,
        MaxDepth = 256,
    };

    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = MinimalJsonEncoder.Instance,
        MaxDepth = 256,
    };

    /// <summary>
    /// Parses a file's JSON (comments and trailing commas allowed). A key written twice and half of a surrogate pair
    /// (<c>"\ud83d"</c>) are a <see cref="JsonException"/> here rather than an exception on first access to the value.
    /// </summary>
    public static JsonNode Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, nodeOptions: default, DocumentOptions);
        }
        catch (JsonException ex) when (ex.Message.Contains("is an invalid JSON literal", StringComparison.Ordinal))
        {
            // That message quotes the rest of the text, values (secrets) included.
            throw new JsonException($"Invalid JSON at line {ex.LineNumber + 1}: expected true, false or null.", ex.Path, ex.LineNumber, ex.BytePositionInLine);
        }

        ThrowIfUnpairedSurrogate(json);
        return node ?? throw new JsonException("The JSON document is empty.");
    }

    /// <summary>Serializes a node in Postman's on-disk style. No trailing newline is appended.</summary>
    public static string Serialize(JsonNode node) => Write(node, FileWriterOptions);

    /// <summary>Pretty-prints a node for display (two-space indentation).</summary>
    public static string SerializeForDisplay(JsonNode node) => Write(node, DisplayWriterOptions);

    /// <summary>Single-line JSON (line breaks inside strings are escaped), e.g. for JSON Lines files.</summary>
    public static string SerializeCompact(JsonNode node) => Write(node, CompactWriterOptions);

    /// <summary>Pretty-prints arbitrary JSON text for display; returns null when the text is not valid JSON.</summary>
    public static string? TryPrettyPrint(string text)
    {
        try
        {
            var node = JsonNode.Parse(text, nodeOptions: default, new JsonDocumentOptions { MaxDepth = 256 });
            return node is null ? "null" : SerializeForDisplay(node);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null; // InvalidOperationException: a string holds half of a surrogate pair ("\ud83d")
        }
    }

    /// <summary>
    /// Throws a <see cref="JsonException"/> naming the position of a <c>\uXXXX</c> escape in a string of
    /// <paramref name="json"/> (valid JSON) that is half of a surrogate pair, as JavaScript writes half of an emoji cut in
    /// two: System.Text.Json reads it, then throws on first access to the string.
    /// </summary>
    private static void ThrowIfUnpairedSurrogate(string json)
    {
        // Text read as UTF-8 cannot hold one otherwise: only escapes of \uD800 to \uDFFF need a closer look.
        if (!HasSurrogateEscape(json))
        {
            return;
        }

        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < json.Length; i++)
        {
            switch (json[i])
            {
                case '\n':
                    line++;
                    lineStart = i + 1;
                    break;
                case '/' when i + 1 < json.Length && json[i + 1] == '/':
                    while (i + 1 < json.Length && json[i + 1] != '\n')
                    {
                        i++;
                    }

                    break;
                case '/' when i + 1 < json.Length && json[i + 1] == '*':
                    for (i += 2; i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/'); i++)
                    {
                        if (json[i] == '\n')
                        {
                            line++;
                            lineStart = i + 1;
                        }
                    }

                    i++;
                    break;
                case '"':
                    // Strings cannot hold line breaks; an escaped character is skipped with its backslash.
                    for (i++; json[i] != '"'; i++)
                    {
                        if (json[i] != '\\')
                        {
                            continue;
                        }

                        if (json[i + 1] != 'u')
                        {
                            i++;
                            continue;
                        }

                        var unit = (char)int.Parse(json.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                        if (char.IsHighSurrogate(unit) && json.AsSpan(i + 6).StartsWith("\\u", StringComparison.Ordinal)
                            && char.IsLowSurrogate((char)int.Parse(json.AsSpan(i + 8, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)))
                        {
                            i += 11;
                        }
                        else if (char.IsSurrogate(unit))
                        {
                            throw new JsonException(
                                $"Unpaired surrogate \"{json.AsSpan(i, 6)}\" at line {line}, column {i - lineStart + 1} (half of a character, such as an emoji cut in two): remove it, or add its other half.");
                        }
                        else
                        {
                            i += 5;
                        }
                    }

                    break;
            }
        }
    }

    private static bool HasSurrogateEscape(string json)
    {
        for (var i = json.IndexOf("\\u", StringComparison.Ordinal); i >= 0; i = json.IndexOf("\\u", i + 2, StringComparison.Ordinal))
        {
            if (i + 3 < json.Length && json[i + 2] is 'd' or 'D' && json[i + 3] is >= '8' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                return true;
            }
        }

        return false;
    }

    private static string Write(JsonNode node, JsonWriterOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ----- small DOM helpers ---------------------------------------------------------------

    public static string? GetString(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var s))
        {
            return s;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        };
    }

    public static bool? GetBool(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        var s = value.GetString();
        return s is not null && bool.TryParse(s, out var parsed) ? parsed : null;
    }

    public static int? GetInt(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        var s = value.GetString();
        return s is not null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Sets a property. Existing properties keep their position; new properties are inserted according to
    /// <paramref name="canonicalOrder"/> so newly written keys appear where Postman would put them.
    /// </summary>
    public static void SetOrdered(this JsonObject obj, string key, JsonNode? value, string[] canonicalOrder)
    {
        if (obj.ContainsKey(key))
        {
            obj[key] = value;
            return;
        }

        var myRank = Array.IndexOf(canonicalOrder, key);
        if (myRank >= 0)
        {
            var index = 0;
            foreach (var property in obj)
            {
                var rank = Array.IndexOf(canonicalOrder, property.Key);
                if (rank > myRank)
                {
                    obj.Insert(index, key, value);
                    return;
                }

                index++;
            }
        }

        obj.Add(key, value);
    }

    public static JsonArray ToJsonArray(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
