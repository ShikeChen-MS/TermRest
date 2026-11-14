using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>C# scripts, built and run by the user's .NET SDK (10 or later) with the tr object.</summary>
public class CSharpScriptingTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HttpRequestSpec CSharp(string url, string preRequest = "", string test = "") =>
        new() { Url = url, Scripts = new ScriptSet(preRequest, test, ScriptLanguage.CSharp) };

    private static (ApiWorkspace Workspace, PostmanCollection Collection) NewWorkspace()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        return (workspace, workspace.CreateCollection("API"));
    }

    /// <summary>A comment that makes a program no test has built yet (built programs are kept in the cache).</summary>
    private static string Fresh() => "// " + Guid.NewGuid().ToString("N");

    /// <summary>A dotnet that runs <paramref name="dotnet"/> and writes each command line it gets to the returned file.</summary>
    private static (string Dotnet, string Commands) LoggingDotnet(string dotnet)
    {
        var folder = TestFiles.NewTempDirectory();
        var real = ScriptRuntimeLocator.Executable(dotnet)!;
        var wrapper = Path.Combine(folder, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet");
        File.WriteAllText(wrapper, OperatingSystem.IsWindows()
            ? $"@echo %*>>\"%~dp0commands.txt\"\r\n@\"{real}\" %*\r\n"
            : $"#!/bin/sh\necho \"$*\" >> \"$(dirname \"$0\")/commands.txt\"\nexec \"{real}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return (wrapper, Path.Combine(folder, "commands.txt"));
    }

    [Fact]
    public async Task CSharp_scripts_run_around_a_request_with_the_tr_object()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "base", Value = server.Url("echo") }]);
        environment.Save();
        workspace.ActiveEnvironment = environment;
        collection.SetVariables([new KeyValueEntry { Key = "who", Value = "Ada" }]);
        collection.SetScripts(collection.RootNode, new ScriptSet(Fresh() + """

            tr.Request.Headers.Add("X-Trace", "collection");
            tr.Environment["token"] = "abc-123";
            """, "", ScriptLanguage.CSharp));
        var folder = collection.AddFolder(collection.RootNode, "Users");
        collection.SetScripts(folder, new ScriptSet("", """tr.Test("status is 200", () => tr.AssertEqual(200, tr.Response.Status));""", ScriptLanguage.CSharp));
        var request = collection.AddRequest(folder, "Create", new HttpRequestSpec
        {
            Url = "{{base}}?q=1",
            Scripts = new ScriptSet(language: ScriptLanguage.CSharp, preRequest: """
                using System.Security.Cryptography;
                using static System.Math;
                using Json = System.Text.Json.JsonSerializer;

                tr.Request.Method = "put";
                tr.Request.Url += "&added=" + Max(0, 1);
                tr.Request.Body = Json.Serialize(new { name = tr.Variables.ReplaceIn("{{who}}") });
                tr.Request.Headers.Set("Content-Type", "application/json");
                tr.Request.Headers["X-Signature"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tr.Request.Body)))[..8];
                tr.Variables.Set("started", 42);
                await Task.Delay(1);
                """,
            test: """
                var sent = tr.Response.Json();
                tr.Test("the pre-request scripts changed the request", () =>
                {
                    tr.AssertEqual("PUT", sent["method"]);
                    tr.AssertEqual(new Dictionary<string, string> { ["q"] = "1", ["added"] = "1" }, sent["query"]);
                    tr.AssertEqual("collection", sent["headers"]["x-trace"]);
                    tr.AssertEqual("Ada", JsonNode.Parse((string)sent["body"])["name"]);
                    tr.Assert(((string)sent["headers"]["x-signature"]).Length == 8);
                });
                tr.Test("variables of this request", () => tr.AssertEqual("42", tr.Variables["started"]));
                tr.Test("fails on purpose", () => tr.AssertEqual(201, tr.Response.Code));
                tr.Test("async", async () =>
                {
                    await Task.Delay(5);
                    tr.Assert(tr.Response.Time >= 0 && tr.Response.Headers.Has("content-type"));
                });
                tr.CollectionVariables.Set("last_status", tr.Response.Code);
                Console.WriteLine($"token is {tr.Environment["token"]} in {tr.Info.Environment}");
                """),
        });
        collection.Save();
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal(4, results.Ran);
        Assert.Equal(
            [("status is 200", true), ("the pre-request scripts changed the request", true), ("variables of this request", true), ("fails on purpose", false), ("async", true)],
            results.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("tr.Response.Code: expected 201, got 200", results.Tests[3].Error);
        Assert.Equal("folder 'Users'", results.Tests[0].Script);
        Assert.Equal(["token is abc-123 in dev"], results.Logs.Select(l => l.Text));
        Assert.Contains("Environment.token", results.VariablesSet);
        Assert.Contains("CollectionVariables.last_status", results.VariablesSet);
        Assert.Contains("Variables.started", results.VariablesSet);
        Assert.Equal("abc-123", runner.Session.For(environment)["token"]);
        Assert.Equal("200", runner.Session.For(collection)["last_status"]);
        Assert.Contains(results.Notes, n => n.StartsWith("The C# scripts were built", StringComparison.Ordinal));
        Assert.Equal([".NET"], runner.RunningRuntimes);
        Assert.Equal("{{base}}?q=1", request.ReadSpec().Url); // scripts change what is sent, not the request

        // The program was kept: the next TermRest starts it without building.
        using var next = new RequestRunner(workspace);
        next.SetCSharpRuntime(dotnet);
        var again = await next.SendAsync(collection, request, request.ReadSpec(), null, _ct);
        Assert.True(again.Scripts!.Error is null, again.Scripts.Describe());
        Assert.Equal(5, again.Scripts.Tests.Count);
        Assert.DoesNotContain(again.Scripts.Notes, n => n.StartsWith("The C# scripts were built", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compile_errors_name_the_script_and_line_and_do_not_stop_other_scripts()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        collection.SetScripts(collection.RootNode, new ScriptSet("Console.WriteLine(\"collection ran\"); " + Fresh(), "", ScriptLanguage.CSharp));
        var broken = collection.AddRequest(collection.RootNode, "Broken",
            CSharp(server.Url("echo"), preRequest: "using System.Text;\n\nvar n = 1;\nint text = \"words\";\nConsole.WriteLine(n + text);"));
        var fine = collection.AddRequest(collection.RootNode, "Fine", CSharp(server.Url("echo"), test: "tr.Test(\"ok\", () => tr.AssertEqual(200, tr.Response.Status));"));
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        var exchange = await runner.SendAsync(collection, broken, broken.ReadSpec(), null, _ct);

        Assert.Null(exchange.Response);
        var error = exchange.Scripts!.Error!;
        Assert.Equal("request 'Broken'", error.Script);
        Assert.Equal("CS0029: Cannot implicitly convert type 'string' to 'int' (line 4)", error.Message);
        Assert.Contains("File \"request 'Broken'\", line 4, column ", error.Traceback, StringComparison.Ordinal);
        Assert.Contains("    int text = \"words\";", error.Traceback, StringComparison.Ordinal);
        Assert.Equal(["collection ran"], exchange.Scripts.Logs.Select(l => l.Text)); // the scripts before it ran
        Assert.Equal(2, exchange.Scripts.Ran);

        // A script that does not compile does not keep the collection's other requests from running theirs.
        var other = await runner.SendAsync(collection, fine, fine.ReadSpec(), null, _ct);
        Assert.True(other.Scripts!.Succeeded, other.Scripts.Describe());
        Assert.Equal(["collection ran"], other.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["ok"], other.Scripts.Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Errors_show_the_lines_of_the_script()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        // An exception: the script's lines in the stack, outermost first, as in Python.
        var thrown = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), test: "using System.Text;\n" + Fresh() + """

            var items = new List<int>();
            int First() => items[0];
            Console.WriteLine("before");
            Console.WriteLine(First());
            """), _ct);
        var error = thrown.Scripts!.Error!;
        Assert.Equal(200, thrown.Response!.StatusCode);
        Assert.Equal("request", error.Script);
        Assert.StartsWith("ArgumentOutOfRangeException: Index was out of range.", error.Message, StringComparison.Ordinal);
        Assert.Equal(["  File \"request\", line 6", "    Console.WriteLine(First());", "  File \"request\", line 4", "    int First() => items[0];"],
            error.Traceback!.Split('\n').Take(4));
        Assert.Equal(["before"], thrown.Scripts.Logs.Select(l => l.Text));

        // A script that does not end: building it alone finds it.
        var unclosed = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), preRequest: Fresh() + "\nif (tr.Request.Method == \"GET\")\n{\n    Console.WriteLine(1);\n"), _ct);
        Assert.Null(unclosed.Response);
        Assert.Equal("request", unclosed.Scripts!.Error!.Script);
        Assert.Equal("CS1513: } expected (at the end of the script)", unclosed.Scripts.Error.Message);

        // Assertions outside tests fail the script.
        var assertion = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), test: Fresh() + "\ntr.Assert(tr.Response.Status == 404);"), _ct);
        Assert.Equal("AssertionException: assertion failed: tr.Response.Status == 404", assertion.Scripts!.Error!.Message);

        // A package that cannot be restored is the script's error too (NU1101 not found, or NU1301 offline).
        var package = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), preRequest: "#:package TermRest.No.Such.Package@0.0.1\n" + Fresh()), _ct);
        Assert.Null(package.Response);
        Assert.Equal("request", package.Scripts!.Error!.Script);
        Assert.StartsWith("NU1", package.Scripts.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Programs_that_scripts_start_cannot_disturb_the_program()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        // Their output does not get mixed into the answers, and reading input they get nothing (not a request).
        var exchange = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), preRequest: "#:property AllowUnsafeBlocks=true\nusing System.Diagnostics;\nusing System.Runtime.InteropServices;\n" + Fresh() + """

            var echo = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", ["/c", "echo from a child process"]) : new ProcessStartInfo("/bin/sh", ["-c", "echo from a child process"]);
            Process.Start(echo).WaitForExit();
            var reader = OperatingSystem.IsWindows() ? new ProcessStartInfo("findstr.exe", ["/r", "^"]) : new ProcessStartInfo("/bin/cat");
            reader.RedirectStandardOutput = true;
            using var child = Process.Start(reader);
            var read = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            Console.WriteLine($"a child reads [{read.Trim()}]");
            Console.WriteLine("captured ünïcödé ✓");
            if (!OperatingSystem.IsWindows())
            {
                unsafe
                {
                    var getsid = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "getsid");
                    tr.Globals["session"] = getsid(0) == Environment.ProcessId ? "its own" : "TermRest's";
                }
            }
            """, test: "tr.Test(\"still answers\", () => { });"), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a child reads []", "captured ünïcödé ✓"], exchange.Scripts.Logs.Select(l => l.Text));

        // Nor can they open TermRest's terminal (/dev/tty) to ask for a password over its screen: the program is in a session
        // of its own, which has none (this test may run without a terminal, so trying to open it would tell nothing).
        Assert.Equal(OperatingSystem.IsWindows() ? null : "its own", runner.Session.Globals.GetValueOrDefault("session"));
    }

    [Fact]
    public async Task Halves_of_emoji_do_not_break_the_answer()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        // A line cut at 4,000 characters, not inside an emoji, and a script's own substring of one (not text: it becomes U+FFFD).
        var exchange = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), test: Fresh() + """

            Console.WriteLine(new string('a', 3999) + "😀");
            var half = "😀"[..1];
            Console.WriteLine("half " + half + " and a whole 😀");
            tr.Test("test " + half, () => { });
            tr.Globals.Set("name " + half, half);
            """), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal([new string('a', 3999), "half \uFFFD and a whole 😀"], exchange.Scripts.Logs.Select(l => l.Text));
        Assert.Equal(["test \uFFFD"], exchange.Scripts.Tests.Select(t => t.Name));
        Assert.Equal("\uFFFD", runner.Session.Globals["name \uFFFD"]);
    }

    [Fact]
    public async Task A_global_using_applies_to_the_whole_program()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        // C# allows global usings only before the namespaces each script gets.
        var exchange = await runner.SendAsync(collection, null, CSharp(server.Url("echo"),
            preRequest: "global using System.Net;\nglobal using System.Net;\n" + Fresh(),
            test: "global using System.Net;\n" + Fresh() + "\ntr.Test(\"status\", () => tr.Assert(HttpStatusCode.OK == (HttpStatusCode)tr.Response.Code));"), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["status"], exchange.Scripts.Tests.Where(t => t.Passed).Select(t => t.Name));
    }

    [Fact]
    public async Task Dates_uris_and_json_strings_set_in_scripts_are_their_text()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);

        var exchange = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), test: Fresh() + """

            tr.CollectionVariables.Set("site", new Uri("https://example.org/a%20b"));
            tr.CollectionVariables.Set("name", System.Text.Json.JsonSerializer.SerializeToElement("Ada"));
            tr.CollectionVariables.Set("flags", new { on = true });
            tr.CollectionVariables.Set("day", new DateOnly(2024, 1, 2));
            tr.CollectionVariables.Set("at", new TimeOnly(3, 4, 5));
            var order = new JsonObject { ["id"] = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301") };
            tr.CollectionVariables.Set("id", order["id"]);
            tr.Test("a GUID held by JSON is its text", () => tr.AssertEqual("3f2504e0-4f89-11d3-9a0c-0305e82c3301", order["id"]));
            """), _ct);

        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        var stored = runner.Session.For(collection);
        Assert.Equal(("https://example.org/a%20b", "Ada", """{"on":true}"""), (stored["site"], stored["name"], stored["flags"]));
        Assert.Equal(("2024-01-02", "03:04:05.0000000", "3f2504e0-4f89-11d3-9a0c-0305e82c3301"), (stored["day"], stored["at"], stored["id"]));
    }

    [Fact]
    public async Task Requests_in_different_languages_run_in_their_own_runtimes()
    {
        var dotnet = TestCSharp.Require();
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.SetPythonInterpreter(python);
        runner.SetCSharpRuntime(dotnet);

        var csharp = await runner.SendAsync(collection, null, CSharp(server.Url("echo"),
            preRequest: Fresh() + "\ntr.Request.Headers.Set(\"X-From\", \"cs\");",
            test: "tr.Test(\"csharp\", () => tr.AssertEqual(\"cs\", tr.Response.Json()[\"headers\"][\"x-from\"]));"), _ct);
        var python3 = await runner.SendAsync(collection, null,
            new HttpRequestSpec { Url = server.Url("echo"), Scripts = new ScriptSet("", "tr.test('python', lambda: None)", ScriptLanguage.Python) }, _ct);

        Assert.True(csharp.Scripts!.Succeeded, csharp.Scripts.Describe());
        Assert.Equal(["csharp"], csharp.Scripts.Tests.Select(t => t.Name));
        Assert.True(python3.Scripts!.Succeeded, python3.Scripts.Describe());
        Assert.Equal(["python"], python3.Scripts.Tests.Select(t => t.Name));
        Assert.Equal([".NET", "Python"], runner.RunningRuntimes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Stopping_scripts_while_they_are_built_leaves_no_program_running()
    {
        var dotnet = TestCSharp.Require();
        using var worker = ScriptWorker.For(ScriptLanguage.CSharp, dotnet, TestFiles.NewTempDirectory());
        var message = new JsonObject
        {
            ["phase"] = "test",
            ["scripts"] = new JsonArray(new JsonObject { ["name"] = "request", ["code"] = Fresh() + "\nConsole.WriteLine(1);" }),
        };

        // Tools › Scripts › Stop scripts while the scripts are built: the program does not start once they are.
        var run = worker.RunAsync(message, TimeSpan.FromSeconds(30), cancellationToken: _ct);
        await Task.Delay(300, _ct);
        Assert.False(run.IsCompleted);
        worker.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => run);
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task A_program_that_runs_stays_whole_when_it_is_built_again()
    {
        var dotnet = TestCSharp.Require();
        var workspace = TestFiles.NewTempDirectory();
        var code = Fresh() + "\ntr.Test(\"ran\", () => { });";
        JsonObject Message() => new() { ["phase"] = "test", ["scripts"] = new JsonArray(new JsonObject { ["name"] = "request", ["code"] = code }) };

        // A TermRest runs the program while another forgets it (its own run of it ended before it answered) and builds it again.
        using var running = ScriptWorker.For(ScriptLanguage.CSharp, dotnet, workspace);
        await running.RunAsync(Message(), TimeSpan.FromSeconds(30), cancellationToken: _ct);
        Assert.True(running.IsRunning);
        var programs = new CSharpPrograms(dotnet, workspace);
        var (program, _) = await programs.PrepareAsync(Message(), [], _ct);
        var folder = Path.GetDirectoryName(program)!;
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        CSharpPrograms.Forget(program);

        var (again, _) = await programs.PrepareAsync(Message(), [], _ct);

        Assert.Equal(program, again);
        Assert.Equal(files, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        using var next = ScriptWorker.For(ScriptLanguage.CSharp, dotnet, workspace);
        var reply = await next.RunAsync(Message(), TimeSpan.FromSeconds(30), cancellationToken: _ct);
        Assert.Equal("ran", reply["tests"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_script_of_the_collection_that_does_not_build_is_left_out_at_once_the_next_time()
    {
        var (dotnet, commands) = LoggingDotnet(TestCSharp.Require());
        var programs = new CSharpPrograms(dotnet, TestFiles.NewTempDirectory());
        var mine = Fresh() + "\ntr.Test(\"ran\", () => { });";
        var theirs = "#:project no-such-project\n" + Fresh(); // another request's script, whose project is not there
        ScriptCode[] collection = [new("", mine, ScriptLanguage.CSharp), new("", theirs, ScriptLanguage.CSharp)];
        JsonObject Message(string code) => new() { ["phase"] = "test", ["scripts"] = new JsonArray(new JsonObject { ["name"] = "request", ["code"] = code }) };
        int Builds() => File.Exists(commands) ? File.ReadLines(commands).Count(l => l.StartsWith("run ", StringComparison.Ordinal)) : 0;
        var (alone, _) = await programs.PrepareAsync(Message(mine), [], _ct);

        // The collection's program does not build: the script runs in its own program, built before, so nothing was built.
        var (program, note) = await programs.PrepareAsync(Message(mine), collection, _ct);
        var builds = Builds();
        var (again, noteAgain) = await programs.PrepareAsync(Message(mine), collection, _ct);

        Assert.Equal(alone, program);
        Assert.Null(note);
        Assert.Equal(alone, again); // without building the collection's program again
        Assert.Null(noteAgain);
        Assert.Equal(builds, Builds());

        // The script that does not build fails with its own error, built again each time it runs (it may have been fixed).
        var first = Message(theirs);
        var (_, firstNote) = await programs.PrepareAsync(first, collection, _ct);
        builds = Builds();
        var second = Message(theirs);
        await programs.PrepareAsync(second, collection, _ct);
        Assert.Contains("no-such-project", first["scripts"]![0]!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("no-such-project", second["scripts"]![0]!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(Builds() > builds);
        Assert.DoesNotContain("start at once next time", firstNote ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directives_of_another_script_of_the_collection_do_not_break_the_scripts_that_run()
    {
        var (dotnet, commands) = LoggingDotnet(TestCSharp.Require());
        using var worker = ScriptWorker.For(ScriptLanguage.CSharp, dotnet, TestFiles.NewTempDirectory());
        var mine = Fresh() + "\nvar unused = 1;\ntr.Test(\"ok\", () => { });";
        var theirs = "#:property TreatWarningsAsErrors=true\n" + Fresh(); // another request's: it makes the warning in mine (CS0219) an error
        ScriptCode[] collection = [new("", mine, ScriptLanguage.CSharp), new("", theirs, ScriptLanguage.CSharp)];
        JsonObject Message() => new() { ["phase"] = "test", ["scripts"] = new JsonArray(new JsonObject { ["name"] = "request", ["code"] = mine }) };
        int Builds() => File.ReadLines(commands).Count(l => l.StartsWith("run ", StringComparison.Ordinal));

        var reply = await worker.RunAsync(Message(), TimeSpan.FromSeconds(30), collection, _ct);
        var builds = Builds();
        var again = await worker.RunAsync(Message(), TimeSpan.FromSeconds(30), collection, _ct);

        Assert.Null(reply["error"]);
        Assert.Equal("ok", reply["tests"]![0]!["name"]!.GetValue<string>());
        Assert.Null(again["error"]);
        Assert.Equal(builds, Builds()); // the program with the other script is not built again to fail
    }

    [Fact]
    public async Task The_compiler_server_is_stopped_where_the_scripts_are_built()
    {
        var dotnet = TestCSharp.Require();
        var message = new JsonObject { ["phase"] = "test", ["scripts"] = new JsonArray(new JsonObject { ["name"] = "request", ["code"] = Fresh() }) };
        await new CSharpPrograms(dotnet, TestFiles.NewTempDirectory()).PrepareAsync(message, [], _ct); // this TermRest built scripts

        // Not in TermRest's current folder, whose global.json would choose the SDK: this dotnet tells where it runs.
        var folder = TestFiles.NewTempDirectory();
        var fake = Path.Combine(folder, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet");
        await File.WriteAllTextAsync(fake, OperatingSystem.IsWindows()
            ? "@cd > \"%~dp0where.tmp\"\r\n@move /y \"%~dp0where.tmp\" \"%~dp0where.txt\" > nul\r\n"
            : "#!/bin/sh\nhere=$(dirname \"$0\")\npwd > \"$here/where.tmp\" && mv \"$here/where.tmp\" \"$here/where.txt\"\n", _ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        CSharpPrograms.StopCompilerServer(fake);

        var where = Path.Combine(folder, "where.txt");
        for (var i = 0; i < 200 && !File.Exists(where); i++)
        {
            await Task.Delay(50, _ct);
        }

        Assert.EndsWith(Path.Combine("csharp", "source"), (await File.ReadAllTextAsync(where, _ct)).Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_to_included_files_build_the_scripts_again()
    {
        var dotnet = TestCSharp.Require();
        var (_, sdk) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.CSharp, dotnet, _ct);
        if (!Version.TryParse(sdk.Split(' ')[2].Split('-')[0], out var version) || version < new Version(10, 0, 300))
        {
            Assert.Skip($"#:include needs the .NET SDK 10.0.300 or later ({sdk}).");
        }

        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var helpers = Path.Combine(workspace.RootPath, "helpers.cs");
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);
        var spec = CSharp(server.Url("echo"), test: "#:include helpers.cs\n" + Fresh() + "\ntr.Test(Helpers.Name, () => { });");
        async Task<ScriptResults> Run(string name)
        {
            await File.WriteAllTextAsync(helpers, $"static class Helpers {{ public static string Name => {name}; }}", _ct);
            return (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        }

        Assert.Equal(["one"], (await Run("\"one\"")).Tests.Select(t => t.Name));
        Assert.Equal(["two!"], (await Run("\"two!\"")).Tests.Select(t => t.Name));

        // A compile error in the file names the file and its position, and is not reported again once it is fixed.
        Assert.Equal("CS0103: The name 'three' does not exist in the current context (helpers.cs, line 1, column 53)", (await Run("three")).Error!.Message);
        Assert.Equal(["three"], (await Run("\"three\"")).Tests.Select(t => t.Name));
    }

    [Fact]
    public async Task Changes_to_a_referenced_project_build_the_scripts_again()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var project = Path.Combine(workspace.RootPath, "lib");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Lib.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""", _ct);
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);
        var spec = CSharp(server.Url("echo"), test: "#:project lib/Lib.csproj\n" + Fresh() + "\ntr.Test(Names.Name, () => { });");
        async Task<ScriptResults> Run(string name)
        {
            await File.WriteAllTextAsync(Path.Combine(project, "Names.cs"), $"public static class Names {{ public static string Name => \"{name}\"; }}", _ct);
            return (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        }

        Assert.Equal(["one"], (await Run("one")).Tests.Select(t => t.Name));
        Assert.Equal(["two!"], (await Run("two!")).Tests.Select(t => t.Name));

        // What builds write in the project's folder (bin, obj) is no change: the program is not built again; nor is what the
        // program of the project writes there when it runs (a database, logs).
        await File.WriteAllTextAsync(Path.Combine(project, "app.db"), "rows", _ct);
        Directory.CreateDirectory(Path.Combine(project, "logs"));
        await File.WriteAllTextAsync(Path.Combine(project, "logs", "today.txt"), "GET /users 200", _ct);
        var again = (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        Assert.Equal(["two!"], again.Tests.Select(t => t.Name));
        Assert.DoesNotContain(again.Notes, n => n.StartsWith("The C# scripts were built", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changes_to_the_projects_and_settings_that_a_referenced_project_uses_build_the_scripts_again()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var api = Path.Combine(workspace.RootPath, "src", "Api");
        var contracts = Path.Combine(workspace.RootPath, "src", "Contracts");
        Directory.CreateDirectory(api);
        Directory.CreateDirectory(contracts);
        await File.WriteAllTextAsync(Path.Combine(api, "Api.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Contracts\Contracts.csproj" /></ItemGroup>
            </Project>
            """, _ct);
        await File.WriteAllTextAsync(Path.Combine(contracts, "Contracts.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""", _ct);
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);
        var spec = CSharp(server.Url("echo"), test: "#:project src/Api/Api.csproj\n" + Fresh() + "\ntr.Test(Names.Name, () => { });");
        async Task<ScriptResults> Run(string name)
        {
            await File.WriteAllTextAsync(Path.Combine(contracts, "Names.cs"),
                $"public static class Names\n{{\n#if LOUD\n    public static string Name => \"{name.ToUpperInvariant()}\";\n#else\n    public static string Name => \"{name}\";\n#endif\n}}\n", _ct);
            return (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        }

        Assert.Equal(["one"], (await Run("one")).Tests.Select(t => t.Name));

        // The code of a project that the referenced one references (src/Contracts, beside it).
        Assert.Equal(["two!"], (await Run("two!")).Tests.Select(t => t.Name));

        // MSBuild's settings in a folder above the projects (the workspace's).
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "Directory.Build.props"),
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);LOUD</DefineConstants></PropertyGroup></Project>", _ct);
        var settings = (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        Assert.Equal(["TWO!"], settings.Tests.Select(t => t.Name));

        // The other files of the folders above are no change.
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, "app.db"), "rows", _ct);
        var again = (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        Assert.Equal(["TWO!"], again.Tests.Select(t => t.Name));
        Assert.DoesNotContain(again.Notes, n => n.StartsWith("The C# scripts were built", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_reached_through_a_symbolic_link_builds_with_the_projects_it_references()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs Developer Mode or an administrator on Windows.");
        var dotnet = TestCSharp.Require();
        await using var server = await EchoServer.StartAsync();
        var real = TestFiles.NewTempDirectory();
        var contracts = Path.Combine(real, "src", "Contracts");
        Directory.CreateDirectory(Path.Combine(real, "src", "Api"));
        Directory.CreateDirectory(contracts);
        await File.WriteAllTextAsync(Path.Combine(real, "src", "Api", "Api.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Contracts\Contracts.csproj" /></ItemGroup>
            </Project>
            """, _ct);
        await File.WriteAllTextAsync(Path.Combine(contracts, "Contracts.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""", _ct);
        var link = Path.Combine(TestFiles.NewTempDirectory(), "workspace");
        Directory.CreateSymbolicLink(link, real);
        var workspace = ApiWorkspace.Open(link);
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.SetCSharpRuntime(dotnet);
        async Task<ScriptResults> Run(string name)
        {
            await File.WriteAllTextAsync(Path.Combine(contracts, "Names.cs"), $"public static class Names {{ public static string Name => {name}; }}", _ct);
            var spec = CSharp(server.Url("echo"), test: "#:project src/Api/Api.csproj\n" + Fresh() + "\ntr.Test(Names.Name, () => { });");
            return (await runner.SendAsync(collection, null, spec, _ct)).Scripts!;
        }

        // On Linux and macOS (where /var and /tmp are links), NuGet leaves out what a project references when the path to it
        // goes through a link: the scripts would not know Names, from src/Contracts.
        var built = await Run("\"one\"");
        Assert.True(built.Succeeded, built.Describe());
        Assert.Equal(["one"], built.Tests.Select(t => t.Name));

        // An error in those projects names the file in the workspace, as without the link.
        Assert.Contains("(src/Contracts/Names.cs, line 1, column ", (await Run("one")).Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dotnet_sdk_is_found_and_must_be_dotnet_10_or_later()
    {
        var dotnet = TestCSharp.Require();
        var (ok, message) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.CSharp, dotnet, _ct);
        Assert.True(ok, message);
        Assert.Matches(@"^\.NET SDK \d+\.\d+\.\d+\S* · ", message);
        var (missing, why) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.CSharp, "no-such-dotnet-here", _ct);
        Assert.False(missing);
        Assert.Contains("could not be started", why, StringComparison.Ordinal);
        Assert.NotNull(await ScriptRuntimeLocator.FindAsync(ScriptLanguage.CSharp, TestFiles.NewTempDirectory(), r => r, _ct));

        var root = Path.Combine(Path.GetTempPath(), "dotnet");
        var sdk = Path.Combine(root, "sdk");
        Assert.Equal((true, $".NET SDK 10.0.100 · {root}"),
            ScriptRuntimeLocator.DescribeSdks("dotnet", 0, ["8.0.400 [" + sdk + "]", "10.0.100-rc.2.25502.107 [" + sdk + "]", "10.0.100 [" + sdk + "]"], ""));
        Assert.Equal((false, $".NET SDK 9.0.300 is too old: C# scripts need the .NET 10 SDK or later ({root})."),
            ScriptRuntimeLocator.DescribeSdks("dotnet", 0, ["9.0.300 [" + sdk + "]"], ""));
        Assert.Equal((false, "dotnet has no .NET SDK: C# scripts need the .NET 10 SDK or later (a .NET runtime alone cannot build them)."),
            ScriptRuntimeLocator.DescribeSdks("dotnet", 0, [], ""));
    }

    [Fact]
    public void Scripts_become_methods_of_one_program_with_their_directives_on_top()
    {
        var workspace = TestFiles.NewTempDirectory();
        var code = "#:package Humanizer.Core@2.14.1\n#:project ../lib/Lib.csproj\nusing System.Text;\n// a comment\nusing var stream = new MemoryStream();\nConsole.WriteLine(1);";
        var id = CSharpWorkerCode.Id(code);
        var program = CSharpWorkerCode.Generate(new Dictionary<string, string> { [id] = code, [CSharpWorkerCode.Id("#:property Nullable=enable")] = "#:property Nullable=enable" }, workspace);

        Assert.StartsWith("// TermRest writes this program", program, StringComparison.Ordinal);
        Assert.Contains("\n#:package Humanizer.Core@2.14.1\n", program, StringComparison.Ordinal);
        Assert.Contains($"\n#:project {Path.GetFullPath(Path.Combine(workspace, "../lib/Lib.csproj"))}\n", program, StringComparison.Ordinal);
        Assert.Contains("\n#:property Nullable=enable\n", program, StringComparison.Ordinal);
        Assert.DoesNotContain("#:property Nullable=disable", program, StringComparison.Ordinal); // the script's own wins
        Assert.Contains($"#line 3 \"termrest-script-{id}\"\n    using System.Text;\n", program, StringComparison.Ordinal);

        // The body keeps its line numbers: hoisted lines stay, empty; using statements are not directives.
        Assert.Contains($"#line 1 \"termrest-script-{id}\"\n\n\n\n// a comment\nusing var stream = new MemoryStream();\nConsole.WriteLine(1);\n#line default", program, StringComparison.Ordinal);
        Assert.Equal(id, CSharpWorkerCode.ScriptIdOf($"C:\\cache\\source\\termrest-script-{id}"));
        Assert.Null(CSharpWorkerCode.ScriptIdOf("C:\\cache\\source\\termrest-scripts.cs"));

        // Global usings go before the namespaces, once each, mapped to the first script's line.
        var withGlobals = CSharpWorkerCode.Generate(new Dictionary<string, string>
        {
            ["a"] = "using System.Text;\nglobal using System.Net;\nglobal using static System.Math;",
            ["b"] = "global using System.Net;",
        }, workspace);
        var first = withGlobals.IndexOf("\nnamespace ", StringComparison.Ordinal);
        var globals = "\n#line 2 \"termrest-script-a\"\nglobal using System.Net;\n#line 3 \"termrest-script-a\"\nglobal using static System.Math;\n#line default\n";
        Assert.InRange(withGlobals.IndexOf(globals, StringComparison.Ordinal), 0, first);
        Assert.Equal(1, withGlobals.Split("global using System.Net;").Length - 1);
        Assert.Contains("#line 1 \"termrest-script-a\"\n    using System.Text;\n", withGlobals, StringComparison.Ordinal);
    }
}
