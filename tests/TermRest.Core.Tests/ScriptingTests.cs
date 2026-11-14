using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
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

    /// <summary>Python that waits until the file <paramref name="flag"/> exists, so a test can act while scripts run.</summary>
    private static string WaitFor(string flag) => $"import os, time\nwhile not os.path.exists({JsonSerializer.Serialize(flag)}):\n    time.sleep(0.01)\n";

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
    public async Task Dates_uuids_and_decimals_set_in_scripts_are_their_text()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        var exchange = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: """
            import datetime, decimal, uuid
            tr.collection_variables.set("when", datetime.datetime(2024, 1, 2, 3, 4, 5, tzinfo=datetime.timezone.utc))
            tr.collection_variables.set("day", datetime.date(2024, 1, 2))
            tr.collection_variables.set("id", uuid.UUID("3f2504e0-4f89-11d3-9a0c-0305e82c3301"))
            tr.collection_variables.set("price", decimal.Decimal("1.10"))
            tr.collection_variables.set("flags", {"on": True})
            """), null, _ct);

        Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
        var stored = runner.Session.For(collection);
        Assert.Equal(("2024-01-02T03:04:05+00:00", "2024-01-02", "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "1.10", """{"on": true}"""),
            (stored["when"], stored["day"], stored["id"], stored["price"], stored["flags"]));
    }

    [Theory]
    [InlineData(ScriptLanguage.Python)]
    [InlineData(ScriptLanguage.JavaScript)]
    [InlineData(ScriptLanguage.CSharp)]
    [InlineData(ScriptLanguage.PowerShell)]
    public async Task Scripts_replace_dynamic_variables_as_requests_do_so_a_stored_value_stays_one_value(string language)
    {
        const string Names = "guid randomUUID timestamp isoTimestamp randomInt randomBoolean randomAlphaNumeric randomHexadecimal randomFirstName randomLastName randomFullName randomUserName randomEmail randomWord randomPassword";
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        var script = language switch
        {
            ScriptLanguage.Python => "for name in 'NAMES'.split():\n    tr.collection_variables.set(name, tr.variables.replace_in('{{$' + name + '}}'))",
            ScriptLanguage.JavaScript => "for (const name of 'NAMES'.split(' ')) pm.collectionVariables.set(name, pm.variables.replaceIn('{{$' + name + '}}'));",
            ScriptLanguage.CSharp => "foreach (var name in \"NAMES\".Split(' ')) tr.CollectionVariables.Set(name, tr.Variables.ReplaceIn(\"{{$\" + name + \"}}\"));",
            _ => "foreach ($name in 'NAMES'.Split(' ')) { $tr.CollectionVariables.Set($name, $tr.Variables.ReplaceIn('{{$' + $name + '}}')) }",
        };
        switch (language)
        {
            case ScriptLanguage.Python: runner.SetPythonInterpreter(TestPython.Require()); break;
            case ScriptLanguage.JavaScript: runner.SetJavaScriptRuntime(TestJavaScript.Require()); break;
            case ScriptLanguage.CSharp: runner.SetCSharpRuntime(TestCSharp.Require()); break;
            default: runner.SetPowerShellRuntime(TestPowerShell.Require()); break;
        }

        var exchange = await runner.SendAsync(collection, null, new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Headers = [new() { Key = "X-Email", Value = "{{randomEmail}}" }, new() { Key = "X-Again", Value = "{{randomEmail}}" }],
            Scripts = new ScriptSet(script.Replace("NAMES", Names, StringComparison.Ordinal), "", language),
        }, _ct);

        Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
        var stored = runner.Session.For(collection);
        foreach (var name in Names.Split(' '))
        {
            AssertDynamicValue(name, VariableResolver.Empty.Resolve("{{$" + name + "}}")); // as requests resolve it
            AssertDynamicValue(name, stored[name]);
        }

        Assert.Equal((stored["randomEmail"], stored["randomEmail"]), (Header(exchange, "x-email"), Header(exchange, "x-again")));
    }

    /// <summary>A value of the dynamic variable <c>$name</c> in the form requests give it.</summary>
    private static void AssertDynamicValue(string name, string? value)
    {
        static string[] Lower(string[] names) => [.. names.Select(n => n.ToLowerInvariant())];
        var text = value ?? "";
        var person = Regex.Match(text, @"^([a-z]+)\.([a-z]+)(\d{1,2}|@example\.com)$");
        var valid = name switch
        {
            "guid" or "randomUUID" => Regex.IsMatch(text, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"),
            "timestamp" => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) < 600,
            "isoTimestamp" => Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")
                && Math.Abs((DateTime.UtcNow - DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)).TotalMinutes) < 10,
            "randomInt" => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 1000,
            "randomBoolean" => text is "true" or "false",
            "randomAlphaNumeric" => Regex.IsMatch(text, "^[a-z0-9]$"),
            "randomHexadecimal" => Regex.IsMatch(text, "^[0-9a-f]$"),
            "randomFirstName" => VariableResolver.FirstNames.Contains(text),
            "randomLastName" => VariableResolver.LastNames.Contains(text),
            "randomFullName" => text.Split(' ') is [var first, var last] && VariableResolver.FirstNames.Contains(first) && VariableResolver.LastNames.Contains(last),
            "randomUserName" or "randomEmail" => person.Success && Lower(VariableResolver.FirstNames).Contains(person.Groups[1].Value)
                && Lower(VariableResolver.LastNames).Contains(person.Groups[2].Value) && person.Groups[3].Value.StartsWith('@') == (name == "randomEmail"),
            "randomWord" => VariableResolver.Words.Contains(text),
            "randomPassword" => Regex.IsMatch(text, "^[a-z0-9]{15}$"),
            _ => false,
        };
        Assert.True(valid, $"${name}: '{value}'");
    }

    [Fact]
    public async Task Log_lines_reach_the_script_that_writes_them_through_a_handler_an_earlier_script_made()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        collection.SetScripts(collection.RootNode, new ScriptSet(
            "import logging\nlogging.basicConfig(level=logging.INFO, format='%(message)s')\nlogging.info('collection logs')", "", ScriptLanguage.Python));
        var first = collection.AddRequest(collection.RootNode, "First", Python(server.Url("echo"), preRequest: "import logging\nlogging.info('request logs')\nprint('request prints')"));
        var second = collection.AddRequest(collection.RootNode, "Second", Python(server.Url("echo"), preRequest: "import logging\nlogging.info('logged again')"));
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        var sentFirst = await runner.SendAsync(collection, first, first.ReadSpec(), null, _ct);
        var sentSecond = await runner.SendAsync(collection, second, second.ReadSpec(), null, _ct);

        // logging.basicConfig keeps the sys.stderr of the script that called it: the same capture for every script.
        Assert.Equal(["collection logs", "request logs", "request prints"], sentFirst.Scripts!.Logs.Select(l => l.Text));
        Assert.Equal(["collection logs", "logged again"], sentSecond.Scripts!.Logs.Select(l => l.Text));

        // An exception whose text can't be read fails its script, not the whole answer: what it printed is kept.
        var failing = await runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: """
            class Unreadable(Exception):
                def __str__(self):
                    raise RuntimeError("no text")
            print("before")
            raise Unreadable()
            """), _ct);
        Assert.Equal("Unreadable: (its message could not be read)", failing.Scripts!.Error!.Message);
        Assert.Contains("before", failing.Scripts.Logs.Select(l => l.Text));
    }

    [Fact]
    public async Task A_body_set_to_bytes_a_dict_or_a_list_is_sent_as_text()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);
        async Task<HttpExchange> Send(string preRequest)
        {
            var spec = Python(server.Url("echo"), preRequest: preRequest);
            spec.Method = "POST";
            spec.Body = new RequestBody { Mode = BodyMode.Raw, Raw = "original" };
            return await runner.SendAsync(collection, null, spec, _ct);
        }

        async Task<string> SentBody(string preRequest)
        {
            var exchange = await Send(preRequest);
            Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
            return Echoed(exchange)["body"]!.GetValue<string>();
        }

        Assert.Equal("""{"name": "Zoë"}""", await SentBody("import json\ntr.request.body = json.dumps({'name': 'Zoë'}, ensure_ascii=False).encode()"));

        // Objects and lists are sent as JSON, as variables get them, not as Python's text for them.
        Assert.Equal("""{"name": "Zo\u00eb", "n": 1}""", await SentBody("tr.request.body = {'name': 'Zoë', 'n': 1}"));
        Assert.Equal("[1, true, null]", await SentBody("tr.request.body = (1, True, None)"));
        Assert.Equal("true", await SentBody("tr.request.body = True"));
        var notJson = await Send("import datetime\ntr.request.body = {'day': datetime.date(2026, 1, 2)}");
        Assert.Null(notJson.Response);
        Assert.StartsWith("The pre-request scripts set the request body to a dict that cannot be sent as JSON: ", notJson.Scripts!.Error!.Message, StringComparison.Ordinal);

        // Headers may be given as with requests: a dict (or any other mapping, or pairs) replaces them.
        var headers = await Send("tr.request.headers = {'X-Api-Key': 'k-1', 'X-Count': 2}");
        Assert.True(headers.Scripts!.Succeeded, headers.Scripts.Describe());
        Assert.Equal(("k-1", "2"), (Header(headers, "x-api-key"), Header(headers, "x-count")));
        var mapping = await Send("import types\ntr.request.headers = types.MappingProxyType({'X-Api-Key': 'k-2'})");
        Assert.True(mapping.Scripts!.Succeeded, mapping.Scripts.Describe());
        Assert.Equal("k-2", Header(mapping, "x-api-key"));

        // A request the scripts left unusable is not sent, and what they did is kept.
        var broken = await Send("""
            class Unreadable:
                def __str__(self):
                    raise ValueError("no text")
            print("before")
            tr.test("ran", lambda: None)
            tr.collection_variables.set("kept", "yes")
            tr.request.url = Unreadable()
            """);
        Assert.Null(broken.Response);
        Assert.Equal("The pre-request scripts left a request that cannot be sent: ValueError: no text", broken.Scripts!.Error!.Message);
        Assert.Equal(["before"], broken.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["ran"], broken.Scripts.Tests.Select(t => t.Name));
        Assert.Equal("yes", runner.Session.For(collection)["kept"]);
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
    public async Task The_request_is_sent_and_kept_as_it_was_though_it_is_edited_while_its_scripts_run()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        runner.SetPythonInterpreter(python);
        var edited = Path.Combine(TestFiles.NewTempDirectory(), "edited");
        var spec = Python(server.Url("echo/orders/1"), preRequest: WaitFor(edited));

        // The full-screen UI's editor stays open while the scripts run (the first build of C# scripts takes seconds).
        var sending = runner.SendAsync(collection, null, spec, _ct);
        spec.Url = server.Url("echo/orders/2");
        await File.WriteAllTextAsync(edited, "", _ct);
        var exchange = await sending;

        Assert.Equal("/echo/orders/1", Echoed(exchange)["path"]!.GetValue<string>());
        var entry = Assert.Single(runner.History!.Load());
        Assert.EndsWith("/echo/orders/1", entry.Url, StringComparison.Ordinal);
        Assert.EndsWith("/echo/orders/1", entry.ReadSpec().Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_runs_in_the_environment_it_was_sent_with_though_another_is_chosen_meanwhile()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        const string Ticket = "T-8d3f61c2a9";
        var temp = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var login = server.Url($"bytes/{Convert.ToHexString(Encoding.UTF8.GetBytes($"{{\"ticket\":\"{Ticket}\"}}"))}?type=application/json");
        var prod = workspace.CreateEnvironment("prod");
        prod.SetValues([new KeyValueEntry { Key = "login", Value = login }, new KeyValueEntry { Key = "ticket", Value = "", Type = "secret" }]);
        var sandbox = workspace.CreateEnvironment("partner-sandbox");
        sandbox.SetValues([new KeyValueEntry { Key = "login", Value = server.Url("echo/sandbox") }]);
        workspace.ActiveEnvironment = prod;
        var collection = workspace.CreateCollection("API");
        var switched = Path.Combine(temp, "switched");
        var node = collection.AddRequest(collection.RootNode, "Login", Python("{{login}}",
            preRequest: WaitFor(switched) + "tr.environment.set('region', 'eu')",
            test: "tr.environment.set('ticket', tr.response.json()['ticket'])\nprint('sent in', tr.info.environment)"));
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(temp, "history"), 10) };
        runner.SetPythonInterpreter(python);
        using var recorder = RequestRecorder.Start(new RecordingOptions { TempRoot = temp, WorkspaceName = "tests" });
        runner.Recorder = recorder;

        // "partner-sandbox" is chosen (F7) while the pre-request scripts run.
        var sending = runner.SendAsync(collection, node, node.ReadSpec(), null, _ct);
        workspace.ActiveEnvironment = sandbox;
        await File.WriteAllTextAsync(switched, "", _ct);
        var exchange = await sending;

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(login, exchange.Request.Url);
        Assert.Equal(["sent in prod"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(("eu", Ticket), (runner.Session.Find(prod)?["region"], runner.Session.Find(prod)?["ticket"]));
        Assert.Null(runner.Session.Find(sandbox));
        var entry = Assert.Single(runner.History!.Load());
        Assert.Equal("prod", entry.Environment);
        Assert.Equal("{\"ticket\":\"***\"}", Encoding.UTF8.GetString(runner.History.ReadBody(entry)!)); // typed secret in prod
        var har = JsonNode.Parse(await File.ReadAllTextAsync(recorder.Save(Path.Combine(temp, "login.har")).Path, _ct))!;
        Assert.Equal("prod", har["log"]!["entries"]![0]!["_termrest"]!["environment"]!.GetValue<string>());
    }

    [Fact]
    public async Task Values_scripts_give_secret_variables_are_masked_in_the_history()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        const string RequestKey = "request-key-7f3a9c";
        const string Session = "session-4be1d07a9f";
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("prod");
        environment.SetValues([new KeyValueEntry { Key = "functionKey", Value = "", Type = "secret" }]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        collection.SetVariables([new KeyValueEntry { Key = "session", Value = "", Type = "secret" }]);
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        runner.SetPythonInterpreter(python);
        var login = Encoding.UTF8.GetBytes($"{{\"session\":\"{Session}\"}}");

        // The key is set for this request only; the session is kept in the environment, where "session" is not typed secret.
        var exchange = await runner.SendAsync(collection, null, Python(server.Url($"bytes/{Convert.ToHexString(login)}?type=application/json") + "&code={{functionKey}}",
            preRequest: $"tr.variables.set('functionKey', '{RequestKey}')",
            test: "tr.environment.set('session', tr.response.json()['session'])"), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.EndsWith($"&code={RequestKey}", exchange.Request.Url, StringComparison.Ordinal);
        Assert.Equal(Session, runner.Session.Find(environment)?["session"]);
        var entry = Assert.Single(runner.History!.Load());
        Assert.EndsWith("&code=***", entry.Url, StringComparison.Ordinal);
        Assert.Equal("{\"session\":\"***\"}", Encoding.UTF8.GetString(runner.History.ReadBody(entry)!));
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
    public async Task Stopping_scripts_stops_a_script_that_runs_at_once()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);
        var running = Path.Combine(workspace.RootPath, "running");
        var sending = runner.SendAsync(collection, null, Python(server.Url("echo"), preRequest: "import time\nopen('running', 'w').close()\ntime.sleep(20)"), _ct);
        for (var i = 0; i < 600 && !File.Exists(running); i++)
        {
            await Task.Delay(50, _ct);
        }

        Assert.True(File.Exists(running));

        // Tools › Scripts › Stop scripts, on the UI thread: Python would read the end of its input only after the script.
        var watch = Stopwatch.StartNew();
        runner.StopScripts();
        var stopping = watch.Elapsed;
        var exchange = await sending;

        Assert.True(stopping < TimeSpan.FromSeconds(1), $"Stopping took {stopping.TotalMilliseconds:0} ms.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The run ended after {watch.Elapsed.TotalSeconds:0.0} s.");
        Assert.Null(exchange.Response);
        Assert.Equal("Not sent: Python was stopped while the scripts ran.", exchange.ErrorMessage);
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
            if os.name != "nt":
                tr.globals.set("session", "its own" if os.getsid(0) == os.getpid() else "TermRest's")
            """, test: "tr.test('still answers', lambda: None)"), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a child reads ''", "captured ünïcödé ✓"], exchange.Scripts.Logs.Select(l => l.Text));

        // Nor can they open TermRest's terminal (/dev/tty) to ask for a password over its screen: Python is in a session of its
        // own, which has none (this test may run without a terminal, so trying to open it would tell nothing).
        Assert.Equal(OperatingSystem.IsWindows() ? null : "its own", runner.Session.Globals.GetValueOrDefault("session"));
    }

    [Fact]
    public async Task Workspace_modules_named_like_python_modules_do_not_replace_those_of_the_worker()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        foreach (var name in (string[])["types", "json", "re", "traceback"])
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, name + ".py"), $"raise ImportError('the workspace has its own {name}')", _ct);
        }

        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "helpers.py"), "def greet(name):\n    return 'hello ' + name\n", _ct);
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        // The scripts still import the workspace's modules.
        var exchange = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: "import helpers\ntr.test(helpers.greet('Ada'), lambda: None)"), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["hello Ada"], exchange.Scripts.Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Python_runs_scripts_through_a_batch_file_that_passes_its_arguments_on()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Batch files are Windows'.");
        }

        var python = TestPython.Require();
        var (ok, description) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.Python, python, _ct);
        Assert.True(ok, description);
        var executable = description.Split(" · ")[1];
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");

        // A batch file that passes its arguments on, as pyenv-win's shims do (python.bat: call pyenv exec python %*, which keeps
        // them in a variable and expands them in blocks). cmd.exe runs it, which ends a command line at its first line break.
        var shim = Path.Combine(TestFiles.NewTempDirectory(), "python.bat");
        await File.WriteAllTextAsync(shim,
            $"@echo off\r\ncall :exec %*\r\nexit /b\r\n:exec\r\nset cmdline=%*\r\nif defined cmdline (\"{executable}\" %cmdline%)\r\n", _ct);
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(shim);

        var exchange = await runner.SendAsync(collection, null, Python(server.Url("echo"),
            preRequest: "tr.request.headers.set('X-Ran', 'yes')", test: "tr.test('ran', lambda: None)"), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.ErrorMessage ?? exchange.Scripts.Describe());
        Assert.Equal("yes", Header(exchange, "x-ran"));
        Assert.Equal(["ran"], exchange.Scripts.Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Python_keeps_only_the_output_it_shows()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        // A script that prints without end: what is not shown is counted, not kept in memory.
        var exchange = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: """
            import tracemalloc
            print("first", end="\r\n")
            tracemalloc.start()
            for i in range(300000):
                print(f"line {i} " + "x" * 100)
            tr.globals.set("kept", tracemalloc.get_traced_memory()[0])
            tracemalloc.stop()
            """), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(1000, exchange.Scripts.Logs.Count);
        Assert.Equal(("first", "line 998 " + new string('x', 100)), (exchange.Scripts.Logs[0].Text, exchange.Scripts.Logs[^1].Text));
        Assert.Contains("299001 more lines the test scripts printed are not shown.", exchange.Scripts.Notes);
        Assert.True(long.Parse(runner.Session.Globals["kept"]!, CultureInfo.InvariantCulture) < 2_000_000, runner.Session.Globals["kept"]);
    }

    [Fact]
    public async Task Scripts_get_the_bytes_of_the_response_as_they_arrived()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);
        async Task<ScriptResults> Get(string hex, string type) => (await runner.SendAsync(collection, null,
            Python(server.Url($"bytes/{hex}?type={Uri.EscapeDataString(type)}"), test: $"""
                tr.test(repr(tr.response.text()), lambda: None)
                assert tr.response.content == bytes.fromhex("{hex}"), tr.response.content.hex()
                assert len(tr.response.content) == tr.response.size
                """), _ct)).Scripts!;

        // Protobuf (no zero byte: it looks like text), Latin-1 and a byte order mark: the text is decoded, the bytes stay.
        var protobuf = await Get("089601120178", "application/x-protobuf");
        var latin1 = await Get("436166E9", "text/plain; charset=iso-8859-1");
        var bom = await Get("EFBBBF7B7D", "application/json");

        Assert.True(protobuf.Succeeded, protobuf.Describe());
        Assert.True(latin1.Succeeded, latin1.Describe());
        Assert.True(bom.Succeeded, bom.Describe());
        Assert.Equal(["'Café'", "'{}'"], [latin1.Tests[0].Name, bom.Tests[0].Name]);
    }

    [Fact]
    public async Task Large_bodies_reach_the_scripts_whole()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);
        var hashes = Python("", test: """
            import hashlib
            tr.globals.set("text", hashlib.sha256(tr.response.text().encode()).hexdigest())
            tr.globals.set("bytes", hashlib.sha256(tr.response.content).hexdigest())
            """);

        // They are written to Python in parts: text with escapes and an emoji across the end of a part (5 × 60,000
        // characters), and bytes not in whole groups of 3 for base64.
        hashes.Url = server.Url("bytes/225C0AF09F9880?type=text%2Fplain&repeat=60000");
        var text = await runner.SendAsync(collection, null, hashes, _ct);
        Assert.True(text.Scripts!.Succeeded, text.Scripts.Describe());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(text.Response!.Body)), runner.Session.Globals["text"]);
        Assert.Equal(runner.Session.Globals["text"], runner.Session.Globals["bytes"]);

        hashes.Url = server.Url("bytes/00FF?repeat=150001");
        var bytes = await runner.SendAsync(collection, null, hashes, _ct);
        Assert.True(bytes.Scripts!.Succeeded, bytes.Scripts.Describe());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes.Response!.Body)), runner.Session.Globals["bytes"]);
    }

    [Fact]
    public async Task Halves_of_surrogate_pairs_in_answers_become_replacement_characters()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);

        // Python's json writes them as escapes (\ud83d), which are not text; whole pairs (😀) stay.
        var exchange = await runner.SendAsync(collection, null, Python(server.Url("echo"), test: """
            print("lone \udc00, pair 😀")
            tr.test("half \ud83d", lambda: None)
            tr.globals.set("name \ud800", "\\ud83d")
            """), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["lone \uFFFD, pair 😀"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["half \uFFFD"], exchange.Scripts.Tests.Select(t => t.Name));
        Assert.Equal(@"\ud83d", runner.Session.Globals["name \uFFFD"]);
    }

    [Fact]
    public async Task What_python_wrote_last_is_kept_without_half_of_an_emoji()
    {
        var python = TestPython.Require();
        using var worker = ScriptWorker.For(ScriptLanguage.Python, python, TestFiles.NewTempDirectory());
        var message = new JsonObject
        {
            ["phase"] = "test",
            ["scripts"] = new JsonArray(new JsonObject { ["name"] = "request", ["code"] = "import os\nos.write(2, ('😀' * 2500 + 'x').encode())\nos._exit(3)" }),
        };

        // Its last 4,000 characters (UTF-16) start with the second half of an emoji, which is left out.
        var stopped = await Assert.ThrowsAsync<ScriptException>(() => worker.RunAsync(message, TimeSpan.FromSeconds(30), cancellationToken: _ct));

        Assert.Equal("Python stopped.\n" + string.Concat(Enumerable.Repeat("😀", 1999)) + "x", stopped.Message);
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
    public async Task A_command_starts_a_runtime_from_the_folders_of_path_only()
    {
        var folder = TestFiles.NewTempDirectory();
        var name = "termrest-probe-" + Guid.NewGuid().ToString("N")[..8];
        var program = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);
        await File.WriteAllTextAsync(program, "", _ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(program, ScriptRuntimeLocator.Executable(name, string.Join(Path.PathSeparator, TestFiles.NewTempDirectory(), "", folder)));
        Assert.Null(ScriptRuntimeLocator.Executable(name, TestFiles.NewTempDirectory()));
        Assert.Equal(Path.Combine(".venv", "bin", "python"), ScriptRuntimeLocator.Executable(Path.Combine(".venv", "bin", "python"), ""));

        // A dot in a command's name is not an extension that Windows runs: python3.12 is python3.12.exe there, as in shells.
        var dotted = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".12.exe" : name + ".12");
        File.Copy(program, dotted);
        Assert.Equal(dotted, ScriptRuntimeLocator.Executable(name + ".12", folder));

        // Windows and .NET look in the running program's folder (and in the current one, the workspace often) before PATH:
        // a program of that name there is not run.
        var command = OperatingSystem.IsWindows() ? name + ".cmd" : name;
        var lookalike = Path.Combine(AppContext.BaseDirectory, command);
        await File.WriteAllTextAsync(lookalike, OperatingSystem.IsWindows() ? "@echo Python 3.99.0\r\n@echo %~f0\r\n" : "#!/bin/sh\necho Python 3.99.0\necho \"$0\"\n", _ct);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(lookalike, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Assert.Equal((false, $"{command} could not be started: it is not on PATH."),
                await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.Python, command, _ct));
        }
        finally
        {
            File.Delete(lookalike);
        }
    }

    [Fact]
    public async Task Find_proposes_the_python_of_the_workspace_without_running_it()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var venv = OperatingSystem.IsWindows() ? Path.Combine(".venv", "Scripts", "python.exe") : Path.Combine(".venv", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(workspace.RootPath, venv))!);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, venv), "not a program: running it fails", _ct);
        using var runner = new RequestRunner(workspace);

        var found = await ScriptRuntimeLocator.FindAsync(ScriptLanguage.Python, workspace.RootPath, runner.ResolveRuntime, _ct);

        Assert.Equal((venv, "Found in this workspace, not run: Check runs it.", false), found);
    }

    [Fact]
    public async Task Find_runs_runtimes_outside_the_workspace()
    {
        // In the workspace, a runtime would obey its files before it is chosen: Bun runs what a bunfig.toml preloads, and
        // dotnet lists the SDKs that a global.json points to. This runtime tells where it runs.
        var workspace = TestFiles.NewTempDirectory();
        var runtime = Path.Combine(TestFiles.NewTempDirectory(), OperatingSystem.IsWindows() ? "where.cmd" : "where");
        await File.WriteAllTextAsync(runtime, OperatingSystem.IsWindows() ? "@echo Node.js 99.0.0\r\n@cd\r\n" : "#!/bin/sh\necho Node.js 99.0.0\npwd\n", _ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var found = await ScriptRuntimeLocator.FindAsync(ScriptLanguage.JavaScript, workspace, _ => runtime, _ct);

        Assert.NotNull(found);
        Assert.True(found.Value.Checked, found.Value.Description);
        Assert.StartsWith("Node.js 99.0.0 · ", found.Value.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(workspace), found.Value.Description, StringComparison.Ordinal);
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
