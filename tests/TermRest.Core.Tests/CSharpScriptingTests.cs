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
        var exchange = await runner.SendAsync(collection, null, CSharp(server.Url("echo"), preRequest: "using System.Diagnostics;\n" + Fresh() + """

            var echo = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", ["/c", "echo from a child process"]) : new ProcessStartInfo("/bin/sh", ["-c", "echo from a child process"]);
            Process.Start(echo).WaitForExit();
            var reader = OperatingSystem.IsWindows() ? new ProcessStartInfo("findstr.exe", ["/r", "^"]) : new ProcessStartInfo("/bin/cat");
            reader.RedirectStandardOutput = true;
            using var child = Process.Start(reader);
            var read = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            Console.WriteLine($"a child reads [{read.Trim()}]");
            Console.WriteLine("captured ünïcödé ✓");
            """, test: "tr.Test(\"still answers\", () => { });"), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());
        Assert.Equal(["a child reads []", "captured ünïcödé ✓"], exchange.Scripts.Logs.Select(l => l.Text));
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
    public async Task The_dotnet_sdk_is_found_and_must_be_dotnet_10_or_later()
    {
        var dotnet = TestCSharp.Require();
        var (ok, message) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.CSharp, dotnet, Path.GetTempPath(), _ct);
        Assert.True(ok, message);
        Assert.Matches(@"^\.NET SDK \d+\.\d+\.\d+\S* · ", message);
        var (missing, why) = await ScriptRuntimeLocator.CheckAsync(ScriptLanguage.CSharp, "no-such-dotnet-here", Path.GetTempPath(), _ct);
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
    }
}
