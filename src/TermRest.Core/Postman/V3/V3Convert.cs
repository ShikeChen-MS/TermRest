using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.Json;
using TermRest.Core.Scripting;

namespace TermRest.Core.Postman.V3;

/// <summary>
/// Converts between Postman collection format v3 (one YAML file per request, read as JSON) and the v2.1 item JSON that
/// TermRest edits in memory. Writing starts from the original v3 object, so keys TermRest does not understand (ids,
/// <c>uses</c>, examples, settings it does not edit, vault sources …), their order and their shape (map vs list) survive.
/// </summary>
internal static class V3Convert
{
    public const string HttpRequestKind = "http-request";
    public const string CollectionKind = "collection";

    internal static readonly string[] RequestOrder =
        ["$kind", "id", "name", "description", "url", "method", "headers", "queryParams", "pathVariables", "body", "auth", "scripts", "termrest", "settings", "examples", "order"];

    internal static readonly string[] DefinitionOrder = ["$kind", "id", "name", "description", "auth", "variables", "scripts", "order"];

    private static readonly string[] KeyValueOrder = ["key", "value", "description", "disabled", "$type"];
    private static readonly string[] FormDataOrder = ["type", "key", "value", "src", "contentType", "disabled"];
    private static readonly string[] AuthOrder = ["id", "type", "name", "rules", "credentials"];
    private static readonly string[] ScriptOrder = ["type", "code", "language", "packages", "requests"];
    private static readonly string[] VariableOrder = ["key", "value", "disabled", "secret", "description", "source"];
    private static readonly string[] EnvironmentValueOrder = ["key", "value", "enabled", "type", "description"];

    /// <summary>Request settings the v3 schema allows (v2.1 <c>protocolProfileBehavior</c> has a few more).</summary>
    private static readonly HashSet<string> V3Settings =
    [
        "protocolVersion", "strictSSL", "followRedirects", "maxRedirects", "followOriginalHttpMethod", "followAuthorizationHeader",
        "removeRefererHeaderOnRedirect", "insecureHTTPParser", "disableUrlEncoding", "disableCookies", "maxResponseSize",
        "maxHeaderSize", "tlsPreferServerCiphers", "tlsDisabledProtocols", "tlsCipherSelection", "disabledSystemHeaders",
    ];

    private static readonly HashSet<string> RawLanguages = ["json", "text", "xml", "html", "javascript"];

    // ----- requests: v3 → v2.1 ---------------------------------------------------------------------

    public static JsonObject RequestToV21(JsonObject v3, string name)
    {
        var item = new JsonObject { ["name"] = name };
        var events = ScriptsToEvents(v3["scripts"]);
        if (events.Count > 0)
        {
            item["event"] = events;
        }

        if (v3["termrest"] is JsonObject termrest)
        {
            item["termrest"] = termrest.DeepClone();
        }

        if (SettingsToV21(v3["settings"]) is { } settings)
        {
            item["protocolProfileBehavior"] = settings;
        }

        var request = new JsonObject();
        if (AuthToV21(v3["auth"]) is { } auth)
        {
            request["auth"] = auth;
        }

        request["method"] = v3["method"].GetString() ?? "GET";
        request["header"] = KeyValuesToV21(v3["headers"]);
        if (BodyToV21(v3["body"]) is { } body)
        {
            request["body"] = body;
        }

        var url = new JsonObject { ["raw"] = v3["url"].GetString() ?? "" };
        if (v3["queryParams"] is JsonArray or JsonObject)
        {
            url["query"] = KeyValuesToV21(v3["queryParams"]);
        }

        if (v3["pathVariables"] is JsonArray or JsonObject)
        {
            url["variable"] = KeyValuesToV21(v3["pathVariables"]);
        }

        request["url"] = url;
        if (v3["description"].GetString() is { } description)
        {
            request["description"] = description;
        }

        item["request"] = request;
        item["response"] = new JsonArray();
        return item;
    }

