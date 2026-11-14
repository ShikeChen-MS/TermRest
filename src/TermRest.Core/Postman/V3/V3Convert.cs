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
    private static readonly string[] FormDataOrder = ["type", "key", "value", "src", "contentType", "description", "disabled"];
    private static readonly string[] AuthOrder = ["id", "type", "name", "rules", "credentials"];
    private static readonly string[] ScriptOrder = ["type", "code", "language", "packages", "requests"];
    private static readonly string[] VariableOrder = ["key", "value", "disabled", "secret", "description", "source"];
    private static readonly string[] EnvironmentValueOrder = ["key", "value", "enabled", "disabled", "type", "description", "secret", "source"];

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

    /// <summary>Request, folder or collection auth. "inherit" (or no auth) is null; of several credential sets, the first.</summary>
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
    /// A collection's or folder's credential sets selected by rules (a list of auths), for <see cref="AuthSetsProperty"/>:
    /// each with its name, its rules as key/value pairs (Postman writes them as a map, <c>url: api.example.com</c>; its
    /// schema as a list of <c>{key, value}</c>) and its auth in v2.1 form. Null for one auth, or a list of one without rules.
    /// </summary>
    public static JsonArray? CredentialSetsToV21(JsonNode? node)
    {
        if (node is not JsonArray list || list.OfType<JsonObject>().ToList() is not { Count: > 0 } sets || (sets.Count == 1 && sets[0]["rules"] is null))
        {
            return null;
        }

        var result = new JsonArray();
        foreach (var set in sets)
        {
            var rules = new JsonArray();
            switch (set["rules"])
            {
                case null:
                    break;
                case JsonObject map:
                    foreach (var (key, value) in map)
                    {
                        rules.Add((JsonNode)new JsonObject { ["key"] = key, ["value"] = value.GetString() ?? value?.ToJsonString() });
                    }

                    break;
                case JsonArray entries:
                    foreach (var entry in entries)
                    {
                        // An entry that isn't {key, value} (a plain pattern, say) can't be checked: a rule that never matches.
                        rules.Add((JsonNode)(entry is JsonObject rule
                            ? new JsonObject { ["key"] = rule["key"].GetString() ?? "", ["value"] = rule["value"].GetString() ?? rule["value"]?.ToJsonString() }
                            : new JsonObject { ["key"] = "", ["value"] = entry?.ToJsonString() }));
                    }

                    break;
                case var other:
                    // Rules of another shape can't be checked: a rule that never matches, so its credentials go nowhere.
                    rules.Add((JsonNode)new JsonObject { ["key"] = "", ["value"] = other.ToJsonString() });
                    break;
            }

            result.Add((JsonNode)new JsonObject
            {
                ["name"] = set["name"].GetString() ?? set["id"].GetString() ?? Model.AuthSpec.DisplayName(set["type"].GetString() ?? "noauth"),
                ["rules"] = rules,
                ["auth"] = AuthToV21(set),
                ["v3"] = set.DeepClone(), // as the file has it: a copy of the folder is written with it (see DefinitionToV3)
            });
        }

        return result;
    }

    /// <summary>
    /// The property of a v2.1 folder (or collection) object that holds its v3 credential sets (see
    /// <see cref="CredentialSetsToV21"/>), next to its <c>auth</c> (the first set). Not written to v2.1 files.
    /// </summary>
    public const string AuthSetsProperty = "x-v3-auth-sets";

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

    /// <summary>Collection and folder variables: v3 allows a list of entries or, for plain ones, a <c>{name: value}</c> map.</summary>
    public static JsonArray VariablesToV21(JsonNode? node)
    {
        var array = new JsonArray();
        if (node is JsonObject map)
        {
            foreach (var (key, value) in map)
            {
                array.Add((JsonNode)new JsonObject { ["key"] = key, ["value"] = Text(value) });
            }

            return array;
        }

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
        if (template is null)
        {
            v3.SetOrdered("$kind", HttpRequestKind, RequestOrder); // a file keeps its own, or none
        }

        if (item["request"] is not JsonObject request)
        {
            request = new JsonObject { ["method"] = "GET", ["url"] = item["request"]?.DeepClone() };
        }

        Set(v3, "description", Description(request["description"], template), RequestOrder);
        var (raw, query, variables) = ReadUrl(request["url"]);
        Set(v3, "url", raw.Length == 0 && template?["url"] is null ? null : JsonValue.Create(raw), RequestOrder);
        v3.SetOrdered("method", request["method"].GetString() ?? "GET", RequestOrder);
        Set(v3, "headers", KeyValuesToV3(HeaderList(request["header"]), template?["headers"], mapWhenNew: true), RequestOrder);
        Set(v3, "queryParams", KeyValuesToV3(query, template?["queryParams"], mapWhenNew: false), RequestOrder);
        Set(v3, "pathVariables", KeyValuesToV3(variables, template?["pathVariables"], mapWhenNew: false), RequestOrder);

        // A body that wasn't edited stays as the file has it, also one TermRest can't read (a type it doesn't know, fields
        // under another key): rebuilt from what TermRest read of it, it would be lost.
        var fileBody = template?["body"];
        Set(v3, "body", fileBody is not null && JsonNode.DeepEquals(BodyToV21(fileBody), request["body"])
            ? fileBody.DeepClone()
            : BodyToV3(request["body"] as JsonObject, fileBody), RequestOrder);
        Set(v3, "auth", AuthToV3(request["auth"] as JsonObject, template?["auth"]), RequestOrder);
        Set(v3, "scripts", EventsToScripts(item["event"] as JsonArray, template?["scripts"], http: false), RequestOrder);
        Set(v3, "termrest", item["termrest"] is JsonObject { Count: > 0 } termrest ? termrest.DeepClone() : null, RequestOrder);
        Set(v3, "settings", SettingsToV3(item["protocolProfileBehavior"] as JsonObject, template?["settings"]), RequestOrder);
        return v3;
    }

    private static (string Raw, JsonArray? Query, JsonArray? Variables) ReadUrl(JsonNode? url) => url switch
    {
        JsonObject obj => (PostmanRequestMapper.RawUrl(obj), obj["query"] as JsonArray, obj["variable"] as JsonArray),
        _ => (url.GetString() ?? "", null, null),
    };

    /// <summary>Headers as a list of entries: Postman v2 also allows one <c>"Key: Value\n…"</c> string.</summary>
    private static JsonArray? HeaderList(JsonNode? node) => node switch
    {
        JsonArray list => list,
        JsonValue => PostmanRequestMapper.WriteKeyValues(PostmanRequestMapper.ReadKeyValues(node), KeyValueStyle.Header),
        _ => null,
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
                     && list.All(e => (e["value"] is JsonValue || (template is JsonObject && e["value"] is null))
                                      && e["key"].GetString() is { } key && (key.Length > 0 || template is JsonObject)
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

            DescriptionToText(result);
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
            // A body without a type, which TermRest can't read (one written in v2.1's form, say) and showed as none, is kept.
            return template is JsonObject t && t["type"].GetString() is null or "none" ? t.DeepClone() : null;
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
            DescriptionToText(result);
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

        // Postman v2.0 stores the parameters as a plain object, v2.1 as a list of entries.
        List<(string Key, JsonNode? Value)> parameters = auth[type] switch
        {
            JsonArray entries => [.. entries.OfType<JsonObject>().Select(p => (p["key"].GetString() ?? "", p["value"]))],
            JsonObject map => [.. map.Select(p => (p.Key, p.Value))],
            _ => [],
        };
        if (parameters.Count == 0 && previous is not null && !previous.ContainsKey("credentials"))
        {
            return result; // as the file has it: no credentials
        }

        JsonNode credentials;
        if (previous?["credentials"] is JsonArray)
        {
            var array = new JsonArray();
            foreach (var (key, value) in parameters)
            {
                array.Add((JsonNode)new JsonObject { ["key"] = key, ["value"] = value?.DeepClone() });
            }

            credentials = array;
        }
        else
        {
            var map = new JsonObject();
            var previousMap = previous?["credentials"] as JsonObject;
            foreach (var (key, value) in parameters)
            {
                map[key] = Reuse(previousMap?[key], value);
            }

            credentials = map;
        }

        result.SetOrdered("credentials", credentials, AuthOrder);
        return result;
    }

    /// <summary>
    /// v2.1 events → v3 scripts (<c>beforeRequest</c>/<c>afterResponse</c>, or <c>http:…</c> on folders): one per type and
    /// language, each written over the script of that type and language the file had. A disabled JavaScript script is
    /// left out: v3 cannot mark a script disabled, and Postman does not run it.
    /// </summary>
    public static JsonArray? EventsToScripts(JsonArray? events, JsonNode? template, bool http)
    {
        var templates = (template as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var scripts = new JsonArray();
        foreach (var e in events?.OfType<JsonObject>() ?? [])
        {
            var script = e["script"] as JsonObject;
            var language = ScriptLanguage.FromType(script?["type"].GetString());
            if (language == ScriptLanguage.JavaScript && e["disabled"].GetBool() == true)
            {
                continue;
            }

            var listen = e["listen"].GetString() ?? "";
            var type = e["x-v3-type"].GetString() is { Length: > 0 } original ? original : listen switch
            {
                "prerequest" => http ? "http:beforeRequest" : "beforeRequest",
                "test" => http ? "http:afterResponse" : "afterResponse",
                _ => listen,
            };
            var exec = script?["exec"];
            var code = exec is JsonArray lines ? string.Join('\n', lines.Select(l => l.GetString() ?? "")) : exec.GetString() ?? "";
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

            // A script's language comes with its code; an empty one keeps the language the file has (Postman's: JavaScript),
            // and a JavaScript one in a file that leaves its language out stays without it.
            var languageType = code.Trim().Length == 0 && previous?["language"] is { } kept ? kept : script?["type"] ?? previous?["language"];
            if (previous is null || previous.ContainsKey("language") || language != ScriptLanguage.JavaScript)
            {
                result.SetOrdered("language", languageType?.DeepClone() ?? "text/javascript", ScriptOrder);
            }
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
            if (CredentialSetsToV21(definition["auth"]) is { } sets)
            {
                target[AuthSetsProperty] = sets;
            }
        }

        if (definition["variables"] is JsonArray or JsonObject)
        {
            target["variable"] = VariablesToV21(definition["variables"]);
        }
    }

    /// <summary>The v3 definition file content for a folder item or the collection root.</summary>
    public static JsonObject DefinitionToV3(JsonObject source, JsonObject? template, bool isRoot)
    {
        var v3 = template is null ? new JsonObject() : (JsonObject)template.DeepClone();
        if (template is null)
        {
            // A file keeps its own: Postman writes "collection", also for folders, but some files say "folder" or have none.
            v3.SetOrdered("$kind", CollectionKind, DefinitionOrder);
        }

        var description = isRoot ? (source["info"] as JsonObject)?["description"] : source["description"];
        Set(v3, "description", Description(description, template), DefinitionOrder);
        Set(v3, "auth", AuthToV3(source["auth"] as JsonObject, AuthTemplate(source, template)), DefinitionOrder);
        Set(v3, "variables", source["variable"] is JsonArray variables ? VariablesToV3(variables, template?["variables"]) : null, DefinitionOrder);
        Set(v3, "scripts", EventsToScripts(source["event"] as JsonArray, template?["scripts"], http: true), DefinitionOrder);
        return v3;
    }

    /// <summary>
    /// The auth a folder's definition is written over: its file's; for a folder without a file of its own yet (a copy of
    /// one), the credential sets it holds (see <see cref="CredentialSetsToV21"/>) as its original's file has them, with ids
    /// of their own, so that the copy keeps their rules and its credentials go no further than the original's.
    /// </summary>
    private static JsonNode? AuthTemplate(JsonObject source, JsonObject? template)
    {
        if (template?["auth"] is not null || source[AuthSetsProperty] is not JsonArray sets || sets.Any(s => s?["v3"] is not JsonObject))
        {
            return template?["auth"];
        }

        var list = new JsonArray();
        foreach (var set in sets)
        {
            var copy = (JsonObject)set!["v3"]!.DeepClone();
            if (copy.ContainsKey("id"))
            {
                copy["id"] = Guid.NewGuid().ToString();
            }

            list.Add((JsonNode)copy);
        }

        return list;
    }

    /// <summary>
    /// The secret variables (of a v2.1 collection and its folders) whose values a conversion to v3 leaves out: Postman's v3
    /// collections keep secret values out of their files, where a secret variable has no value.
    /// </summary>
    public static List<string> SecretValuesLeftOut(JsonObject collection)
    {
        var names = new List<string>();
        void Visit(JsonObject node)
        {
            foreach (var variable in (node["variable"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (variable["type"].GetString() == "secret" && Text(variable["value"]) is { Length: > 0 } && variable["key"].GetString() is { } key)
                {
                    names.Add(key);
                }
            }

            foreach (var folder in (node["item"] as JsonArray)?.OfType<JsonObject>().Where(i => i["item"] is JsonArray) ?? [])
            {
                Visit(folder);
            }
        }

        Visit(collection);
        return names;
    }

    /// <summary>
    /// Variables keep the form the file used: a <c>{name: value}</c> map (possible while every variable is a plain, enabled,
    /// unique name/value pair, as Postman writes them) or a list of entries.
    /// </summary>
    private static JsonNode VariablesToV3(JsonArray variables, JsonNode? template)
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

            // "disabled: false" stays where the file has it (some Postman versions write it for every variable).
            var previous = templates.FirstOrDefault(t => t["key"].GetString() == result["key"].GetString());
            if (result["disabled"].GetBool() != true)
            {
                if (previous?["disabled"].GetBool() == false)
                {
                    result["disabled"] = false;
                }
                else
                {
                    result.Remove("disabled");
                }
            }

            DescriptionToText(result);

            // Secrets from a vault have no value; TermRest's editor reads a missing value as "".
            if (previous is not null && !previous.ContainsKey("value") && result["value"].GetString() is "" or null)
            {
                result.Remove("value");
            }

            if (previous is null && template is not JsonObject)
            {
                // One the file doesn't have yet (a converted collection's) takes the shape of Postman's v3 schema: its value is
                // text, a secret variable has none (Postman keeps secret values out of the file; see SecretValuesLeftOut),
                // and v2.1's other keys (id, name, system …) have no place there. (A map's entries are plain name/value pairs
                // already, kept below as the file writes them.)
                foreach (var key in result.Select(p => p.Key).Where(k => !VariableOrder.Contains(k)).ToList())
                {
                    result.Remove(key);
                }

                if (secret || result["value"] is null)
                {
                    result.Remove("value");
                }
                else
                {
                    result["value"] = Text(result["value"]);
                }
            }

            Order(result, VariableOrder);
            var match = templates.FirstOrDefault(t => JsonNode.DeepEquals(t, result));
            array.Add(match is null ? result : match.DeepClone());
        }

        if (template is not JsonObject map
            || !array.All(v => v is JsonObject { Count: 2 } e && e["key"].GetString() is { Length: > 0 } && e.TryGetPropertyValue("value", out var value) && value is JsonValue or null)
            || array.Select(v => v!["key"].GetString()).Distinct(StringComparer.Ordinal).Count() != array.Count)
        {
            return array;
        }

        var mapped = new JsonObject();
        foreach (var variable in array.Cast<JsonObject>())
        {
            var key = variable["key"].GetString()!;
            var value = variable["value"];

            // The file's value when it means the same text: a number stays unquoted, an empty value stays as it was written.
            mapped[key] = map.TryGetPropertyValue(key, out var previous) && (Text(previous) ?? "") == (Text(value) ?? "") ? previous?.DeepClone() : value?.DeepClone();
        }

        return mapped;
    }

    // ----- environments and globals ------------------------------------------------------------

    public static JsonObject EnvironmentToV21(JsonObject v3, string fallbackName, bool globals)
    {
        var env = (JsonObject)v3.DeepClone();
        env["name"] ??= fallbackName;
        if (env["values"] is not JsonArray values)
        {
            env["values"] = new JsonArray();
        }
        else
        {
            // Postman also marks a secret with "secret: true", its value left out of the file; v2.1 has "type: secret". And
            // a disabled value with "disabled: true" where values have no "enabled"; v2.1 environments have "enabled: false".
            foreach (var value in values.OfType<JsonObject>())
            {
                if (value["secret"].GetBool() is { } secret)
                {
                    value.Remove("secret");
                    if (secret)
                    {
                        value["type"] = "secret";
                    }
                }

                if (value["disabled"].GetBool() is { } disabled)
                {
                    value.Remove("disabled");
                    if (disabled)
                    {
                        value["enabled"] = false;
                    }
                }
            }
        }

        env["_postman_variable_scope"] = globals ? "globals" : "environment";
        return env;
    }

    public static JsonObject EnvironmentToV3(JsonObject env, JsonObject? template)
    {
        var v3 = template is null ? new JsonObject() : (JsonObject)template.DeepClone();
        v3.SetOrdered("name", env["name"]?.DeepClone() ?? "Environment", ["name", "values"]);
        var templates = (template?["values"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        // Secrets keep the form the file marks them with: "secret: true" (Postman's, also for new ones there) or "type: secret".
        var secretKeyForm = templates.Any(t => t.ContainsKey("secret")) && !templates.Any(t => t["type"].GetString() == "secret");

        // Disabled values too: "disabled: true" in a file whose values have no "enabled" (as Postman writes them), else
        // "enabled: false".
        var disabledKeyForm = templates.Count > 0 && !templates.Any(t => t.ContainsKey("enabled"));
        var values = new JsonArray();
        foreach (var value in (env["values"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var result = (JsonObject)value.DeepClone();
            var previous = templates.FirstOrDefault(t => t["key"].GetString() == result["key"].GetString());
            if ((previous?.ContainsKey("disabled") ?? false) || (!(previous?.ContainsKey("enabled") ?? false) && disabledKeyForm))
            {
                var enabled = result["enabled"].GetBool() ?? true;
                result.Remove("enabled");
                if (!enabled)
                {
                    result["disabled"] = true;
                }
                else if (previous?["disabled"].GetBool() == false)
                {
                    result["disabled"] = false;
                }
            }

            if (previous is null ? secretKeyForm : previous.ContainsKey("secret") || (secretKeyForm && previous["type"] is null))
            {
                if (result["type"].GetString() == "secret")
                {
                    result["secret"] = true;
                    if (previous?["type"] is { } type && type.GetString() != "secret")
                    {
                        result["type"] = type.DeepClone();
                    }
                    else
                    {
                        result.Remove("type");
                    }
                }
                else if (previous?["secret"].GetBool() == false)
                {
                    result["secret"] = false;
                }
            }

            if (result["type"].GetString() == "default" && previous?["type"] is null)
            {
                result.Remove("type"); // Postman only writes "type" for secrets
            }

            // A value the file leaves out (Postman's, for a secret it keeps elsewhere) stays out while it is empty.
            if (previous is not null && !previous.ContainsKey("value") && result["value"].GetString() is "" or null)
            {
                result.Remove("value");
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
            if (HeaderList(node) is not { Count: > 0 } headers)
            {
                return null;
            }

            var clean = new JsonArray();
            foreach (var header in headers.OfType<JsonObject>())
            {
                var entry = new JsonObject { ["key"] = header["key"].GetString() ?? header["name"].GetString() ?? "", ["value"] = Text(header["value"]) ?? "" };
                if (DescriptionText(header["description"]) is { Length: > 0 } description)
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
        var text = DescriptionText(node);
        return string.IsNullOrEmpty(text) && template?["description"].GetString() == "" ? "" : text;
    }

    /// <summary>A v2.1 description as text: v2.1 also allows a <c>{content, type}</c> object.</summary>
    private static string? DescriptionText(JsonNode? node) => Text(node is JsonObject d ? d["content"] : node);

    /// <summary>
    /// Makes the description of a header, parameter, field or variable the text v3 allows: a v2.1 <c>{content, type}</c>
    /// object becomes its content, and one without content, or null, is left out.
    /// </summary>
    private static void DescriptionToText(JsonObject entry)
    {
        switch (entry["description"])
        {
            case JsonObject description when DescriptionText(description) is { Length: > 0 } text:
                entry["description"] = text;
                break;
            case JsonObject or null:
                entry.Remove("description");
                break;
        }
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
