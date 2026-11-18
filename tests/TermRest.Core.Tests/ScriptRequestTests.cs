using TermRest.Core.Certificates;
using TermRest.Core.Model;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Requests that scripts send (pm.sendRequest, tr.send_request, tr.SendRequest, $tr.SendRequest) go through TermRest.</summary>
public sealed class ScriptRequestTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TestPki _pki = TestPki.Create();

    public void Dispose() => _pki.Dispose();

    public static TheoryData<string> Languages => [ScriptLanguage.JavaScript, ScriptLanguage.Python, ScriptLanguage.CSharp, ScriptLanguage.PowerShell];

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task A_request_a_script_sends_goes_out_with_the_workspaces_client_certificate(string language)
    {
        var runtime = language switch
        {
            ScriptLanguage.JavaScript => TestJavaScript.Require(),
            ScriptLanguage.Python => TestPython.Require(),
            ScriptLanguage.CSharp => TestCSharp.Require(),
            _ => TestPowerShell.Require(),
        };
        await using var secure = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        await using var plain = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        workspace.Settings.CaBundle = _pki.CaPemPath;
        workspace.Certificates.Entries.Add(new CertificateEntry
        {
            Name = "client",
            Matches = [$"https://localhost:{secure.BaseUri.Port}/*"],
            Pfx = new FileSource { Src = _pki.ClientPfxPath },
            Passphrase = TestPki.Passphrase,
        });
        var collection = workspace.CreateCollection("API");
        collection.SetVariables([new KeyValueEntry { Key = "secure", Value = secure.Url("echo") }]);

        // A POST with a header and a JSON body, then one with a {{variable}} that is sent as written, which fails.
        var script = language switch
        {
            ScriptLanguage.JavaScript => """
                const url = pm.collectionVariables.get('secure') + '?q=1';
                const [response, again] = await Promise.all([
                  pm.sendRequest({ url, method: 'POST', header: { 'X-From': 'script' }, body: { mode: 'raw', raw: { n: 1 } } }),
                  pm.sendRequest(url),
                ]);
                const echo = response.json();
                pm.collectionVariables.set('thumb', echo.clientCertificateThumbprint);
                pm.collectionVariables.set('body', echo.body);
                pm.collectionVariables.set('from', echo.headers['x-from']);
                pm.collectionVariables.set('again', again.json().method);
                try { await pm.sendRequest('{{nothing}}/x'); } catch (error) { pm.collectionVariables.set('error', error.message); }
                """,
            ScriptLanguage.Python => """
                response = tr.send_request(tr.collection_variables.get("secure") + "?q=1", method="POST", headers={"X-From": "script"}, body={"n": 1})
                echo = response.json()
                tr.collection_variables.set("thumb", echo["clientCertificateThumbprint"])
                tr.collection_variables.set("body", echo["body"])
                tr.collection_variables.set("from", echo["headers"]["x-from"])
                try:
                    tr.send_request("{{nothing}}/x")
                except ConnectionError as error:
                    tr.collection_variables.set("error", str(error))
                """,
            ScriptLanguage.CSharp => """
                var response = tr.SendRequest(new
                {
                    url = tr.CollectionVariables.Get("secure") + "?q=1",
                    method = "POST",
                    header = new Dictionary<string, string> { ["X-From"] = "script" },
                    body = new { n = 1 },
                });
                var echo = response.Json();
                tr.CollectionVariables.Set("thumb", (string)echo["clientCertificateThumbprint"]);
                tr.CollectionVariables.Set("body", (string)echo["body"]);
                tr.CollectionVariables.Set("from", (string)echo["headers"]["x-from"]);
                try { await tr.SendRequestAsync("{{nothing}}/x"); } catch (HttpRequestException error) { tr.CollectionVariables.Set("error", error.Message); }
                """,
            _ => """
                $response = $tr.SendRequest(@{ url = $tr.CollectionVariables.Get('secure') + '?q=1'; method = 'POST'; header = @{ 'X-From' = 'script' }; body = @{ n = 1 } })
                $echo = $response.Json()
                $tr.CollectionVariables.Set('thumb', $echo.clientCertificateThumbprint)
                $tr.CollectionVariables.Set('body', $echo.body)
                $tr.CollectionVariables.Set('from', $echo.headers.'x-from')
                try { $null = $tr.SendRequest('{{nothing}}/x') } catch [System.Net.Http.HttpRequestException] { $tr.CollectionVariables.Set('error', $_.Exception.Message) }
                """,
        };
        var request = collection.AddRequest(collection.RootNode, "Echo", new HttpRequestSpec { Url = plain.Url("echo"), Scripts = new ScriptSet(script, "", language) });
        using var runner = new RequestRunner(workspace);
        runner.SetRuntime(language, runtime);

        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(200, exchange.Response?.StatusCode);
        var set = runner.Session.For(collection);
        Assert.Equal(_pki.Client.Thumbprint, set["thumb"]);
        Assert.Equal("""{"n":1}""", set["body"]);
        Assert.Equal("script", set["from"]);
        Assert.Equal("'http://{{nothing}}/x' is not a valid http(s) URL. ({{nothing}} sent as written: " + language switch
        {
            ScriptLanguage.Python => "tr.variables.replace_in(…)",
            ScriptLanguage.CSharp => "tr.Variables.ReplaceIn(…)",
            ScriptLanguage.PowerShell => "$tr.Variables.ReplaceIn(…)",
            _ => "pm.variables.replaceIn(…)",
        } + " replaces variables)", set["error"]);
        var logs = results.Logs.Select(log => log.Text).ToList();
        Assert.Contains(logs, log => log.StartsWith($"POST https://localhost:{secure.BaseUri.Port}/echo?… → 200 OK (", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.StartsWith("GET http://{{nothing}}/x → not sent: 'http://{{nothing}}/x' is not a valid", StringComparison.Ordinal));
        if (language == ScriptLanguage.JavaScript)
        {
            Assert.Equal("GET", set["again"]);
        }
    }

    [Fact]
    public async Task A_request_still_running_when_the_scripts_end_is_cancelled_and_the_next_run_goes_on()
    {
        var node = TestJavaScript.Require();
        using var hold = new ManualResetEventSlim();
        await using var slow = await EchoServer.StartAsync(beforeEcho: () => hold.Wait(TimeSpan.FromSeconds(30)));
        await using var fast = await EchoServer.StartAsync();
        try
        {
            var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
            var collection = workspace.CreateCollection("API");
            collection.SetVariables([new KeyValueEntry { Key = "slow", Value = slow.Url("echo") }, new KeyValueEntry { Key = "fast", Value = fast.Url("echo") }]);
            var failing = collection.AddRequest(collection.RootNode, "Failing", new HttpRequestSpec
            {
                Url = fast.Url("echo"),
                Scripts = new ScriptSet("pm.sendRequest(pm.collectionVariables.get('slow'), () => {});\nthrow new Error('boom');", "", ScriptLanguage.JavaScript),
            });
            var next = collection.AddRequest(collection.RootNode, "Next", new HttpRequestSpec
            {
                Url = fast.Url("echo"),
                Scripts = new ScriptSet("""
                    const response = await pm.sendRequest(pm.collectionVariables.get('fast') + '?n=2');
                    pm.collectionVariables.set('n', response.json().query.n);
                    """, "", ScriptLanguage.JavaScript),
            });
            using var runner = new RequestRunner(workspace);
            runner.SetJavaScriptRuntime(node);

            var started = System.Diagnostics.Stopwatch.StartNew();
            var failed = await runner.SendAsync(collection, failing, failing.ReadSpec(), null, _ct);
            Assert.Contains("boom", failed.Scripts!.Error?.Message, StringComparison.Ordinal);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"{started.Elapsed}: the scripts waited for the request they left");

            // The answer to the cancelled request never comes: the runtime reads the next message as one.
            var exchange = await runner.SendAsync(collection, next, next.ReadSpec(), null, _ct);
            Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
            Assert.Equal("2", runner.Session.For(collection)["n"]);
        }
        finally
        {
            hold.Set();
        }
    }
}
