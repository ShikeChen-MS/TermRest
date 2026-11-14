using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;
using TermRest.Core.Variables;

namespace TermRest.Core.Workspace;

// Scripts: Python, JavaScript, C# or PowerShell code run before a request is sent and after its response arrives, at
// collection, folder and request level, by the runtime the user chose for each language. Each level may have a script
// per language; a request runs those of one language only (ScriptPlan), one at a time, in one runtime.
public sealed partial class RequestRunner
{
    /// <summary>A response body as bytes (base64 for scripts): <see cref="ScriptWorker"/> writes it in parts, with no copy as text.</summary>
    private static readonly JsonTypeInfo<byte[]> BodyBytes = JsonMetadataServices.CreateValueInfo<byte[]>(
        new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() }, JsonMetadataServices.ByteArrayConverter);

    private readonly Lock _scriptLock = new();
    private readonly Dictionary<string, string> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptWorker> _workers = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _scriptLanguageOrder = ScriptLanguage.All;

    /// <summary>Variables set by scripts in this session (Postman's "current values"). Scripts never change files.</summary>
    public SessionVariables Session { get; } = new();

    /// <summary>Whether scripts run (CLI <c>--no-scripts</c> turns them off).</summary>
    public bool ScriptsEnabled { get; set; } = true;

    /// <summary>
    /// What happens to scripts whose language has no runtime chosen. True (the command line): it is an error, so a
    /// pre-request script stops the request. False (the full-screen UI): they are skipped, with a note, and the request
    /// is sent.
    /// </summary>
    public bool RequireScriptRuntimes { get; set; } = true;

    /// <summary>
    /// Why the user's settings, which choose the runtimes and the language order, could not be read by
    /// <see cref="ConfigureScripts"/>; null when they could (or there are none). Their defaults are used then.
    /// </summary>
    public string? UserSettingsError { get; private set; }

    /// <summary>The Python that runs Python scripts; null when none is chosen.</summary>
    public string? PythonInterpreter => GetRuntime(ScriptLanguage.Python);

    /// <summary>The Node.js or Bun that runs JavaScript scripts; null when none is chosen.</summary>
    public string? JavaScriptRuntime => GetRuntime(ScriptLanguage.JavaScript);

    /// <summary>The <c>dotnet</c> (.NET SDK 10 or later) that builds and runs C# scripts; null when none is chosen.</summary>
    public string? CSharpRuntime => GetRuntime(ScriptLanguage.CSharp);

    /// <summary>The <c>pwsh</c> (PowerShell 7 or later) that runs PowerShell scripts; null when none is chosen.</summary>
    public string? PowerShellRuntime => GetRuntime(ScriptLanguage.PowerShell);

    /// <summary>
    /// The order in which the languages of a request's scripts are tried when its Scripts tab chooses none: the first
    /// language with all the scripts it runs runs them (Tools › Scripts in the full-screen UI, <c>--script-order</c>).
    /// Always has every language.
    /// </summary>
    public IReadOnlyList<string> ScriptLanguageOrder
    {
        get => _scriptLanguageOrder;
        set => _scriptLanguageOrder = ScriptLanguage.Order(value);
    }

    /// <summary>Longest time the scripts of one phase (pre-request or test) may take; the runtime is then stopped.</summary>
    public TimeSpan ScriptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a runtime keeps running without scripts to run, ready for the next request.</summary>
    public TimeSpan ScriptIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether a runtime is running now: each starts for its first script and stops once idle.</summary>
    public bool ScriptsRunning => RunningRuntimes.Count > 0;

    /// <summary>The runtimes running now (Python, Node.js, Bun, .NET, PowerShell).</summary>
    public IReadOnlyList<string> RunningRuntimes
    {
        get
        {
            lock (_scriptLock)
            {
                return _workers.Values.Where(w => w.IsRunning).Select(w => w.Name).ToList();
            }
        }
    }

    public string? GetRuntime(string language)
    {
        lock (_scriptLock)
        {
            return _runtimes.GetValueOrDefault(language);
        }
    }

    /// <summary>
    /// Chooses the runtimes that run scripts: <paramref name="python"/>, <paramref name="javaScript"/>,
    /// <paramref name="cSharp"/> and <paramref name="powerShell"/> (CLI <c>--python</c>, <c>--javascript</c>,
    /// <c>--csharp</c>, <c>--powershell</c>), else the ones chosen for this workspace in the user's settings (Tools ›
    /// Scripts in the full-screen UI); and the language order: <paramref name="scriptOrder"/> first (CLI
    /// <c>--script-order</c>), then the user's. When the user's settings cannot be read, see <see cref="UserSettingsError"/>.
    /// </summary>
    /// <param name="userSettingsDirectory">Overrides the user's configuration folder (tests, portable setups).</param>
    public void ConfigureScripts(string? python = null, string? javaScript = null, string? cSharp = null, string? powerShell = null,
        string? userSettingsDirectory = null, IEnumerable<string>? scriptOrder = null)
    {
        var settings = UserSettings.Load(userSettingsDirectory);
        UserSettingsError = settings.LoadError;
        Choose(ScriptLanguage.Python, python);
        Choose(ScriptLanguage.JavaScript, javaScript);
        Choose(ScriptLanguage.CSharp, cSharp);
        Choose(ScriptLanguage.PowerShell, powerShell);
        ScriptLanguageOrder = [.. (scriptOrder ?? []).Concat(settings.GetScriptLanguageOrder())];

        void Choose(string language, string? chosen) =>
            SetRuntime(language, string.IsNullOrWhiteSpace(chosen) ? settings.GetScriptRuntime(language, Workspace.RootPath) : chosen);
    }

    /// <summary>Sets the runtime for scripts in <paramref name="language"/> (see <see cref="ResolveRuntime"/>); a running one is stopped.</summary>
    public void SetRuntime(string language, string? runtime)
    {
        var resolved = string.IsNullOrWhiteSpace(runtime) ? null : ResolveRuntime(runtime);
        lock (_scriptLock)
        {
            if (resolved == _runtimes.GetValueOrDefault(language))
            {
                return;
            }

            if (_workers.Remove(language, out var worker))
            {
                worker.Dispose();
            }

            if (resolved is null)
            {
                _runtimes.Remove(language);
            }
            else
            {
                _runtimes[language] = resolved;
            }
        }
    }

    public void SetPythonInterpreter(string? interpreter) => SetRuntime(ScriptLanguage.Python, interpreter);

    public void SetJavaScriptRuntime(string? runtime) => SetRuntime(ScriptLanguage.JavaScript, runtime);

    public void SetCSharpRuntime(string? dotnet) => SetRuntime(ScriptLanguage.CSharp, dotnet);

    public void SetPowerShellRuntime(string? pwsh) => SetRuntime(ScriptLanguage.PowerShell, pwsh);

    /// <summary>
    /// A command name (<c>python3</c>, <c>node</c>, <c>dotnet</c>, <c>pwsh</c>) stays as it is, to be looked up on PATH only
    /// when it starts (<see cref="ScriptRuntimeLocator.Executable"/>); a relative path is relative to the workspace folder.
    /// </summary>
    public string ResolveRuntime(string runtime)
    {
        runtime = runtime.Trim().Trim('"');
        return runtime.IndexOfAny(['/', '\\']) < 0 || Path.IsPathRooted(runtime)
            ? runtime
            : Path.GetFullPath(Path.Combine(Workspace.RootPath, runtime));
    }

    /// <summary>Stops the runtimes now (they start again for the next script), e.g. after installing packages.</summary>
    /// <param name="compilerServer">
    /// Also stop the .NET SDK's C# compiler server, which builds of C# scripts keep running for 10 minutes (Tools ›
    /// Scripts › Stop scripts); not when TermRest just closes, so the next build stays quick.
    /// </param>
    public void StopScripts(bool compilerServer = false)
    {
        lock (_scriptLock)
        {
            foreach (var worker in _workers.Values)
            {
                worker.Dispose();
            }

            _workers.Clear();
        }

        if (compilerServer && GetRuntime(ScriptLanguage.CSharp) is { } dotnet)
        {
            CSharpPrograms.StopCompilerServer(dotnet);
        }
    }

    /// <summary>
    /// Sends a request with its scripts. The pre-request scripts of the collection, its folders (outermost first) and the
    /// request run first: they can set variables and change the request. A failed one stops the request: it is not sent.
    /// The request is then prepared (<paramref name="prepared"/> sees it before it goes out) and sent, and the test
    /// scripts run on the response, in the same order. Only then is it recorded in <see cref="History"/> and
    /// <see cref="Recorder"/>, with what the scripts did in <see cref="HttpExchange.Scripts"/>; values they kept in
    /// variables typed secret or named like secrets count among the request's secrets there (a token from a login).
    /// The scripts that run are those of one language (<see cref="PlanScripts"/>), so one runtime runs them, one after
    /// the other. When no language has all the scripts the request runs, it is not sent.
    /// The request is <paramref name="spec"/> as it is, and runs in the <see cref="Environment"/> chosen, when this is
    /// called: editing the request or switching the environment while it runs changes later requests only.
    /// Without scripts this is exactly <see cref="Prepare(PostmanCollection, RequestNode?, HttpRequestSpec)"/> and send.
    /// </summary>
    public async Task<HttpExchange> SendAsync(PostmanCollection collection, RequestNode? node, HttpRequestSpec spec,
        Action<PreparedRequest>? prepared, CancellationToken cancellationToken = default)
    {
        // No ConfigureAwait(false) here either: UI callers resume on their own thread.
        var environment = Environment;
        var plan = ScriptsEnabled ? PlanScripts(collection, node, spec.Scripts, spec.ScriptSettings) : ScriptPlan.None;
        if (plan.IsEmpty && plan.Error is null && plan.Notes.Count == 0)
        {
            var request = Prepare(environment, collection, node, spec, spec, null);
            prepared?.Invoke(request);
            return await SendAsync(environment, request, cancellationToken).ConfigureAwait(true);
        }

        // Copied before the scripts run: the caller's request may be edited meanwhile (the full-screen UI's editor stays open).
        var source = spec.Clone();
        var results = new ScriptResults();
        results.Notes.AddRange(plan.Notes);
        var local = new Dictionary<string, string?>(StringComparer.Ordinal);
        var working = source;
        if (plan.Error is { } planError)
        {
            results.Error = new ScriptError("", planError) { NotRun = true };
        }
        else if (plan.PreRequest.Count > 0)
        {
            working = source.Clone();
            var request = RequestToSend(working, node?.Name);
            var reply = await RunScriptsAsync("prerequest", [.. plan.PreRequest], environment, collection, local, request, null, node?.Name, results,
                cancellationToken).ConfigureAwait(true);
            if (reply?["request"] is JsonObject edited)
            {
                ApplyRequestEdits(edited, request, working, results);
            }
        }

        if (results.Error is not null || cancellationToken.IsCancellationRequested)
        {
            var request = Prepare(environment, collection, node, source, source, null);
            prepared?.Invoke(request);
            var notSent = new HttpExchange(request) { Scripts = results };
            if (results.Error is { } error)
            {
                request.Errors.Add(error.Script.Length == 0 ? error.Message : $"A pre-request script failed: {error}");
                notSent.ErrorMessage = $"Not sent: {error}";
            }
            else
            {
                notSent.Cancelled = true;
                notSent.ErrorMessage = "Request cancelled.";
            }

            return notSent; // nothing was sent: not kept in history or recordings
        }

        var ready = Prepare(environment, collection, node, working, source, local);
        prepared?.Invoke(ready);
        var exchange = await Executor.SendAsync(ready, cancellationToken).ConfigureAwait(true);
        exchange.Scripts = results;
        if (plan.PostResponse.Count > 0 && exchange.Response is not null && !exchange.Cancelled)
        {
            await RunScriptsAsync("test", [.. plan.PostResponse], environment, collection, local, RequestSent(exchange, node?.Name), ResponseReceived(exchange),
                node?.Name, results, cancellationToken).ConfigureAwait(true);

            // What they kept in secret variables or variables named like secrets (a token from a login response) is
            // redacted in this very exchange too, not only in later ones.
            var resolver = CreateResolver(environment, collection, local);
            exchange.Request.AddSecretValues(SecretVariableValues(environment, collection, resolver));
            exchange.Request.AddSensitiveValues(VariablesNamedLikeSecrets(environment, collection, resolver, local));
        }

        Record(exchange, environment?.Name);
        Recorder?.Record(exchange, environment?.Name);
        return exchange;
    }

    /// <summary>
    /// Which scripts run around a request with these <paramref name="scripts"/> and <paramref name="settings"/>: those of
    /// its collection, its folders and its own, in one language (see <see cref="ScriptPlan"/>).
    /// </summary>
    public ScriptPlan PlanScripts(PostmanCollection collection, RequestNode? node, ScriptSet scripts, ScriptSettings settings)
    {
        var levels = new List<ScriptLevel> { new($"collection '{collection.Name}'", collection.GetScripts(collection.RootNode)) };
        if (node is not null && node.Collection == collection)
        {
            levels.AddRange(node.Ancestors().Where(f => f is not CollectionRootNode).Reverse()
                .Select(f => new ScriptLevel($"folder '{f.PathInCollection}'", collection.GetScripts(f))));
        }

        levels.Add(new ScriptLevel(node is null ? "request" : $"request '{node.PathInCollection}'", scripts));
        return ScriptPlan.For(levels, settings, ScriptLanguageOrder);
    }

    /// <summary>Runs the scripts of one phase, all in one language, and takes in what they did; null when they did not run.</summary>
    /// <param name="environment">The request's environment (see <see cref="Environment"/>): the scripts read and set its variables.</param>
    private async Task<JsonObject?> RunScriptsAsync(string phase, List<ScriptCode> scripts, PostmanEnvironment? environment, PostmanCollection collection,
        Dictionary<string, string?> local, JsonObject request, JsonObject? response, string? requestName, ScriptResults results,
        CancellationToken cancellationToken)
    {
        var label = phase == "prerequest" ? "pre-request" : "test";
        var language = scripts[0].Language;
        ScriptWorker worker;
        lock (_scriptLock)
        {
            if (_runtimes.GetValueOrDefault(language) is not { } runtime)
            {
                if (RequireScriptRuntimes)
                {
                    var needed = language switch
                    {
                        ScriptLanguage.Python => "Python",
                        ScriptLanguage.CSharp => "the .NET SDK (10 or later)",
                        ScriptLanguage.PowerShell => "PowerShell (7 or later)",
                        _ => "a JavaScript runtime (Node.js or Bun)",
                    };
                    results.Error ??= new ScriptError("",
                        $"These scripts need {needed}, and none is chosen for this workspace: pass {ScriptLanguage.Option(language)} " +
                        "(or --no-scripts), or choose one in the full-screen UI (Tools › Scripts…).") { NotRun = true };
                }
                else
                {
                    results.NotRun.AddRange(scripts.Select(s => new ScriptSkip(s.Name, language)));
                }

                return null;
            }

            if (!_workers.TryGetValue(language, out var existing))
            {
                existing = ScriptWorker.For(language, runtime, Workspace.RootPath);
                existing.IdleTimeout = ScriptIdleTimeout;
                _workers[language] = existing;
            }

            worker = existing;
        }

        var message = new JsonObject
        {
            ["phase"] = phase,
            ["scripts"] = PostmanJson.ToJsonArray(scripts.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["code"] = s.Code })),
            ["variables"] = new JsonObject
            {
                ["environment"] = Values(environment?.Values, environment is not null ? Session.Find(environment) : null),
                ["collection"] = Values(collection.GetVariables(), Session.Find(collection)),
                ["globals"] = Values(Workspace.Globals?.Values, Session.Globals),
                ["local"] = Values(LocalVariables.Select(v => new KeyValueEntry { Key = v.Key, Value = v.Value }), local),
            },
            ["request"] = request,
            ["response"] = response,
            ["info"] = new JsonObject { ["request_name"] = requestName, ["collection"] = collection.Name, ["environment"] = environment?.Name },
            ["dynamic"] = new JsonObject
            {
                ["firstNames"] = PostmanJson.ToJsonArray(VariableResolver.FirstNames.Select(n => (JsonNode?)n)),
                ["lastNames"] = PostmanJson.ToJsonArray(VariableResolver.LastNames.Select(n => (JsonNode?)n)),
                ["words"] = PostmanJson.ToJsonArray(VariableResolver.Words.Select(n => (JsonNode?)n)),
            },
        };

        JsonObject reply;
        try
        {
            // Off the caller's thread: starting a runtime (or building C#) takes a moment, and the UI must stay responsive.
            var related = language == ScriptLanguage.CSharp ? CSharpScriptsOf(collection) : null;
            reply = await Task.Run(() => worker.RunAsync(message, ScriptTimeout, related, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
        catch (ScriptException ex)
        {
            results.Error ??= new ScriptError("", ex.Message);
            return null;
        }
        catch (ObjectDisposedException)
        {
            results.Error ??= new ScriptError("", $"{worker.Name} was stopped while the scripts ran.");
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
        ApplyVariableChanges(reply["changes"] as JsonObject, environment, collection, local, results, language);
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

        foreach (var note in (reply["notes"] as JsonArray)?.Select(Text).OfType<string>() ?? [])
        {
            results.Notes.Add(note);
        }

        if (error is not null)
        {
            results.Error ??= new ScriptError(Text(error["script"]) ?? "", Text(error["message"]) ?? "error", Text(error["traceback"]));
        }

        return reply;
    }

    /// <summary>
    /// Keeps the variables scripts set: for this session (environment, collection, globals) or this request. They are
    /// listed with the names the script's language uses (<c>collection_variables</c> in Python, <c>collectionVariables</c>
    /// in JavaScript, <c>CollectionVariables</c> in C# and PowerShell).
    /// </summary>
    private void ApplyVariableChanges(JsonObject? changes, PostmanEnvironment? environment, PostmanCollection collection, Dictionary<string, string?> local,
        ScriptResults results, string language)
    {
        if (changes is null)
        {
            return;
        }

        var pascal = language is ScriptLanguage.CSharp or ScriptLanguage.PowerShell;
        Apply("environment", pascal ? "Environment" : "environment", () => environment is not null ? Session.For(environment) : null);
        Apply("collection", language switch
        {
            ScriptLanguage.Python => "collection_variables",
            ScriptLanguage.CSharp or ScriptLanguage.PowerShell => "CollectionVariables",
            _ => "collectionVariables",
        }, () => Session.For(collection));
        Apply("globals", pascal ? "Globals" : "globals", () => Session.Globals);
        Apply("local", pascal ? "Variables" : "variables", () => local);

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

    /// <summary>Takes in what pre-request scripts changed in the request (<c>tr.request</c>, <c>pm.request</c>, <c>tr.Request</c>, <c>$tr.Request</c>).</summary>
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
        if (edited["body"] is { } bodyNode && body is null)
        {
            // A raw body is text: anything else would be lost, not sent.
            var what = bodyNode switch { JsonObject => "an object", JsonArray => "a list", _ => bodyNode.ToJsonString() };
            results.Error ??= new ScriptError("", $"The pre-request scripts set the request body to {what}, not to text.");
        }
        else if (body != Text(original["body"]))
        {
            if (spec.Body.Mode is BodyMode.Raw or BodyMode.None)
            {
                spec.Body.Mode = BodyMode.Raw;
                spec.Body.Raw = body ?? "";
            }
            else
            {
                results.Notes.Add("The request body was not changed: scripts can change raw bodies only.");
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
        var text = BodyFormatter.LooksLikeText(response.Body, response.ContentType) ? BodyFormatter.Decode(response.Body, response.ContentType) : null;
        if (text is not null)
        {
            json["text"] = text;
        }

        // Scripts encode the text as UTF-8 for the bytes: they get the bytes as well when that would not give them back.
        if (text is null || !IsUtf8Of(text, response.Body))
        {
            json["base64"] = JsonValue.Create(response.Body, BodyBytes);
        }

        return json;
    }

    /// <summary>Whether <paramref name="bytes"/> are <paramref name="text"/> in UTF-8, compared a few KB at a time (no copy of a large body).</summary>
    private static bool IsUtf8Of(string text, byte[] bytes)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[4096];
        var chars = text.AsSpan();
        var offset = 0;
        while (true)
        {
            encoder.Convert(chars, buffer, flush: true, out var used, out var written, out var completed);
            if (!bytes.AsSpan(offset).StartsWith(buffer[..written]))
            {
                return false;
            }

            offset += written;
            chars = chars[used..];
            if (completed)
            {
                return offset == bytes.Length;
            }
        }
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

    /// <summary>
    /// The C# scripts of a whole collection, as saved: they are built into one program with the scripts that run now,
    /// so that the collection's other requests need no new build.
    /// </summary>
    private static List<ScriptCode> CSharpScriptsOf(PostmanCollection collection)
    {
        var scripts = new List<ScriptCode>();
        void Add(ScriptSet set)
        {
            var csharp = set.For(ScriptLanguage.CSharp);
            scripts.AddRange(((string[])[csharp.PreRequest, csharp.Test]).Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => new ScriptCode("", code, ScriptLanguage.CSharp)));
        }

        Add(collection.GetScripts(collection.RootNode));
        foreach (var node in collection.Descendants())
        {
            switch (node)
            {
                case FolderNode folder:
                    Add(collection.GetScripts(folder));
                    break;
                case RequestNode request:
                    Add(request.Baseline.Scripts);
                    break;
            }
        }

        return scripts;
    }
}
