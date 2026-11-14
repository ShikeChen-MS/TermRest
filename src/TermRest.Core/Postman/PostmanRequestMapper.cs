using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Scripting;

namespace TermRest.Core.Postman;

public enum PostmanSchemaVersion
{
    V20,
    V21,
}

/// <summary>How a key/value array is written back to Postman JSON.</summary>
internal enum KeyValueStyle
{
    Query,
    PathVariable,
    Header,
    UrlEncoded,
    FormData,
    Environment,
    CollectionVariable,
}

/// <summary>
/// Maps Postman v2.0/v2.1 item JSON to <see cref="HttpRequestSpec"/> and back. Writing is surgical: only sections
/// that differ from the baseline are regenerated, and regenerated objects are cloned from their source JSON so
/// unknown properties and key order survive.
/// </summary>
public static class PostmanRequestMapper
{
    internal static readonly string[] RootOrder = ["info", "item", "auth", "event", "variable", "protocolProfileBehavior"];
    internal static readonly string[] ItemOrder = ["id", "name", "description", "item", "event", "termrest", "auth", "protocolProfileBehavior", "request", "response", "variable"];
    internal static readonly string[] RequestOrder = ["auth", "method", "header", "body", "url", "description", "certificate", "proxy"];
    internal static readonly string[] UrlOrder = ["raw", "protocol", "auth", "host", "port", "path", "query", "hash", "variable"];
    internal static readonly string[] BodyOrder = ["mode", "raw", "urlencoded", "formdata", "file", "graphql", "options", "disabled"];
    internal static readonly string[] KeyValueOrder = ["key", "value", "description", "type", "src", "contentType", "disabled", "enabled"];
    internal static readonly string[] AuthParamOrder = ["key", "value", "type"];
    internal static readonly string[] CertificateOrder = ["name", "matches", "key", "cert", "pfx", "passphrase"];
    internal static readonly string[] EventOrder = ["id", "listen", "script", "disabled"];
    internal static readonly string[] ScriptOrder = ["id", "exec", "type", "packages"];
    private static readonly string[] TermRestOrder = ["scripts"];
    private static readonly string[] ScriptSettingsOrder = ["language", "preRequest", "postResponse"];

    private static readonly string[] BodyModeKeys = ["raw", "urlencoded", "formdata", "file", "graphql"];

    // ----- reading ---------------------------------------------------------------------------

    public static HttpRequestSpec Read(JsonObject item, PostmanSchemaVersion version)
    {
        var spec = new HttpRequestSpec();
        switch (item["request"])
        {
            case JsonValue shortForm:
                spec.Url = shortForm.GetString() ?? "";
                spec.UrlWasString = true;
                spec.QueryParams = QueryFromRaw(spec.Url);
                spec.PathVariables = PostmanUrl.SyncPathVariables(spec.Url, []);
                break;
            case JsonObject request:
                spec.Method = request["method"].GetString() ?? "GET";
                ReadUrl(request["url"], spec);
                spec.Headers = ReadKeyValues(request["header"]);
                spec.Body = ReadBody(request["body"] as JsonObject);
                spec.Auth = ReadAuth(request["auth"], version);
                spec.Certificate = ReadCertificate(request["certificate"]);
                spec.Description = ReadDescription(request["description"]);
                break;
        }

        spec.Settings = ReadSettings(item["protocolProfileBehavior"]);
        spec.Scripts = ReadScripts(item["event"]);
        spec.ScriptSettings = ReadScriptSettings(item["termrest"]);
        return spec;
    }

    internal static List<KeyValueEntry> ReadKeyValues(JsonNode? node, bool environmentStyle = false)
    {
        var list = new List<KeyValueEntry>();
        if (node is JsonArray array)
        {
            foreach (var element in array)
            {
                if (element is JsonObject obj)
                {
                    list.Add(ReadEntry(obj, environmentStyle));
                }
            }
        }
        else if (node.GetString() is { Length: > 0 } text)
        {
            // Postman v2 allows headers as a single "Key: Value\n..." string.
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                list.Add(colon >= 0
                    ? new KeyValueEntry { Key = trimmed[..colon].Trim(), Value = trimmed[(colon + 1)..].Trim() }
                    : new KeyValueEntry { Key = trimmed.Trim() });
            }
        }

