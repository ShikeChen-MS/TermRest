using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;

namespace TermRest.Core.Workspace;

// Scripts: Python code run before a request is sent and after its response arrives, at collection, folder and request level.
public sealed partial class RequestRunner
{
    private const string NoInterpreter =
        "These scripts need Python, and none is chosen for this workspace: choose one in Tools › Scripts… (full-screen UI) or pass --python.";

    private readonly Lock _scriptLock = new();
    private PythonWorker? _python;

    /// <summary>Variables set by scripts in this session (Postman's "current values"). Scripts never change files.</summary>
    public SessionVariables Session { get; } = new();

    /// <summary>Whether scripts run (CLI <c>--no-scripts</c> turns them off).</summary>
    public bool ScriptsEnabled { get; set; } = true;

    /// <summary>The Python that runs scripts; null when none is chosen (requests with scripts are then not sent).</summary>
    public string? PythonInterpreter { get; private set; }

    /// <summary>Longest time the scripts of one phase (pre-request or test) may take; Python is then stopped.</summary>
    public TimeSpan ScriptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long Python keeps running without scripts to run, ready for the next request.</summary>
    public TimeSpan ScriptIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether Python is running now: it starts for the first script and stops once idle.</summary>
    public bool ScriptsRunning => _python?.IsRunning == true;

    /// <summary>
    /// Chooses the Python that runs scripts: <paramref name="python"/> (CLI <c>--python</c>), else the one chosen for this
    /// workspace in the user's settings (Tools › Scripts in the full-screen UI).
    /// </summary>
    /// <param name="userSettingsDirectory">Overrides the user's configuration folder (tests, portable setups).</param>
    public void ConfigureScripts(string? python = null, string? userSettingsDirectory = null) =>
        SetPythonInterpreter(string.IsNullOrWhiteSpace(python) ? UserSettings.Load(userSettingsDirectory).GetPythonInterpreter(Workspace.RootPath) : python);

    /// <summary>Sets the Python that runs scripts (see <see cref="ResolveInterpreter"/>); a running one is stopped.</summary>
    public void SetPythonInterpreter(string? interpreter)
    {
        var resolved = string.IsNullOrWhiteSpace(interpreter) ? null : ResolveInterpreter(interpreter);
        lock (_scriptLock)
        {
            if (resolved == PythonInterpreter)
            {
                return;
            }

            _python?.Dispose();
            _python = null;
            PythonInterpreter = resolved;
        }
    }

    /// <summary>A command name (<c>python3</c>) is looked up on PATH; a relative path is relative to the workspace folder.</summary>
    public string ResolveInterpreter(string interpreter)
    {
        interpreter = interpreter.Trim().Trim('"');
        return interpreter.IndexOfAny(['/', '\\']) < 0 || Path.IsPathRooted(interpreter)
            ? interpreter
            : Path.GetFullPath(Path.Combine(Workspace.RootPath, interpreter));
    }

    /// <summary>Stops Python now (it starts again for the next script), e.g. after installing packages.</summary>
    public void StopScripts()
    {
        lock (_scriptLock)
        {
            _python?.Dispose();
            _python = null;
        }
    }

