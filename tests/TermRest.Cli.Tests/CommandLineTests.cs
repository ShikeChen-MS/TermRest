using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Cli.Tests.Infrastructure;
using TermRest.Core.History;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Cli.Tests;

/// <summary>The termrest commands as scripts and CI use them: exit codes, standard output, and the files they write.</summary>
public sealed class CommandLineTests
{
    private const string Schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    /// <summary>Writes a v2.1 JSON collection of simple requests (name, method, URL) and returns its path.</summary>
    private static string WriteCollection(string directory, string name, IEnumerable<(string Name, string Method, string Url)> requests, JsonObject? auth = null)
    {
        var collection = new JsonObject
        {
            ["info"] = new JsonObject { ["name"] = name, ["schema"] = Schema },
            ["item"] = new JsonArray([.. requests.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["request"] = new JsonObject { ["method"] = r.Method, ["url"] = r.Url } })]),
        };
        if (auth is not null)
        {
            collection["auth"] = auth;
        }

        var path = Path.Combine(directory, name + ".postman_collection.json");
        File.WriteAllText(path, collection.ToJsonString());
        return path;
    }

    /// <summary>Writes files (relative path, content) under <paramref name="directory"/>.</summary>
    private static void WriteFiles(string directory, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static string HttpRequestYaml(string url) => $"$kind: http-request\nurl: {url}\nmethod: GET\n";

    /// <summary>Denies or allows reading a file to the user who runs the tests.</summary>
    private static void SetReadable(string path, bool readable)
    {
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
            if (readable)
            {
                security.RemoveAccessRule(rule);
            }
            else
            {
                security.AddAccessRule(rule);
            }

            file.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, readable ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.None);
        }
    }

    [Fact]
    public async Task Send_curl_prints_no_command_for_a_request_that_cannot_be_sent()
    {
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Bad", "GET;CALC", "https://h/x"), ("Good", "GET", "https://h/x")]);

        var bad = await Termrest.RunAsync(["send", collection, "Bad", "--curl"]);
        var good = await Termrest.RunAsync(["send", collection, "Good", "--curl"]);

        Assert.True(bad.ExitCode == 2, bad.ToString());
        Assert.Empty(bad.Output);
        Assert.Contains("'GET;CALC' is not a valid HTTP method", bad.Error, StringComparison.Ordinal);
        Assert.True(good.ExitCode == 0, good.ToString());
        Assert.StartsWith("curl", good.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_fetches_an_oauth2_token_first_and_a_curl_command_says_it_has_none()
    {
        await using var server = await TestServer.StartAsync();
        static JsonObject Parameter(string key, string value) => new() { ["key"] = key, ["value"] = value, ["type"] = "string" };
        var auth = new JsonObject
        {
            ["type"] = "oauth2",
            ["oauth2"] = new JsonArray(Parameter("grant_type", "client_credentials"), Parameter("accessTokenUrl", server.BaseUrl + "/token"),
                Parameter("clientId", "cli"), Parameter("clientSecret", "{{secret}}")),
        };
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Me", "GET", server.BaseUrl + "/auth")], auth);

        var sent = await Termrest.RunAsync(["send", collection, "Me", "--var", "secret=cli-s3cret", "--verbose", "--no-history"]);
        var curl = await Termrest.RunAsync(["send", collection, "Me", "--var", "secret=cli-s3cret", "--curl"]);

        Assert.True(sent.ExitCode == 0, sent.ToString());
        Assert.Equal($"Bearer {TestServer.Token}", JsonNode.Parse(sent.Text)!["authorization"]!.GetValue<string>());
        Assert.Contains($"* Auth: OAuth 2.0, client credentials: new token from {server.BaseUrl}/token (200 in ", sent.Error, StringComparison.Ordinal);
        Assert.True(curl.ExitCode == 0, curl.ToString());
        Assert.DoesNotContain("Authorization", curl.Text, StringComparison.Ordinal);
        Assert.Contains($"warning: OAuth 2.0: no token was fetched yet, so the command has no access token: TermRest fetches one from {server.BaseUrl}/token when it sends the request.",
            curl.Error, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests); // the token and the request: the cURL command sent nothing
    }

    [Fact]
    public async Task Run_and_send_fail_when_files_of_the_collection_cannot_be_read()
    {
        await using var server = await TestServer.StartAsync();
        var root = TestFiles.NewTempDirectory();
        WriteFiles(root,
            ("postman/collections/Api/.resources/definition.yaml", "<<<<<<< HEAD\n$kind: collection\n=======\n$kind: collection\ndescription: theirs\n>>>>>>> theirs\n"),
            ("postman/collections/Api/Ping.request.yaml", HttpRequestYaml(server.BaseUrl + "/echo/ping")),
            ("postman/collections/Sockets/.resources/definition.yaml", "$kind: collection\n"),
            ("postman/collections/Sockets/Stream.request.yaml", "$kind: websocket-request\nurl: wss://h/stream\n"),
            ("postman/collections/Named/Ping.request.yaml", HttpRequestYaml(server.BaseUrl + "/echo/ping")),
            ("postman/collections/Named/non-HTTP.request.yaml", "<<<<<<< HEAD\n$kind: http-request\n=======\n$kind: http-request\nurl: https://h/theirs\n>>>>>>> theirs\n"));
        var broken = Path.Combine(root, "postman", "collections", "Api");

        var run = await Termrest.RunAsync(["run", broken]);
        var send = await Termrest.RunAsync(["send", broken, "Ping"]);
        var hiddenRequestsOnly = await Termrest.RunAsync(["run", Path.Combine(root, "postman", "collections", "Sockets")]);
        var namedLikeTheNote = await Termrest.RunAsync(["run", Path.Combine(root, "postman", "collections", "Named")]);

        // The request itself passes, but the collection's definition (its auth and scripts) was not read.
        Assert.True(run.ExitCode == 1, run.ToString());
        Assert.Contains("1 passed, 0 failed", run.Error, StringComparison.Ordinal);
        Assert.Contains("definition.yaml", run.Error, StringComparison.Ordinal);
        Assert.True(send.ExitCode == 1, send.ToString());
        Assert.Contains("/echo/ping", send.Text, StringComparison.Ordinal);
        Assert.True(hiddenRequestsOnly.ExitCode == 0, hiddenRequestsOnly.ToString());

        // A file whose name holds "non-HTTP" is no note about hidden requests.
        Assert.True(namedLikeTheNote.ExitCode == 1, namedLikeTheNote.ToString());
        Assert.Contains("non-HTTP.request.yaml", namedLikeTheNote.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Themes_use_never_saves_over_settings_it_cannot_read()
    {
        var config = TestFiles.NewTempDirectory();
        const string user = "{ \"theme\": \"Dracula\", \"pythonInterpreters\": { \"C:\\work\": \"C:\\Python312\\python.exe\" } }";
        File.WriteAllText(Path.Combine(config, "settings.json"), user);
        var workspace = TestFiles.NewTempDirectory();
        const string shared = "{\n<<<<<<< HEAD\n  \"activeEnvironment\": \"dev\",\n=======\n  \"activeEnvironment\": \"prod\",\n>>>>>>> theirs\n  \"proxy\": \"http://proxy:8080\"\n}\n";
        WriteFiles(workspace, (".termrest/settings.json", shared));
        var environment = new Dictionary<string, string> { ["TERMREST_CONFIG_DIR"] = config };

        var forUser = await Termrest.RunAsync(["themes", "use", "Nord", "-w", workspace], environment);
        var forWorkspace = await Termrest.RunAsync(["themes", "use", "Nord", "--only-here", "-w", workspace], environment);
        var listed = await Termrest.RunAsync(["themes", "-w", workspace], environment);

        Assert.True(forUser.ExitCode == 1, forUser.ToString());
        Assert.StartsWith("error: " + Path.Combine(config, "settings.json"), forUser.Error, StringComparison.Ordinal);
        Assert.Equal(user, File.ReadAllText(Path.Combine(config, "settings.json")));
        Assert.True(forWorkspace.ExitCode == 1, forWorkspace.ToString());
        Assert.StartsWith("error: " + Path.Combine(workspace, ".termrest", "settings.json"), forWorkspace.Error, StringComparison.Ordinal);
        Assert.Equal(shared, File.ReadAllText(Path.Combine(workspace, ".termrest", "settings.json")));
        Assert.StartsWith("warning: " + Path.Combine(config, "settings.json"), listed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Certificate_commands_fail_when_the_registry_cannot_be_read()
    {
        var workspace = TestFiles.NewTempDirectory();
        WriteFiles(workspace, (".termrest/certificates.json", "[\n<<<<<<< HEAD\n  { \"name\": \"dev\", \"matches\": [\"https://h/*\"] }\n=======\n]\n"));

        var list = await Termrest.RunAsync(["certs", "list", workspace]);
        var match = await Termrest.RunAsync(["certs", "match", "https://h/x", workspace]);

        Assert.True(list.ExitCode == 1, list.ToString());
        Assert.Contains("certificates.json", list.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("No certificates registered", list.Error, StringComparison.Ordinal);
        Assert.True(match.ExitCode == 1, match.ToString());
        Assert.Contains("certificates.json", match.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_report_masks_secrets_and_is_private()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var uri = new Uri(server.BaseUrl);
        var auth = JsonNode.Parse("""{ "type": "apikey", "apikey": [ { "key": "key", "value": "X-Key" }, { "key": "value", "value": "{{apiKey}}" }, { "key": "in", "value": "query" } ] }""")!.AsObject();
        var collection = WriteCollection(directory, "Api",
        [
            ("Ping", "GET", $"http://ada:pa55-w0rd@{uri.Authority}/echo/ping?token={{{{token}}}}"),
            ("Broken", "GET", "ftp://h/{{apiKey}}"),
            ("No port", "GET", $"http://ada:pw12@{uri.Host}:{{{{port}}}}/echo/ping"), // not valid ({{port}} is unset), so its short password is no known secret
        ], auth);
        var environment = Path.Combine(directory, "ci.postman_environment.json");
        File.WriteAllText(environment, """
            { "name": "ci", "values": [
              { "key": "apiKey", "value": "sk-live-0123456789", "type": "secret", "enabled": true },
              { "key": "token", "value": "tok-abcdef-98765", "type": "default", "enabled": true } ] }
            """);
        var report = Path.Combine(directory, "report.json");

        var run = await Termrest.RunAsync(["run", collection, "-e", environment, "--report-json", report]);

        Assert.True(run.ExitCode == 1, run.ToString()); // Broken is not sent
        var text = File.ReadAllText(report);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-abcdef-98765", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pa55-w0rd", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pw12", text, StringComparison.Ordinal);
        Assert.Contains("is not a valid http(s) URL", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(report));
        }
    }

    [Fact]
    public async Task Run_report_redacts_tests_as_the_recording_does()
    {
        var javaScript = TestJavaScript.Require();
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var collection = Path.Combine(directory, "Api.postman_collection.json");
        File.WriteAllText(collection, $$"""
            { "info": { "name": "Api", "schema": "{{Schema}}" }, "item": [
              { "name": "Login", "request": { "method": "GET", "url": "{{server.BaseUrl}}/token" },
                "event": [ { "listen": "test", "script": { "type": "text/javascript", "exec": [
                  "pm.test('key ' + pm.environment.get('apiKey') + ' ok', () => {});",
                  "pm.test('token', () => pm.expect(pm.response.json().access_token).to.have.lengthOf(32));" ] } } ] } ] }
            """);
        var environment = Path.Combine(directory, "ci.postman_environment.json");
        File.WriteAllText(environment, """{ "name": "ci", "values": [ { "key": "apiKey", "value": "sk-live-0123456789", "type": "secret", "enabled": true } ] }""");
        var report = Path.Combine(directory, "report.json");
        var har = Path.Combine(directory, "run.har");

        var run = await Termrest.RunAsync(["run", collection, "-e", environment, "--javascript", javaScript, "--report-json", report, "--record", har]);

        Assert.True(run.ExitCode == 1, run.ToString()); // the token test fails
        var tests = JsonNode.Parse(File.ReadAllText(report))!["results"]![0]!["tests"]!;
        var recorded = JsonNode.Parse(File.ReadAllText(har))!["log"]!["entries"]![0]!["_termrest"]!["scripts"]!["tests"]!;
        Assert.Equal("key [REDACTED] ok", (string?)tests[0]!["name"]);

        // The token is in no variable: it is redacted because the response has it in a field named like a secret.
        Assert.Equal("expected '[REDACTED]' to have a length of 32 but got 40", (string?)tests[1]!["error"]);
        Assert.Equal((string?)recorded[0]!["name"], (string?)tests[0]!["name"]);
        Assert.Equal((string?)recorded[1]!["error"], (string?)tests[1]!["error"]);
    }

    [Fact]
    public async Task The_password_of_the_system_proxy_is_masked_in_the_error_of_a_tunnel_it_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var proxy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        proxy.Start();
        var url = $"http://ada:P%C3%A4ss%20wort@127.0.0.1:{((System.Net.IPEndPoint)proxy.LocalEndpoint).Port}";
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Secure", "GET", "https://api.example.com/x")]);

        // The system proxy comes from HTTPS_PROXY here; .NET's error names it unescaped ("Päss wort").
        var sending = Termrest.RunAsync(["send", collection, "Secure"], new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = url,
            ["https_proxy"] = url,
            ["NO_PROXY"] = "",
            ["no_proxy"] = "",
        });
        var accepting = proxy.AcceptTcpClientAsync(ct).AsTask();
        if (await Task.WhenAny(accepting, sending) == sending)
        {
            Assert.Fail($"The request did not go through the proxy: {await sending}");
        }

        using (var connection = await accepting)
        {
            var stream = connection.GetStream();
            var request = new byte[16384];
            var read = 0;
            while (request.AsSpan(0, read).IndexOf("\r\n\r\n"u8) < 0 && await stream.ReadAsync(request.AsMemory(read), ct) is > 0 and var count)
            {
                read += count;
            }

            await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
        }

        var send = await sending;
        Assert.True(send.ExitCode == 1, send.ToString());
        Assert.Contains("'http://ada:***@127.0.0.1:", send.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("wort", send.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_path_is_relative_to_the_current_folder_as_the_other_paths_are()
    {
        var javaScript = TestJavaScript.Require();
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var runtime = OperatingSystem.IsWindows() ? "tools/js.cmd" : "tools/js"; // the repository's own, as a .venv's python is
        WriteFiles(directory,
            ("postman/Api.postman_collection.json", $$"""
                { "info": { "name": "Api", "schema": "{{Schema}}" }, "item": [
                  { "name": "Ping", "request": { "method": "GET", "url": "{{server.BaseUrl}}/echo/ping" },
                    "event": [ { "listen": "test", "script": { "type": "text/javascript", "exec": [ "pm.test('ran', () => {});" ] } } ] } ] }
                """),
            (runtime, OperatingSystem.IsWindows() ? $"@\"{javaScript}\" %*\r\n" : $"#!/bin/sh\nexec \"{javaScript}\" \"$@\"\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(directory, runtime), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Not relative to the collection's folder (the workspace).
        var run = await Termrest.RunAsync(["run", "postman/Api.postman_collection.json", "--javascript", runtime], workingDirectory: directory);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("✓ ran", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_does_not_keep_the_responses_it_has_counted()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, "Exports", Enumerable.Range(1, 80).Select(i => ($"Export {i}", "GET", server.BaseUrl + "/large")));
        var report = Path.Combine(directory, "report.json");

        // 80 responses of 1.5 MB (120 MB in all), with a heap of at most 64 MB.
        var run = await Termrest.RunAsync(["run", collection, "--report-json", report], new Dictionary<string, string> { ["DOTNET_GCHeapHardLimit"] = "0x4000000" });

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("80 passed, 0 failed", run.Error, StringComparison.Ordinal);
        Assert.Equal(80, JsonNode.Parse(File.ReadAllText(report))!["results"]!.AsArray().Count);
    }

    [Fact]
    public async Task Run_creates_the_folder_of_the_report_and_reports_a_report_it_cannot_write()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping")]);
        var report = Path.Combine(directory, "out", "report.json");
        var blocked = Path.Combine(collection, "report.json"); // in a "folder" that is a file

        var written = await Termrest.RunAsync(["run", collection, "--report-json", report]);
        var notWritten = await Termrest.RunAsync(["run", collection, "--report-json", blocked]);

        Assert.True(written.ExitCode == 0, written.ToString());
        Assert.Equal("Api", (string?)JsonNode.Parse(File.ReadAllText(report))!["collection"]);
        Assert.True(notWritten.ExitCode == 1, notWritten.ToString());
        Assert.Contains("1 passed, 0 failed", notWritten.Error, StringComparison.Ordinal);
        Assert.Contains("error: The report could not be written to " + blocked, notWritten.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", notWritten.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_run_still_saves_its_recording_and_report_however_long_that_takes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Signals a process, as a cancelled CI job does: Unix only (Ctrl+C needs a console on Windows).");
        }

        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping"), ("Slow", "GET", server.BaseUrl + "/wait")]);
        var har = Path.Combine(directory, "run.har");
        var report = Path.Combine(directory, "report.json");
        using (var mkfifo = Process.Start("mkfifo", [report])!)
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken); // writing the report waits until it is read
        }

        var written = "";
        var run = await Termrest.RunAsync(["run", collection, "--record", har, "--report-json", report], whileRunning: async process =>
        {
            await server.Waiting.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            using (var kill = Process.Start("kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)])!)
            {
                await kill.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            // Longer than System.CommandLine waits for a command after a signal before it ends the process.
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            var reading = Task.Run(() => File.ReadAllText(report));
            if (await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)) != reading)
            {
                // Nothing writes it any more: this ends the reading (sharing the file, as the reader locks it too).
                new FileStream(report, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose();
            }

            written = await reading;
        });

        Assert.True(run.ExitCode == 130, run.ToString());
        Assert.Contains("Run cancelled. 1 passed, 0 failed, 1 skipped", run.Error, StringComparison.Ordinal);
        Assert.Contains("Saving the recording to " + har, run.Error, StringComparison.Ordinal);
        Assert.Contains("/echo/ping", File.ReadAllText(har), StringComparison.Ordinal);
        Assert.True(written.Length > 0, run.ToString());
        Assert.Equal(1, (int?)JsonNode.Parse(written)!["passed"]);
    }

    [Fact]
    public async Task Run_says_a_folder_is_no_collection_only_when_it_is_none()
    {
        var root = TestFiles.NewTempDirectory();
        WriteFiles(root, ("postman/collections/Api/Ping.request.yaml", HttpRequestYaml("https://h/ping")));

        var unknownEnvironment = await Termrest.RunAsync(["run", Path.Combine(root, "postman", "collections", "Api"), "-e", "staging"]);
        var workspace = await Termrest.RunAsync(["run", root]);

        Assert.True(unknownEnvironment.ExitCode == 2, unknownEnvironment.ToString());
        Assert.Contains("Environment 'staging' was not found", unknownEnvironment.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("collection folder", unknownEnvironment.Error, StringComparison.Ordinal);
        Assert.True(workspace.ExitCode == 2, workspace.ToString());
        Assert.Contains($"'{root}' is not a Postman v3 collection folder; pass the collection itself.", workspace.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_environment_name_that_several_environments_have_is_refused()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, "Api", [("Ping", "GET", server.BaseUrl + "/echo/{{from}}")]);

        // As "termrest convert dev.postman_environment.json" leaves them: both named "dev"; the YAML one is loaded first.
        WriteFiles(directory,
            ("dev.postman_environment.json", """{ "name": "dev", "values": [ { "key": "from", "value": "json", "enabled": true } ] }"""),
            ("postman/environments/dev.environment.yaml", "name: dev\nvalues:\n  - key: from\n    value: yaml\n    enabled: true\n"),
            (".termrest/settings.json", """{ "activeEnvironment": "dev", "activeEnvironmentFile": "dev.postman_environment.json" }"""));
        var yaml = Path.Combine(directory, "postman", "environments", "dev.environment.yaml");

        var send = await Termrest.RunAsync(["send", collection, "Ping", "-e", "Dev", "--no-history"]);
        var run = await Termrest.RunAsync(["run", collection, "-e", "dev"]);
        var sent = server.Requests;
        var byFile = await Termrest.RunAsync(["send", collection, "Ping", "-e", yaml, "--no-history"]);
        var active = await Termrest.RunAsync(["send", collection, "Ping", "--no-history"]);

        Assert.True(send.ExitCode == 2, send.ToString());
        Assert.Contains("Environment 'Dev' is ambiguous", send.Error, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(directory, "dev.postman_environment.json"), send.Error, StringComparison.Ordinal);
        Assert.Contains(yaml, send.Error, StringComparison.Ordinal);
        Assert.True(run.ExitCode == 2, run.ToString());
        Assert.Equal(0, sent);
        Assert.True(byFile.ExitCode == 0, byFile.ToString());
        Assert.Contains("/echo/yaml", byFile.Text, StringComparison.Ordinal);

        // Without -e: the active environment, by its file.
        Assert.True(active.ExitCode == 0, active.ToString());
        Assert.Contains("/echo/json", active.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_collection_folder_given_with_a_trailing_separator_is_the_one_used()
    {
        await using var server = await TestServer.StartAsync();
        var root = TestFiles.NewTempDirectory();
        WriteFiles(root,
            ("postman/collections/A/Health.request.yaml", HttpRequestYaml(server.BaseUrl + "/echo/a")),
            ("postman/collections/B/Health.request.yaml", HttpRequestYaml(server.BaseUrl + "/echo/b")));
        var b = Path.Combine(root, "postman", "collections", "B") + Path.DirectorySeparatorChar;

        var send = await Termrest.RunAsync(["send", b, "Health", "--no-history"]);
        var run = await Termrest.RunAsync(["run", b]);

        Assert.True(send.ExitCode == 0, send.ToString());
        Assert.Contains("/echo/b", send.Text, StringComparison.Ordinal);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("1 passed", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_curl_adds_only_to_the_collection_named()
    {
        var directory = TestFiles.NewTempDirectory();
        var good = WriteCollection(directory, "Good", [("Ping", "GET", "https://h/ping")]);
        var broken = Path.Combine(directory, "Broken.postman_collection.json");
        File.WriteAllText(broken, "<<<<<<< HEAD\n{}\n");
        var before = File.ReadAllText(good);

        var intoBroken = await Termrest.RunAsync(["import-curl", broken, "curl https://h/new"]);

        Assert.True(intoBroken.ExitCode == 2, intoBroken.ToString());
        Assert.Contains("Broken.postman_collection.json", intoBroken.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(good));

        var intoGood = await Termrest.RunAsync(["import-curl", good, "curl https://h/new"]);

        Assert.True(intoGood.ExitCode == 0, intoGood.ToString());
        Assert.Contains("https://h/new", File.ReadAllText(good), StringComparison.Ordinal);
        Assert.DoesNotContain("Broken", intoGood.Error, StringComparison.Ordinal); // the other collection is not even read
    }

    [Fact]
    public async Task An_environment_file_that_cannot_be_read_is_reported()
    {
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, "Api", [("Ping", "GET", "https://h/ping")]);
        var environment = Path.Combine(directory, "ci.postman_environment.json");
        File.WriteAllBytes(environment, [0x7B, 0xFF, 0x7D]); // not UTF-8

        var send = await Termrest.RunAsync(["send", collection, "Ping", "-e", environment, "--no-history"]);

        Assert.True(send.ExitCode == 2, send.ToString());
        Assert.Contains("ci.postman_environment.json", send.Error, StringComparison.Ordinal);
        Assert.Contains("not UTF-8", send.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_keeps_private_files_private()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes only.");
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var directory = TestFiles.NewTempDirectory();
        var environment = TestFiles.CopyToTemp(TestFiles.DemoEnvironment, directory);
        var collection = TestFiles.CopyToTemp(TestFiles.DemoCollection, directory);
        File.SetUnixFileMode(environment, Private);
        File.SetUnixFileMode(collection, Private);
        var yaml = Path.Combine(directory, "postman", "environments", "Postman Echo.environment.yaml");
        var json = Path.Combine(directory, "copy.postman_environment.json");
        var v3 = Path.Combine(directory, "v3");
        var v21 = Path.Combine(directory, "copy.postman_collection.json");

        CommandResult[] results =
        [
            await Termrest.RunAsync(["convert", environment]),
            await Termrest.RunAsync(["convert", yaml, "-o", json]),
            await Termrest.RunAsync(["convert", collection, "-o", v3]),
            await Termrest.RunAsync(["convert", v3, "-o", v21]),
        ];

        Assert.All(results, r => Assert.True(r.ExitCode == 0, r.ToString()));
        Assert.Equal(Private, File.GetUnixFileMode(yaml));
        Assert.Equal(Private, File.GetUnixFileMode(json));
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(v3)); // a folder needs x to be entered
        foreach (var file in Directory.EnumerateFiles(v3, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(Private, File.GetUnixFileMode(file));
        }

        Assert.Equal(Private, File.GetUnixFileMode(v21));
    }

    [Fact]
    public async Task Convert_writes_nothing_when_files_of_the_collection_cannot_be_read()
    {
        var root = TestFiles.NewTempDirectory();
        WriteFiles(root,
            ("postman/collections/Api/.resources/definition.yaml", "<<<<<<< HEAD\n$kind: collection\n=======\n$kind: collection\ndescription: theirs\n>>>>>>> theirs\n"),
            ("postman/collections/Api/Ping.request.yaml", HttpRequestYaml("https://h/ping")),
            ("postman/collections/Sockets/Ping.request.yaml", HttpRequestYaml("https://h/ping")),
            ("postman/collections/Sockets/Stream.request.yaml", "$kind: websocket-request\nurl: wss://h/stream\n"),
            ("postman/collections/Named/Ping.request.yaml", HttpRequestYaml("https://h/ping")),
            ("postman/collections/Named/non-HTTP.request.yaml", "<<<<<<< HEAD\n$kind: http-request\n=======\n$kind: http-request\nurl: https://h/theirs\n>>>>>>> theirs\n"));
        var api = Path.Combine(root, "api.postman_collection.json");
        var sockets = Path.Combine(root, "sockets.postman_collection.json");
        var named = Path.Combine(root, "named.postman_collection.json");
        var namedFolder = Path.Combine(root, "postman", "collections", "Named");

        var broken = await Termrest.RunAsync(["convert", Path.Combine(root, "postman", "collections", "Api"), "-o", api]);
        var hiddenRequestsOnly = await Termrest.RunAsync(["convert", Path.Combine(root, "postman", "collections", "Sockets"), "-o", sockets]);
        var namedLikeTheNote = await Termrest.RunAsync(["convert", namedFolder, "-o", named]);
        var validated = await Termrest.RunAsync(["validate", namedFolder]);

        // The collection's definition (its auth, variables and scripts) was not read: converting it would lose them.
        Assert.True(broken.ExitCode == 1, broken.ToString());
        Assert.Contains("definition.yaml", broken.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(api));
        Assert.True(hiddenRequestsOnly.ExitCode == 0, hiddenRequestsOnly.ToString());
        Assert.True(File.Exists(sockets));

        // A file whose name holds "non-HTTP" is no note about hidden requests.
        Assert.True(namedLikeTheNote.ExitCode == 1, namedLikeTheNote.ToString());
        Assert.False(File.Exists(named));
        Assert.True(validated.ExitCode == 1, validated.ToString());
    }

    [Fact]
    public async Task Convert_and_validate_refuse_a_folder_that_is_not_a_v3_collection()
    {
        // A workspace folder (with its sources and node_modules) is not one collection: as run, they say so.
        var root = TestFiles.NewTempDirectory();
        WriteFiles(root,
            ("postman/collections/Api/Ping.request.yaml", HttpRequestYaml("https://h/ping")),
            ("src/lib/deep/notes.txt", "x"));

        var convert = await Termrest.RunAsync(["convert", root]);
        var validate = await Termrest.RunAsync(["validate", root]);

        Assert.True(convert.ExitCode == 1, convert.ToString());
        Assert.Contains("is not a Postman v3 collection folder", convert.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "*.json"));
        Assert.True(validate.ExitCode == 1, validate.ToString());
        Assert.Contains("is not a Postman v3 collection folder", validate.Text + validate.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_names_what_it_writes_so_that_the_workspace_loads_it()
    {
        // A leading dot would hide the converted collection: the workspace does not load folders whose names start with one.
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, ".NET API", [("Ping", "GET", "https://h/ping")]);
        var environment = Path.Combine(directory, "dotnet.postman_environment.json");
        File.WriteAllText(environment, """{ "name": ".NET dev", "values": [ { "key": "baseUrl", "value": "https://h", "enabled": true } ] }""");

        var convertCollection = await Termrest.RunAsync(["convert", collection]);
        var convertEnvironment = await Termrest.RunAsync(["convert", environment]);

        Assert.True(convertCollection.ExitCode == 0, convertCollection.ToString());
        Assert.True(convertEnvironment.ExitCode == 0, convertEnvironment.ToString());
        var workspace = ApiWorkspace.Open(directory);
        Assert.Contains(workspace.Collections, c => c.IsV3 && c.Name == ".NET API" && c.FilePath == Path.Combine(directory, "postman", "collections", "-NET API"));
        Assert.True(File.Exists(Path.Combine(directory, "postman", "environments", "-NET dev.environment.yaml")));
        Assert.Contains(workspace.Environments, e => e.IsYaml && e.Name == ".NET dev");
    }

    [Fact]
    public async Task Convert_writes_files_from_the_postman_folders_next_to_them()
    {
        // JSON files kept where the workspace loads them: the converted copies go into the same workspace.
        var root = TestFiles.NewTempDirectory();
        var collection = WriteCollection(Directory.CreateDirectory(Path.Combine(root, "postman", "collections")).FullName, "Smoke", [("Ping", "GET", "https://h/ping")]);
        WriteFiles(root, ("postman/environments/staging.postman_environment.json", """{ "name": "staging", "values": [ { "key": "baseUrl", "value": "https://h", "enabled": true } ] }"""));

        var convertCollection = await Termrest.RunAsync(["convert", collection]);
        var convertEnvironment = await Termrest.RunAsync(["convert", Path.Combine(root, "postman", "environments", "staging.postman_environment.json")]);

        Assert.True(convertCollection.ExitCode == 0, convertCollection.ToString());
        Assert.True(convertEnvironment.ExitCode == 0, convertEnvironment.ToString());
        var workspace = ApiWorkspace.Open(root);
        Assert.Contains(workspace.Collections, c => c.IsV3 && c.FilePath == Path.Combine(root, "postman", "collections", "Smoke"));
        Assert.Contains(workspace.Environments, e => e.IsYaml && e.FilePath == Path.Combine(root, "postman", "environments", "staging.environment.yaml"));
        Assert.False(Directory.Exists(Path.Combine(root, "postman", "collections", "postman")));
    }

    [Fact]
    public async Task A_collection_file_in_postman_collections_is_sent_in_its_workspace()
    {
        await using var server = await TestServer.StartAsync();
        var root = TestFiles.NewTempDirectory();
        var collection = WriteCollection(Directory.CreateDirectory(Path.Combine(root, "postman", "collections")).FullName, "smoke", [("Ping", "GET", "{{baseUrl}}/echo/{{from}}")]);
        string EnvironmentYaml(string name) => $"name: {name}\nvalues:\n  - key: baseUrl\n    value: {server.BaseUrl}\n    enabled: true\n  - key: from\n    value: {name}\n    enabled: true\n";
        WriteFiles(root,
            ("postman/environments/local.environment.yaml", EnvironmentYaml("local")),
            ("postman/environments/staging.environment.yaml", EnvironmentYaml("staging")),
            (".termrest/settings.json", """{ "activeEnvironment": "local" }"""));
        var state = new Dictionary<string, string> { ["TERMREST_STATE_DIR"] = TestFiles.NewTempDirectory() };

        // The workspace's settings (a theme chosen for it, the active environment, the history) and environments, as for the
        // workspace folder.
        var theme = await Termrest.RunAsync(["themes", "use", "Nord", "--only-here", "-w", collection]);
        var active = await Termrest.RunAsync(["send", collection, "Ping"], state);
        var named = await Termrest.RunAsync(["send", collection, "Ping", "-e", "staging", "--no-history"]);
        var history = await Termrest.RunAsync(["history", root, "--json"], state);

        Assert.True(theme.ExitCode == 0, theme.ToString());
        Assert.Equal("Nord", ApiWorkspace.Open(root).Settings.Theme);
        Assert.True(active.ExitCode == 0, active.ToString());
        Assert.Contains("/echo/local", active.Text, StringComparison.Ordinal);
        Assert.True(named.ExitCode == 0, named.ToString());
        Assert.Contains("/echo/staging", named.Text, StringComparison.Ordinal);
        Assert.True(history.ExitCode == 0, history.ToString());
        Assert.Contains("/echo/local", history.Text, StringComparison.Ordinal);

        // A v3 collection folder given as the workspace means the workspace that holds it, as for send and the UI.
        WriteFiles(root, ("postman/collections/Smoke/.resources/definition.yaml", "$kind: collection\nname: Smoke\n"));
        var folder = Path.Combine(root, "postman", "collections", "Smoke");
        var copy = await Termrest.RunAsync(["themes", "copy", "Nord", "Mine", "--in-workspace", "-w", folder]);
        Assert.True(copy.ExitCode == 0, copy.ToString());
        Assert.True(Directory.Exists(Path.Combine(root, ".termrest", "themes")), copy.ToString());
        Assert.False(Directory.Exists(Path.Combine(folder, ".termrest")));

        // A copy can't take a custom theme's name (case aside): only one of them would ever show.
        var again = await Termrest.RunAsync(["themes", "copy", "Dark", "MINE", "--in-workspace", "-w", folder]);
        Assert.True(again.ExitCode == 2, again.ToString());
        Assert.Contains("A custom theme called 'Mine' exists already", again.Error, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(root, ".termrest", "themes")));
    }

    [Fact]
    public async Task History_lists_and_shows_an_entry_whose_id_was_edited_short()
    {
        await using var server = await TestServer.StartAsync();
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping")]);
        var workspace = Path.GetDirectoryName(collection)!;
        var stateDirectory = TestFiles.NewTempDirectory();
        var state = new Dictionary<string, string> { ["TERMREST_STATE_DIR"] = stateDirectory };
        var sent = await Termrest.RunAsync(["send", collection, "Ping"], state);
        Assert.True(sent.ExitCode == 0, sent.ToString());
        var file = Assert.Single(Directory.GetFiles(stateDirectory, HistoryStore.FileName, SearchOption.AllDirectories));
        var entry = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        entry["id"] = "a1";
        File.WriteAllText(file, entry.ToJsonString() + "\n");

        var listed = await Termrest.RunAsync(["history", workspace], state);
        var shown = await Termrest.RunAsync(["history", workspace, "--show", "a1"], state);

        Assert.True(listed.ExitCode == 0, listed.ToString());
        Assert.StartsWith("a1        ", listed.Text, StringComparison.Ordinal);
        Assert.True(shown.ExitCode == 0, shown.ToString());
    }

    [Fact]
    public async Task History_shows_a_response_without_a_body_as_such_and_a_body_whose_copy_is_gone_as_not_kept()
    {
        await using var server = await TestServer.StartAsync();
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Probe", "HEAD", server.BaseUrl + "/echo/probe"), ("Ping", "GET", server.BaseUrl + "/echo/ping")]);
        var workspace = Path.GetDirectoryName(collection)!;
        var stateDirectory = TestFiles.NewTempDirectory();
        var state = new Dictionary<string, string> { ["TERMREST_STATE_DIR"] = stateDirectory };
        foreach (var name in (string[])["Probe", "Ping"])
        {
            var sent = await Termrest.RunAsync(["send", collection, name], state);
            Assert.True(sent.ExitCode == 0, sent.ToString());
        }

        var entries = JsonNode.Parse((await Termrest.RunAsync(["history", workspace, "--json"], state)).Text)!.AsArray();
        string Id(string method) => entries.Single(e => e!["method"]!.GetValue<string>() == method)!["id"]!.GetValue<string>();
        File.Delete(Assert.Single(Directory.GetFiles(stateDirectory, "*.bin", SearchOption.AllDirectories))); // the GET's, as by hand

        var probe = await Termrest.RunAsync(["history", workspace, "--show", Id("HEAD")], state);
        var ping = await Termrest.RunAsync(["history", workspace, "--show", Id("GET")], state);

        Assert.True(probe.ExitCode == 0, probe.ToString());
        Assert.DoesNotContain("not kept", probe.Text, StringComparison.Ordinal); // there was none to keep
        Assert.True(ping.ExitCode == 0, ping.ToString());
        Assert.EndsWith("(response body not kept)", ping.Text.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_reports_files_another_program_has_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: elsewhere a file that is open can be read and deleted.");
        }

        await using var server = await TestServer.StartAsync();
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping")]);
        var workspace = Path.GetDirectoryName(collection)!;
        var stateDirectory = TestFiles.NewTempDirectory();
        var state = new Dictionary<string, string> { ["TERMREST_STATE_DIR"] = stateDirectory };
        var sent = await Termrest.RunAsync(["send", collection, "Ping"], state);
        Assert.True(sent.ExitCode == 0, sent.ToString());
        var history = Assert.Single(Directory.GetFiles(stateDirectory, HistoryStore.FileName, SearchOption.AllDirectories));
        var body = Assert.Single(Directory.GetFiles(stateDirectory, "*.bin", SearchOption.AllDirectories));

        // As an antivirus scan holds them.
        CommandResult listed, notCleared, shown, cleared;
        using (File.Open(history, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            listed = await Termrest.RunAsync(["history", workspace], state);
            notCleared = await Termrest.RunAsync(["history", workspace, "--clear"], state);
        }

        using (File.Open(body, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            shown = await Termrest.RunAsync(["history", workspace, "--show", Path.GetFileNameWithoutExtension(body)], state);
            cleared = await Termrest.RunAsync(["history", workspace, "--clear"], state);
        }

        foreach (var (result, message) in (ValueTuple<CommandResult, string>[])[(listed, "The history could not be read"), (notCleared, "The history could not be cleared"),
            (shown, "could not be read"), (cleared, "History cleared, but some response bodies could not be deleted")])
        {
            Assert.True(result.ExitCode == 1, result.ToString());
            Assert.Contains(message, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal); // a message, not a stack trace
        }
    }

    [Fact]
    public async Task Send_writes_binary_bodies_as_received()
    {
        await using var server = await TestServer.StartAsync();
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api",
        [
            ("Image", "GET", server.BaseUrl + "/bytes"),
            ("Octets", "GET", server.BaseUrl + "/octets"),
            ("Protobuf", "GET", server.BaseUrl + "/protobuf"),
            ("Report", "GET", server.BaseUrl + "/xlsx"),
            ("Json", "GET", server.BaseUrl + "/echo/json"),
            ("User", "GET", server.BaseUrl + "/utf8"),
        ]);

        var piped = await Termrest.RunAsync(["send", collection, "Image", "--no-history"]);
        var raw = await Termrest.RunAsync(["send", collection, "Image", "--raw", "--no-history"]);
        var octets = await Termrest.RunAsync(["send", collection, "Octets", "--no-history"]);
        var protobuf = await Termrest.RunAsync(["send", collection, "Protobuf", "--no-history"]);
        var report = await Termrest.RunAsync(["send", collection, "Report", "--no-history"]);
        var json = await Termrest.RunAsync(["send", collection, "Json", "--no-history"]);
        var rawJson = await Termrest.RunAsync(["send", collection, "User", "--raw", "--no-history"]);

        Assert.True(piped.ExitCode == 0, piped.ToString());
        Assert.Equal(TestServer.Bytes, piped.Output);
        Assert.Equal(TestServer.Bytes, raw.Output);

        // Binary data need not have a NUL byte: its type says what it is.
        Assert.Equal(TestServer.Octets, octets.Output);
        Assert.Equal(TestServer.Protobuf, protobuf.Output);
        Assert.Equal(TestServer.Workbook, report.Output); // a ZIP file, though its type names XML formats
        Assert.Contains("\n  \"method\": \"GET\"", json.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal); // text is still pretty-printed
        Assert.Equal(Encoding.UTF8.GetBytes(TestServer.Utf8Json), rawJson.Output); // with --raw as received, no line break added
    }

    [Fact]
    public async Task Send_filter_prints_the_values_a_jsonpath_query_selects()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var collection = WriteCollection(directory, "Api",
        [
            ("Login", "GET", server.BaseUrl + "/token"),
            ("User", "GET", server.BaseUrl + "/utf8"),
            ("Image", "GET", server.BaseUrl + "/bytes"),
        ]);
        var file = Path.Combine(directory, "type.json");

        var pretty = await Termrest.RunAsync(["send", collection, "Login", "--filter", "$[?@ == 'Bearer' || @ > 3000]", "--no-history"]);
        var compact = await Termrest.RunAsync(["send", collection, "User", "--filter", "$.name", "--raw", "--no-history"]);
        var saved = await Termrest.RunAsync(["send", collection, "Login", "--filter", "$.token_type", "-o", file, "--no-history"]);
        var binary = await Termrest.RunAsync(["send", collection, "Image", "--filter", "$", "--no-history"]);
        var invalid = await Termrest.RunAsync(["send", collection, "Login", "--filter", "$.a[", "--no-history"]);

        Assert.True(pretty.ExitCode == 0, pretty.ToString());
        Assert.Equal("[\n  \"Bearer\",\n  3600\n]\n", pretty.Text.ReplaceLineEndings("\n"));
        Assert.Equal("[\"" + "Zoë – 東京 ✓" + "\"]\n", compact.Text.ReplaceLineEndings("\n"));
        Assert.True(saved.ExitCode == 0, saved.ToString());
        Assert.Empty(saved.Output);
        Assert.Equal("[\n  \"Bearer\"\n]\n", File.ReadAllText(file));
        Assert.Contains("Saved 1 value to " + file, saved.Error, StringComparison.Ordinal);
        Assert.True(binary.ExitCode == 1, binary.ToString());
        Assert.Contains("error: --filter: The body is binary, not JSON.", binary.Error, StringComparison.Ordinal);

        // A query that is not JSONPath is a usage error: the request is not sent.
        Assert.True(invalid.ExitCode == 2, invalid.ToString());
        Assert.Contains("--filter: The query ends where", invalid.Error, StringComparison.Ordinal);
        Assert.Equal(4, server.Requests);
    }

    [Fact]
    public async Task Send_curl_prints_no_command_that_curl_would_send_differently()
    {
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "README.md"), "readme");
        var collection = Path.Combine(directory, "Api.postman_collection.json");
        File.WriteAllText(collection, $$"""
            { "info": { "name": "Api", "schema": "{{Schema}}" }, "item": [
              { "name": "Upload", "request": { "method": "POST", "url": "https://h/upload", "body": { "mode": "formdata",
                "formdata": [ { "key": "doc=</proc/self/environ;", "type": "file", "src": "README.md" } ] } } },
              { "name": "Probe", "request": { "method": "HEAD", "url": "https://h/x", "body": { "mode": "raw", "raw": "{}" } } } ] }
            """);

        var upload = await Termrest.RunAsync(["send", collection, "Upload", "--curl"]);
        var probe = await Termrest.RunAsync(["send", collection, "Probe", "--curl"]);

        // curl would send its own environment for this field, and can't send a HEAD request with a body.
        Assert.True(upload.ExitCode == 2, upload.ToString());
        Assert.Empty(upload.Output);
        Assert.Contains("'doc=</proc/self/environ;'", upload.Error, StringComparison.Ordinal);
        Assert.True(probe.ExitCode == 2, probe.ToString());
        Assert.Empty(probe.Output);
        Assert.Contains("HEAD request with a body", probe.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_reports_what_happened_while_sending_and_fails_when_the_body_was_cut()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        WriteFiles(directory, (".termrest/settings.json", "{ \"maxResponseSizeMb\": 1 }"));
        var collection = Path.Combine(directory, "Api.postman_collection.json");
        File.WriteAllText(collection, $$"""
            { "info": { "name": "Api", "schema": "{{Schema}}" }, "item": [
              { "name": "Export", "request": { "method": "GET", "url": "{{server.BaseUrl}}/large" } },
              { "name": "Typed", "request": { "method": "GET", "url": "{{server.BaseUrl}}/echo/typed",
                "header": [ { "key": "Content-Type", "value": "application/json" } ] } } ] }
            """);
        var file = Path.Combine(directory, "export.zip");

        var cut = await Termrest.RunAsync(["send", collection, "Export", "-o", file, "--no-history"]);
        var typed = await Termrest.RunAsync(["send", collection, "Typed", "--no-history"]);

        // The file holds the first megabyte only: the exit code is curl's when --max-filesize stops a transfer.
        Assert.True(cut.ExitCode == 63, cut.ToString());
        Assert.Equal(1024 * 1024, new FileInfo(file).Length);
        Assert.Contains("warning: The response body was truncated at 1.0 MB.", cut.Error, StringComparison.Ordinal);
        Assert.True(typed.ExitCode == 0, typed.ToString());
        Assert.Contains("warning: 'Content-Type' was not sent because the request has no body.", typed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_reports_a_body_it_cannot_save_and_still_says_what_happened()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        WriteFiles(directory, (".termrest/settings.json", "{ \"maxResponseSizeMb\": 1 }"));
        var collection = WriteCollection(directory, "Api", [("Export", "GET", server.BaseUrl + "/large")]);
        var file = Path.Combine(directory, "missing", "export.zip");

        var send = await Termrest.RunAsync(["send", collection, "Export", "-o", file, "--no-history"]);

        Assert.True(send.ExitCode == 1, send.ToString());
        Assert.Contains("error: The body could not be saved to " + file, send.Error, StringComparison.Ordinal);
        Assert.Contains("warning: The response body was truncated at 1.0 MB.", send.Error, StringComparison.Ordinal); // printed after the response
        Assert.DoesNotContain("Unhandled exception", send.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_checks_the_other_files_when_one_cannot_be_read()
    {
        var unreadable = TestFiles.CopyToTemp(TestFiles.DemoCollection);
        var readable = TestFiles.CopyToTemp(TestFiles.DemoCollection);
        SetReadable(unreadable, false);
        try
        {
            Assert.SkipWhen(CanRead(unreadable), "This user can read any file.");

            var validate = await Termrest.RunAsync(["validate", unreadable, readable]);

            Assert.True(validate.ExitCode == 1, validate.ToString());
            Assert.Contains($"✗ {unreadable}: ", validate.Error, StringComparison.Ordinal);
            Assert.Contains($"✓ {readable}: ", validate.Error, StringComparison.Ordinal);
        }
        finally
        {
            SetReadable(unreadable, true);
        }

        static bool CanRead(string path)
        {
            try
            {
                File.ReadAllBytes(path);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task Validate_shows_a_difference_in_white_space()
    {
        var file = TestFiles.CopyToTemp(TestFiles.DemoCollection);
        File.WriteAllText(file, File.ReadAllText(file).Replace("\t", "  ", StringComparison.Ordinal)); // not indented as Postman does

        var validate = await Termrest.RunAsync(["validate", file]);

        Assert.True(validate.ExitCode == 1, validate.ToString());
        Assert.Contains("differs at line 2\n    original: ··\"info\":·{\n    termrest: \\t\"info\":·{", validate.Error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_shows_a_line_break_that_saving_would_change()
    {
        var file = TestFiles.CopyToTemp(TestFiles.DemoCollection);
        var lines = File.ReadAllText(file).Split('\n');
        lines[1] += "\r"; // one line saved by a Windows editor: saving writes LF there, as on every other line
        File.WriteAllText(file, string.Join('\n', lines));
        var crlf = TestFiles.CopyToTemp(TestFiles.DemoCollection);
        File.WriteAllText(crlf, File.ReadAllText(crlf).Replace("\t", "  ", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

        var validate = await Termrest.RunAsync(["validate", file, crlf]);

        Assert.True(validate.ExitCode == 1, validate.ToString());
        var error = validate.Error.ReplaceLineEndings("\n");
        Assert.Contains($"✗ {file}: ", error, StringComparison.Ordinal);
        Assert.Contains("differs at line 2\n    original: \\t\"info\":·{\\r\n    termrest: \\t\"info\":·{\n", error, StringComparison.Ordinal);

        // A file with CRLF line breaks throughout: they are no difference.
        Assert.Contains("differs at line 2\n    original: ··\"info\":·{\n    termrest: \\t\"info\":·{\n", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_is_utf8_in_files_and_pipes()
    {
        await using var server = await TestServer.StartAsync();
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("User", "GET", server.BaseUrl + "/utf8")]);

        // Editors on Windows often save a file with a byte order mark.
        byte[] command = [.. OperatingSystem.IsWindows() ? Encoding.UTF8.GetPreamble() : [], .. Encoding.UTF8.GetBytes("curl https://h/Zürich")];

        var send = await Termrest.RunAsync(["send", collection, "User", "--no-history"]);
        var run = await Termrest.RunAsync(["run", collection]);
        var import = await Termrest.RunAsync(["import-curl", collection], input: command);

        // Not in the code page of a console (437, 850, 936 …), which lacks some of these characters; no byte order mark.
        Assert.True(send.ExitCode == 0, send.ToString());
        Assert.Equal("{\n  \"name\": \"Zoë – 東京 ✓\"\n}", send.Text.ReplaceLineEndings("\n").TrimEnd());
        Assert.Contains("✓ 200", run.Error, StringComparison.Ordinal);
        Assert.True(import.ExitCode == 0, import.ToString());
        Assert.Contains("https://h/Zürich", File.ReadAllText(collection), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_does_not_take_one_of_several_requests_for_the_one_named()
    {
        await using var server = await TestServer.StartAsync();
        var root = TestFiles.NewTempDirectory();
        var request = HttpRequestYaml(server.BaseUrl + "/echo/x");
        WriteFiles(root,
            ("postman/collections/Admin/.resources/definition.yaml", "$kind: collection\n"),
            ("postman/collections/Admin/Users/Delete user.request.yaml", request),
            ("postman/collections/Admin/Health/Ping.request.yaml", request),
            ("postman/collections/Admin/Status/Ping.request.yaml", request),
            ("postman/collections/Public/.resources/definition.yaml", "$kind: collection\n"),
            ("postman/collections/Public/Users/Delete user.request.yaml", request),
            ("postman/collections/Public/Misc/Ping.request.yaml", request));

        var inBoth = await Termrest.RunAsync(["send", root, "Users/Delete user", "--no-history"]);
        var byName = await Termrest.RunAsync(["send", root, "Ping", "--no-history"]);
        var sent = server.Requests;
        var byPath = await Termrest.RunAsync(["send", root, "Health/Ping", "--no-history"]);
        var inOne = await Termrest.RunAsync(["send", Path.Combine(root, "postman", "collections", "Admin"), "Users/Delete user", "--no-history"]);

        Assert.True(inBoth.ExitCode == 2, inBoth.ToString());
        Assert.Contains("'Users/Delete user' is ambiguous", inBoth.Error, StringComparison.Ordinal);
        Assert.Contains("Users/Delete user  (Admin)", inBoth.Error, StringComparison.Ordinal);
        Assert.Contains("Users/Delete user  (Public)", inBoth.Error, StringComparison.Ordinal);

        // A name not unique in the first collection is not taken for the request of another one either.
        Assert.True(byName.ExitCode == 2, byName.ToString());
        Assert.Contains("Misc/Ping  (Public)", byName.Error, StringComparison.Ordinal);
        Assert.Equal(0, sent);
        Assert.True(byPath.ExitCode == 0, byPath.ToString());
        Assert.True(inOne.ExitCode == 0, inOne.ToString());
    }

    [Fact]
    public async Task Send_and_run_send_nothing_while_the_workspace_settings_cannot_be_read()
    {
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        WriteFiles(directory, (".termrest/settings.json",
            "{\n<<<<<<< HEAD\n  \"recordingRedact\": [\"X-Tenant-Key\"],\n=======\n  \"recordingRedact\": [\"X-Tenant-Key\"],\n  \"historyEnabled\": false,\n>>>>>>> theirs\n}\n"));
        var collection = WriteCollection(directory, "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping")]);
        var har = Path.Combine(directory, "run.har");
        var report = Path.Combine(directory, "report.json");

        var send = await Termrest.RunAsync(["send", collection, "Ping"]);
        var run = await Termrest.RunAsync(["run", collection, "--record", har, "--report-json", report]);

        // They may turn the history off and name what recordings must redact.
        Assert.True(send.ExitCode == 1, send.ToString());
        Assert.Contains("Fix or delete " + Path.Combine(directory, ".termrest", "settings.json"), send.Error, StringComparison.Ordinal);
        Assert.Empty(send.Output);
        Assert.True(run.ExitCode == 1, run.ToString());
        Assert.False(File.Exists(har));
        Assert.False(File.Exists(report));
        Assert.Equal(0, server.Requests);

        // The full-screen UI goes on with the defaults, without a history.
        using var runner = new RequestRunner(ApiWorkspace.Open(directory));
        runner.ConfigureHistory(TestFiles.NewTempDirectory());
        Assert.Null(runner.History);
    }

    [Fact]
    public async Task Run_warns_when_your_settings_cannot_be_read()
    {
        await using var server = await TestServer.StartAsync();
        var config = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(config, "settings.json"), "{\n<<<<<<< HEAD\n  \"scriptLanguageOrder\": [\"python\"]\n=======\n}\n");
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping")]);

        var run = await Termrest.RunAsync(["run", collection], new Dictionary<string, string> { ["TERMREST_CONFIG_DIR"] = config });

        // They choose the runtimes of scripts: without them, a request with scripts fails as if none were chosen.
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.StartsWith("warning: " + Path.Combine(config, "settings.json") + ": ", run.Error, StringComparison.Ordinal);
        Assert.Contains("script runtimes chosen for this workspace", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proxy_in_the_workspace_settings_is_not_used_until_it_is_allowed()
    {
        // A cloned repository's settings name a proxy (nothing listens there): requests go without it, and a warning says so.
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        const string proxy = "http://ada:Pr0xy-s3cret@127.0.0.1:1";
        WriteFiles(directory, (".termrest/settings.json", $$"""{ "proxy": "{{proxy}}" }"""));
        var collection = WriteCollection(directory, "Api", [("Ping", "GET", server.BaseUrl + "/echo/ping")]);

        var unknown = await Termrest.RunAsync(["send", collection, "Ping"]);
        var run = await Termrest.RunAsync(["run", collection]);

        // Allowed for this folder in the UI (Tools > Settings > Save), which keeps that in the user's settings.
        var config = TestFiles.NewTempDirectory();
        var user = new UserSettings();
        user.AllowProxy(directory, proxy);
        user.Save(config);
        var allowed = await Termrest.RunAsync(["send", collection, "Ping"], new Dictionary<string, string> { ["TERMREST_CONFIG_DIR"] = config });

        Assert.True(unknown.ExitCode == 0, unknown.ToString());
        Assert.Contains("/echo/ping", unknown.Text, StringComparison.Ordinal);
        Assert.Contains("(http://ada:***@127.0.0.1:1) is not used", unknown.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Pr0xy-s3cret", unknown.Error, StringComparison.Ordinal);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("(http://ada:***@127.0.0.1:1) is not used", run.Error, StringComparison.Ordinal);
        Assert.True(allowed.ExitCode == 1, allowed.ToString()); // the proxy refused the connection
        Assert.DoesNotContain("is not used", allowed.Error, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task Send_signs_in_to_a_proxy_with_proxy_user_and_says_what_a_proxy_that_wants_a_sign_in_needs()
    {
        await using var proxy = new SignInProxy("ada", "s3cret");
        var collection = WriteCollection(TestFiles.NewTempDirectory(), "Api", [("Hello", "GET", "http://api.example.test/hello")]);

        var signedIn = await Termrest.RunAsync(["send", collection, "Hello", "--proxy", proxy.Url, "--proxy-user", "ada:s3cret"]);
        var refused = await Termrest.RunAsync(["send", collection, "Hello", "-x", proxy.Url["http://".Length..]]); // host:port, as curl takes it
        var noPassword = await Termrest.RunAsync(["send", collection, "Hello", "--proxy", proxy.Url, "--proxy-user", "ada"]); // nobody to type it
        var invalid = await Termrest.RunAsync(["send", collection, "Hello", "--proxy", "ftp://proxy:21"]);

        Assert.True(signedIn.ExitCode == 0, signedIn.ToString());
        Assert.Contains("through the proxy", signedIn.Text, StringComparison.Ordinal);
        Assert.True(refused.ExitCode == 0, refused.ToString()); // a response: the proxy's 407
        Assert.Contains("407", refused.Error, StringComparison.Ordinal);
        Assert.Contains("The proxy asks for a user name and password", refused.Error, StringComparison.Ordinal);
        Assert.Equal(2, noPassword.ExitCode);
        Assert.Contains("give the password as user:password", noPassword.Error, StringComparison.Ordinal);
        Assert.Equal(2, invalid.ExitCode);
        Assert.Contains("--proxy expects a URL", invalid.Error, StringComparison.Ordinal);
        Assert.Equal(3, proxy.Requests.Count); // the two that reached it: one with a sign-in after the challenge
    }

    [Fact]
    public async Task Files_from_outside_the_workspace_are_sent_only_with_allow_outside_files()
    {
        // A collection someone else wrote, whose body is a file of the user's.
        await using var server = await TestServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var secret = Path.Combine(TestFiles.NewTempDirectory(), "credentials");
        File.WriteAllText(secret, "s3cret");
        var collection = Path.Combine(directory, "Api.postman_collection.json");
        File.WriteAllText(collection, $$"""
            { "info": { "name": "Api", "schema": "{{Schema}}" }, "item": [
              { "name": "Upload", "request": { "method": "PUT", "url": "{{server.BaseUrl}}/upload", "body": { "mode": "file",
                "file": { "src": {{JsonValue.Create(secret).ToJsonString()}} } } } } ] }
            """);

        var refused = await Termrest.RunAsync(["send", collection, "Upload"]);
        var allowed = await Termrest.RunAsync(["send", collection, "Upload", "--allow-outside-files"]);

        Assert.True(refused.ExitCode == 1, refused.ToString());
        Assert.Contains($"Body file '{secret}' is outside the workspace folder, so it is not sent", refused.Error, StringComparison.Ordinal);
        Assert.True(allowed.ExitCode == 0, allowed.ToString());
        Assert.Equal(1, server.Requests);
    }
}