        return list;
    }

    private static KeyValueEntry ReadEntry(JsonObject obj, bool environmentStyle)
    {
        string? value = "";
        if (obj.TryGetPropertyValue("value", out var valueNode))
        {
            value = ValueText(valueNode);
        }

        return new KeyValueEntry
        {
            Key = obj["key"].GetString() ?? obj["name"].GetString() ?? "",
            Value = value,
            Enabled = environmentStyle ? obj["enabled"].GetBool() ?? true : !(obj["disabled"].GetBool() ?? false),
            Description = ReadDescription(obj["description"]),
            Type = obj["type"].GetString(),
            Src = ReadSrc(obj["src"]),
            ContentType = obj["contentType"].GetString(),
            Source = obj,
        };
    }

    private static string? ReadSrc(JsonNode? node) => node switch
    {
        JsonArray array => string.Join('\n', array.Select(n => n.GetString()).Where(s => s is not null)),
        _ => node.GetString(),
    };

    /// <summary>A value as the text that is edited: numbers, booleans and objects as their JSON.</summary>
    private static string? ValueText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v => v.GetString() ?? v.ToJsonString(),
        _ => node.ToJsonString(),
    };

    internal static string? ReadDescription(JsonNode? node) => node is JsonObject obj ? obj["content"].GetString() : node.GetString();

    private static void ReadUrl(JsonNode? node, HttpRequestSpec spec)
    {
        switch (node)
        {
            case JsonValue value:
                spec.Url = value.GetString() ?? "";
                spec.UrlWasString = true;
                spec.QueryParams = QueryFromRaw(spec.Url);
                spec.PathVariables = PostmanUrl.SyncPathVariables(spec.Url, []);
                break;
            case JsonObject obj:
                var query = obj["query"] is JsonArray ? ReadKeyValues(obj["query"]) : null;
                spec.Url = RawUrl(obj, query);
                spec.QueryParams = query ?? QueryFromRaw(spec.Url);
                var variables = ReadKeyValues(obj["variable"]);
                foreach (var name in PostmanUrl.PathVariableNames(spec.Url))
                {
                    if (!variables.Any(v => v.Key == name))
                    {
                        variables.Add(new KeyValueEntry { Key = name, Value = "" });
                    }
                }

                spec.PathVariables = variables;
                break;
        }
    }

    /// <summary>
    /// The URL of a URL object: its <c>raw</c> text, else built from its parts (Postman allows either).
    /// <paramref name="query"/>: its <c>query</c> entries when already read.
    /// </summary>
    internal static string RawUrl(JsonObject url, List<KeyValueEntry>? query = null) =>
        url["raw"].GetString() ?? PostmanUrl.BuildRaw(
            url["protocol"].GetString(),
            ReadParts(url["host"], '.'),
            url["port"].GetString(),
            ReadParts(url["path"], '/'),
            query ?? (url["query"] is JsonArray ? ReadKeyValues(url["query"]) : []),
            url["hash"].GetString());

    private static IEnumerable<string> ReadParts(JsonNode? node, char separator) => node switch
    {
        JsonArray array => array.Select(n => n.GetString() ?? ""),
        _ when node.GetString() is { } s => s.Split(separator),
        _ => [],
    };

    private static List<KeyValueEntry> QueryFromRaw(string raw) =>
        PostmanUrl.Parse(raw).Query.Select(q => new KeyValueEntry { Key = q.Key, Value = q.Value }).ToList();

    private static RequestBody ReadBody(JsonObject? obj)
    {
        var body = new RequestBody { Source = obj };
        if (obj is null)
        {
            return body;
        }

        body.Disabled = obj["disabled"].GetBool() ?? false;
        body.Raw = obj["raw"].GetString() ?? "";
        body.RawLanguage = ((obj["options"] as JsonObject)?["raw"] as JsonObject)?["language"].GetString() ?? "text";
        body.UrlEncoded = ReadKeyValues(obj["urlencoded"]);
        body.FormData = ReadKeyValues(obj["formdata"]);
        body.FileSrc = (obj["file"] as JsonObject)?["src"].GetString();
        body.FileLanguage = BodyFile.Normalize((obj["file"] as JsonObject)?["language"].GetString());
        if (obj["graphql"] is JsonObject graphql)
        {
            body.GraphQLQuery = graphql["query"].GetString() ?? "";
            body.GraphQLVariables = graphql["variables"].GetString() ?? "";
        }

        body.Mode = obj["mode"].GetString() switch
        {
            "raw" => BodyMode.Raw,
            "urlencoded" => BodyMode.UrlEncoded,
            "formdata" => BodyMode.FormData,
            "file" => BodyMode.File,
            "graphql" => BodyMode.GraphQL,
            _ => BodyMode.None,
        };
        return body;
    }

    internal static AuthSpec? ReadAuth(JsonNode? node, PostmanSchemaVersion version)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var type = obj["type"].GetString() ?? "noauth";
        var spec = new AuthSpec { Type = type, Source = obj };
        switch (obj[type])
        {
            case JsonArray parameters:
                foreach (var p in parameters.OfType<JsonObject>())
                {
                    spec.Params.Add(new AuthParam
                    {
                        Key = p["key"].GetString() ?? "",
                        Value = p["value"]?.DeepClone(),
                        Type = p["type"].GetString(),
                        Source = p,
                    });
                }

                break;
            case JsonObject map:
                // Postman v2.0 stores auth parameters as a plain object.
                foreach (var (key, value) in map)
                {
                    spec.Params.Add(new AuthParam { Key = key, Value = value?.DeepClone() });
                }

                break;
        }

        _ = version;
        return spec;
    }

    private static CertificateSpec? ReadCertificate(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return new CertificateSpec
        {
            Name = obj["name"].GetString(),
            Matches = (obj["matches"] as JsonArray)?.Select(m => m.GetString()).OfType<string>().ToList() ?? [],
            CertSrc = (obj["cert"] as JsonObject)?["src"].GetString(),
            KeySrc = (obj["key"] as JsonObject)?["src"].GetString(),
            PfxSrc = (obj["pfx"] as JsonObject)?["src"].GetString(),
            Passphrase = obj["passphrase"].GetString(),
            Source = obj,
        };
    }

    private static RequestSettings ReadSettings(JsonNode? node)
    {
        var obj = node as JsonObject;
        return new RequestSettings
        {
            StrictSsl = obj?["strictSSL"].GetBool(),
            FollowRedirects = obj?["followRedirects"].GetBool(),
            MaxRedirects = obj?["maxRedirects"].GetInt(),
            DisableCookies = obj?["disableCookies"].GetBool(),
            Source = obj,
        };
    }

    /// <summary>
    /// Reads the scripts from the <c>event</c> array of a collection, folder or request: a pre-request and a post-response
    /// script per language, the language being the script's <c>type</c> (Postman's are JavaScript). A disabled JavaScript
    /// script is not read, as Postman does not run it. Scripts in TermRest's other languages are read though they are
    /// marked disabled: TermRest marks them so for Postman, which would run them as JavaScript.
    /// </summary>
    public static ScriptSet ReadScripts(JsonNode? node)
    {
        var scripts = new Dictionary<string, (string PreRequest, string Test)>(StringComparer.Ordinal);
        foreach (var e in (node as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (PhaseOf(e) is not { } phase)
            {
                continue;
            }

            var script = e["script"] as JsonObject;
            var language = ScriptLanguage.FromType(script?["type"].GetString());
            if (language == ScriptLanguage.JavaScript && e["disabled"].GetBool() == true)
            {
                continue;
            }

            var exec = script?["exec"];
            var text = exec is JsonArray lines ? string.Join('\n', lines.Select(l => l.GetString() ?? "")) : exec.GetString() ?? "";
            var current = scripts.GetValueOrDefault(language, (PreRequest: "", Test: ""));
            scripts[language] = phase == ScriptPhase.PreRequest ? (text, current.Test) : (current.PreRequest, text);
        }

        return new ScriptSet(scripts.Select(p => new LanguageScripts(p.Key, p.Value.PreRequest, p.Value.Test)));
    }

    /// <summary>How a request runs its scripts: TermRest's <c>"termrest": {"scripts": {…}}</c>; the defaults when absent.</summary>
    public static ScriptSettings ReadScriptSettings(JsonNode? node) =>
        (node as JsonObject)?["scripts"] is JsonObject scripts
            ? new ScriptSettings(ScriptLanguage.Parse(scripts["language"].GetString()), scripts["preRequest"].GetBool(), scripts["postResponse"].GetBool())
            : ScriptSettings.Auto;

    private static ScriptPhase? PhaseOf(JsonObject e) => e["listen"].GetString() switch
    {
        "prerequest" => ScriptPhase.PreRequest,
        "test" => ScriptPhase.PostResponse,
        _ => null,
    };

    // ----- writing ---------------------------------------------------------------------------

    /// <summary>Writes the sections of <paramref name="spec"/> that differ from <paramref name="baseline"/> into <paramref name="item"/>.</summary>
    public static void Apply(JsonObject item, HttpRequestSpec spec, HttpRequestSpec baseline, PostmanSchemaVersion version)
    {
        var force = false;
        if (item["request"] is not JsonObject request)
        {
            request = new JsonObject();
            item.SetOrdered("request", request, ItemOrder);
            force = true;
        }

        if (force || spec.Method != baseline.Method)
        {
            request.SetOrdered("method", spec.Method, RequestOrder);
        }

        if (force || !spec.UrlEquals(baseline))
        {
            var url = WriteUrl(spec, request["url"]);
            if (url is null)
            {
                request.Remove("url");
            }
            else
            {
                request.SetOrdered("url", url, RequestOrder);
            }
        }

        if (force || !KeyValueEntry.ListEquals(spec.Headers, baseline.Headers))
        {
            request.SetOrdered("header", WriteKeyValues(spec.Headers, KeyValueStyle.Header), RequestOrder);
        }

        if (force || !spec.Body.ContentEquals(baseline.Body))
        {
            var body = WriteBody(spec.Body);
            if (body is null)
            {
                request.Remove("body");
            }
            else
            {
                request.SetOrdered("body", body, RequestOrder);
            }
        }

        if (force || !AuthSpec.AreEqual(spec.Auth, baseline.Auth))
        {
            if (spec.Auth is null)
            {
                request.Remove("auth");
            }
            else
            {
                request.SetOrdered("auth", WriteAuth(spec.Auth, version), RequestOrder);
            }
        }

        if (force || !CertificateSpec.AreEqual(spec.Certificate, baseline.Certificate))
        {
            if (spec.Certificate is null)
            {
                request.Remove("certificate");
            }
            else
            {
                request.SetOrdered("certificate", WriteCertificate(spec.Certificate), RequestOrder);
            }
        }

        if (spec.Description != baseline.Description)
        {
            if (string.IsNullOrEmpty(spec.Description))
            {
                request.Remove("description");
            }
            else
            {
                request.SetOrdered("description", WriteDescription(request["description"], spec.Description), RequestOrder);
            }
        }

        if (!spec.Settings.ContentEquals(baseline.Settings))
        {
            var settings = WriteSettings(spec.Settings);
            if (settings is null)
            {
                item.Remove("protocolProfileBehavior");
            }
            else
            {
                item.SetOrdered("protocolProfileBehavior", settings, ItemOrder);
            }
        }

        ApplyScripts(item, spec.Scripts, baseline.Scripts, ItemOrder);
        ApplyScriptSettings(item, spec.ScriptSettings, baseline.ScriptSettings);
    }

    /// <summary>
    /// Writes the scripts that differ from <paramref name="baseline"/> into the <c>event</c> array of a collection
    /// (<paramref name="order"/> = <see cref="RootOrder"/>), folder or request (<see cref="ItemOrder"/>): one event per
    /// phase and language.
    /// </summary>
    public static void ApplyScripts(JsonObject owner, ScriptSet scripts, ScriptSet baseline, string[] order)
    {
        foreach (var language in scripts.Languages.Concat(baseline.Languages).Select(s => s.Language).Distinct(StringComparer.Ordinal).ToList())
        {
            var now = scripts.For(language);
            var before = baseline.For(language);
            foreach (var phase in ScriptPhases.All)
            {
                if (now.Code(phase) != before.Code(phase))
                {
                    WriteScript(owner, phase, now.Code(phase), language, order);
                }
            }
        }
    }

    /// <summary>Writes how a request runs its scripts into <c>"termrest": {"scripts": {…}}</c>, removed when it is the default.</summary>
    public static void ApplyScriptSettings(JsonObject item, ScriptSettings settings, ScriptSettings baseline)
    {
        if (settings == baseline)
        {
            return;
        }

        var termrest = item["termrest"] as JsonObject;
        if (settings.IsAuto)
        {
            if (termrest is not null)
            {
                termrest.Remove("scripts");
                if (termrest.Count == 0)
                {
                    item.Remove("termrest");
                }
            }

            return;
        }

        if (termrest is null)
        {
            termrest = new JsonObject();
            item.SetOrdered("termrest", termrest, ItemOrder);
        }

        if (termrest["scripts"] is not JsonObject scripts)
        {
            scripts = new JsonObject();
            termrest.SetOrdered("scripts", scripts, TermRestOrder);
        }

        Set("language", settings.Language is null ? null : JsonValue.Create(settings.Language));
        Set("preRequest", settings.PreRequest is { } preRequest ? JsonValue.Create(preRequest) : null);
        Set("postResponse", settings.PostResponse is { } postResponse ? JsonValue.Create(postResponse) : null);

        void Set(string key, JsonNode? value)
        {
            if (value is null)
            {
                scripts.Remove(key);
            }
            else
            {
                scripts.SetOrdered(key, value, ScriptSettingsOrder);
            }
        }
    }

    internal static JsonArray WriteKeyValues(IEnumerable<KeyValueEntry> entries, KeyValueStyle style)
    {
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add((JsonNode)WriteEntry(entry, style));
        }

        return array;
    }

    private static JsonObject WriteEntry(KeyValueEntry entry, KeyValueStyle style)
    {
        var isNew = entry.Source is null;
        var obj = entry.Source is { } source ? (JsonObject)source.DeepClone() : new JsonObject();
        obj.SetOrdered("key", entry.Key, KeyValueOrder);

        // An unchanged value keeps its JSON type (a number, boolean or object is edited as its text).
        var skipValue = (style == KeyValueStyle.FormData && entry.IsFile && !obj.ContainsKey("value"))
                        || (obj.TryGetPropertyValue("value", out var value) && ValueText(value) == entry.Value);
        if (!skipValue)
        {
            obj.SetOrdered("value", entry.Value is null ? null : JsonValue.Create(entry.Value), KeyValueOrder);
        }

        if (entry.Description != ReadDescription(obj["description"]))
        {
            if (string.IsNullOrEmpty(entry.Description))
            {
                obj.Remove("description");
            }
            else
            {
                obj.SetOrdered("description", WriteDescription(obj["description"], entry.Description), KeyValueOrder);
            }
        }

        var type = entry.Type ?? (isNew ? DefaultType(style) : null);
        if (type is not null)
        {
            obj.SetOrdered("type", type, KeyValueOrder);
        }

        if (style == KeyValueStyle.FormData)
        {
            if (entry.IsFile)
            {
                var sources = (entry.Src ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                JsonNode? src = obj["src"] is JsonArray || sources.Length > 1
                    ? PostmanJson.ToJsonArray(sources.Select(s => (JsonNode?)JsonValue.Create(s)))
                    : sources.Length == 1 ? JsonValue.Create(sources[0]) : null;
                obj.SetOrdered("src", src, KeyValueOrder);
            }

            if (entry.ContentType != obj["contentType"].GetString())
            {
                if (string.IsNullOrEmpty(entry.ContentType))
                {
                    obj.Remove("contentType");
                }
                else
                {
                    obj.SetOrdered("contentType", entry.ContentType, KeyValueOrder);
                }
            }
        }

        if (style == KeyValueStyle.Environment)
        {
            // A value without "enabled" counts as enabled: it only gets one once disabled, so an edit doesn't touch the others.
            if (isNew || !entry.Enabled || obj.ContainsKey("enabled"))
            {
                obj.SetOrdered("enabled", entry.Enabled, KeyValueOrder);
            }
        }
        else if (!entry.Enabled)
        {
            obj.SetOrdered("disabled", true, KeyValueOrder);
        }
        else if (obj["disabled"].GetBool() == true)
        {
            obj.Remove("disabled");
        }

        return obj;
    }

    private static string? DefaultType(KeyValueStyle style) => style switch
    {
        KeyValueStyle.Header or KeyValueStyle.UrlEncoded or KeyValueStyle.FormData => "text",
        KeyValueStyle.Environment => "default",
        KeyValueStyle.CollectionVariable => "string",
        _ => null,
    };

    private static JsonNode WriteDescription(JsonNode? existing, string description)
    {
        if (existing is JsonObject obj)
        {
            var clone = (JsonObject)obj.DeepClone();
            clone["content"] = description;
            return clone;
        }

        return JsonValue.Create(description);
    }

    private static JsonNode? WriteUrl(HttpRequestSpec spec, JsonNode? existing)
    {
        if (spec.Url.Length == 0 && spec.QueryParams.Count == 0 && spec.PathVariables.Count == 0 && existing is null)
        {
            return null;
        }

        var needsObject = existing is JsonObject
            || !spec.UrlWasString
            || spec.QueryParams.Any(q => !q.Enabled || q.Description is not null)
            || spec.PathVariables.Any(p => !string.IsNullOrEmpty(p.Value) || p.Description is not null);
        if (!needsObject)
        {
            return JsonValue.Create(spec.Url);
        }

        var obj = existing is JsonObject source ? (JsonObject)source.DeepClone() : new JsonObject();
        var parsed = PostmanUrl.Parse(spec.Url);
        obj.SetOrdered("raw", spec.Url, UrlOrder);
        SetOrRemove(obj, "protocol", parsed.Protocol, UrlOrder);
        if (parsed.AuthUser is not null)
        {
            var auth = new JsonObject { ["user"] = parsed.AuthUser };
            if (parsed.AuthPassword is not null)
            {
                auth["password"] = parsed.AuthPassword;
            }

            obj.SetOrdered("auth", auth, UrlOrder);
        }
        else
        {
            obj.Remove("auth");
        }

        SetOrRemoveArray(obj, "host", parsed.Host);
        SetOrRemove(obj, "port", parsed.Port, UrlOrder);
        SetOrRemoveArray(obj, "path", parsed.Path);
        if (spec.QueryParams.Count > 0)
        {
            obj.SetOrdered("query", WriteKeyValues(spec.QueryParams, KeyValueStyle.Query), UrlOrder);
        }
        else
        {
            obj.Remove("query");
        }

        SetOrRemove(obj, "hash", parsed.Hash, UrlOrder);
        if (spec.PathVariables.Count > 0)
        {
            obj.SetOrdered("variable", WriteKeyValues(spec.PathVariables, KeyValueStyle.PathVariable), UrlOrder);
        }
        else
        {
            obj.Remove("variable");
        }

        return obj;
    }

    private static void SetOrRemoveArray(JsonObject obj, string key, List<string> values)
    {
        if (values.Count > 0)
        {
            obj.SetOrdered(key, PostmanJson.ToJsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v))), UrlOrder);
        }
        else
        {
            obj.Remove(key);
        }
    }

    private static void SetOrRemove(JsonObject obj, string key, string? value, string[] order)
    {
        if (value is null)
        {
            obj.Remove(key);
        }
        else
        {
            obj.SetOrdered(key, value, order);
        }
    }

    private static JsonObject? WriteBody(RequestBody body)
    {
        if (body.Mode == BodyMode.None)
        {
            return null;
        }

        var mode = body.Mode switch
        {
            BodyMode.Raw => "raw",
            BodyMode.UrlEncoded => "urlencoded",
            BodyMode.FormData => "formdata",
            BodyMode.File => "file",
            _ => "graphql",
        };

        var obj = body.Source is { } source ? (JsonObject)source.DeepClone() : new JsonObject();
        if (obj["mode"].GetString() is { } previousMode && previousMode != mode)
        {
            foreach (var key in BodyModeKeys)
            {
                if (key != mode)
                {
                    obj.Remove(key);
                }
            }

            if (body.Mode != BodyMode.Raw)
            {
                obj.Remove("options");
            }
        }

        obj.SetOrdered("mode", mode, BodyOrder);
        switch (body.Mode)
        {
            case BodyMode.Raw:
                obj.SetOrdered("raw", body.Raw, BodyOrder);
                var language = string.IsNullOrEmpty(body.RawLanguage) ? "text" : body.RawLanguage;
                if (obj["options"] is JsonObject options)
                {
                    if (options["raw"] is JsonObject rawOptions)
                    {
                        rawOptions["language"] = language;
                    }
                    else
                    {
                        options["raw"] = new JsonObject { ["language"] = language };
                    }
                }
                else if (language != "text")
                {
                    obj.SetOrdered("options", new JsonObject { ["raw"] = new JsonObject { ["language"] = language } }, BodyOrder);
                }

                break;
            case BodyMode.UrlEncoded:
                obj.SetOrdered("urlencoded", WriteKeyValues(body.UrlEncoded, KeyValueStyle.UrlEncoded), BodyOrder);
                break;
            case BodyMode.FormData:
                obj.SetOrdered("formdata", WriteKeyValues(body.FormData, KeyValueStyle.FormData), BodyOrder);
                break;
            case BodyMode.File:
                var file = obj["file"] is JsonObject existingFile ? existingFile : new JsonObject();
                file["src"] = body.FileSrc;

                // Written only when the type changed, so a value TermRest does not know stays as it was.
                if (BodyFile.Normalize(file["language"].GetString()) != body.FileLanguage)
                {
                    if (body.FileLanguage is null)
                    {
                        file.Remove("language");
                    }
                    else
                    {
                        file["language"] = body.FileLanguage;
                    }
                }

                if (file.Parent is null)
                {
                    obj.SetOrdered("file", file, BodyOrder);
                }

                break;
            case BodyMode.GraphQL:
                var graphql = obj["graphql"] is JsonObject existingGraphQL ? existingGraphQL : new JsonObject();
                graphql["query"] = body.GraphQLQuery;
                graphql["variables"] = body.GraphQLVariables;
                if (graphql.Parent is null)
                {
                    obj.SetOrdered("graphql", graphql, BodyOrder);
                }

                break;
        }

        if (body.Disabled)
        {
            obj.SetOrdered("disabled", true, BodyOrder);
        }
        else if (obj["disabled"].GetBool() == true)
        {
            obj.Remove("disabled");
        }

        return obj;
    }

    internal static JsonObject WriteAuth(AuthSpec auth, PostmanSchemaVersion version)
    {
        var reuse = auth.Source is { } source && source["type"].GetString() == auth.Type;
        var obj = reuse ? (JsonObject)auth.Source!.DeepClone() : new JsonObject();
        obj.SetOrdered("type", auth.Type, ["type"]);
        if (auth.Type == "noauth")
        {
            return obj;
        }

        var v20Style = reuse ? obj[auth.Type] is JsonObject : version == PostmanSchemaVersion.V20;
        if (v20Style)
        {
            var map = new JsonObject();
            foreach (var p in auth.Params)
            {
                map[p.Key] = p.Value?.DeepClone();
            }

            obj[auth.Type] = map;
        }
        else
        {
            var array = new JsonArray();
            foreach (var p in auth.Params)
            {
                var po = p.Source is { } ps ? (JsonObject)ps.DeepClone() : new JsonObject();
                po.SetOrdered("key", p.Key, AuthParamOrder);
                po.SetOrdered("value", p.Value?.DeepClone(), AuthParamOrder);
                po.SetOrdered("type", p.Type ?? InferAuthParamType(p.Value), AuthParamOrder);
                array.Add((JsonNode)po);
            }

            obj[auth.Type] = array;
        }

        return obj;
    }

    private static string InferAuthParamType(JsonNode? value) => value switch
    {
        JsonValue v when v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
        JsonValue => "string",
        null => "string",
        _ => "any",
    };

    private static JsonObject WriteCertificate(CertificateSpec certificate)
    {
        var obj = certificate.Source is { } source ? (JsonObject)source.DeepClone() : new JsonObject();
        SetOrRemove(obj, "name", certificate.Name, CertificateOrder);
        obj.SetOrdered("matches", PostmanJson.ToJsonArray(certificate.Matches.Select(m => (JsonNode?)JsonValue.Create(m))), CertificateOrder);
        SetSrc(obj, "key", certificate.KeySrc);
        SetSrc(obj, "cert", certificate.CertSrc);
        SetSrc(obj, "pfx", certificate.PfxSrc);
        SetOrRemove(obj, "passphrase", certificate.Passphrase, CertificateOrder);
        return obj;

        static void SetSrc(JsonObject obj, string key, string? src)
        {
            if (src is null)
            {
                obj.Remove(key);
            }
            else if (obj[key] is JsonObject existing)
            {
                existing["src"] = src;
            }
            else
            {
                obj.SetOrdered(key, new JsonObject { ["src"] = src }, CertificateOrder);
            }
        }
    }

    private static JsonObject? WriteSettings(RequestSettings settings)
    {
        var obj = settings.Source is { } source ? (JsonObject)source.DeepClone() : new JsonObject();
        SetOrRemoveBool(obj, "strictSSL", settings.StrictSsl);
        SetOrRemoveBool(obj, "followRedirects", settings.FollowRedirects);
        if (settings.MaxRedirects is { } max)
        {
            obj["maxRedirects"] = max;
        }
        else
        {
            obj.Remove("maxRedirects");
        }

        SetOrRemoveBool(obj, "disableCookies", settings.DisableCookies);
        return obj.Count == 0 && settings.Source is null ? null : obj;

        static void SetOrRemoveBool(JsonObject obj, string key, bool? value)
        {
            if (value is { } b)
            {
                obj[key] = b;
            }
            else
            {
                obj.Remove(key);
            }
        }
    }

    /// <summary>
    /// Writes the script of <paramref name="phase"/> in <paramref name="language"/>. Postman's JavaScript scripts are
    /// written as Postman writes them. Scripts in TermRest's other languages are marked <c>"disabled": true</c>:
    /// Postman and Newman run every script that is not disabled as JavaScript. Clearing one of them removes it.
    /// </summary>
    private static void WriteScript(JsonObject owner, ScriptPhase phase, string text, string language, string[] order)
    {
        var listen = phase == ScriptPhase.PreRequest ? "prerequest" : "test";
        var javaScript = language == ScriptLanguage.JavaScript;
        var events = owner["event"] as JsonArray;
        var existing = events?.OfType<JsonObject>().LastOrDefault(e =>
            e["listen"].GetString() == listen
            && ScriptLanguage.FromType((e["script"] as JsonObject)?["type"].GetString()) == language
            && !(javaScript && e["disabled"].GetBool() == true));
        var empty = javaScript ? text.Length == 0 : text.Trim().Length == 0;
        if (existing is null && empty)
        {
            return;
        }

        if (!javaScript && empty)
        {
            events!.Remove(existing);
            if (events.Count == 0)
            {
                owner.Remove("event");
            }

            return;
        }

        if (events is null)
        {
            events = [];
            owner.SetOrdered("event", events, order);
        }

        if (existing is null)
        {
            var created = new JsonObject { ["exec"] = new JsonArray(), ["type"] = ScriptLanguage.ToType(language) };
            if (javaScript)
            {
                created["packages"] = new JsonObject();
            }

            existing = new JsonObject { ["listen"] = listen, ["script"] = created };
            events.Add((JsonNode)existing);
        }

        if (existing["script"] is not JsonObject script)
        {
            script = new JsonObject();
            existing.SetOrdered("script", script, EventOrder);
        }

        var lines = text.Length == 0 ? [""] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        script.SetOrdered("exec", PostmanJson.ToJsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l))), ScriptOrder);
        if (ScriptLanguage.FromType(script["type"].GetString()) != language)
        {
            script.SetOrdered("type", ScriptLanguage.ToType(language), ScriptOrder);
        }

        if (!javaScript && existing["disabled"].GetBool() != true)
        {
            existing.SetOrdered("disabled", true, EventOrder);
        }
    }
}