    /// <summary>
    /// Sends a request with its scripts. The pre-request scripts of the collection, its folders (outermost first) and the
    /// request run first: they can set variables and change the request. A failed one stops the request: it is not sent.
    /// The request is then prepared (<paramref name="prepared"/> sees it before it goes out) and sent, and the test
    /// scripts run on the response, in the same order. Only then is it recorded in <see cref="History"/> and
    /// <see cref="Recorder"/>, with what the scripts did in <see cref="HttpExchange.Scripts"/>.
    /// Without scripts this is exactly <see cref="Prepare(PostmanCollection, RequestNode?, HttpRequestSpec)"/> and send.
    /// </summary>
    public async Task<HttpExchange> SendAsync(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec,
        Action<PreparedRequest>? prepared, CancellationToken cancellationToken = default)
    {
        // No ConfigureAwait(false) here either: UI callers resume on their own thread.
        var chain = ScriptsEnabled ? ScriptChain.For(collection, node, spec) : new ScriptChain();
        if (chain.IsEmpty)
        {
            var request = Prepare(collection, node, spec);
            prepared?.Invoke(request);
            return await SendAsync(request, cancellationToken).ConfigureAwait(true);
        }

        var results = new ScriptResults();
        results.NotRun.AddRange(chain.JavaScript);
        var local = new Dictionary<string, string?>(StringComparer.Ordinal);
        var working = spec;
        if (chain.PreRequest.Count > 0)
        {
            working = spec.Clone();
            var request = RequestToSend(working, node?.Name);
            var reply = await RunScriptsAsync("prerequest", chain.PreRequest, collection, local, request, null, node?.Name, results, cancellationToken).ConfigureAwait(true);
            if (reply?["request"] is JsonObject edited)
            {
                ApplyRequestEdits(edited, request, working, results);
            }
        }

        if (results.Error is not null || cancellationToken.IsCancellationRequested)
        {
            var request = Prepare(collection, node, spec);
            prepared?.Invoke(request);
            var notSent = new HttpExchange(request) { Scripts = results };
            if (results.Error is { } error)
            {
                request.Errors.Add($"A pre-request script failed: {error}");
                notSent.ErrorMessage = $"Not sent: {error}";
            }
            else
            {
                notSent.Cancelled = true;
                notSent.ErrorMessage = "Request cancelled.";
            }

            return notSent; // nothing was sent: not kept in history or recordings
        }

        var ready = Prepare(collection, node, working, spec, local);
        prepared?.Invoke(ready);
        var exchange = await Executor.SendAsync(ready, cancellationToken).ConfigureAwait(true);
        exchange.Scripts = results;
        if (chain.Test.Count > 0 && exchange.Response is not null && !exchange.Cancelled)
        {
            await RunScriptsAsync("test", chain.Test, collection, local, RequestSent(exchange, node?.Name), ResponseReceived(exchange), node?.Name, results, cancellationToken).ConfigureAwait(true);
        }

        Record(exchange);
        Recorder?.Record(exchange, Environment?.Name);
        return exchange;
    }