    /// <summary>Headers, parameters and urlencoded fields: v3 allows a <c>{name: value}</c> map or a list of entries.</summary>
    private static JsonArray KeyValuesToV21(JsonNode? node)
    {
        var array = new JsonArray();
        switch (node)
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    array.Add((JsonNode)new JsonObject { ["key"] = key, ["value"] = Text(value) });
                }

                break;
            case JsonArray list:
                foreach (var entry in list.OfType<JsonObject>())
                {
                    array.Add(entry.DeepClone());
                }

                break;
        }

        return array;
    }

    private static JsonObject? BodyToV21(JsonNode? node)
    {
        if (node is not JsonObject body || body["type"].GetString() is not { } type)
        {
            return null;
        }

        var content = body["content"];
        JsonObject result;
        switch (type)
        {
            case "urlencoded":
                result = new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = KeyValuesToV21(content) };
                break;
            case "formdata":
                result = new JsonObject { ["mode"] = "formdata", ["formdata"] = KeyValuesToV21(content) };
                break;
            case "file":
                result = new JsonObject { ["mode"] = "file", ["file"] = content is JsonObject file ? file.DeepClone() : new JsonObject() };
                break;
            case "graphql":
                result = new JsonObject { ["mode"] = "graphql", ["graphql"] = content is JsonObject graphql ? graphql.DeepClone() : new JsonObject() };
                break;
            case "none":
                return null;
            default:
                result = new JsonObject
                {
                    ["mode"] = "raw",
                    ["raw"] = content is JsonValue ? content.GetString() ?? "" : content?.ToJsonString() ?? "",
                    ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = RawLanguages.Contains(type) ? type : "text" } },
                };
                break;
        }

        if (body["$type"] is { } contentType)
        {
            result["$type"] = contentType.DeepClone();
        }

        return result;
    }

    /// <summary>Request, folder or collection auth. "inherit" (or no auth) is null; for several auths only the first is used.</summary>
    public static JsonObject? AuthToV21(JsonNode? node)
    {
        if (node is JsonArray multiple)
        {
            node = multiple.OfType<JsonObject>().FirstOrDefault();
        }

        if (node is not JsonObject auth || auth["type"].GetString() is not { } type || type == "inherit")
        {
            return null;
        }

        var result = new JsonObject { ["type"] = type };
        if (type == "noauth")
        {
            return result;
        }

        var parameters = new JsonArray();
        switch (auth["credentials"])
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    parameters.Add((JsonNode)AuthParameter(key, value));
                }

                break;
            case JsonArray list:
                foreach (var entry in list.OfType<JsonObject>())
                {
                    parameters.Add((JsonNode)AuthParameter(entry["key"].GetString() ?? "", entry["value"]));
                }

                break;
        }

        result[type] = parameters;
        return result;
    }

    private static JsonObject AuthParameter(string key, JsonNode? value) => new()
    {
        ["key"] = key,
        ["value"] = value?.DeepClone(),
        ["type"] = value switch
        {
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValue or null => "string",
            _ => "any",
        },
    };

    /// <summary>
    /// v3 scripts → v2.1 events. Script types other than HTTP ones keep their v3 name as the event name. Scripts in
    /// TermRest's other languages than JavaScript become disabled events, as TermRest writes them in v2.1 files: Postman
    /// would run them as JavaScript.
    /// </summary>
    public static JsonArray ScriptsToEvents(JsonNode? node)
    {
        var events = new JsonArray();
        foreach (var script in (node as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var type = script["type"].GetString() ?? "";
            var listen = type switch
            {
                "beforeRequest" or "http:beforeRequest" => "prerequest",
                "afterResponse" or "http:afterResponse" => "test",
                _ => type,
            };
            var code = script["code"].GetString() ?? "";
            var language = script["language"].GetString() ?? "text/javascript";
            var body = new JsonObject
            {
                ["exec"] = PostmanJson.ToJsonArray(code.Split('\n').Select(l => (JsonNode?)JsonValue.Create(l))),
                ["type"] = language,
            };
            foreach (var extra in (string[])["packages", "requests"])
            {
                if (script[extra] is { } value)
                {
                    body[extra] = value.DeepClone();
                }
            }

            var e = new JsonObject { ["listen"] = listen, ["script"] = body, ["x-v3-type"] = type };
            if (ScriptLanguage.FromType(language) != ScriptLanguage.JavaScript && listen is "prerequest" or "test")
            {
                e["disabled"] = true;
            }

            events.Add((JsonNode)e);
        }

        return events;
    }

    private static JsonObject? SettingsToV21(JsonNode? node)
    {
        if (node is not JsonObject settings)
        {
            return null;
        }

        var result = (JsonObject)settings.DeepClone();
        if (result["disabledSystemHeaders"] is JsonArray disabled)
        {
            // v2.1 stores these as { "header-name": true }.
            var map = new JsonObject();
            foreach (var name in disabled.Select(n => n.GetString()).OfType<string>())
            {
                map[name] = true;
            }

            result["disabledSystemHeaders"] = map;
        }

        return result;
    }

    public static JsonArray VariablesToV21(JsonNode? node)
    {
        var array = new JsonArray();
        foreach (var variable in (node as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var entry = (JsonObject)variable.DeepClone();
            if (entry["secret"].GetBool() == true)
            {
                entry["type"] = "secret";
            }

            array.Add((JsonNode)entry);
        }

        return array;
    }

    // ----- requests: v2.1 → v3 ---------------------------------------------------------------------

    /// <summary>The v3 request for a v2.1 item, based on <paramref name="template"/> (the file as it was read).</summary>
    public static JsonObject RequestToV3(JsonObject item, JsonObject? template)
    {
        var v3 = template is null ? new JsonObject() : (JsonObject)template.DeepClone();
        v3.SetOrdered("$kind", HttpRequestKind, RequestOrder);
        if (item["request"] is not JsonObject request)
        {
            request = new JsonObject { ["method"] = "GET", ["url"] = item["request"]?.DeepClone() };
        }

        Set(v3, "description", Description(request["description"], template), RequestOrder);
        var (raw, query, variables) = ReadUrl(request["url"]);
        Set(v3, "url", raw.Length == 0 && template?["url"] is null ? null : JsonValue.Create(raw), RequestOrder);
        v3.SetOrdered("method", request["method"].GetString() ?? "GET", RequestOrder);
        Set(v3, "headers", KeyValuesToV3(request["header"] as JsonArray, template?["headers"], mapWhenNew: true), RequestOrder);
        Set(v3, "queryParams", KeyValuesToV3(query, template?["queryParams"], mapWhenNew: false), RequestOrder);
        Set(v3, "pathVariables", KeyValuesToV3(variables, template?["pathVariables"], mapWhenNew: false), RequestOrder);
        Set(v3, "body", BodyToV3(request["body"] as JsonObject, template?["body"]), RequestOrder);
        Set(v3, "auth", AuthToV3(request["auth"] as JsonObject, template?["auth"]), RequestOrder);
        Set(v3, "scripts", EventsToScripts(item["event"] as JsonArray, template?["scripts"], http: false), RequestOrder);
        Set(v3, "termrest", item["termrest"] is JsonObject { Count: > 0 } termrest ? termrest.DeepClone() : null, RequestOrder);
        Set(v3, "settings", SettingsToV3(item["protocolProfileBehavior"] as JsonObject, template?["settings"]), RequestOrder);
        return v3;
    }

    private static (string Raw, JsonArray? Query, JsonArray? Variables) ReadUrl(JsonNode? url) => url switch
    {
        JsonObject obj => (obj["raw"].GetString() ?? "", obj["query"] as JsonArray, obj["variable"] as JsonArray),
        _ => (url.GetString() ?? "", null, null),
    };

    /// <summary>
    /// Headers, parameters and urlencoded fields keep the form the file used: a <c>{name: value}</c> map (possible while
    /// every entry is a plain, enabled, unique name/value pair) or a list of entries. New ones use
    /// <paramref name="mapWhenNew"/>: Postman writes maps for simple headers and lists for parameters.
    /// </summary>
    private static JsonNode? KeyValuesToV3(JsonArray? entries, JsonNode? template, bool mapWhenNew)
    {
        if (entries is null || entries.Count == 0)
        {
            return template is JsonArray { Count: 0 } or JsonObject { Count: 0 } ? template.DeepClone() : null;
        }

        var list = entries.OfType<JsonObject>().ToList();
        var simple = (template is JsonObject || (template is null && mapWhenNew))
                     && list.All(e => e["value"] is JsonValue && e["key"].GetString() is { Length: > 0 }
                                      && e.All(p => p.Key is "key" or "value" or "type" || (p.Key == "disabled" && p.Value.GetBool() == false))
                                      && (e["type"] is null || e["type"].GetString() == "text"))
                     && list.Select(e => e["key"].GetString()).Distinct(StringComparer.Ordinal).Count() == list.Count;
        if (!simple)
        {
            return KeyValueListToV3(entries, template);
        }

        var map = new JsonObject();
        foreach (var entry in list)
        {
            var key = entry["key"].GetString()!;
            map[key] = Reuse((template as JsonObject)?[key], entry["value"]);
        }

        return map;
    }

    private static JsonArray KeyValueListToV3(JsonArray entries, JsonNode? template)
    {
        var templates = (template as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var array = new JsonArray();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var result = (JsonObject)entry.DeepClone();
            if (result["type"].GetString() is "text" or "default" or "string" or null)
            {
                result.Remove("type"); // a v2.1 detail; v3 uses $type
            }

            Order(result, KeyValueOrder);
            var match = templates.FirstOrDefault(t => JsonNode.DeepEquals(t, result));
            array.Add(match is null ? result : match.DeepClone());
        }

        return array;
    }

    private static JsonNode? BodyToV3(JsonObject? body, JsonNode? template)
    {
        var mode = body?["mode"].GetString();
        if (body is null || mode is null or "none")
        {
            return template is JsonObject t && t["type"].GetString() == "none" ? t.DeepClone() : null;
        }

        var type = mode switch
        {
            "raw" => ((body["options"] as JsonObject)?["raw"] as JsonObject)?["language"].GetString() is { } language && RawLanguages.Contains(language) ? language : "text",
            "urlencoded" or "formdata" or "file" or "graphql" => mode,
            _ => null,
        };
        if (type is null)
        {
            return template?.DeepClone();
        }

        // Same kind of body as before: start from the original (key order, $type, extra keys).
        var templateBody = template as JsonObject;
        var previousType = templateBody?["type"].GetString();
        var sameKind = previousType == type || (mode == "raw" && previousType is not null && RawLanguages.Contains(previousType));
        var result = sameKind ? (JsonObject)templateBody!.DeepClone() : new JsonObject();
        result.SetOrdered("type", type, ["type", "content", "$type"]);
        JsonNode content = mode switch
        {
            "raw" => JsonValue.Create(body["raw"].GetString() ?? "")!,
            "urlencoded" => KeyValuesToV3(body["urlencoded"] as JsonArray ?? [], templateBody?["content"], mapWhenNew: false) ?? new JsonArray(),
            "formdata" => FormDataToV3(body["formdata"] as JsonArray ?? [], templateBody?["content"]),
            "file" => FileToV3(body["file"] as JsonObject),
            _ => GraphQLToV3(body["graphql"] as JsonObject ?? [], sameKind ? templateBody?["content"] as JsonObject : null),
        };
        if (!JsonNode.DeepEquals(result["content"], content))
        {
            result.SetOrdered("content", content, ["type", "content", "$type"]);
        }

        if (body["$type"] is { } contentType)
        {
            result.SetOrdered("$type", contentType.DeepClone(), ["type", "content", "$type"]);
        }
        else
        {
            result.Remove("$type");
        }

        return result;
    }

    /// <summary>A file body's content: where the file is, and its type when it has one (as in v2.1's <c>file</c> object).</summary>
    private static JsonObject FileToV3(JsonObject? file)
    {
        var content = new JsonObject { ["src"] = file?["src"]?.DeepClone() };
        if (file?["language"] is { } language)
        {
            content["language"] = language.DeepClone();
        }

        return content;
    }

    private static JsonObject GraphQLToV3(JsonObject graphql, JsonObject? previous)
    {
        var content = previous is null ? new JsonObject() : (JsonObject)previous.DeepClone();
        content.SetOrdered("query", graphql["query"].GetString() ?? "", ["query", "variables", "operationName"]);
        Set(content, "variables", Text(graphql["variables"]) is { Length: > 0 } variables ? variables : null, ["query", "variables", "operationName"]);
        return content;
    }

    private static JsonArray FormDataToV3(JsonArray fields, JsonNode? template)
    {
        var templates = (template as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var array = new JsonArray();
        foreach (var field in fields.OfType<JsonObject>())
        {
            var result = (JsonObject)field.DeepClone();
            result["type"] = result["type"].GetString() == "file" ? "file" : "text";
            result.Remove("description");
            if (result["type"].GetString() == "file")
            {
                result.Remove("value");
                result["src"] ??= new JsonArray();
            }

            Order(result, FormDataOrder);
            var match = templates.FirstOrDefault(t => JsonNode.DeepEquals(t, result));
            array.Add(match is null ? result : match.DeepClone());
        }

        return array;
    }

    /// <summary>v2.1 auth → v3, keeping the template's id, name, shape (single or list of auths) and credential form.</summary>
    public static JsonNode? AuthToV3(JsonObject? auth, JsonNode? template)
    {
        if (template is JsonArray list && list.OfType<JsonObject>().FirstOrDefault() is { } first)
        {
            // Several auths with URL rules: only the first one is edited.
            if (AuthToV3(auth, first) is not JsonObject single)
            {
                return null;
            }

            var copy = (JsonArray)list.DeepClone();
            copy[0] = single;
            return copy;
        }

        var previous = template as JsonObject;
        if (auth is null)
        {
            return previous?["type"].GetString() == "inherit" ? previous.DeepClone() : null;
        }

        var type = auth["type"].GetString() ?? "noauth";
        var result = previous is not null && previous["type"].GetString() == type ? (JsonObject)previous.DeepClone() : new JsonObject();
        result.SetOrdered("type", type, AuthOrder);
        if (type == "noauth")
        {
            result.Remove("credentials");
            return result;
        }

        var parameters = (auth[type] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        JsonNode credentials;
        if (previous?["credentials"] is JsonArray)
        {
            var array = new JsonArray();
            foreach (var p in parameters)
            {
                array.Add((JsonNode)new JsonObject { ["key"] = p["key"].GetString() ?? "", ["value"] = p["value"]?.DeepClone() });
            }

            credentials = array;
        }
        else
        {
            var map = new JsonObject();
            var previousMap = previous?["credentials"] as JsonObject;
            foreach (var p in parameters)
            {
                var key = p["key"].GetString() ?? "";
                map[key] = Reuse(previousMap?[key], p["value"]);
            }

            credentials = map;
        }

        result.SetOrdered("credentials", credentials, AuthOrder);
        return result;
    }

    /// <summary>
    /// v2.1 events → v3 scripts (<c>beforeRequest</c>/<c>afterResponse</c>, or <c>http:…</c> on folders): one per type and
    /// language, each written over the script of that type and language the file had.
    /// </summary>
    public static JsonArray? EventsToScripts(JsonArray? events, JsonNode? template, bool http)
    {
        var templates = (template as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var scripts = new JsonArray();
        foreach (var e in events?.OfType<JsonObject>() ?? [])
        {
            var listen = e["listen"].GetString() ?? "";
            var type = e["x-v3-type"].GetString() is { Length: > 0 } original ? original : listen switch
            {
                "prerequest" => http ? "http:beforeRequest" : "beforeRequest",
                "test" => http ? "http:afterResponse" : "afterResponse",
                _ => listen,
            };
            var script = e["script"] as JsonObject;
            var exec = script?["exec"];
            var code = exec is JsonArray lines ? string.Join('\n', lines.Select(l => l.GetString() ?? "")) : exec.GetString() ?? "";
            var language = ScriptLanguage.FromType(script?["type"].GetString());
            var previous = templates.FirstOrDefault(t => t["type"].GetString() == type && ScriptLanguage.FromType(t["language"].GetString()) == language);
            if (previous is not null)
            {
                templates.Remove(previous);
            }
            else if (code.Length == 0)
            {
                continue; // Postman exports keep empty script events; v3 files do not need them
            }

            var result = previous is null ? new JsonObject() : (JsonObject)previous.DeepClone();
            result.SetOrdered("type", type, ScriptOrder);
            result.SetOrdered("code", code, ScriptOrder);

            // A script's language comes with its code; an empty one keeps the language the file has (Postman's: JavaScript).
            var languageType = code.Trim().Length == 0 && previous?["language"] is { } kept ? kept : script?["type"] ?? previous?["language"];
            result.SetOrdered("language", languageType?.DeepClone() ?? "text/javascript", ScriptOrder);
            foreach (var extra in (string[])["packages", "requests"])
            {
                if (script?[extra] is JsonObject { Count: > 0 } value)
                {
                    result.SetOrdered(extra, value.DeepClone(), ScriptOrder);
                }
            }

            scripts.Add((JsonNode)result);
        }

        return scripts.Count == 0 ? template is JsonArray { Count: 0 } ? new JsonArray() : null : scripts;
    }

    private static JsonObject? SettingsToV3(JsonObject? behavior, JsonNode? template)
    {
        if (behavior is null || behavior.Count == 0)
        {
            return template is JsonObject { Count: 0 } ? new JsonObject() : null;
        }

        var result = template is JsonObject previous ? (JsonObject)previous.DeepClone() : new JsonObject();
        foreach (var key in result.Select(p => p.Key).Where(k => !behavior.ContainsKey(k)).ToList())
        {
            result.Remove(key);
        }

        foreach (var (key, value) in behavior)
        {
            if (!V3Settings.Contains(key))
            {
                continue;
            }

            var converted = key == "disabledSystemHeaders" && value is JsonObject map
                ? PostmanJson.ToJsonArray(map.Where(p => p.Value.GetBool() == true).Select(p => (JsonNode?)JsonValue.Create(p.Key)))
                : value?.DeepClone();
            if (!JsonNode.DeepEquals(result[key], converted))
            {
                result[key] = converted;
            }
        }

        return result.Count == 0 ? null : result;
    }

    // ----- folders and collections ---------------------------------------------------------------

    /// <summary>Copies a folder or collection definition into v2.1 form (<paramref name="target"/>: folder item or collection root).</summary>
    public static void DefinitionToV21(JsonObject? definition, JsonObject target, bool isRoot)
    {
        if (definition is null)
        {
            return;
        }

        if (definition["description"].GetString() is { } description)
        {
            if (isRoot)
            {
                ((JsonObject)target["info"]!)["description"] = description;
            }
            else
            {
                target["description"] = description;
            }
        }

        var events = ScriptsToEvents(definition["scripts"]);
        if (events.Count > 0)
        {
            target["event"] = events;
        }

        if (AuthToV21(definition["auth"]) is { } auth)
        {
            target["auth"] = auth;
        }

        if (definition["variables"] is JsonArray)
        {
            target["variable"] = VariablesToV21(definition["variables"]);
        }
    }

    /// <summary>The v3 definition file content for a folder item or the collection root.</summary>
    public static JsonObject DefinitionToV3(JsonObject source, JsonObject? template, bool isRoot)
    {
        var v3 = template is null ? new JsonObject() : (JsonObject)template.DeepClone();
        v3.SetOrdered("$kind", CollectionKind, DefinitionOrder);
        var description = isRoot ? (source["info"] as JsonObject)?["description"] : source["description"];
        Set(v3, "description", Description(description, template), DefinitionOrder);
        Set(v3, "auth", AuthToV3(source["auth"] as JsonObject, template?["auth"]), DefinitionOrder);
        Set(v3, "variables", source["variable"] is JsonArray variables ? VariablesToV3(variables, template?["variables"]) : null, DefinitionOrder);
        Set(v3, "scripts", EventsToScripts(source["event"] as JsonArray, template?["scripts"], http: true), DefinitionOrder);
        return v3;
    }

    private static JsonArray VariablesToV3(JsonArray variables, JsonNode? template)
    {
        var templates = (template as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var array = new JsonArray();
        foreach (var variable in variables.OfType<JsonObject>())
        {
            var result = (JsonObject)variable.DeepClone();
            var secret = result["type"].GetString() == "secret";
            result.Remove("type");
            if (secret)
            {
                result["secret"] = true;
            }
            else if (result["secret"].GetBool() == true)
            {
                result.Remove("secret");
            }

            if (result["disabled"].GetBool() != true)
            {
                result.Remove("disabled");
            }

            // Secrets from a vault have no value; TermRest's editor reads a missing value as "".
            var previous = templates.FirstOrDefault(t => t["key"].GetString() == result["key"].GetString());
            if (previous is not null && !previous.ContainsKey("value") && result["value"].GetString() is "" or null)
            {
                result.Remove("value");
            }

            Order(result, VariableOrder);
            var match = templates.FirstOrDefault(t => JsonNode.DeepEquals(t, result));
            array.Add(match is null ? result : match.DeepClone());
        }

        return array;
    }

    // ----- environments and globals ------------------------------------------------------------

    public static JsonObject EnvironmentToV21(JsonObject v3, string fallbackName, bool globals)
    {
        var env = (JsonObject)v3.DeepClone();
        env["name"] ??= fallbackName;
        if (env["values"] is not JsonArray)
        {
            env["values"] = new JsonArray();
        }

        env["_postman_variable_scope"] = globals ? "globals" : "environment";
        return env;
    }

    public static JsonObject EnvironmentToV3(JsonObject env, JsonObject? template)
    {
        var v3 = template is null ? new JsonObject() : (JsonObject)template.DeepClone();
        v3.SetOrdered("name", env["name"]?.DeepClone() ?? "Environment", ["name", "values"]);
        var templates = (template?["values"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var values = new JsonArray();
        foreach (var value in (env["values"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var result = (JsonObject)value.DeepClone();
            var previous = templates.FirstOrDefault(t => t["key"].GetString() == result["key"].GetString());
            if (result["type"].GetString() == "default" && previous?["type"] is null)
            {
                result.Remove("type"); // Postman only writes "type" for secrets
            }

            Order(result, EnvironmentValueOrder);
            var match = templates.FirstOrDefault(t => JsonNode.DeepEquals(t, result));
            values.Add((JsonNode)(match is null ? result : match.DeepClone()));
        }

        v3.SetOrdered("values", values, ["name", "values"]);
        return v3;
    }

    // ----- examples (saved responses) ------------------------------------------------------------

    /// <summary>A v2.1 saved response as a v3 <c>http-example</c> file.</summary>
    public static JsonObject ExampleToV3(JsonObject response)
    {
        var original = response["originalRequest"] as JsonObject;
        var request = new JsonObject
        {
            ["url"] = ReadUrl(original?["url"]).Raw,
            ["method"] = original?["method"].GetString() ?? "GET",
        };
        if (KeyValuesToV3(CleanHeaders(original?["header"]), null, mapWhenNew: true) is { } requestHeaders)
        {
            request["headers"] = requestHeaders;
        }

        if (BodyToV3(original?["body"] as JsonObject, null) is { } requestBody)
        {
            request["body"] = requestBody;
        }

        var result = new JsonObject
        {
            ["statusCode"] = response["code"].GetInt() ?? 200,
            ["statusText"] = response["status"].GetString() ?? "",
        };
        if (KeyValuesToV3(CleanHeaders(response["header"]), null, mapWhenNew: true) is { } headers)
        {
            result["headers"] = headers;
        }

        var language = response["_postman_previewlanguage"].GetString();
        result["body"] = new JsonObject
        {
            ["type"] = language is not null && (RawLanguages.Contains(language) || language == "yaml") ? language : "text",
            ["content"] = response["body"].GetString() ?? "",
        };

        return new JsonObject { ["$kind"] = "http-example", ["request"] = request, ["response"] = result };

        static JsonArray? CleanHeaders(JsonNode? node)
        {
            if (node is not JsonArray { Count: > 0 } headers)
            {
                return null;
            }

            var clean = new JsonArray();
            foreach (var header in headers.OfType<JsonObject>())
            {
                var entry = new JsonObject { ["key"] = header["key"].GetString() ?? header["name"].GetString() ?? "", ["value"] = Text(header["value"]) ?? "" };
                if (header["description"].GetString() is { Length: > 0 } description)
                {
                    entry["description"] = description;
                }

                if (header["disabled"].GetBool() == true)
                {
                    entry["disabled"] = true;
                }

                clean.Add((JsonNode)entry);
            }

            return clean;
        }
    }

    // ----- helpers -----------------------------------------------------------------------------

    private static void Set(JsonObject obj, string key, JsonNode? value, string[] order)
    {
        if (value is null)
        {
            obj.Remove(key);
        }
        else if (!JsonNode.DeepEquals(obj[key], value))
        {
            obj.SetOrdered(key, value, order);
        }
    }

    private static void Set(JsonObject obj, string key, string? value, string[] order) =>
        Set(obj, key, value is null ? null : JsonValue.Create(value), order);

    /// <summary>A v2.1 description (text or <c>{content}</c>); an empty description the file had stays as written.</summary>
    private static string? Description(JsonNode? node, JsonObject? template)
    {
        var text = Text(node is JsonObject d ? d["content"] : node);
        return string.IsNullOrEmpty(text) && template?["description"].GetString() == "" ? "" : text;
    }

    /// <summary>A scalar as text (v2.1 keeps header and parameter values as strings).</summary>
    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.GetString() ?? value.ToJsonString(),
        _ => node.ToJsonString(),
    };

    /// <summary>The template's value when it means the same text (keeps e.g. a number unquoted).</summary>
    private static JsonNode? Reuse(JsonNode? template, JsonNode? value) =>
        template is JsonValue t && value is JsonValue v && Text(t) == Text(v) ? t.DeepClone() : value?.DeepClone();

    /// <summary>Reorders known keys into canonical order (unknown keys stay after them).</summary>
    private static void Order(JsonObject obj, string[] order)
    {
        var properties = obj.Select(p => (p.Key, Value: p.Value)).ToList();
        if (properties.Select(p => p.Key).SequenceEqual(properties.Select(p => p.Key).OrderBy(k => Array.IndexOf(order, k) is var i and >= 0 ? i : int.MaxValue)))
        {
            return;
        }

        obj.Clear();
        foreach (var (key, value) in properties.OrderBy(p => Array.IndexOf(order, p.Key) is var i and >= 0 ? i : int.MaxValue))
        {
            obj[key] = value?.DeepClone();
        }
    }
}
