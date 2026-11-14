using System.Globalization;
using System.Text.Json.Nodes;
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
    public async Task Host_output_keeps_its_place_among_the_script_output_without_colors()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        var exchange = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: """
            Write-Host 'before'
            [pscustomobject] @{ id = 1; name = 'Ada' } | Format-Table id, name | Out-Host
            [Console]::Write('console ')
            [Console]::WriteLine('line')
            Write-Host 'after'
            """), _ct);

        Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
        var lines = exchange.Scripts.Logs.Select(l => l.Text.TrimEnd()).Where(l => l.Length > 0).ToList();
        Assert.Equal("before", lines[0]);
        Assert.Contains("id name", lines); // PowerShell 7.2 and later color table headers on a terminal
        Assert.Equal(["console line", "after"], lines.TakeLast(2));
        Assert.DoesNotContain(lines, l => l.Contains('\u001b', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Console_writes_from_parallel_blocks_and_thread_jobs_reach_the_scripts_output()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        // They run in runspaces of their own, where a PowerShell class's methods can't run: such a console writer made them
        // wait for the script, which waited for them, until the time limit stopped PowerShell.
        var exchange = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: """
            'a', 'b' | ForEach-Object -Parallel { [Console]::WriteLine("parallel $_") } -ThrottleLimit 2
            Start-ThreadJob { [Console]::Error.WriteLine('thread job') } | Receive-Job -Wait -AutoRemoveJob
            Write-Host 'after'
            """), _ct);

        Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
        var lines = exchange.Scripts.Logs.Select(l => l.Text).ToList();
        Assert.Equal(["parallel a", "parallel b", "thread job"], lines.SkipLast(1).Order());
        Assert.Equal("after", lines[^1]);
    }

    [Fact]
    public async Task Guids_dates_chars_and_uris_set_in_scripts_are_their_text()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        var exchange = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), preRequest: """
            $tr.Request.Headers.Set('X-Id', [guid] '3f2504e0-4f89-11d3-9a0c-0305e82c3301')
            $tr.CollectionVariables.Set('when', [DateTimeOffset]::new(2024, 1, 2, 3, 4, 5, [TimeSpan]::Zero))
            $tr.CollectionVariables.Set('letter', [char] 'a')
            $tr.CollectionVariables.Set('site', [uri] 'https://example.org/a%20b')
            $tr.CollectionVariables.Set('flags', [ordered] @{ on = $true })
            $tr.CollectionVariables.Set('day', [DateOnly]::new(2024, 1, 2))
            $tr.CollectionVariables.Set('at', [TimeOnly]::new(3, 4, 5))
            """), _ct);

        Assert.True(exchange.Scripts!.Error is null, exchange.Scripts.Describe());
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", JsonNode.Parse(exchange.Response!.Body)!["headers"]!["x-id"]!.GetValue<string>());
        var stored = runner.Session.For(collection);
        Assert.Equal(("2024-01-02T03:04:05.0000000+00:00", "a", "https://example.org/a%20b", """{"on":true}"""), (stored["when"], stored["letter"], stored["site"], stored["flags"]));
        Assert.Equal(("2024-01-02", "03:04:05.0000000"), (stored["day"], stored["at"]));
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
            if ($IsLinux) {
                $stat = [System.IO.File]::ReadAllText('/proc/self/stat')
                $session = [int] $stat.Substring($stat.LastIndexOf(')') + 2).Split(' ')[3]
                $tr.Globals.Set('session', $(if ($session -eq $PID) { 'its own' } else { "TermRest's" }))
            }
            """, test: "$tr.Test('still answers', { })"), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a child reads []", "a child's output, taken: taken", "captured ünïcödé ✓"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["still answers"], exchange.Scripts.Tests.Select(t => t.Name));

        // Nor can they open TermRest's terminal (/dev/tty) to ask for a password over its screen: PowerShell is in a session
        // of its own, which has none (this test may run without a terminal, so trying to open it would tell nothing).
        Assert.Equal(OperatingSystem.IsLinux() ? "its own" : null, runner.Session.Globals.GetValueOrDefault("session"));
    }

    [Fact]
    public async Task Exit_break_and_continue_end_the_script_not_PowerShell()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        collection.SetScripts(collection.RootNode, new ScriptSet("Write-Host 'collection'\n$tr.Globals.Set('pid', $PID)\nbreak\nWrite-Host 'not reached'", "",
            ScriptLanguage.PowerShell));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.SetScripts(folder, new ScriptSet("if ($true) { continue }\nWrite-Host 'not reached'", "", ScriptLanguage.PowerShell));
        var request = collection.AddRequest(folder, "Exit", PowerShell(server.Url("echo"),
            preRequest: "Write-Host 'request'\nif ($tr.Request.Method -eq 'GET') { exit }\nWrite-Host 'not reached'",
            test: "function Stop { exit 0 }\n$tr.Test('before', { })\nStop\n$tr.Test('not reached', { })"));
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        // Each ends its own script quietly, as exit does a script file (and sys.exit() a Python script): the next ones run.
        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(4, exchange.Scripts.Ran);
        Assert.Equal(["collection", "request"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["before"], exchange.Scripts.Tests.Select(t => t.Name));

        // An exit code other than 0 fails the script, and the same PowerShell keeps running.
        var failed = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), preRequest: "$tr.Globals.Set('again', $PID)\nexit 3"), _ct);
        Assert.Null(failed.Response);
        Assert.Equal(("request", "ExitException: 3"), (failed.Scripts!.Error!.Script, failed.Scripts.Error.Message));
        Assert.Equal(runner.Session.Globals["pid"], runner.Session.Globals["again"]);
    }

    [Fact]
    public async Task PowerShell_keeps_only_the_output_it_shows()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        // A script that writes without end: what is not shown is counted, not kept in memory.
        var exchange = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: """
            'first'
            $before = [GC]::GetTotalMemory($true)
            foreach ($i in 1..200000) { "line $i " + ('x' * 100) }
            foreach ($i in 1..50000) { [Console]::WriteLine("console $i " + ('x' * 100)) }
            $tr.Globals.Set('kept', [GC]::GetTotalMemory($true) - $before)
            """), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(1000, exchange.Scripts.Logs.Count);
        Assert.Equal(("first", "line 999 " + new string('x', 100)), (exchange.Scripts.Logs[0].Text, exchange.Scripts.Logs[^1].Text));
        Assert.Contains("249001 more lines the test scripts printed are not shown.", exchange.Scripts.Notes);
        Assert.True(long.Parse(runner.Session.Globals["kept"]!, CultureInfo.InvariantCulture) < 12_000_000, runner.Session.Globals["kept"]);
    }

    [Fact]
    public async Task Halves_of_emoji_do_not_break_the_answer()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);

        // A line cut at 4,000 characters, not inside an emoji, and a script's own substring of one (not text: it becomes U+FFFD).
        var exchange = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), test: """
            Write-Host (('a' * 3999) + '😀')
            $half = '😀'.Substring(0, 1)
            Write-Host ('half ' + $half + ' and a whole 😀')
            $tr.Test('test ' + $half, { })
            $tr.Globals.Set('name ' + $half, $half)
            """), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal([new string('a', 3999), "half \uFFFD and a whole 😀"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["test \uFFFD"], exchange.Scripts.Tests.Select(t => t.Name));
        Assert.Equal("\uFFFD", runner.Session.Globals["name \uFFFD"]);
    }

    [Fact]
    public async Task A_body_set_to_bytes_or_a_hashtable_is_sent_as_text()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPowerShellRuntime(pwsh);
        async Task<string> SentBody(string preRequest)
        {
            var spec = PowerShell(server.Url("echo"), preRequest: preRequest);
            spec.Method = "POST";
            spec.Body = new RequestBody { Mode = BodyMode.Raw, Raw = "original" };
            var exchange = await runner.SendAsync(collection, null, spec, _ct);
            Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
            return JsonNode.Parse(exchange.Response!.Body)!["body"]!.GetValue<string>();
        }

        Assert.Equal("Zoë ✓", await SentBody("$tr.Request.Body = [Text.Encoding]::UTF8.GetBytes('Zoë ✓')"));
        Assert.Equal("""{"name":"Zoë","n":1}""", await SentBody("$tr.Request.Body = [ordered] @{ name = 'Zoë'; n = 1 }"));

        // A request the scripts left unusable is not sent, and what they did is kept, as in the other languages.
        var broken = await runner.SendAsync(collection, null, PowerShell(server.Url("echo"), preRequest: """
            'before'
            $tr.Test('ran', { })
            $tr.CollectionVariables.Set('kept', 'yes')
            $tr.Request.Headers = $null
            """), _ct);
        Assert.Null(broken.Response);
        Assert.StartsWith("The pre-request scripts left a request that cannot be sent: ", broken.Scripts!.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(["before"], broken.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["ran"], broken.Scripts.Tests.Select(t => t.Name));
        Assert.Equal("yes", runner.Session.For(collection)["kept"]);
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
        var (ok, message) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, pwsh, _ct);
        Assert.True(ok, message);
        Assert.Matches(@"^PowerShell \d+\.\d+\.\d+\S* · \S", message);
        var (missing, why) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, "no-such-pwsh-here", _ct);
        Assert.False(missing);
        Assert.Contains("could not be started", why, StringComparison.Ordinal);
        if (ScriptRuntimeLocator.Candidates(ScriptLanguage.PowerShell, Path.GetTempPath()).Contains(pwsh)) // not one that TERMREST_TEST_POWERSHELL names elsewhere
        {
            Assert.NotNull(await ScriptRuntimeLocator.FindAsync(ScriptLanguage.PowerShell, TestFiles.NewTempDirectory(), r => r, _ct));
        }

        // Windows PowerShell (5.1) is not PowerShell 7.
        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (OperatingSystem.IsWindows() && File.Exists(windowsPowerShell))
        {
            var (old, text) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, windowsPowerShell, _ct);
            Assert.False(old);
            Assert.Matches(@"^PowerShell 5\.1\.\S+ is too old: scripts need PowerShell 7\.0 or later \(.+powershell\.exe\)\.$", text);
        }
    }
}