    /// <summary>Runs the scripts of one phase in Python and takes in what they did; null when they could not run.</summary>
    private async Task<JsonObject?> RunScriptsAsync(string phase, List<ScriptCode> scripts, PostmanCollection collection,
        Dictionary<string, string?> local, JsonObject request, JsonObject? response, string? requestName, ScriptResults results,
        CancellationToken cancellationToken)
    {
        var label = phase == "prerequest" ? "pre-request" : "test";
        PythonWorker worker;
        lock (_scriptLock)
        {
            if (PythonInterpreter is null)
            {
                results.Error ??= new ScriptError("", NoInterpreter);
                return null;
            }

            worker = _python ??= new PythonWorker(PythonInterpreter, Workspace.RootPath) { IdleTimeout = ScriptIdleTimeout };
        }

        var message = new JsonObject
        {
            ["phase"] = phase,
            ["scripts"] = PostmanJson.ToJsonArray(scripts.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["code"] = s.Code })),
            ["variables"] = new JsonObject
            {
                ["environment"] = Values(Environment?.Values, Environment is { } environment ? Session.Find(environment) : null),
                ["collection"] = Values(collection.GetVariables(), Session.Find(collection)),
                ["globals"] = Values(Workspace.Globals?.Values, Session.Globals),
                ["local"] = Values(LocalVariables.Select(v => new KeyValueEntry { Key = v.Key, Value = v.Value }), local),
            },
            ["request"] = request,
            ["response"] = response,
            ["info"] = new JsonObject { ["request_name"] = requestName, ["collection"] = collection.Name, ["environment"] = Environment?.Name },
        };

        JsonObject reply;
        try
        {
            // Off the caller's thread: starting Python takes a moment, and the UI must stay responsive meanwhile.
            reply = await Task.Run(() => worker.RunAsync(message, ScriptTimeout, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
        catch (ScriptException ex)
        {
            results.Error ??= new ScriptError("", ex.Message);
            return null;
        }
        catch (ObjectDisposedException)
        {
            results.Error ??= new ScriptError("", "Python was stopped while the scripts ran.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            results.Notes.Add($"Cancelled while the {label} scripts ran.");
            return null;
        }

        var error = reply["error"] as JsonObject;
        var failed = error is null ? -1 : scripts.FindIndex(s => s.Name == Text(error["script"]));
        results.Ran += failed < 0 ? scripts.Count : failed + 1;
        ApplyVariableChanges(reply["changes"] as JsonObject, collection, local, results);
        foreach (var test in (reply["tests"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var passed = test["passed"] is JsonValue value && value.TryGetValue<bool>(out var ok) && ok;
            results.Tests.Add(new ScriptTest(Text(test["name"]) ?? "", passed, Text(test["error"]), Text(test["script"]) ?? ""));
        }

        foreach (var log in (reply["logs"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            results.Logs.Add(new ScriptLog(label, Text(log["script"]) ?? "", Text(log["text"]) ?? ""));
        }

        if (reply["dropped"] is JsonValue dropped && dropped.TryGetValue<int>(out var more) && more > 0)
        {
            results.Notes.Add($"{more} more lines the {label} scripts printed are not shown.");
        }

        if (error is not null)
        {
            results.Error ??= new ScriptError(Text(error["script"]) ?? "", Text(error["message"]) ?? "error", Text(error["traceback"]));
        }

        return reply;
    }

    /// <summary>Keeps the variables scripts set: for this session (environment, collection, globals) or this request.</summary>
    private void ApplyVariableChanges(JsonObject? changes, PostmanCollection collection, Dictionary<string, string?> local, ScriptResults results)
    {
        if (changes is null)
        {
            return;
        }

        Apply("environment", "environment", () => Environment is { } environment ? Session.For(environment) : null);
        Apply("collection", "collection_variables", () => Session.For(collection));
        Apply("globals", "globals", () => Session.Globals);
        Apply("local", "variables", () => local);

        void Apply(string scope, string name, Func<Dictionary<string, string?>?> target)
        {
            if (changes[scope] is not JsonObject { Count: > 0 } values)
            {
                return;
            }

            if (target() is not { } store)
            {
                results.Notes.Add($"Not kept, because no environment is selected: {string.Join(", ", values.Select(v => $"{name}.{v.Key}"))}.");
                return;
            }

            foreach (var (key, value) in values)
            {
                store[key] = Text(value); // null: unset
                results.VariablesSet.Add($"{name}.{key}");
            }
        }
    }

    /// <summary>Takes in what pre-request scripts changed in <c>tr.request</c>.</summary>
    private static void ApplyRequestEdits(JsonObject edited, JsonObject original, HttpRequestSpec spec, ScriptResults results)
    {
        if (Text(edited["method"]) is { Length: > 0 } method && method != Text(original["method"]))
        {
            spec.Method = method;
        }

        if (Text(edited["url"]) is { } url && url != Text(original["url"]))
        {
            spec.Url = url;
            spec.QueryParams = PostmanUrl.SyncQueryFromRaw(url, spec.QueryParams);
            spec.PathVariables = PostmanUrl.SyncPathVariables(url, spec.PathVariables);
        }

        if (!JsonNode.DeepEquals(edited["headers"], original["headers"]))
        {
            // Unchanged headers keep their entries (descriptions…); disabled ones stay as they were.
            var unused = spec.Headers.Where(h => h.Enabled).ToList();
            var headers = new List<KeyValueEntry>();
            foreach (var pair in (edited["headers"] as JsonArray)?.OfType<JsonArray>() ?? [])
            {
                if (pair.Count < 2 || Text(pair[0]) is not { Length: > 0 } key)
                {
                    continue;
                }

                var value = Text(pair[1]) ?? "";
                var kept = unused.FirstOrDefault(h => h.Key == key && (h.Value ?? "") == value);
                if (kept is not null)
                {
                    unused.Remove(kept);
                }

                headers.Add(kept ?? new KeyValueEntry { Key = key, Value = value });
            }

            headers.AddRange(spec.Headers.Where(h => !h.Enabled));
            spec.Headers = headers;
        }

        var body = Text(edited["body"]);
        if (body != Text(original["body"]))
        {
            if (spec.Body.Mode is BodyMode.Raw or BodyMode.None)
            {
                spec.Body.Mode = BodyMode.Raw;
                spec.Body.Raw = body ?? "";
            }
            else
            {
                results.Notes.Add("tr.request.body was not used: scripts can change raw bodies only.");
            }
        }
    }

    /// <summary>The request as pre-request scripts see it: as written, with <c>{{variables}}</c> not yet replaced.</summary>
    private static JsonObject RequestToSend(HttpRequestSpec spec, string? name) => new()
    {
        ["name"] = name,
        ["method"] = spec.Method,
        ["url"] = spec.Url,
        ["headers"] = Pairs(spec.Headers.Where(h => h.Enabled && h.Key.Length > 0).Select(h => new KeyValuePair<string, string>(h.Key, h.Value ?? ""))),
        ["body"] = spec.Body.Mode == BodyMode.Raw ? spec.Body.Raw : null,
    };

    /// <summary>The request as test scripts see it: as it was sent (files are not read).</summary>
    private static JsonObject RequestSent(HttpExchange exchange, string? name)
    {
        var request = exchange.Request;
        return new JsonObject
        {
            ["name"] = name,
            ["method"] = request.Method,
            ["url"] = request.Uri?.AbsoluteUri ?? request.Url,
            ["headers"] = Pairs(exchange.SentHeaders.Count > 0 ? exchange.SentHeaders : request.Headers),
            ["body"] = request.Body.Kind switch
            {
                PreparedBodyKind.Text => request.Body.Text,
                PreparedBodyKind.FormUrlEncoded => PreparedBody.EncodeForm(request.Body.Fields),
                _ => null,
            },
        };
    }

    private static JsonObject ResponseReceived(HttpExchange exchange)
    {
        var response = exchange.Response!;
        var json = new JsonObject
        {
            ["status"] = response.StatusCode,
            ["reason"] = response.ReasonPhrase,
            ["headers"] = Pairs(response.Headers),
            ["time"] = Math.Round(exchange.Timing.Total?.TotalMilliseconds ?? 0, 3),
            ["size"] = response.Body.LongLength,
        };
        if (BodyFormatter.LooksLikeText(response.Body, response.ContentType))
        {
            json["text"] = BodyFormatter.Decode(response.Body, response.ContentType);
        }
        else
        {
            json["base64"] = Convert.ToBase64String(response.Body);
        }

        return json;
    }

    /// <summary>The enabled values of a scope, with the values scripts set on top (null: unset).</summary>
    private static JsonObject Values(IEnumerable<KeyValueEntry>? entries, IReadOnlyDictionary<string, string?>? setByScripts)
    {
        var values = new JsonObject();
        foreach (var entry in entries ?? [])
        {
            if (entry.Enabled && entry.Key.Length > 0)
            {
                values[entry.Key] = entry.Value ?? "";
            }
        }

        foreach (var (key, value) in setByScripts ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static JsonArray Pairs(IEnumerable<KeyValuePair<string, string>> pairs) =>
        PostmanJson.ToJsonArray(pairs.Select(p => (JsonNode?)PostmanJson.ToJsonArray([JsonValue.Create(p.Key), JsonValue.Create(p.Value)])));

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The scripts around one request, in the order they run: collection, folders (outermost first), request.</summary>
    private sealed class ScriptChain
    {
        public List<ScriptCode> PreRequest { get; } = [];

        public List<ScriptCode> Test { get; } = [];

        /// <summary>Where JavaScript (Postman) scripts are: kept, not run.</summary>
        public List<string> JavaScript { get; } = [];

        public bool IsEmpty => PreRequest.Count == 0 && Test.Count == 0 && JavaScript.Count == 0;

        public static ScriptChain For(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec)
        {
            var levels = new List<(string Name, ScriptSet Scripts)> { ($"collection '{collection.Name}'", collection.GetScripts(collection.RootNode)) };
            if (node is not null && node.Collection == collection)
            {
                levels.AddRange(node.Ancestors().Where(f => f is not CollectionRootNode).Reverse()
                    .Select(f => ($"folder '{f.PathInCollection}'", collection.GetScripts(f))));
            }

            levels.Add((node is null ? "request" : $"request '{node.PathInCollection}'", spec.Scripts));
            var chain = new ScriptChain();
            foreach (var (name, scripts) in levels.Where(l => !l.Scripts.IsEmpty))
            {
                if (!scripts.IsPython)
                {
                    chain.JavaScript.Add(name);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(scripts.PreRequest))
                {
                    chain.PreRequest.Add(new ScriptCode(name, scripts.PreRequest));
                }

                if (!string.IsNullOrWhiteSpace(scripts.Test))
                {
                    chain.Test.Add(new ScriptCode(name, scripts.Test));
                }
            }

            return chain;
        }
    }
}
