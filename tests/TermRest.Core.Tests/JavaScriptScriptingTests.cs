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
              () => pm.expect([1, 2]).to.have.lengthOf.above(1),
              () => pm.expect([1, 2]).to.have.lengthOf.at.least(2),
              () => pm.expect([1, 2]).to.have.lengthOf.within(1, 3),
              () => pm.expect(Object.freeze({})).to.be.frozen,
              () => pm.expect(pm.response).to.be.postmanResponse,
              () => pm.expect(1).to.not.be.null.and.not.undefined,
              () => pm.expect([1, 2]).to.have.lengthOf.at.least(1).and.at.most(10),
              () => pm.expect([1, 2]).to.have.lengthOf(2).and.be.above(1),
              () => pm.expect([1, 2]).to.have.lengthOf('2'),
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
              [() => pm.expect(null).to.be.within(0, 120), 'expected null to be a number or a date'],
              [() => pm.expect('42').to.not.be.within(0, 1), "expected '42' to be a number or a date"],
              [() => pm.expect(null).to.be.closeTo(1, 0.5), 'expected null to be a number'],
              [() => pm.expect('1.5').to.be.closeTo(1.4, 0.2), "expected '1.5' to be a number"],
              [() => pm.expect(1.5).to.be.closeTo(1.4), 'the arguments to closeTo or approximately must be numbers, and a delta is required'],
              [() => pm.expect([1, 2]).to.have.lengthOf.above(5), 'expected [ 1, 2 ] to have a length above 5 but got 2'],
              [() => pm.expect(false).to.be.ture, 'Error: Invalid Chai property: ture. Did you mean "true"?'],
              [() => pm.expect([1]).to.be.array, 'Error: Invalid Chai property: array. Did you mean "any"?'],
              [() => pm.expect([1]).to.include.membres([1]), 'Error: Invalid Chai property: membres. Did you mean "members"?'],
              [() => pm.response.to.be.created, 'Error: Invalid Chai property: created. Did you mean "nested"?'],
              [() => pm.response.to.be.withoutContent, 'expected response code to be 204 but found 200'],
              [() => pm.expect(null).to.exists, 'expected null to exist'],
              [() => pm.expect({}).to.be.sealed, 'expected {} to be sealed'],
              [() => pm.expect(undefined).to.not.be.null.and.not.undefined, 'expected undefined not to be undefined'],
              [() => pm.expect(1).to.not.not.equal(1), 'expected 1 to not equal 1'],
              [() => pm.expect([1, 2]).to.have.lengthOf.at.least(1).and.at.most(1), 'expected [ 1, 2 ] to have a length at most 1 but got 2'],
              [() => pm.expect([1, 2]).to.have.length.equal(2), 'expected [ 1, 2 ] to equal 2'],
              [() => pm.expect(new Date(5)).to.be.within(1, 9), 'the arguments to within must be dates'],
              [() => pm.expect(5).to.not.be.within('1', '9'), 'the arguments to within must be numbers'],
              [() => pm.expect([1, 2]).to.have.lengthOf.within('1', 3), 'the arguments to within must be numbers'],
              [() => pm.expect(new Date(0)).to.be.within(new Date(5), new Date(9)), 'expected 1970-01-01T00:00:00.000Z to be within 1970-01-01T00:00:00.005Z..1970-01-01T00:00:00.009Z'],
              [() => pm.expect(null).to.not.have.property('error'), 'Target cannot be null or undefined.'],
              [() => pm.expect(undefined).to.have.nested.property('a.b'), 'Target cannot be null or undefined.'],
              [() => pm.expect(null, 'data').to.not.have.own.property('a'), 'data: Target cannot be null or undefined.'],
              [() => pm.expect(5).to.be.above('1'), 'the argument to above must be a number'],
              [() => pm.expect(5).to.not.be.lte('9'), 'the argument to most must be a number'],
              [() => pm.expect(new Date(5)).to.be.below(9), 'the argument to below must be a date'],
              [() => pm.expect([1, 2]).to.have.lengthOf.at.least('1'), 'the argument to least must be a number'],
              [() => pm.expect([1, 2]).to.not.have.lengthOf('2'), 'expected [ 1, 2 ] to not have a length of 2'],
              [() => pm.expect({}).to.have.lengthOf(undefined), "expected {} to have property 'length'"],
              [() => pm.expect(null).to.have.lengthOf(0), 'Target cannot be null or undefined.'],
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
        Assert.Equal(45, failures.Count);
        Assert.All(results.Tests.Where(t => !t.Name.StartsWith("fails ", StringComparison.Ordinal)), t => Assert.True(t.Passed, $"{t.Name}: {t.Error}"));
        Assert.Equal(40, results.Tests.Count(t => t.Passed));
        Assert.All(failures, t =>
        {
            Assert.False(t.Passed, t.Name);
            Assert.Equal(t.Name[(t.Name.IndexOf('|', StringComparison.Ordinal) + 1)..], t.Error);
        });

        var noContent = await runner.SendAsync(collection, null, JavaScript(server.Url("status/204"), test: """
            pm.test('no content', () => pm.response.to.be.withoutContent.and.not.withBody);
            """), null, _ct);

        Assert.Equal(204, noContent.Response!.StatusCode);
        Assert.True(noContent.Scripts!.Error is null, noContent.Scripts.Describe());
        Assert.Equal([("no content", true)], noContent.Scripts.Tests.Select(t => (t.Name, t.Passed)));
    }

    /// <summary>
    /// A stand-in for ajv in the workspace's node_modules (scripts take it from there): it checks only a schema's
    /// <c>type</c> and <c>format</c>. As ajv 8 with <c>strict: false</c>, it logs a format it doesn't know and ignores it;
    /// as ajv 6 (<paramref name="version"/> 6), it knows <c>email</c> and throws on a format it doesn't know (unless told to
    /// ignore it). With <paramref name="formats"/>, a stand-in for ajv-formats that adds <c>email</c>: as the real one, it
    /// works with ajv 7 and later only (it sets <c>ajv.opts.code.formats</c>).
    /// </summary>
    private static void WriteAjv(string root, bool formats, int version = 8)
    {
        var ajv = Path.Combine(root, "node_modules", "ajv");
        Directory.CreateDirectory(ajv);
        File.WriteAllText(Path.Combine(ajv, "index.js"), version >= 7
            ? """
              module.exports = class Ajv {
                constructor(opts) { this.opts = Object.assign({ code: {} }, opts); this.formats = {}; }
                addFormat(name, check) { this.formats[name] = check; return this; }
                validate(schema, data) {
                  const type = Array.isArray(data) ? 'array' : data === null ? 'null' : typeof data;
                  const errors = schema.type === undefined || schema.type === type ? [] : [{ message: 'must be ' + schema.type }];
                  if (schema.format !== undefined && this.formats[schema.format] === undefined) {
                    this.opts.logger.warn('unknown format "' + schema.format + '" ignored in schema at path "#"');
                  } else if (schema.format !== undefined && !this.formats[schema.format](data)) {
                    errors.push({ message: 'must match format "' + schema.format + '"' });
                  }
                  this.errors = errors.length > 0 ? errors : null;
                  return this.errors === null;
                }
                errorsText() { return this.errors ? 'data ' + this.errors.map(e => e.message).join(', ') : 'No errors'; }
              };
              """
            : """
              module.exports = class Ajv {
                constructor(opts) { this._opts = opts; this._formats = { email: s => /^[^@\s]+@[^@\s]+$/.test(s) }; }
                validate(schema, data) {
                  const type = Array.isArray(data) ? 'array' : data === null ? 'null' : typeof data;
                  const errors = schema.type === undefined || schema.type === type ? [] : [{ message: 'should be ' + schema.type }];
                  if (schema.format !== undefined && this._formats[schema.format] === undefined) {
                    if (this._opts.unknownFormats !== 'ignore') throw new Error('unknown format "' + schema.format + '" is used in schema at path "#"');
                    this._opts.logger.warn('unknown format "' + schema.format + '" ignored in schema at path "#"');
                  } else if (schema.format !== undefined && !this._formats[schema.format](data)) {
                    errors.push({ message: 'should match format "' + schema.format + '"' });
                  }
                  this.errors = errors.length > 0 ? errors : null;
                  return this.errors === null;
                }
                errorsText() { return this.errors ? 'data ' + this.errors.map(e => e.message).join(', ') : 'No errors'; }
              };
              """);
        if (formats)
        {
            var addFormats = Path.Combine(root, "node_modules", "ajv-formats");
            Directory.CreateDirectory(addFormats);
            File.WriteAllText(Path.Combine(addFormats, "index.js"), "module.exports = ajv => { ajv.opts.code.formats = {}; ajv.addFormat('email', s => /^[^@\\s]+@[^@\\s]+$/.test(s)); };\n");
        }
    }

    [Fact]
    public async Task Json_schema_checks_a_response_body_or_any_value_as_in_postman()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        WriteAjv(workspace.RootPath, formats: false);
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            const body = pm.response.json();
            pm.test('response', () => pm.response.to.have.jsonSchema({ type: 'object' }));
            pm.test('value', () => pm.expect(body).to.have.jsonSchema({ type: 'object' }));
            pm.test('part', () => pm.expect([body.method]).to.have.jsonSchema({ type: 'array' }));
            pm.test('mismatch', () => pm.expect(body).to.have.jsonSchema({ type: 'array' }));
            pm.test('negated', () => pm.expect(body).to.not.have.jsonSchema({ type: 'object' }));
            """), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(
            [("response", true, null), ("value", true, null), ("part", true, null),
             ("mismatch", false, "expected data to satisfy schema but found following errors: \ndata must be array"), ("negated", false, "expected data to not satisfy schema")],
            results.Tests.Select(t => (t.Name, t.Passed, t.Error)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_schema_formats_are_checked_as_in_postman_or_the_test_says_what_to_install(bool formats)
    {
        // Postman's ajv 6 checks formats; ajv 8 only with ajv-formats, which would otherwise pass anything.
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        WriteAjv(workspace.RootPath, formats);
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            pm.test('valid', () => pm.expect('ada@example.com').to.have.jsonSchema({ type: 'string', format: 'email' }));
            pm.test('invalid', () => pm.expect('not an email').to.have.jsonSchema({ type: 'string', format: 'email' }));
            pm.test('unknown', () => pm.expect('555').to.have.jsonSchema({ type: 'string', format: 'phone' }));
            """), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        static string Unknown(string format, bool install) =>
            $"unknown format \"{format}\" ignored in schema at path \"#\"" + (install ? ": install ajv-formats in the workspace (npm install ajv-formats), as Postman checks formats" : "");
        (string, bool, string?)[] expected = formats
            ? [("valid", true, null), ("invalid", false, "expected data to satisfy schema but found following errors: \ndata must match format \"email\""), ("unknown", false, Unknown("phone", false))]
            : [("valid", false, Unknown("email", true)), ("invalid", false, Unknown("email", true)), ("unknown", false, Unknown("phone", true))];
        Assert.Equal(expected, results.Tests.Select(t => (t.Name, t.Passed, t.Error)));
    }

    [Fact]
    public async Task Json_schema_uses_an_ajv_6_as_it_is_even_where_ajv_formats_is_installed()
    {
        // Postman's ajv 6 (or ESLint's) next to ajv-formats (for ajv 8, as npm installs it): ajv 6 checks formats itself.
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        WriteAjv(workspace.RootPath, formats: true, version: 6);
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            pm.test('body', () => pm.response.to.have.jsonSchema({ type: 'object' }));
            pm.test('invalid', () => pm.expect('not an email').to.have.jsonSchema({ type: 'string', format: 'email' }));
            pm.test('unknown', () => pm.expect('555').to.have.jsonSchema({ type: 'string', format: 'phone' }));
            pm.test('ignored', () => pm.expect('555').to.have.jsonSchema({ type: 'string', format: 'phone' }, { unknownFormats: 'ignore' }));
            """), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(
            [("body", true, null), ("invalid", false, "expected data to satisfy schema but found following errors: \ndata should match format \"email\""),
             ("unknown", false, "Error: unknown format \"phone\" is used in schema at path \"#\""), ("ignored", true, null)],
            results.Tests.Select(t => (t.Name, t.Passed, t.Error)));
    }

    [Fact]
    public async Task Dates_and_urls_set_in_scripts_are_their_text()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            pm.collectionVariables.set('when', new Date(0));
            pm.collectionVariables.set('site', new URL('https://example.org/a'));
            pm.collectionVariables.set('flags', { on: true });
            """), null, _ct);

        Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
        var stored = runner.Session.For(collection);
        Assert.Equal(("1970-01-01T00:00:00.000Z", "https://example.org/a", """{"on":true}"""), (stored["when"], stored["site"], stored["flags"]));
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

        // What can't be turned into text fails its script (and its test), not the whole answer: what it logged is kept.
        var bare = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "console.log('before');\nthrow Object.create(null);"), _ct);
        Assert.Equal("[object Object]", bare.Scripts!.Error!.Message);
        Assert.Equal(["before"], bare.Scripts.Logs.Select(l => l.Text));
        var unreadable = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            const error = new TypeError();
            Object.defineProperty(error, 'message', { get() { throw new Error('no text'); } });
            pm.test('unreadable', () => { throw error; });
            pm.test('bare', () => { throw Object.create(null); });
            throw error;
            """), _ct);
        Assert.Equal("TypeError: (its message could not be read)", unreadable.Scripts!.Error!.Message);
        Assert.Equal(["TypeError: (its message could not be read)", "[object Object]"], unreadable.Scripts.Tests.Select(t => t.Error));

        var skipped = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), preRequest: "pm.execution.skipRequest();"), _ct);
        Assert.Null(skipped.Response);
        Assert.Contains("skipRequest() is not supported by TermRest, so the request is not sent", skipped.ErrorMessage, StringComparison.Ordinal);

        var next = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: "pm.test('still works', () => {});"), _ct);
        Assert.True(next.Scripts!.Succeeded, next.Scripts.Describe());
    }

    [Fact]
    public async Task Scripts_may_set_the_host_and_path_of_the_url_as_text_as_in_postman()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        var moved = await runner.SendAsync(collection, null, JavaScript("https://api.example.com:8443/v1/items?q=1#top", preRequest: $$"""
            pm.request.url.protocol = 'http';
            pm.request.url.host = '{{server.BaseUri.Host}}';
            pm.request.url.port = {{server.BaseUri.Port}};
            pm.request.url.path = '/echo/moved';
            pm.request.url.hash = null;
            console.log(pm.request.url.getHost(), pm.request.url.getPath());
            """), _ct);

        Assert.True(moved.Scripts!.Succeeded, moved.Scripts.Describe());
        Assert.Equal([$"{server.BaseUri.Host} /echo/moved"], moved.Scripts.Logs.Select(l => l.Text));
        var echo = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(moved.Response!.Body))!;
        Assert.Equal(("/echo/moved", "?q=1"), (echo["path"]!.GetValue<string>(), echo["rawQuery"]!.GetValue<string>()));

        // A request the scripts left unusable fails them, not the whole answer: what they printed, tested and set is kept.
        var broken = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), preRequest: """
            console.log('before');
            pm.collectionVariables.set('kept', 'yes');
            pm.test('ran', () => {});
            pm.request.url.query = 'a=1';
            """), _ct);

        Assert.Null(broken.Response);
        Assert.StartsWith("The pre-request scripts left a request that cannot be sent: TypeError: ", broken.Scripts!.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("\nScripts failed\n  The pre-request scripts left", broken.Scripts.Describe(), StringComparison.Ordinal); // they ran
        Assert.Equal(["before"], broken.Scripts.Logs.Select(l => l.Text));
        Assert.Equal([("ran", true)], broken.Scripts.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("yes", runner.Session.For(collection)["kept"]);
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
        Assert.StartsWith("Scripts did not run\n", refused.Scripts.Describe(), StringComparison.Ordinal);
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
            const process = require('process');
            if (process.platform === 'linux') {
              const stat = require('fs').readFileSync('/proc/self/stat', 'utf8');
              pm.globals.set('session', Number(stat.slice(stat.lastIndexOf(')') + 2).split(' ')[3]) === process.pid ? 'its own' : "TermRest's");
            }
            """), _ct);
        Assert.True(stray.Scripts!.Succeeded, stray.Scripts.Describe());
        Assert.Equal(["captured"], stray.Scripts.Logs.Select(l => l.Text));
        Assert.True(runner.ScriptsRunning);

        // Nor can it open TermRest's terminal (/dev/tty) on Linux, where setsid starts the runtime in a session of its own,
        // which has none; setsid then became the runtime, which still runs (it did not fork it off).
        if (OperatingSystem.IsLinux() && ScriptRuntimeLocator.Executable("setsid") is not null)
        {
            Assert.Equal("its own", runner.Session.Globals.GetValueOrDefault("session"));
        }

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
    public async Task Halves_of_emoji_do_not_break_the_answer()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        // A line cut at 4,000 characters, and a script's own substring of an emoji (not text: it becomes U+FFFD).
        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), test: """
            console.log('a' + '😀'.repeat(2000));
            const half = '😀'.substring(0, 1);
            console.log('half ' + half + ' and a whole 😀');
            pm.test('test ' + half, () => {});
            pm.globals.set('name ' + half, half);
            """), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a" + string.Concat(Enumerable.Repeat("😀", 1999)), "half \uFFFD and a whole 😀"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["test \uFFFD"], exchange.Scripts.Tests.Select(t => t.Name));
        Assert.Equal("\uFFFD", runner.Session.Globals["name \uFFFD"]);
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
    public async Task What_required_modules_log_goes_to_the_scripts_output()
    {
        var node = TestJavaScript.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "helpers.js"),
            "module.exports.hi = () => { console.log('from helper module'); console.error('and its error'); };", _ct);
        using var runner = new RequestRunner(workspace);
        runner.SetJavaScriptRuntime(node);

        // Modules run outside the scripts' context, with the runtime's own console.
        var exchange = await runner.SendAsync(collection, null, JavaScript(server.Url("echo"), preRequest: "require('./helpers').hi();\nconsole.log('from script');"), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["from helper module", "error: and its error", "from script"], exchange.Scripts.Logs.Select(l => l.Text));
    }

    [Fact]
    public async Task The_runtime_check_names_the_runtime_and_its_version()
    {
        var node = TestJavaScript.Require();
        var directory = TestFiles.NewTempDirectory();

        var (ok, message) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.JavaScript, node, _ct);
        Assert.True(ok, message);
        Assert.Matches(@"^(Node\.js|Bun) \d+\.\d+\.\d+ · .+", message);

        var (missingOk, missingMessage) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.JavaScript, "termrest-no-such-runtime", _ct);
        Assert.False(missingOk);
        Assert.StartsWith("termrest-no-such-runtime could not be started", missingMessage, StringComparison.Ordinal);

        var found = await ScriptRuntimeLocator.FindAsync(ScriptLanguage.JavaScript, directory, runtime => runtime, _ct);
        if (found is not null || node is "node" or "bun") // TERMREST_TEST_JAVASCRIPT may name one that is not on PATH
        {
            Assert.NotNull(found);
            Assert.Contains(found.Value.Runtime, (string[])["node", "bun"]);
        }
    }
}
