using System.Diagnostics;
using System.Text.Json.Nodes;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class ScriptingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static JsonObject Echoed(HttpExchange exchange) => JsonNode.Parse(exchange.Response!.Body)!.AsObject();

    private static string Header(HttpExchange exchange, string name) => Echoed(exchange)["headers"]![name]!.GetValue<string>();

    private static HttpRequestSpec Python(string url, string preRequest = "", string test = "") =>
        new() { Url = url, PreRequestScript = preRequest, TestScript = test, ScriptLanguage = ScriptLanguage.Python };

    [Fact]
    public async Task Scripts_run_around_a_request_from_the_collection_down_to_the_request()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "base", Value = server.Url("echo") }]);
        environment.Save();
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        collection.SetScripts(collection.RootNode, new ScriptSet(
            "tr.variables.set('order', (tr.variables.get('order') or '') + 'c')", "print('collection test')", ScriptLanguage.Python));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.SetScripts(folder, new ScriptSet("tr.variables.set('order', tr.variables.get('order') + 'f')", "", ScriptLanguage.Python));
        var node = collection.AddRequest(folder, "Echo", new HttpRequestSpec
        {
            Url = "{{base}}?q=1",
            Headers = [new() { Key = "X-Order", Value = "{{order}}" }, new() { Key = "X-Off", Value = "1", Enabled = false }],
            PreRequestScript = """
                tr.variables.set("order", tr.variables.get("order") + "r")
                tr.request.headers.set("X-Added", "yes")
                tr.request.url = tr.request.url + "&added=1"
                tr.environment.set("token", "abc-123")
                """,
            TestScript = """
                @tr.test("echoes what the pre-request scripts set")
                def _():
                    sent = tr.response.json()
                    assert sent["headers"]["x-order"] == "cfr", sent["headers"]
                    assert sent["headers"]["x-added"] == "yes"
                    assert sent["query"] == {"q": "1", "added": "1"}

                def wrong_status():
                    assert tr.response.status == 201, "expected 201"

                tr.test("fails on purpose", wrong_status)
                print("token is", tr.environment.get("token"), "in", tr.info.environment)
                tr.collection_variables.set("last_status", tr.response.code)
                """,
        });
        collection.Save();
        var environmentFile = File.ReadAllText(environment.FilePath!);
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        var exchange = await runner.SendAsync(collection, node, node.ReadSpec(), null, _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        var results = exchange.Scripts!;
        Assert.Null(results.Error);
        Assert.Equal(5, results.Ran);
        Assert.Equal([("echoes what the pre-request scripts set", true), ("fails on purpose", false)], results.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("expected 201", results.Tests[1].Error);
        Assert.Equal("request 'Users/Echo'", results.Tests[1].Script);
        Assert.Equal(["collection test", "token is abc-123 in dev"], results.Logs.Select(l => l.Text));
        Assert.Equal("✗ 1 of 2 tests failed", results.Summary);
        Assert.False(results.Succeeded);
        Assert.Contains("environment.token", results.VariablesSet);
        Assert.Contains("collection_variables.last_status", results.VariablesSet);
        Assert.Contains("variables.order", results.VariablesSet);

        // Values set for the environment and collection last for the session, in memory: files are not changed.
        Assert.Equal("abc-123", runner.Session.For(environment)["token"]);
        Assert.Equal("200", runner.Session.For(collection)["last_status"]);
        Assert.Equal(environmentFile, File.ReadAllText(environment.FilePath!));
        Assert.False(environment.IsDirty);
        var unchanged = node.ReadSpec();
        Assert.Equal("{{base}}?q=1", unchanged.Url);
        Assert.DoesNotContain(unchanged.Headers, h => h.Key == "X-Added");
        Assert.Equal("{{base}}?q=1", exchange.Request.Source!.Spec.Url);

        var next = await runner.SendAsync(collection, null, new HttpRequestSpec
        {
            Url = "{{base}}",
            Headers = [new() { Key = "X-Token", Value = "{{token}}" }, new() { Key = "X-Order", Value = "[{{order}}]" }],
        }, _ct);
        Assert.Equal("abc-123", Header(next, "x-token"));
        Assert.Equal("[c]", Header(next, "x-order")); // tr.variables last for one request only
        Assert.Contains("abc-123", next.Request.SensitiveValues); // named like a secret: recordings redact it

        runner.Session.Clear();
        var cleared = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = "{{base}}", Headers = [new() { Key = "X-Token", Value = "{{token}}" }] }, _ct);
        Assert.Equal("{{token}}", Header(cleared, "x-token"));
    }

    [Fact]
    public async Task Pre_request_scripts_can_change_the_method_and_body_and_read_variables()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        collection.SetVariables([new KeyValueEntry { Key = "who", Value = "Ada" }, new KeyValueEntry { Key = "greeting", Value = "Hi {{who}}" }]);
        var node = collection.AddRequest(collection.RootNode, "Create", Python(server.Url("echo"), preRequest: """
            import json
            tr.request.method = "PUT"
            tr.request.headers.set("Content-Type", "application/json")
            tr.request.body = json.dumps({"text": tr.variables.replace_in("{{greeting}}!"), "raw": tr.variables.get("greeting"), "name": tr.info.request_name})
            tr.collection_variables.unset("who")
            """));
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        var exchange = await runner.SendAsync(collection, node, node.ReadSpec(), null, _ct);

        var echoed = Echoed(exchange);
        Assert.Equal("PUT", echoed["method"]!.GetValue<string>());
        Assert.Equal("""{"text": "Hi Ada!", "raw": "Hi {{who}}", "name": "Create"}""", echoed["body"]!.GetValue<string>());
        Assert.Equal("application/json", Header(exchange, "content-type"));
        Assert.Null(runner.Session.For(collection)["who"]);
        Assert.Equal("Hi {{who}}", runner.CreateResolver(collection).Resolve("{{greeting}}"));
    }

    [Fact]
    public async Task A_failing_pre_request_script_stops_the_request()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        var node = collection.AddRequest(collection.RootNode, "Login", Python(server.Url("echo"), preRequest: "x = 1\nraise ValueError('no token yet')", test: "print('not run')"));
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        runner.SetPythonInterpreter(python);
        PreparedRequest? shown = null;

        var exchange = await runner.SendAsync(collection, node, node.ReadSpec(), p => shown = p, _ct);

        Assert.Null(exchange.Response);
        Assert.Same(shown, exchange.Request);
        Assert.Equal("Not sent: request 'Login': ValueError: no token yet", exchange.ErrorMessage);
        var error = exchange.Scripts!.Error!;
        Assert.Equal("request 'Login'", error.Script);
        Assert.Contains("line 2", error.Details, StringComparison.Ordinal);
        Assert.Contains("raise ValueError('no token yet')", error.Details, StringComparison.Ordinal);
        Assert.Empty(exchange.Scripts.Logs);
        Assert.Equal("✗ script error", exchange.Scripts.Summary);
        Assert.Empty(runner.History.Load());

        var syntax = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: "print('a')\ndef broken(:\n    pass"), _ct);
        Assert.Null(syntax.Response);
        Assert.StartsWith("SyntaxError", syntax.Scripts!.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", syntax.Scripts.Error.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripts_without_a_runtime_stop_the_request_or_are_skipped_with_a_note()
    {
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.ConfigureScripts(userSettingsDirectory: TestFiles.NewTempDirectory());
        Assert.Equal((null, null, null), (runner.PythonInterpreter, runner.JavaScriptRuntime, runner.CSharpRuntime));
        var javaScript = new HttpRequestSpec { Url = server.Url("echo"), PreRequestScript = "pm.variables.set('a', 1);", ScriptLanguage = ScriptLanguage.JavaScript };

        // The command line: scripts that cannot run are an error, so a pre-request one stops the request.
        var notSent = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: "pass"), _ct);
        Assert.Null(notSent.Response);
        Assert.Contains("--python", notSent.ErrorMessage, StringComparison.Ordinal);
        var notSentJavaScript = await runner.SendAsync(collection, null, javaScript, _ct);
        Assert.Null(notSentJavaScript.Response);
        Assert.Contains("--javascript", notSentJavaScript.ErrorMessage, StringComparison.Ordinal);
        var notSentCSharp = await runner.SendAsync(collection, null,
            new HttpRequestSpec { Url = server.Url("echo"), PreRequestScript = "tr.Variables.Set(\"a\", 1);", ScriptLanguage = ScriptLanguage.CSharp }, _ct);
        Assert.Null(notSentCSharp.Response);
        Assert.Contains("need the .NET SDK (10 or later), and none is chosen for this workspace: pass --csharp", notSentCSharp.ErrorMessage, StringComparison.Ordinal);

        var testsOnly = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: "pass"), _ct);
        Assert.Equal(200, testsOnly.Response!.StatusCode);
        Assert.Contains("--python", testsOnly.Scripts!.Error!.Message, StringComparison.Ordinal);

        // The full-screen UI: they are skipped, with a note, and the request is sent.
        runner.RequireScriptRuntimes = false;
        var skipped = await runner.SendAsync(collection, null, javaScript, _ct);
        Assert.Equal(200, skipped.Response!.StatusCode);
        Assert.True(skipped.Scripts!.Succeeded);
        Assert.Equal((0, "⚠ scripts not run"), (skipped.Scripts.Ran, skipped.Scripts.Summary));
        Assert.Equal([new ScriptSkip("request", ScriptLanguage.JavaScript)], skipped.Scripts.NotRun);
        Assert.Contains("Not run, because no JavaScript runtime (Node.js or Bun) is chosen for this workspace (Tools › Scripts…): the JavaScript scripts of request.",
            skipped.Scripts.Describe(), StringComparison.Ordinal);

        runner.ScriptsEnabled = false;
        var disabled = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: "pass"), _ct);
        Assert.Equal(200, disabled.Response!.StatusCode);
        Assert.Null(disabled.Scripts);
        Assert.False(runner.ScriptsRunning);
    }

    [Fact]
    public async Task Python_is_stopped_when_scripts_take_too_long_and_when_it_is_idle()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { ScriptTimeout = TimeSpan.FromSeconds(3), ScriptIdleTimeout = TimeSpan.FromMilliseconds(500) };
        runner.SetPythonInterpreter(python);

        var slow = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: "import time\ntime.sleep(60)"), _ct);
        Assert.Null(slow.Response);
        Assert.Contains("did not finish within 3 s", slow.ErrorMessage, StringComparison.Ordinal);
        Assert.False(runner.ScriptsRunning);

        var quick = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: "tr.test('ok', lambda: None)"), _ct);
        Assert.True(quick.Scripts!.Succeeded, quick.Scripts.Describe());
        Assert.True(runner.ScriptsRunning);

        var idle = Stopwatch.StartNew();
        while (runner.ScriptsRunning && idle.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100, _ct);
        }

        Assert.False(runner.ScriptsRunning);
        var again = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: "tr.test('ok', lambda: None)"), _ct);
        Assert.True(again.Scripts!.Succeeded, again.Scripts.Describe());
    }

    [Fact]
    public async Task Programs_that_scripts_start_cannot_disturb_python()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        // Their output does not get mixed into Python's answers, and reading input they get nothing (not a request).
        var exchange = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: """
            import os, subprocess, sys
            subprocess.run([sys.executable, "-c", "print('from a child process')"])
            os.write(1, b"straight to stdout\n")
            child = subprocess.run([sys.executable, "-c", "import sys; print(repr(sys.stdin.read()))"], capture_output=True, text=True, timeout=10)
            print("a child reads", child.stdout.strip())
            print("captured", "ünïcödé ✓")
            """, test: "tr.test('still answers', lambda: None)"), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a child reads ''", "captured ünïcödé ✓"], exchange.Scripts.Logs.Select(l => l.Text));
    }

    [Fact]
    public void Script_language_is_read_from_and_written_to_the_script_type()
    {
        var item = JsonNode.Parse("""
            {"name":"r","event":[
              {"listen":"prerequest","script":{"exec":[""],"type":"text/javascript"}},
              {"listen":"test","script":{"exec":["tr.test('x', lambda: None)"],"type":"text/x-python"}}],
             "request":{"method":"GET","url":"https://h/"}}
            """)!.AsObject();
        string?[] Types() => item["event"]!.AsArray().Select(e => e!["script"]!["type"]!.GetValue<string>()).ToArray();
        var spec = PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21);
        Assert.Equal(ScriptLanguage.Python, spec.ScriptLanguage);

        // Python code written into Postman's empty JavaScript event makes it a Python script.
        var edited = spec.Clone();
        edited.PreRequestScript = "print(1)";
        PostmanRequestMapper.Apply(item, edited, spec, PostmanSchemaVersion.V21);
        Assert.Equal(["text/x-python", "text/x-python"], Types());

        var python = PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21);
        var switched = python.Clone();
        switched.ScriptLanguage = ScriptLanguage.JavaScript;
        Assert.False(switched.ContentEquals(python));
        PostmanRequestMapper.Apply(item, switched, python, PostmanSchemaVersion.V21);
        Assert.Equal(["text/javascript", "text/javascript"], Types());

        // Without code the language does not matter, and new requests default to Python.
        var empty = PostmanRequestMapper.Read(JsonNode.Parse("""{"name":"e","request":"https://h/"}""")!.AsObject(), PostmanSchemaVersion.V21);
        Assert.Equal(ScriptLanguage.Python, empty.ScriptLanguage);
        var other = empty.Clone();
        other.ScriptLanguage = ScriptLanguage.JavaScript;
        Assert.True(other.ContentEquals(empty));

        // New Python events have no Postman "packages".
        var fresh = JsonNode.Parse("""{"name":"n","request":{"method":"GET","url":"https://h/"}}""")!.AsObject();
        var baseline = PostmanRequestMapper.Read(fresh, PostmanSchemaVersion.V21);
        var withScript = baseline.Clone();
        withScript.TestScript = "pass";
        PostmanRequestMapper.Apply(fresh, withScript, baseline, PostmanSchemaVersion.V21);
        Assert.Equal("""{"listen":"test","script":{"exec":["pass"],"type":"text/x-python"}}""", fresh["event"]![0]!.ToJsonString());

        // C# scripts are text/x-csharp.
        var csharp = PostmanRequestMapper.Read(fresh, PostmanSchemaVersion.V21);
        var toCSharp = csharp.Clone();
        toCSharp.ScriptLanguage = ScriptLanguage.CSharp;
        toCSharp.TestScript = "tr.Test(\"ok\", () => { });";
        PostmanRequestMapper.Apply(fresh, toCSharp, csharp, PostmanSchemaVersion.V21);
        var script = fresh["event"]![0]!["script"]!;
        Assert.Equal(("text/x-csharp", "tr.Test(\"ok\", () => { });"), (script["type"]!.GetValue<string>(), script["exec"]![0]!.GetValue<string>()));
        Assert.Equal(ScriptLanguage.CSharp, PostmanRequestMapper.Read(fresh, PostmanSchemaVersion.V21).ScriptLanguage);
    }

    [Fact]
    public void Collections_and_folders_keep_their_scripts()
    {
        var path = Path.Combine(TestFiles.NewTempDirectory(), "api.postman_collection.json");
        var collection = PostmanCollection.Create("API", path);
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.Save();

        Assert.True(collection.SetScripts(collection.RootNode, new ScriptSet("print('root')", "", ScriptLanguage.Python)));
        Assert.True(collection.IsDirty);
        Assert.True(collection.SetScripts(folder, new ScriptSet("", "tr.test('t', lambda: None)", ScriptLanguage.Python)));
        Assert.False(collection.SetScripts(folder, new ScriptSet("", "tr.test('t', lambda: None)", ScriptLanguage.Python)));
        collection.Save();

        var reloaded = PostmanCollection.Load(path);
        Assert.Equal(new ScriptSet("print('root')", "", ScriptLanguage.Python), reloaded.GetScripts(reloaded.RootNode));
        Assert.Equal(new ScriptSet("", "tr.test('t', lambda: None)", ScriptLanguage.Python), reloaded.GetScripts(reloaded.FindFolder("Users")!));
        var keys = JsonNode.Parse(File.ReadAllText(path))!.AsObject().Select(p => p.Key).ToList();
        Assert.True(keys.IndexOf("event") > keys.IndexOf("item"), string.Join(", ", keys));

        Assert.True(reloaded.SetScripts(reloaded.RootNode, ScriptSet.Empty));
        Assert.True(reloaded.GetScripts(reloaded.RootNode).IsEmpty);
    }

    [Fact]
    public void Python_scripts_keep_their_language_in_v3_collections()
    {
        var root = V3Fixture.Create();
        var collection = Assert.Single(ApiWorkspace.Open(root).Collections);
        collection.SetScripts(collection.RootNode, new ScriptSet("tr.variables.set('a', 1)", "", ScriptLanguage.Python));
        collection.SetScripts(collection.FindFolder("Widgets")!, new ScriptSet("", "print('folder')", ScriptLanguage.Python));
        var node = collection.FindRequest("Widgets/List widgets")!;
        var spec = node.ReadSpec();
        Assert.Equal(ScriptLanguage.JavaScript, spec.ScriptLanguage);
        spec.ScriptLanguage = ScriptLanguage.Python;
        spec.TestScript = "tr.test('ok', lambda: None)";
        Assert.True(collection.UpdateRequest(node, spec));
        collection.Save();

        var reloaded = PostmanCollection.Load(V3Fixture.CollectionPath(root));
        Assert.Equal(new ScriptSet("tr.variables.set('a', 1)", "", ScriptLanguage.Python), reloaded.GetScripts(reloaded.RootNode));
        Assert.Equal(new ScriptSet("", "print('folder')", ScriptLanguage.Python), reloaded.GetScripts(reloaded.FindFolder("Widgets")!));
        var list = reloaded.FindRequest("Widgets/List widgets")!.ReadSpec();
        Assert.Equal((ScriptLanguage.Python, "tr.test('ok', lambda: None)"), (list.ScriptLanguage, list.TestScript));
        var file = Directory.GetFiles(V3Fixture.CollectionPath(root), "*.yaml", SearchOption.AllDirectories)
            .Single(f => File.ReadAllText(f).Contains("tr.test('ok'", StringComparison.Ordinal));
        Assert.Contains("language: text/x-python", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.DoesNotContain("text/javascript", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Runtimes_are_chosen_per_workspace_in_the_user_settings()
    {
        var directory = TestFiles.NewTempDirectory();
        var workspaceRoot = TestFiles.NewTempDirectory();
        var settings = new UserSettings();
        settings.SetScriptRuntime(ScriptLanguage.Python, workspaceRoot, ".venv/bin/python");
        settings.SetScriptRuntime(ScriptLanguage.JavaScript, workspaceRoot, "node");
        settings.SetScriptRuntime(ScriptLanguage.CSharp, workspaceRoot, "dotnet");
        settings.Save(directory);

        var loaded = UserSettings.Load(directory);
        Assert.Equal(".venv/bin/python", loaded.GetScriptRuntime(ScriptLanguage.Python, workspaceRoot + Path.DirectorySeparatorChar));
        Assert.Equal("node", loaded.GetScriptRuntime(ScriptLanguage.JavaScript, workspaceRoot));
        Assert.Equal("dotnet", loaded.GetScriptRuntime(ScriptLanguage.CSharp, workspaceRoot));
        Assert.Null(loaded.GetScriptRuntime(ScriptLanguage.Python, TestFiles.NewTempDirectory()));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(".venv/bin/python", loaded.GetScriptRuntime(ScriptLanguage.Python, workspaceRoot.ToUpperInvariant()));
        }

        using var runner = new RequestRunner(ApiWorkspace.Open(workspaceRoot));
        runner.ConfigureScripts(userSettingsDirectory: directory);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspaceRoot, ".venv/bin/python")), runner.PythonInterpreter);
        Assert.Equal("node", runner.JavaScriptRuntime);
        Assert.Equal("dotnet", runner.CSharpRuntime);
        runner.ConfigureScripts(python: "python3", javaScript: "bun", cSharp: "/opt/dotnet/dotnet", userSettingsDirectory: directory);
        Assert.Equal(("python3", "bun", "/opt/dotnet/dotnet"), (runner.PythonInterpreter, runner.JavaScriptRuntime, runner.CSharpRuntime));

        loaded.SetScriptRuntime(ScriptLanguage.Python, workspaceRoot, null);
        Assert.Null(loaded.PythonInterpreters);
        Assert.NotNull(loaded.JavaScriptRuntimes);
        var json = File.ReadAllText(Path.Combine(directory, "settings.json"));
        Assert.Contains("\"javaScriptRuntimes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"csharpRuntimes\"", json, StringComparison.Ordinal);
    }
}
