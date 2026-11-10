using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>PowerShell scripts, run by the user's PowerShell (7 or later) with the $tr object.</summary>
public class PowerShellScriptingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HttpRequestSpec PowerShell(string url, string preRequest = "", string test = "") =>
        new() { Url = url, Scripts = new ScriptSet(preRequest, test, ScriptLanguage.PowerShell) };

    private static (ApiWorkspace Workspace, PostmanCollection Collection) NewWorkspace()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        return (workspace, workspace.CreateCollection("API"));
    }

    [Fact]
    public async Task PowerShell_scripts_run_around_a_request_with_the_tr_object()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "base", Value = server.Url("echo") }]);
        environment.Save();
        workspace.ActiveEnvironment = environment;
        collection.SetVariables([new KeyValueEntry { Key = "who", Value = "Ada" }]);
        collection.SetScripts(collection.RootNode, new ScriptSet("""
            $tr.Request.Headers.Add('X-Trace', 'collection')
            $tr.Environment.Set('token', 'abc-123')
            $tr.Environment.Set('Token', 'names are case-sensitive')
            """, "", ScriptLanguage.PowerShell));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.SetScripts(folder, new ScriptSet("", "$tr.Test('status is 200', { $tr.AssertEqual(200, $tr.Response.Status) })", ScriptLanguage.PowerShell));
        var request = collection.AddRequest(folder, "Create", new HttpRequestSpec
        {
            Url = "{{base}}?q=1",
            Scripts = new ScriptSet(language: ScriptLanguage.PowerShell, preRequest: """
                $tr.Request.Method = 'put'
                $tr.Request.Url += '&added=' + [Math]::Max(0, 1)
                $tr.Request.Body = @{ name = $tr.Variables.ReplaceIn('{{who}}') } | ConvertTo-Json -Compress
                $tr.Request.Headers.Set('Content-Type', 'application/json')
                $hash = [System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($tr.Request.Body))
                $tr.Request.Headers.Set('X-Signature', [Convert]::ToHexString($hash).Substring(0, 8))
                $tr.Variables.Set('started', 42)
                """,
            test: """
                $sent = $tr.Response.Json()
                $tr.Test('the pre-request scripts changed the request', {
                    $tr.AssertEqual('PUT', $sent.method)
                    $tr.AssertEqual(@{ q = '1'; added = '1' }, $sent.query)
                    $tr.AssertEqual('collection', $sent.headers.'x-trace')
                    $tr.AssertEqual('Ada', ($sent.body | ConvertFrom-Json).name)
                    $tr.Assert($sent.headers.'x-signature'.Length -eq 8)
                })
                $tr.Test('variables of this request', {
                    Write-Host 'inside a test'
                    'output of a test block is dropped'
                    $tr.AssertEqual('42', $tr.Variables.Get('started'))
                })
                $tr.Test('fails on purpose', { $tr.AssertEqual(201, $tr.Response.Code) })
                $tr.Test('throws', { throw 'no luck' })
                $tr.CollectionVariables.Set('last_status', $tr.Response.Code)
                $tr.Globals.Set('greeting', 'héllo ✓ 日本')
                $tr.Globals.Set('ratio', 0.5)
                $tr.Globals.Set('flags', [ordered] @{ on = $true; n = 1 })
                Write-Host "token is $($tr.Environment.Get('token')) in $($tr.Info.Environment)"
                'plain output'
                Write-Warning 'careful'
                [Console]::WriteLine('from the console: ünïcödé ✓')
                """),
        });
        collection.Save();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(4, results.Ran);
        Assert.Equal(
            [("status is 200", true), ("the pre-request scripts changed the request", true), ("variables of this request", true), ("fails on purpose", false), ("throws", false)],
            results.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("$tr.Response.Code: expected 201, got 200", results.Tests[3].Error);
        Assert.Equal("no luck", results.Tests[4].Error);
        Assert.Equal("folder 'Users'", results.Tests[0].Script);
        Assert.Equal(["inside a test", "token is abc-123 in dev", "plain output", "WARNING: careful", "from the console: ünïcödé ✓"], results.Logs.Select(l => l.Text));
        Assert.Contains("Environment.token", results.VariablesSet);
        Assert.Contains("Environment.Token", results.VariablesSet);
        Assert.Contains("CollectionVariables.last_status", results.VariablesSet);
        Assert.Contains("Variables.started", results.VariablesSet);
        Assert.Equal("abc-123", runner.Session.For(environment)["token"]);
        Assert.Equal("names are case-sensitive", runner.Session.For(environment)["Token"]);
        Assert.Equal("200", runner.Session.For(collection)["last_status"]);
        Assert.Equal(("héllo ✓ 日本", "0.5", """{"on":true,"n":1}"""), (runner.Session.Globals["greeting"], runner.Session.Globals["ratio"], runner.Session.Globals["flags"]));
        Assert.Equal(["PowerShell"], runner.RunningRuntimes);
        Assert.Equal("{{base}}?q=1", request.ReadSpec().Url); // scripts change what is sent, not the request

        // The runtime stays: the next request's scripts run in it, and see the values set before.
        var again = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"),
            test: "$tr.Test('kept', { $tr.AssertEqual('héllo ✓ 日本', $tr.Globals.Get('greeting')) })"), _ct);
        Assert.True(again.Scripts!.Succeeded, again.Scripts.Describe());
        Assert.Equal(["kept"], again.Scripts.Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Parse_errors_name_the_script_and_line_and_do_not_stop_other_scripts()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        collection.SetScripts(collection.RootNode, new ScriptSet("Write-Host 'collection ran'", "", ScriptLanguage.PowerShell));
        var broken = collection.AddRequest(collection.RootNode, "Broken",
            PowerShell(server.Url("echo"), preRequest: "$a = 1\n\nif ($a -eq 1 {\n    Write-Host 'x'\n}"));
        var fine = collection.AddRequest(collection.RootNode, "Fine", PowerShell(server.Url("echo"), test: "$tr.Test('ok', { $tr.AssertEqual(200, $tr.Response.Status) })"));
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        var exchange = await runner.SendAsync(collection, broken, broken.ReadSpec(), null, _ct);

        Assert.Null(exchange.Response);
        var error = exchange.Scripts!.Error!;
        Assert.Equal("request 'Broken'", error.Script);
        Assert.Equal("ParseError: Unexpected token '{' in expression or statement. (line 3)", error.Message);
        Assert.Equal(
            "  File \"request 'Broken'\", line 3\n    if ($a -eq 1 {\nParseError: Unexpected token '{' in expression or statement.\n" +
            "ParseError: Missing closing ')' after expression in 'if' statement.",
            error.Traceback);
        Assert.Equal(["collection ran"], exchange.Scripts.Logs.Select(l => l.Text)); // the scripts before it ran
        Assert.Equal(2, exchange.Scripts.Ran);

        // A script that does not parse does not keep the collection's other requests from running theirs.
        var other = await runner.SendAsync(collection, fine, fine.ReadSpec(), null, _ct);
        Assert.True(other.Scripts!.Succeeded, other.Scripts.Describe());
        Assert.Equal(["collection ran"], other.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["ok"], other.Scripts.Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Errors_show_the_lines_of_the_script()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        // An error: the script's lines in the stack, outermost first, as in Python; what it wrote before is kept.
        var thrown = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: """
            function First($items) {
                return $items[0].Name.ToUpper()
            }

            Write-Host 'before'
            First @()
            """), _ct);
        var error = thrown.Scripts!.Error!;
        Assert.Equal(200, thrown.Response!.StatusCode);
        Assert.Equal("request", error.Script);
        Assert.Equal("RuntimeException: You cannot call a method on a null-valued expression.", error.Message);
        Assert.Equal(
            ["  File \"request\", line 6", "    First @()", "  File \"request\", line 2", "    return $items[0].Name.ToUpper()",
             "RuntimeException: You cannot call a method on a null-valued expression."],
            error.Traceback!.Split('\n'));
        Assert.Equal(["before"], thrown.Scripts.Logs.Select(l => l.Text));

        // Errors that PowerShell would only report stop the script too ($ErrorActionPreference is Stop), and errors of
        // .NET methods show their own exception.
        var missing = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"),
            preRequest: "Get-Item (Join-Path ([IO.Path]::GetTempPath()) 'termrest-no-such-file')\nWrite-Host 'not reached'"), _ct);
        Assert.Null(missing.Response);
        Assert.StartsWith("ItemNotFoundException: Cannot find path ", missing.Scripts!.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(missing.Scripts.Logs);
        var parse = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: "[int]::Parse('x')"), _ct);
        Assert.Equal("FormatException: The input string 'x' was not in a correct format.", parse.Scripts!.Error!.Message);

        // Assertions outside tests fail the script.
        var assertion = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: "$tr.Assert($tr.Response.Status -eq 404)"), _ct);
        Assert.Equal("AssertionException: assertion failed: $tr.Response.Status -eq 404", assertion.Scripts!.Error!.Message);

        // Response headers cannot be changed, nor the request once it is sent.
        var sent = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: "$tr.Request.Headers.Set('X-Late', '1')"), _ct);
        Assert.Equal("InvalidOperationException: These headers cannot be changed: change the request in a pre-request script.", sent.Scripts!.Error!.Message);
    }

    [Fact]
    public async Task Programs_that_scripts_start_cannot_disturb_the_worker()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        // What they write to the console does not get mixed into the answers, and reading input they get nothing (not a
        // request). Their output that the script takes is its output, as in any PowerShell.
        var exchange = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), preRequest: """
            if ($IsWindows) { Start-Process cmd.exe -ArgumentList '/c', 'echo from a child process' -NoNewWindow -Wait }
            else { Start-Process /bin/sh -ArgumentList '-c', 'echo from a child process' -NoNewWindow -Wait }
            $read = if ($IsWindows) { findstr.exe /r '^' } else { /bin/cat }
            Write-Host "a child reads [$read]"
            $taken = if ($IsWindows) { cmd.exe /c echo taken } else { /bin/sh -c 'echo taken' }
            Write-Host "a child's output, taken: $taken"
            [Console]::WriteLine('captured ünïcödé ✓')
            """, test: "$tr.Test('still answers', { })"), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a child reads []", "a child's output, taken: taken", "captured ünïcödé ✓"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["still answers"], exchange.Scripts.Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Only_the_first_language_with_all_the_scripts_runs_and_without_one_the_request_is_not_sent()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        collection.SetScripts(collection.RootNode, new ScriptSet([
            new LanguageScripts(ScriptLanguage.PowerShell, "$tr.Globals.Set('ran', 'collection')", ""),
            new LanguageScripts(ScriptLanguage.CSharp, "tr.Globals.Set(\"ran\", \"csharp\");", ""),
        ]));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.SetScripts(folder, new ScriptSet([
            new LanguageScripts(ScriptLanguage.PowerShell, "", "$tr.Test('folder', { })"),
            new LanguageScripts(ScriptLanguage.Python, "", "tr.test('python', lambda: None)"),
        ]));
        var several = collection.AddRequest(folder, "Several", new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Scripts = new ScriptSet([
                new LanguageScripts(ScriptLanguage.PowerShell, "$tr.Globals.Set('ran', $tr.Globals.Get('ran') + ', request')", ""),
                new LanguageScripts(ScriptLanguage.Python, "tr.globals.set('ran', 'python')", ""),
            ]),
        });
        var incomplete = collection.AddRequest(folder, "Incomplete", new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Scripts = new ScriptSet("tr.globals.set('ran', 'python')", "", ScriptLanguage.Python),
        });

        // Only PowerShell has every script: Python and C# have no runtime here, and are not needed.
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);
        var sent = await runner.SendAsync(collection, several, several.ReadSpec(), null, _ct);

        Assert.True(sent.Scripts!.Succeeded, sent.Scripts.Describe());
        Assert.Equal(3, sent.Scripts.Ran);
        Assert.Equal("collection, request", runner.Session.Globals["ran"]);
        Assert.Equal(["folder"], sent.Scripts.Tests.Select(t => t.Name));
        Assert.Equal(["PowerShell"], runner.RunningRuntimes);
        Assert.Equal(
            ["Language: PowerShell, the first of your language order with all the scripts of this request (Python lacks the pre-request script of " +
             "collection 'API'; C# lacks the pre-request script of request 'Users/Several', the post-response script of folder 'Users')."],
            sent.Scripts.Notes);

        // When no language has them all, nothing runs and the request is not sent.
        var refused = await runner.SendAsync(collection, incomplete, incomplete.ReadSpec(), null, _ct);
        Assert.Null(refused.Response);
        Assert.Equal(0, refused.Scripts!.Ran);
        Assert.Equal(
            "Not sent: No language has all the scripts this request runs: Python lacks the pre-request script of collection 'API'; C# lacks the " +
            "pre-request script of request 'Users/Incomplete', the post-response script of folder 'Users'; PowerShell lacks the pre-request script of " +
            "request 'Users/Incomplete'. Write the missing ones in one language, choose a language in the request's Scripts tab, or turn pre-request or " +
            "post-response scripts off there.",
            refused.ErrorMessage);
        Assert.Equal("collection, request", runner.Session.Globals["ran"]);
    }

    [Fact]
    public async Task PowerShell_is_found_and_must_be_PowerShell_7_or_later()
    {
        var pwsh = TestPowerShell.Require();
        var (ok, message) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, pwsh, Path.GetTempPath(), _ct);
        Assert.True(ok, message);
        Assert.Matches(@"^PowerShell \d+\.\d+\.\d+\S* · \S", message);
        var (missing, why) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, "no-such-pwsh-here", Path.GetTempPath(), _ct);
        Assert.False(missing);
        Assert.Contains("could not be started", why, StringComparison.Ordinal);
        Assert.NotNull(await ScriptRuntimeLocator.FindAsync(ScriptLanguage.PowerShell, TestFiles.NewTempDirectory(), r => r, _ct));

        // Windows PowerShell (5.1) is not PowerShell 7.
        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (OperatingSystem.IsWindows() && File.Exists(windowsPowerShell))
        {
            var (old, text) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, windowsPowerShell, Path.GetTempPath(), _ct);
            Assert.False(old);
            Assert.Matches(@"^PowerShell 5\.1\.\S+ is too old: scripts need PowerShell 7\.0 or later \(.+powershell\.exe\)\.$", text);
        }
    }
}
