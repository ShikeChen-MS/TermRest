using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.Model;
using TermRest.Core.Postman;

namespace TermRest.Core.Scripting;

/// <summary>
/// A request that a script sends through TermRest (pm.sendRequest, tr.send_request, tr.SendRequest, $tr.SendRequest), as
/// Postman's pm.sendRequest takes it: a URL, or an object with <c>url</c>, <c>method</c>, <c>header</c>, <c>body</c> and
/// <c>auth</c>. What other languages write naturally works too: headers (<c>header</c> or <c>headers</c>) as a map of names
/// to values or as [name, value] pairs, form fields as a map, and a body as text (raw) or as any other JSON without a
/// <c>mode</c> (sent as JSON).
/// </summary>
internal static class ScriptRequest
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] BodyKeys = ["raw", "urlencoded", "formdata", "file", "graphql"];
    private static readonly string[] FieldKeys = ["urlencoded", "formdata"];

    public static HttpRequestSpec Read(JsonNode? request)
    {
        var spec = new HttpRequestSpec();
        PostmanRequestMapper.ReadRequest(ToPostman(request), spec, PostmanSchemaVersion.V21);
        return spec;
    }

    private static JsonNode ToPostman(JsonNode? request)
    {
        if (request is not JsonObject source)
        {
            return JsonValue.Create(Text(request) ?? "");
        }

        var result = new JsonObject
        {
            ["method"] = Text(source["method"]) is { Length: > 0 } method ? method : "GET",
            ["url"] = source["url"] is JsonObject url ? url.DeepClone() : JsonValue.Create(Text(source["url"]) ?? ""),
            ["header"] = Entries(source["header"] ?? source["headers"]),
        };
        if (source["body"] is { } body)
        {
            result["body"] = Body(body);
        }

        if (source["auth"] is JsonObject auth)
        {
            result["auth"] = auth.DeepClone();
        }

        return result;
    }

    /// <summary>Postman's key/value entries, from entries, [name, value] pairs, "Name: value" lines, or a map.</summary>
    private static JsonNode Entries(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject map:
                return new JsonArray([.. map.Select(pair => (JsonNode?)Entry(pair.Key, pair.Value))]);
            case JsonArray items:
                var entries = new JsonArray();
                foreach (var item in items)
                {
                    switch (item)
                    {
                        case JsonObject entry:
                            entries.Add(entry.DeepClone());
                            break;
                        case JsonArray { Count: 2 } pair:
                            entries.Add((JsonNode)Entry(Text(pair[0]) ?? "", pair[1]));
                            break;
                        case JsonValue line when Text(line) is { } text && text.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon:
                            entries.Add((JsonNode)Entry(text[..colon].Trim(), JsonValue.Create(text[(colon + 1)..].Trim())));
                            break;
                    }
                }

                return entries;
            case JsonValue lines:
                return lines.DeepClone(); // "Name: value" lines, which Postman's reader takes
            default:
                return new JsonArray();
        }
    }

    private static JsonObject Entry(string key, JsonNode? value) => new() { ["key"] = key, ["value"] = Text(value) ?? "" };

    private static JsonObject Body(JsonNode body)
    {
        if (body is JsonObject postman && IsPostmanBody(postman))
        {
            var result = (JsonObject)postman.DeepClone();
            result["mode"] = Text(result["mode"]) ?? BodyKeys.First(result.ContainsKey);
            if (result["raw"] is { } raw && raw.GetValueKind() != JsonValueKind.String)
            {
                result["raw"] = raw.ToJsonString(Relaxed);
                if (raw is not JsonValue)
                {
                    Language(result, "json");
                }
            }

            foreach (var key in FieldKeys)
            {
                if (result[key] is JsonObject map)
                {
                    result[key] = Entries(map);
                }
            }

            if (result["graphql"] is JsonObject graphql && graphql["variables"] is JsonObject or JsonArray)
            {
                graphql["variables"] = graphql["variables"]!.ToJsonString(Relaxed);
            }

            return result;
        }

        if (body is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return new JsonObject { ["mode"] = "raw", ["raw"] = value.GetValue<string>() };
        }

        // Any other JSON (a Python dict, a hashtable, an object, a number …) is sent as JSON.
        var json = new JsonObject { ["mode"] = "raw", ["raw"] = body.ToJsonString(Relaxed) };
        Language(json, "json");
        return json;
    }

    /// <summary>A Postman body: one with a mode, or with no other members than a Postman body has (<c>{"raw": "…"}</c>).</summary>
    private static bool IsPostmanBody(JsonObject body) =>
        body.ContainsKey("mode")
        || (body.Any(member => BodyKeys.Contains(member.Key)) && body.All(member => BodyKeys.Contains(member.Key) || member.Key is "options" or "disabled"));

    private static void Language(JsonObject body, string language)
    {
        if (body["options"] is not JsonObject options)
        {
            body["options"] = options = new JsonObject();
        }

        if (options["raw"] is not JsonObject raw)
        {
            options["raw"] = raw = new JsonObject();
        }

        raw["language"] ??= language;
    }

    /// <summary>A value as text: a string as it is, null as null, other JSON as JSON (true, 42, {"a":1}).</summary>
    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => node.ToJsonString(Relaxed),
    };
}
