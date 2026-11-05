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

    public static JsonNode Parse(string json) =>
        JsonNode.Parse(json, nodeOptions: default, DocumentOptions) ?? throw new JsonException("The JSON document is empty.");

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
        catch (JsonException)
        {
            return null;
        }
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
