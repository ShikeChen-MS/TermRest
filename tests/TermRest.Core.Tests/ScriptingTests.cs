using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static JsonObject Echoed(HttpExchange exchange) => JsonNode.Parse(exchange.Response!.Body)!.AsObject();

    private static string Header(HttpExchange exchange, string name) => Echoed(exchange)["headers"]![name]!.GetValue<string>();

    private static HttpRequestSpec Python(string url, string preRequest = "", string test = "") =>
        new() { Url = url, Scripts = new ScriptSet(preRequest, test, ScriptLanguage.Python) };

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
            Scripts = new ScriptSet(language: ScriptLanguage.Python, preRequest: """
                tr.variables.set("order", tr.variables.get("order") + "r")
                tr.request.headers.set("X-Added", "yes")
                tr.request.url = tr.request.url + "&added=1"
                tr.environment.set("token", "abc-123")
                """,
            test: """
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
                """),
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
        Assert.Equal((null, null, null, null), (runner.PythonInterpreter, runner.JavaScriptRuntime, runner.CSharpRuntime, runner.PowerShellRuntime));
        var javaScript = new HttpRequestSpec { Url = server.Url("echo"), Scripts = new ScriptSet("pm.variables.set('a', 1);", "", ScriptLanguage.JavaScript) };

        // The command line: scripts that cannot run are an error, so a pre-request one stops the request.
        var notSent = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: "pass"), _ct);
        Assert.Null(notSent.Response);
        Assert.Contains("--python", notSent.ErrorMessage, StringComparison.Ordinal);
        var notSentJavaScript = await runner.SendAsync(collection, null, javaScript, _ct);
        Assert.Null(notSentJavaScript.Response);
        Assert.Contains("--javascript", notSentJavaScript.ErrorMessage, StringComparison.Ordinal);
        var notSentCSharp = await runner.SendAsync(collection, null,
            new HttpRequestSpec { Url = server.Url("echo"), Scripts = new ScriptSet("tr.Variables.Set(\"a\", 1);", "", ScriptLanguage.CSharp) }, _ct);
        Assert.Null(notSentCSharp.Response);
        Assert.Contains("need the .NET SDK (10 or later), and none is chosen for this workspace: pass --csharp", notSentCSharp.ErrorMessage, StringComparison.Ordinal);
        var notSentPowerShell = await runner.SendAsync(collection, null,
            new HttpRequestSpec { Url = server.Url("echo"), Scripts = new ScriptSet("$tr.Variables.Set('a', 1)", "", ScriptLanguage.PowerShell) }, _ct);
        Assert.Null(notSentPowerShell.Response);
        Assert.Contains("need PowerShell (7 or later), and none is chosen for this workspace: pass --powershell", notSentPowerShell.ErrorMessage, StringComparison.Ordinal);

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
    public void Scripts_are_kept_per_language_in_postman_events()
    {
        var item = JsonNode.Parse("""
            {"name":"r","event":[
              {"listen":"prerequest","script":{"exec":[""],"type":"text/javascript"}},
              {"listen":"test","script":{"exec":["tr.test('x', lambda: None)"],"type":"text/x-python"}}],
             "request":{"method":"GET","url":"https://h/"}}
            """)!.AsObject();
        var spec = PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21);
        Assert.Equal(new ScriptSet("", "tr.test('x', lambda: None)", ScriptLanguage.Python), spec.Scripts);

        // A JavaScript script goes into Postman's empty event. A new Python one gets an event of its own, disabled for
        // Postman and Newman, which would run it as JavaScript; the Python event that did not change stays as it was.
        var edited = spec.Clone();
        edited.Scripts = spec.Scripts
            .With(new LanguageScripts(ScriptLanguage.JavaScript, "pm.variables.set('a', 1);", ""))
            .With(new LanguageScripts(ScriptLanguage.Python, "print(1)", "tr.test('x', lambda: None)"));
        Assert.False(edited.ContentEquals(spec));
        PostmanRequestMapper.Apply(item, edited, spec, PostmanSchemaVersion.V21);
        Assert.Equal(
            """[{"listen":"prerequest","script":{"exec":["pm.variables.set('a', 1);"],"type":"text/javascript"}},""" +
            """{"listen":"test","script":{"exec":["tr.test('x', lambda: None)"],"type":"text/x-python"}},""" +
            """{"listen":"prerequest","script":{"exec":["print(1)"],"type":"text/x-python"},"disabled":true}]""",
            item["event"]!.ToJsonString(Unescaped));
        var both = PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21);
        Assert.Equal(edited.Scripts, both.Scripts);

        // Clearing a Python script removes its event; Postman's keeps its event, empty, as Postman does.
        var cleared = both.Clone();
        cleared.Scripts = both.Scripts
            .With(new LanguageScripts(ScriptLanguage.Python, "", "tr.test('x', lambda: None)"))
            .With(new LanguageScripts(ScriptLanguage.JavaScript, "", ""));
        PostmanRequestMapper.Apply(item, cleared, both, PostmanSchemaVersion.V21);
        Assert.Equal(
            """[{"listen":"prerequest","script":{"exec":[""],"type":"text/javascript"}},""" +
            """{"listen":"test","script":{"exec":["tr.test('x', lambda: None)"],"type":"text/x-python"}}]""",
            item["event"]!.ToJsonString(Unescaped));

        // New events, one per language: Postman's has "packages", the others are disabled. A disabled JavaScript script
        // is not one TermRest runs (nor does Postman): it is not read, and it is kept.
        var fresh = JsonNode.Parse("""
            {"name":"n","event":[{"listen":"test","script":{"exec":["pm.test('off', () => {});"],"type":"text/javascript"},"disabled":true}],
             "request":{"method":"GET","url":"https://h/"}}
            """)!.AsObject();
        var baseline = PostmanRequestMapper.Read(fresh, PostmanSchemaVersion.V21);
        Assert.True(baseline.Scripts.IsEmpty);
        var all = baseline.Clone();
        all.Scripts = new ScriptSet(ScriptLanguage.All.Select(l => new LanguageScripts(l, "", $"test in {l}")));
        PostmanRequestMapper.Apply(fresh, all, baseline, PostmanSchemaVersion.V21);
        Assert.Equal(
            """[{"listen":"test","script":{"exec":["pm.test('off', () => {});"],"type":"text/javascript"},"disabled":true},""" +
            """{"listen":"test","script":{"exec":["test in python"],"type":"text/x-python"},"disabled":true},""" +
            """{"listen":"test","script":{"exec":["test in javascript"],"type":"text/javascript","packages":{}}},""" +
            """{"listen":"test","script":{"exec":["test in csharp"],"type":"text/x-csharp"},"disabled":true},""" +
            """{"listen":"test","script":{"exec":["test in powershell"],"type":"text/x-powershell"},"disabled":true}]""",
            fresh["event"]!.ToJsonString(Unescaped));
        Assert.Equal(all.Scripts, PostmanRequestMapper.Read(fresh, PostmanSchemaVersion.V21).Scripts);
        Assert.Equal(ScriptLanguage.PowerShell, ScriptLanguage.FromType("application/x-powershell"));
    }

    [Fact]
    public void How_a_request_runs_its_scripts_is_kept_under_termrest()
    {
        var item = JsonNode.Parse("""{"name":"r","event":[],"request":{"method":"GET","url":"https://h/"},"response":[]}""")!.AsObject();
        var spec = PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21);
        Assert.Equal(ScriptSettings.Auto, spec.ScriptSettings);

        var chosen = spec.Clone();
        chosen.ScriptSettings = new ScriptSettings(ScriptLanguage.PowerShell, PreRequest: false);
        Assert.False(chosen.ContentEquals(spec));
        PostmanRequestMapper.Apply(item, chosen, spec, PostmanSchemaVersion.V21);
        Assert.Equal(
            """{"name":"r","event":[],"termrest":{"scripts":{"language":"powershell","preRequest":false}},"request":{"method":"GET","url":"https://h/"},"response":[]}""",
            item.ToJsonString(Unescaped));
        Assert.Equal(chosen.ScriptSettings, PostmanRequestMapper.Read(item, PostmanSchemaVersion.V21).ScriptSettings);

        var back = chosen.Clone();
        back.ScriptSettings = ScriptSettings.Auto;
        PostmanRequestMapper.Apply(item, back, chosen, PostmanSchemaVersion.V21);
        Assert.Equal("""{"name":"r","event":[],"request":{"method":"GET","url":"https://h/"},"response":[]}""", item.ToJsonString(Unescaped));

        // A language TermRest does not know is no choice (and stays in the file until the settings change).
        var unknown = JsonNode.Parse("""{"name":"u","termrest":{"scripts":{"language":"ruby","postResponse":true}},"request":"https://h/"}""")!.AsObject();
        Assert.Equal(new ScriptSettings(null, null, true), PostmanRequestMapper.Read(unknown, PostmanSchemaVersion.V21).ScriptSettings);
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
    public void Scripts_in_other_languages_are_kept_beside_postmans_in_v3_collections()
    {
        var root = V3Fixture.Create();
        var collection = Assert.Single(ApiWorkspace.Open(root).Collections);
        collection.SetScripts(collection.RootNode, new ScriptSet("tr.variables.set('a', 1)", "", ScriptLanguage.Python));
        collection.SetScripts(collection.FindFolder("Widgets")!, new ScriptSet("", "print('folder')", ScriptLanguage.Python));
        var node = collection.FindRequest("Widgets/List widgets")!;
        var spec = node.ReadSpec();
        var postman = spec.Scripts.For(ScriptLanguage.JavaScript);
        Assert.Equal("pm.test('ok', () => pm.response.to.have.status(200));", postman.Test);
        spec.Scripts = spec.Scripts.With(new LanguageScripts(ScriptLanguage.Python, "", "tr.test('ok', lambda: None)"));
        spec.ScriptSettings = new ScriptSettings(PostResponse: true);
        Assert.True(collection.UpdateRequest(node, spec));
        collection.Save();

        var reloaded = PostmanCollection.Load(V3Fixture.CollectionPath(root));
        Assert.Equal(new ScriptSet("tr.variables.set('a', 1)", "", ScriptLanguage.Python), reloaded.GetScripts(reloaded.RootNode));
        Assert.Equal(new ScriptSet("", "print('folder')", ScriptLanguage.Python), reloaded.GetScripts(reloaded.FindFolder("Widgets")!));
        var list = reloaded.FindRequest("Widgets/List widgets")!.ReadSpec();
        Assert.Equal(spec.Scripts, list.Scripts);
        Assert.Equal(postman, list.Scripts.For(ScriptLanguage.JavaScript));
        Assert.Equal(new ScriptSettings(PostResponse: true), list.ScriptSettings);
        var text = File.ReadAllText(Directory.GetFiles(V3Fixture.CollectionPath(root), "*.yaml", SearchOption.AllDirectories)
            .Single(f => File.ReadAllText(f).Contains("lambda: None", StringComparison.Ordinal))).ReplaceLineEndings("\n");
        var scripts = text[text.IndexOf("scripts:", StringComparison.Ordinal)..text.IndexOf("examples:", StringComparison.Ordinal)];
        Assert.Contains("    language: text/javascript\n  - type: afterResponse\n", scripts, StringComparison.Ordinal);
        Assert.Contains("    language: text/x-python\ntermrest:\n  scripts:\n    postResponse: true\n", scripts, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", scripts, StringComparison.Ordinal);
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
        settings.SetScriptRuntime(ScriptLanguage.PowerShell, workspaceRoot, "pwsh");
        settings.Save(directory);

        var loaded = UserSettings.Load(directory);
        Assert.Equal(".venv/bin/python", loaded.GetScriptRuntime(ScriptLanguage.Python, workspaceRoot + Path.DirectorySeparatorChar));
        Assert.Equal("node", loaded.GetScriptRuntime(ScriptLanguage.JavaScript, workspaceRoot));
        Assert.Equal("dotnet", loaded.GetScriptRuntime(ScriptLanguage.CSharp, workspaceRoot));
        Assert.Equal("pwsh", loaded.GetScriptRuntime(ScriptLanguage.PowerShell, workspaceRoot));
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
        Assert.Equal("pwsh", runner.PowerShellRuntime);
        runner.ConfigureScripts(python: "python3", javaScript: "bun", cSharp: "/opt/dotnet/dotnet", powerShell: "/opt/microsoft/powershell/7/pwsh", userSettingsDirectory: directory);
        Assert.Equal(("python3", "bun", "/opt/dotnet/dotnet", "/opt/microsoft/powershell/7/pwsh"),
            (runner.PythonInterpreter, runner.JavaScriptRuntime, runner.CSharpRuntime, runner.PowerShellRuntime));

        loaded.SetScriptRuntime(ScriptLanguage.Python, workspaceRoot, null);
        Assert.Null(loaded.PythonInterpreters);
        Assert.NotNull(loaded.JavaScriptRuntimes);
        var json = File.ReadAllText(Path.Combine(directory, "settings.json"));
        Assert.Contains("\"javaScriptRuntimes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"csharpRuntimes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"powerShellRuntimes\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"scriptLanguageOrder\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_order_is_the_users_for_every_workspace()
    {
        var directory = TestFiles.NewTempDirectory();
        var settings = UserSettings.Load(directory);
        Assert.Equal(ScriptLanguage.All, settings.GetScriptLanguageOrder());

        settings.SetScriptLanguageOrder([ScriptLanguage.PowerShell, "c#", "PowerShell", "ruby"]);
        settings.Save(directory);
        Assert.Contains("\"scriptLanguageOrder\"", File.ReadAllText(Path.Combine(directory, "settings.json")), StringComparison.Ordinal);
        IReadOnlyList<string> mine = [ScriptLanguage.PowerShell, ScriptLanguage.CSharp, ScriptLanguage.Python, ScriptLanguage.JavaScript];
        Assert.Equal(mine, UserSettings.Load(directory).GetScriptLanguageOrder());

        using var runner = new RequestRunner(ApiWorkspace.Open(TestFiles.NewTempDirectory()));
        Assert.Equal(ScriptLanguage.All, runner.ScriptLanguageOrder);
        runner.ConfigureScripts(userSettingsDirectory: directory);
        Assert.Equal(mine, runner.ScriptLanguageOrder);
        runner.ConfigureScripts(userSettingsDirectory: directory, scriptOrder: [ScriptLanguage.JavaScript]);
        Assert.Equal([ScriptLanguage.JavaScript, ScriptLanguage.PowerShell, ScriptLanguage.CSharp, ScriptLanguage.Python], runner.ScriptLanguageOrder);
        runner.ScriptLanguageOrder = [ScriptLanguage.CSharp];
        Assert.Equal([ScriptLanguage.CSharp, ScriptLanguage.Python, ScriptLanguage.JavaScript, ScriptLanguage.PowerShell], runner.ScriptLanguageOrder);

        settings.SetScriptLanguageOrder(ScriptLanguage.All);
        Assert.Null(settings.ScriptLanguageOrder);
        Assert.Equal((ScriptLanguage.CSharp, ScriptLanguage.JavaScript, (string?)null), (ScriptLanguage.Parse(" C# "), ScriptLanguage.Parse("js"), ScriptLanguage.Parse("ruby")));
    }
}
