using System.Diagnostics;
using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Postman's JavaScript scripts (pm.*), run by the user's Node.js or Bun.</summary>
public class JavaScriptScriptingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HttpRequestSpec JavaScript(string url, string preRequest = "", string test = "") =>
        new() { Url = url, Scripts = new ScriptSet(preRequest, test, ScriptLanguage.JavaScript) };

    private static (ApiWorkspace Workspace, PostmanCollection Collection) NewWorkspace()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        return (workspace, workspace.CreateCollection("API"));
    }

    [Fact]
    public async Task Postman_scripts_run_around_a_request_with_the_pm_object()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "base", Value = server.Url("echo") }]);
        environment.Save();
        workspace.ActiveEnvironment = environment;
        collection.SetVariables([new KeyValueEntry { Key = "who", Value = "Ada" }]);
        collection.SetScripts(collection.RootNode, new ScriptSet(
            "pm.request.headers.add({ key: 'X-Trace', value: 'collection' });\npm.environment.set('token', 'abc-123');", "", ScriptLanguage.JavaScript));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.SetScripts(folder, new ScriptSet("", "pm.test('status is 200', () => pm.response.to.have.status(200));", ScriptLanguage.JavaScript));
        var request = collection.AddRequest(folder, "Create", new HttpRequestSpec
        {
            Url = "{{base}}?q=1",
            Headers = [new() { Key = "Authorization", Value = "Bearer {{token}}" }],
            Scripts = new ScriptSet(language: ScriptLanguage.JavaScript, preRequest: """
                pm.request.method = 'put';
                pm.request.url.query.add({ key: 'added', value: '1' });
                pm.request.body.update(JSON.stringify({ name: pm.variables.replaceIn('{{who}}') }));
                pm.request.headers.upsert({ key: 'Content-Type', value: 'application/json' });
                pm.variables.set('started', Date.now());
                """,
            test: """
                const sent = pm.response.json();
                pm.test('the pre-request scripts changed the request', () => {
                    pm.expect(sent.method).to.equal('PUT');
                    pm.expect(sent.query).to.eql({ q: '1', added: '1' });
                    pm.expect(sent.headers['x-trace']).to.equal('collection');
                    pm.expect(sent.headers.authorization).to.equal('Bearer abc-123');
                    pm.expect(JSON.parse(sent.body)).to.deep.equal({ name: 'Ada' });
                });
                pm.test('variables of this request', () => pm.expect(pm.variables.get('started')).to.match(/^\d+$/));
                pm.test('fails on purpose', () => pm.expect(pm.response.code, 'status').to.equal(201));
                pm.response.to.have.jsonBody('method', 'PUT');
                tests['legacy test'] = responseCode.code === 200 && JSON.parse(responseBody).method === 'PUT';
                postman.setEnvironmentVariable('legacy', 'yes');
                pm.collectionVariables.set('last_status', pm.response.code);
                console.log('token is', pm.environment.get('token'), 'in', pm.environment.name, { n: 1 });
                """),
        });
        collection.Save();
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(4, results.Ran);
        Assert.Equal(
            [("status is 200", true), ("the pre-request scripts changed the request", true), ("variables of this request", true), ("fails on purpose", false), ("legacy test", true)],
            results.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("status: expected 200 to equal 201", results.Tests[3].Error);
        Assert.Equal("folder 'Users'", results.Tests[0].Script);
        Assert.Equal(["token is abc-123 in dev { n: 1 }"], results.Logs.Select(l => l.Text));
        Assert.Contains("environment.token", results.VariablesSet);
        Assert.Contains("collectionVariables.last_status", results.VariablesSet);
        Assert.Contains("variables.started", results.VariablesSet);
        Assert.Equal(("abc-123", "yes"), (runner.Session.For(environment)["token"], runner.Session.For(environment)["legacy"]));
        Assert.Equal("200", runner.Session.For(collection)["last_status"]);
        Assert.Equal("{{base}}?q=1", request.ReadSpec().Url); // scripts change what is sent, not the request
        Assert.Contains(runner.RunningRuntimes, name => name is "Node.js" or "Bun");
    }

    [Fact]
    public async Task Expect_has_the_chai_assertions_postman_scripts_use_with_chai_messages()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            const passing = [
              () => pm.expect(1).to.equal(1),
              () => pm.expect({ a: [1, { b: 2 }] }).to.eql({ a: [1, { b: 2 }] }),
              () => pm.expect('hello world').to.include('world'),
              () => pm.expect([1, 2, 3]).to.include(2),
              () => pm.expect({ a: 1, b: 2 }).to.include({ a: 1 }),
              () => pm.expect([{ id: 1 }]).to.deep.include({ id: 1 }),
              () => pm.expect([]).to.be.an('array').that.is.empty,
              () => pm.expect('x').to.be.a('string'),
              () => pm.expect(null).to.be.null,
              () => pm.expect(undefined).to.be.undefined,
              () => pm.expect(0).to.not.be.ok,
              () => pm.expect(true).to.be.true,
              () => pm.expect(5).to.be.above(3).and.below(10),
              () => pm.expect(5).to.be.within(1, 5),
              () => pm.expect([1, 2]).to.have.lengthOf(2),
              () => pm.expect([1, 2, 3]).to.have.length.above(2),
              () => pm.expect({ a: { b: [1, 2] } }).to.have.nested.property('a.b[1]', 2),
              () => pm.expect({ a: 1 }).to.have.property('a').that.equals(1),
              () => pm.expect({ a: 1, b: 2 }).to.have.all.keys('a', 'b'),
              () => pm.expect({ a: 1, b: 2 }).to.include.keys('a'),
              () => pm.expect('abc').to.match(/^a/),
              () => pm.expect(201).to.be.oneOf([200, 201]),
              () => pm.expect([3, 1, 2]).to.have.members([1, 2, 3]),
              () => pm.expect([1, 2, 3]).to.include.members([1, 3]),
              () => pm.expect(() => { throw new TypeError('bad thing'); }).to.throw(TypeError, 'bad'),
              () => pm.expect(1.5).to.be.closeTo(1.4, 0.2),
              () => pm.expect(4).to.satisfy(n => n % 2 === 0),
              () => pm.expect('a').to.exist,
              () => pm.expect(new Date(5)).to.be.a('date'),
              () => pm.expect({}).to.be.empty,
            ];
            const failing = [
              [() => pm.expect(404).to.equal(200), 'expected 404 to equal 200'],
              [() => pm.expect({ a: 1 }).to.eql({ a: 2 }), 'expected { a: 1 } to deeply equal { a: 2 }'],
              [() => pm.expect('abc').to.include('z'), "expected 'abc' to include 'z'"],
              [() => pm.expect(3).to.be.above(5), 'expected 3 to be above 5'],
              [() => pm.expect([1]).to.have.lengthOf(2), 'expected [ 1 ] to have a length of 2 but got 1'],
              [() => pm.expect({ a: 1 }).to.have.property('b'), "expected { a: 1 } to have property 'b'"],
              [() => pm.expect(1).to.be.oneOf([2, 3]), 'expected 1 to be one of [ 2, 3 ]'],
              [() => pm.expect([]).to.not.be.empty, 'expected [] not to be empty'],
              [() => pm.expect('x').to.be.a('number'), "expected 'x' to be a number"],
              [() => pm.expect(5, 'custom').to.equal(6), 'custom: expected 5 to equal 6'],
              [() => pm.response.to.have.status(201), 'expected response to have status code 201 but got 200'],
              [() => pm.response.to.have.header('X-Missing'), "expected response to have header with key 'X-Missing'"],
              [() => pm.response.to.be.clientError, 'expected response code to be 4XX but found 200'],
              [() => pm.expect.fail('boom'), 'boom'],
            ];
            passing.forEach((fn, index) => pm.test('passes ' + index, fn));
            failing.forEach(([fn, message], index) => pm.test('fails ' + index + '|' + message, fn));
            pm.test('response assertions', () => {
              pm.response.to.be.ok;
              pm.response.to.be.success;
              pm.response.to.not.be.error;
              pm.response.to.be.json;
              pm.response.to.have.jsonBody('method');
              pm.response.to.have.header('Content-Type');
              pm.expect(pm.response.headers.get('content-type')).to.include('application/json');
              pm.expect(pm.response.responseTime).to.be.a('number');
            });
            """), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        var failures = results.Tests.Where(t => t.Name.StartsWith("fails ", StringComparison.Ordinal)).ToList();
        Assert.Equal(14, failures.Count);
        Assert.All(results.Tests.Where(t => !t.Name.StartsWith("fails ", StringComparison.Ordinal)), t => Assert.True(t.Passed, $"{t.Name}: {t.Error}"));
        Assert.Equal(31, results.Tests.Count(t => t.Passed));
        Assert.All(failures, t =>
        {
            Assert.False(t.Passed, t.Name);
            Assert.Equal(t.Name[(t.Name.IndexOf('|', StringComparison.Ordinal) + 1)..], t.Error);
        });
    }

    [Fact]
    public async Task Javascript_errors_show_the_script_line_and_a_failing_pre_request_script_stops_the_request()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var request = collection.AddRequest(collection.RootNode, "Broken", JavaScript(server.Url("echo"),
            preRequest: "const data = {};\nconsole.log('before');\nconst value = data.missing.field;", test: "console.log('not run');"));
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var broken = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        Assert.Null(broken.Response);
        Assert.StartsWith("Not sent: request 'Broken': TypeError: ", broken.ErrorMessage, StringComparison.Ordinal);
        var error = broken.Scripts!.Error!;
        Assert.Contains("request 'Broken', line 3", error.Details, StringComparison.Ordinal);
        Assert.Contains("const value = data.missing.field;", error.Details, StringComparison.Ordinal);
        Assert.Equal(["before"], broken.Scripts.Logs.Select(l => l.Text));

        var syntax = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), preRequest: "let x = 1;\nfunction ("), _ct);
        Assert.Null(syntax.Response);
        Assert.StartsWith("SyntaxError", syntax.Scripts!.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", syntax.Scripts.Error.Details, StringComparison.Ordinal);

        var later = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "setTimeout(() => { throw new Error('later'); }, 10);"), _ct);
        Assert.Equal(200, later.Response!.StatusCode);
        Assert.Equal("Error: later", later.Scripts!.Error!.Message);

        var rejected = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "Promise.reject(new Error('nobody caught this'));"), _ct);
        Assert.Equal("Error: nobody caught this", rejected.Scripts!.Error!.Message);

        var skipped = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), preRequest: "pm.execution.skipRequest();"), _ct);
        Assert.Null(skipped.Response);
        Assert.Contains("skipRequest() is not supported by TermRest, so the request is not sent", skipped.ErrorMessage, StringComparison.Ordinal);

        var next = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "pm.test('still works', () => {});"), _ct);
        Assert.True(next.Scripts!.Succeeded, next.Scripts.Describe());
    }

    [Fact]
    public async Task Scripts_wait_for_their_requests_timers_and_asynchronous_tests()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        collection.SetVariables([new KeyValueEntry { Key = "base", Value = server.Url("echo") }]);
        collection.SetScripts(collection.RootNode, new ScriptSet("""
            pm.sendRequest(pm.variables.replaceIn('{{base}}') + '?from=callback', (error, response) => {
              if (error) throw error;
              pm.collectionVariables.set('fetched', response.json().query.from);
            });
            """, "", ScriptLanguage.JavaScript));
        var request = collection.AddRequest(collection.RootNode, "Async", JavaScript("{{base}}", preRequest: """
            const response = await pm.sendRequest({
              url: pm.variables.replaceIn('{{base}}'),
              method: 'POST',
              header: { 'Content-Type': 'application/json' },
              body: { mode: 'raw', raw: JSON.stringify({ a: 1 }) },
            });
            pm.variables.set('posted', response.json().body);
            pm.variables.set('seen', pm.collectionVariables.get('fetched'));
            """, test: """
            pm.test('the collection script finished first', () => pm.expect(pm.variables.get('seen')).to.equal('callback'));
            pm.test('posted with await', () => pm.expect(JSON.parse(pm.variables.get('posted'))).to.eql({ a: 1 }));
            pm.test('done callback', done => { setTimeout(() => { pm.expect(1).to.equal(1); done(); }, 20); });
            pm.test('async function', async () => { await new Promise(resolve => setTimeout(resolve, 10)); pm.expect(2).to.equal(3); });
            """));
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(
            [("the collection script finished first", true), ("posted with await", true), ("done callback", true), ("async function", false)],
            results.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("expected 2 to equal 3", results.Tests[3].Error);
    }

    [Fact]
    public async Task A_request_runs_its_scripts_in_the_first_language_of_the_order_that_has_them_all()
    {
        var python = TestPython.Require();
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        collection.SetScripts(collection.RootNode, new ScriptSet([
            new LanguageScripts(ScriptLanguage.JavaScript, "pm.variables.set('ran', 'js');", "pm.test('collection js', () => {});"),
            new LanguageScripts(ScriptLanguage.Python, "tr.variables.set('ran', 'python')", "tr.test('collection python', lambda: None)"),
        ]));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        var both = collection.AddRequest(folder, "Both", new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Scripts = new ScriptSet([
                new LanguageScripts(ScriptLanguage.JavaScript, "", "pm.test('request js', () => pm.expect(pm.variables.get('ran')).to.equal('js'));"),
                new LanguageScripts(ScriptLanguage.Python, "", "tr.test('request python', lambda: None)"),
            ]),
        });
        var javaScriptOnly = collection.AddRequest(folder, "JavaScript only", new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Scripts = new ScriptSet("", "pm.test('request js', () => pm.expect(pm.variables.get('ran')).to.equal('js'));", ScriptLanguage.JavaScript),
        });
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);
        runner.SetJavaScriptRuntime(node);

        // Python comes first: its scripts run, and only they, in one runtime.
        var inPython = await runner.SendAsync(collection, both, both.ReadSpec(), null, _ct);
        Assert.True(inPython.Scripts!.Succeeded, inPython.Scripts.Describe());
        Assert.Equal(["collection python", "request python"], inPython.Scripts.Tests.Select(t => t.Name));
        Assert.Equal(["Python"], runner.RunningRuntimes);
        Assert.Equal(["Language: Python, the first of your language order with all the scripts of this request."], inPython.Scripts.Notes);

        // A request whose test is only in JavaScript runs all its scripts in JavaScript, the collection's included.
        var inJavaScript = await runner.SendAsync(collection, javaScriptOnly, javaScriptOnly.ReadSpec(), null, _ct);
        Assert.True(inJavaScript.Scripts!.Succeeded, inJavaScript.Scripts.Describe());
        Assert.Equal(["collection js", "request js"], inJavaScript.Scripts.Tests.Select(t => t.Name));
        Assert.Equal(
            "Language: JavaScript, the first of your language order with all the scripts of this request (Python lacks the post-response script of " +
            "request 'Users/JavaScript only').",
            inJavaScript.Scripts.Notes[0]);

        // Another order; a language chosen by the request; scripts turned off.
        runner.ScriptLanguageOrder = [ScriptLanguage.JavaScript];
        var reordered = await runner.SendAsync(collection, both, both.ReadSpec(), null, _ct);
        Assert.Equal(["collection js", "request js"], reordered.Scripts!.Tests.Select(t => t.Name));
        var chosen = both.ReadSpec();
        chosen.ScriptSettings = new ScriptSettings(ScriptLanguage.Python);
        Assert.Equal(["collection python", "request python"], (await runner.SendAsync(collection, both, chosen, null, _ct)).Scripts!.Tests.Select(t => t.Name));
        var wrong = javaScriptOnly.ReadSpec();
        wrong.ScriptSettings = new ScriptSettings(ScriptLanguage.Python);
        var refused = await runner.SendAsync(collection, javaScriptOnly, wrong, null, _ct);
        Assert.Null(refused.Response);
        Assert.Equal(0, refused.Scripts!.Ran);
        Assert.Equal(
            "Not sent: This request runs its scripts in Python (its Scripts tab), but there is no Python version of the post-response script of request " +
            "'Users/JavaScript only'. Write it in Python, choose another language, or turn post-response scripts off in its Scripts tab.",
            refused.ErrorMessage);
        wrong.ScriptSettings = wrong.ScriptSettings with { PostResponse = false };
        var preRequestOnly = await runner.SendAsync(collection, javaScriptOnly, wrong, null, _ct);
        Assert.Equal(200, preRequestOnly.Response!.StatusCode);
        Assert.Empty(preRequestOnly.Scripts!.Tests);
        Assert.Equal(1, preRequestOnly.Scripts.Ran);
        Assert.Contains("Post-response scripts are turned off for this request (its Scripts tab): those of collection 'API', request 'Users/JavaScript only' did not run.",
            preRequestOnly.Scripts.Notes);
    }

    [Fact]
    public async Task The_runtime_is_stopped_when_scripts_take_too_long_and_when_it_is_idle()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace) { ScriptTimeout = TimeSpan.FromSeconds(3), ScriptIdleTimeout = TimeSpan.FromMilliseconds(500) };
        runner.SetJavaScriptRuntime(node);

        var slow = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), preRequest: "await new Promise(() => {});"), _ct);
        Assert.Null(slow.Response);
        Assert.Contains("did not finish within 3 s", slow.ErrorMessage, StringComparison.Ordinal);
        Assert.False(runner.ScriptsRunning);

        // A program a script starts may write to the runtime's own output: TermRest ignores it.
        var stray = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            const child = require('child_process');
            child.spawnSync(require('process').execPath, ['-e', "console.log('stray output')"], { stdio: 'inherit' });
            console.log('captured');
            pm.test('still answers', () => {});
            """), _ct);
        Assert.True(stray.Scripts!.Succeeded, stray.Scripts.Describe());
        Assert.Equal(["captured"], stray.Scripts.Logs.Select(l => l.Text));
        Assert.True(runner.ScriptsRunning);

        var idle = Stopwatch.StartNew();
        while (runner.ScriptsRunning && idle.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100, _ct);
        }

        Assert.False(runner.ScriptsRunning);
        var again = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "pm.test('ok', () => {});"), _ct);
        Assert.True(again.Scripts!.Succeeded, again.Scripts.Describe());
    }

    [Fact]
    public async Task Scripts_require_modules_from_the_workspace_and_say_how_to_install_missing_packages()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "helpers.js"), "module.exports = { greet: name => 'hello ' + name };", _ct);
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var modules = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            const helpers = require('./helpers');
            pm.test('a module of the workspace', () => pm.expect(helpers.greet('Ada')).to.equal('hello Ada'));
            pm.test('a built-in module', () => pm.expect(require('crypto').createHash('sha256').update('x').digest('hex')).to.have.lengthOf(64));
            """), _ct);
        Assert.True(modules.Scripts!.Succeeded, modules.Scripts.Describe());
        Assert.Equal(2, modules.Scripts.Passed);

        var missing = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "require('termrest-missing-package/sub');"), _ct);
        Assert.Contains("npm install termrest-missing-package)", missing.Scripts!.Error!.Message, StringComparison.Ordinal);

        var lodash = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "_.isEqual(1, 1);"), _ct);
        if (ScriptLanguage.JavaScriptRuntimeName(node) == "Bun")
        {
            Assert.Null(lodash.Scripts!.Error); // Bun installs packages it cannot find by itself (no node_modules here)
        }
        else
        {
            Assert.Contains("npm install lodash", lodash.Scripts!.Error!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_runtime_check_names_the_runtime_and_its_version()
    {
        var node = TestJavaScript.Require();
        var directory = TestFiles.NewTempDirectory();

        var (ok, message) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.JavaScript, node, directory, _ct);
        Assert.True(ok, message);
        Assert.Matches(@"^(Node\.js|Bun) \d+\.\d+\.\d+ · .+", message);

        var (missingOk, missingMessage) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.JavaScript, "termrest-no-such-runtime", directory, _ct);
        Assert.False(missingOk);
        Assert.StartsWith("termrest-no-such-runtime could not be started", missingMessage, StringComparison.Ordinal);

        var found = await ScriptRuntimeLocator.FindAsync(ScriptLanguage.JavaScript, directory, runtime => runtime, _ct);
        Assert.NotNull(found);
        Assert.Contains(found.Value.Runtime, (string[])["node", "bun"]);
    }
}
