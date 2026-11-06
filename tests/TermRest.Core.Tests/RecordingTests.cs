using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Core.Auth;
using TermRest.Core.Certificates;
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

/// <summary>Recording sent requests into HAR files: content, redaction, temporary files, memory and recovery.</summary>
public sealed class RecordingTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = TestFiles.NewTempDirectory();

    private RequestRecorder Start(params string[] redactNames) =>
        RequestRecorder.Start(new RecordingOptions { TempRoot = _root, WorkspaceName = "tests", RedactNames = redactNames });

    private static JsonObject ReadHar(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private static JsonArray Entries(JsonObject har) => (JsonArray)har["log"]!["entries"]!;

    private static string? Header(JsonNode? headers, string name) =>
        ((JsonArray)headers!).FirstOrDefault(h => string.Equals(h!["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))?["value"]?.GetValue<string>();

    /// <summary>A finished exchange built in code (no server needed).</summary>
    private static HttpExchange Exchange(string url, byte[]? responseBody = null, string? responseType = "application/json", string method = "GET")
    {
        var prepared = new PreparedRequest { Method = method, Url = url, Uri = new Uri(url) };
        var exchange = new HttpExchange(prepared)
        {
            StartedAt = DateTimeOffset.Now,
            Timing = new TimingBreakdown { Dns = TimeSpan.FromMilliseconds(1), Connect = TimeSpan.FromMilliseconds(2), Waiting = TimeSpan.FromMilliseconds(5), Total = TimeSpan.FromMilliseconds(9), RemoteEndPoint = "127.0.0.1:8443" },
            Response = new ResponseSnapshot
            {
                StatusCode = 200,
                ReasonPhrase = "OK",
                Headers = responseType is null ? [] : [new("Content-Type", responseType)],
                Body = responseBody ?? [],
                ContentType = responseType,
            },
        };
        exchange.SentHeaders.Add(new("User-Agent", "TermRest/test"));
        return exchange;
    }

    [Fact]
    public async Task A_recording_is_a_har_file_with_every_request_and_response_and_no_secrets()
    {
        await using var server = await EchoServer.StartAsync();
        var upload = Path.Combine(_root, "upload.txt");
        await File.WriteAllTextAsync(upload, "file-content", _ct);
        using var runner = new RequestRunner(ApiWorkspace.Open(TestFiles.NewTempDirectory()));
        runner.LocalVariables["base"] = server.BaseUri.ToString().TrimEnd('/');
        runner.LocalVariables["apiToken"] = "tok-1234567890"; // named like a secret, not typed "secret"
        var collection = PostmanCollection.Create("Recorded");
        using var recorder = Start();
        runner.Recorder = recorder;
        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "Bearer-Tok-987654321");

        async Task<HttpExchange> Send(HttpRequestSpec spec) => await runner.SendAsync(runner.Prepare(collection, null, spec), _ct);
        await Send(new HttpRequestSpec { Url = "{{base}}/echo/items/{{apiToken}}?api_key=K3y-Secret-123&page=2", Auth = bearer });
        await Send(new HttpRequestSpec
        {
            Method = "POST",
            Url = "{{base}}/echo",
            Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"user\":\"ada\",\"password\":\"P@ssw0rd!x\",\"profile\":{\"apiKey\":\"AK-123456789\"}}" },
        });
        await Send(new HttpRequestSpec
        {
            Method = "POST",
            Url = "{{base}}/echo",
            Body = new RequestBody { Mode = BodyMode.UrlEncoded, UrlEncoded = [new() { Key = "client_id", Value = "app" }, new() { Key = "client_secret", Value = "S3cr3t-Value-1" }] },
        });
        await Send(new HttpRequestSpec { Url = "{{base}}/set-cookie" });
        await Send(new HttpRequestSpec { Url = "{{base}}/echo" });
        await Send(new HttpRequestSpec { Url = "http://127.0.0.1:1/refused" });
        await Send(new HttpRequestSpec
        {
            Method = "POST",
            Url = "{{base}}/echo",
            Body = new RequestBody
            {
                Mode = BodyMode.FormData,
                FormData = [new() { Key = "password", Value = "Multi-Pass-123" }, new() { Key = "note", Value = "hello" }, new() { Key = "doc", Type = "file", Src = upload }],
            },
        });
        await Send(new HttpRequestSpec { Url = "{{base}}/echo?x={{undefined}}" }); // unresolved variable: sent, with a warning
        Assert.Equal(8, recorder.Count);

        var path = Path.Combine(_root, "session.har");
        var result = recorder.Save(path);

        Assert.Equal((path, 8), (result.Path, result.Requests));
        Assert.True(result.Redactions > 10, result.Redactions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var text = await File.ReadAllTextAsync(path, _ct);
        foreach (var secret in (string[])["K3y-Secret-123", "Bearer-Tok-987654321", "tok-1234567890", "P@ssw0rd!x", "AK-123456789", "S3cr3t-Value-1", "abc123", "Multi-Pass-123"])
        {
            Assert.False(text.Contains(secret, StringComparison.Ordinal), $"'{secret}' is in the recording:\n{text}");
        }

        var har = ReadHar(path);
        var log = har["log"]!;
        Assert.Equal("1.2", log["version"]!.GetValue<string>());
        Assert.Equal("TermRest", log["creator"]!["name"]!.GetValue<string>());
        Assert.Equal(8, log["_termrest"]!["requests"]!.GetValue<int>());
        Assert.Equal("tests", log["_termrest"]!["workspace"]!.GetValue<string>());
        var entries = Entries(har);

        var get = entries[0]!;
        var url = get["request"]!["url"]!.GetValue<string>();
        Assert.Equal($"{server.BaseUri}echo/items/[REDACTED]?api_key=[REDACTED]&page=2", url);
        Assert.Equal("Bearer [REDACTED]", Header(get["request"]!["headers"], "Authorization"));
        Assert.Contains(get["request"]!["queryString"]!.AsArray(), q => q!["name"]!.GetValue<string>() == "page" && q["value"]!.GetValue<string>() == "2");
        Assert.Equal(200, get["response"]!["status"]!.GetValue<int>());
        Assert.StartsWith("application/json", get["response"]!["content"]!["mimeType"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("\"authorization\":\"[REDACTED]\"", get["response"]!["content"]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(get["timings"]!["connect"]!.GetValue<double>() >= 0);
        Assert.Equal("127.0.0.1", get["serverIPAddress"]!.GetValue<string>());
        Assert.Contains(get["_termrest"]!["redacted"]!.AsArray(), n => n!["name"]?.GetValue<string>() == "Authorization" && n["reason"]!.GetValue<string>() == "credential");
        Assert.False(string.IsNullOrEmpty(get["_termrest"]!["auth"]!.GetValue<string>()));

        var json = entries[1]!["request"]!["postData"]!;
        Assert.StartsWith("application/json", json["mimeType"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("{\"user\":\"ada\",\"password\":\"[REDACTED]\",\"profile\":{\"apiKey\":\"[REDACTED]\"}}", json["text"]!.GetValue<string>());

        var form = entries[2]!["request"]!["postData"]!;
        Assert.Equal("client_id=app&client_secret=[REDACTED]", form["text"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", form["params"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "client_secret")!["value"]!.GetValue<string>());

        var cookie = entries[3]!["response"]!["cookies"]![0]!;
        Assert.Equal(("session", "[REDACTED]", "/"), (cookie["name"]!.GetValue<string>(), cookie["value"]!.GetValue<string>(), cookie["path"]!.GetValue<string>()));
        Assert.Equal("session", entries[4]!["request"]!["cookies"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("session=[REDACTED]", Header(entries[4]!["request"]!["headers"], "Cookie"));

        var refused = entries[5]!;
        Assert.Equal(0, refused["response"]!["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(refused["_termrest"]!["error"]!.GetValue<string>()));

        var multipart = entries[6]!["request"]!["postData"]!["params"]!.AsArray();
        Assert.Equal("[REDACTED]", multipart.Single(p => p!["name"]!.GetValue<string>() == "password")!["value"]!.GetValue<string>());
        Assert.Equal("hello", multipart.Single(p => p!["name"]!.GetValue<string>() == "note")!["value"]!.GetValue<string>());
        var file = multipart.Single(p => p!["name"]!.GetValue<string>() == "doc")!;
        Assert.Equal("upload.txt", file["fileName"]!.GetValue<string>());
        Assert.Null(file["value"]); // files are recorded by reference, not content
        Assert.Equal((upload, 12, false), (file["_file"]!["path"]!.GetValue<string>(), file["_file"]!["size"]!.GetValue<int>(), file["_file"]!["contentRecorded"]!.GetValue<bool>()));
        Assert.DoesNotContain("file-content", entries[6]!["request"]!.ToJsonString(), StringComparison.Ordinal);

        Assert.Contains("Unresolved variables: {{undefined}}", entries[7]!["_termrest"]!["warnings"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(recorder.TempDirectory));
    }

    [Fact]
    public async Task Files_sent_with_a_request_are_recorded_by_path_never_by_content()
    {
        await using var server = await EchoServer.StartAsync();
        var payload = Path.Combine(_root, "order.json");
        await File.WriteAllTextAsync(payload, "{\"item\":\"book\",\"note\":\"Only-In-The-File\"}", _ct);
        using var runner = new RequestRunner(ApiWorkspace.Open(TestFiles.NewTempDirectory()));
        using var recorder = Start();
        runner.Recorder = recorder;
        var exchange = await runner.SendAsync(runner.Prepare(PostmanCollection.Create("Recorded"), null, new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = payload, FileLanguage = "json" },
        }), _ct);
        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        Assert.Equal(1, recorder.RequestsWithFiles);

        var har = ReadHar(recorder.Save(Path.Combine(_root, "files.har")).Path);

        var request = Entries(har)[0]!["request"]!;
        var postData = request["postData"]!;
        Assert.Equal("application/json", postData["mimeType"]!.GetValue<string>());
        Assert.Null(postData["text"]);
        var file = postData["_file"]!;
        var size = new FileInfo(payload).Length;
        Assert.Equal((payload, size, false), (file["path"]!.GetValue<string>(), file["size"]!.GetValue<long>(), file["contentRecorded"]!.GetValue<bool>()));
        Assert.False(string.IsNullOrEmpty(file["modified"]?.GetValue<string>()));
        Assert.Equal("Sent from a file. The recording keeps the file's path, not its content.", postData["comment"]!.GetValue<string>());
        Assert.Equal(size, request["bodySize"]!.GetValue<long>());
        Assert.DoesNotContain("Only-In-The-File", request.ToJsonString(), StringComparison.Ordinal); // the echo in the response is the server's
        Assert.Contains("Files sent with a request (a body file or a form-data file) are not included", har["log"]!["comment"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tls_details_and_the_client_certificate_are_recorded()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        var entry = new CertificateEntry { Name = "test client", Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } };
        entry.Matches = [$"https://localhost:{server.BaseUri.Port}/*"];
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory) { Entries = [entry] });
        var prepared = new RequestPreparer(VariableResolver.Empty, provider, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath })
            .Prepare(new HttpRequestSpec { Url = server.Url("echo") }, null, AppContext.BaseDirectory);
        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);
        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        using var recorder = Start();

        recorder.Record(exchange);
        var har = ReadHar(recorder.Save(Path.Combine(_root, "tls.har")).Path);

        var details = Entries(har)[0]!["_termrest"]!;
        Assert.Equal("test client", details["clientCertificate"]!["name"]!.GetValue<string>());
        var tls = details["tls"]![0]!;
        Assert.StartsWith("Tls", tls["protocol"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("CN=localhost", tls["serverCertificates"]![0]!["subject"]!.GetValue<string>());
        Assert.Equal(_pki.Client.Thumbprint, tls["clientCertificate"]!["thumbprint"]!.GetValue<string>());
        Assert.True(tls["clientCertificateRequested"]!.GetValue<bool>());
        Assert.True(Entries(har)[0]!["timings"]!["ssl"]!.GetValue<double>() >= 0);
    }

    [Fact]
    public async Task Large_bodies_are_streamed_to_disk_and_not_kept_in_memory()
    {
        using var recorder = Start();
        var (body, hash) = RecordLargeBinary(recorder);
        RecordLargeJson(recorder, 20_000);
        await recorder.WhenWritten();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(body.IsAlive, "The recorder still holds a response body after writing it.");

        var path = Path.Combine(_root, "large.har");
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = recorder.Save(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(result.Bytes > 20 * 1024 * 1024, result.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(allocated < 2 * 1024 * 1024, $"Saving a {result.Bytes / 1024 / 1024} MB recording allocated {allocated / 1024} KB.");
        var entries = Entries(ReadHar(path));
        var binary = entries[0]!["response"]!["content"]!;
        Assert.Equal("base64", binary["encoding"]!.GetValue<string>());
        Assert.Equal(hash, SHA256.HashData(Convert.FromBase64String(binary["text"]!.GetValue<string>())));
        var json = entries[1]!["response"]!["content"]!["text"]!.GetValue<string>();
        Assert.Equal(20_000, json.Split("\"token\": \"[REDACTED]\"").Length - 1);
        Assert.DoesNotContain("tok-", json, StringComparison.Ordinal);
        Assert.Equal(20_000, entries[1]!["_termrest"]!["redacted"]![0]!["count"]!.GetValue<int>());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Body, byte[] Hash) RecordLargeBinary(RequestRecorder recorder)
    {
        var body = new byte[20 * 1024 * 1024];
        new Random(42).NextBytes(body);
        recorder.Record(Exchange("https://example.com/download", body, "application/octet-stream"));
        return (new WeakReference(body), SHA256.HashData(body));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecordLargeJson(RequestRecorder recorder, int items)
    {
        var builder = new StringBuilder("[\n");
        for (var i = 0; i < items; i++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"  {{\"id\": {i}, \"name\": \"item {i} — ünïcödé\", \"token\": \"tok-{i:D8}-xyz\"}}{(i + 1 < items ? "," : "")}\n");
        }

        recorder.Record(Exchange("https://example.com/items", Encoding.UTF8.GetBytes(builder.Append(']').ToString())));
    }

    [Fact]
    public void Temporary_files_are_private_and_deleted_when_saved_or_discarded()
    {
        var recorder = Start();
        Assert.StartsWith(Path.Combine(_root, RequestRecorder.DirectoryPrefix), recorder.TempDirectory, StringComparison.Ordinal);
        var spool = Path.Combine(recorder.TempDirectory, RequestRecorder.SpoolFileName);
        Assert.True(File.Exists(spool));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(recorder.TempDirectory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(spool));
        }

        recorder.Record(Exchange("https://example.com/a", "{}"u8.ToArray()));
        var saved = recorder.Save(Path.Combine(_root, "nested", "folder", "a.har"));

        Assert.False(Directory.Exists(recorder.TempDirectory));
        Assert.Equal("a.har", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_root, "nested", "folder")))));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(saved.Path));
        }

        var discarded = Start();
        discarded.Record(Exchange("https://example.com/b"));
        discarded.Discard();
        Assert.False(Directory.Exists(discarded.TempDirectory));
        Assert.Throws<ObjectDisposedException>(() => discarded.Save(Path.Combine(_root, "b.har")));
    }

    [Fact]
    public void Saving_again_replaces_the_file_and_a_failed_save_keeps_the_recording()
    {
        var path = Path.Combine(_root, "replace.har");
        File.WriteAllText(path, "old");
        var blocked = Path.Combine(_root, "file-not-folder");
        File.WriteAllText(blocked, "");
        using var recorder = Start();
        recorder.Record(Exchange("https://example.com/one"));

        Assert.ThrowsAny<IOException>(() => recorder.Save(Path.Combine(blocked, "x.har")));
        Assert.True(recorder.IsRecording);
        recorder.Record(Exchange("https://example.com/two"));
        var result = recorder.Save(path);

        Assert.Equal(2, result.Requests);
        Assert.Equal(2, Entries(ReadHar(path)).Count);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task Requests_that_were_never_sent_are_not_recorded()
    {
        using var recorder = Start();
        var invalid = new PreparedRequest { Url = "{{base}}/x" };
        invalid.Errors.Add("'{{base}}/x' is not a valid http(s) URL.");
        var changes = 0;
        recorder.Changed += (_, _) => Interlocked.Increment(ref changes);

        recorder.Record(new HttpExchange(invalid) { ErrorMessage = invalid.Errors[0] });
        recorder.Record(Exchange("https://example.com/sent"));
        await recorder.WhenWritten();

        Assert.Equal(1, recorder.Count);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref changes) == 2, TimeSpan.FromSeconds(10)), $"{changes} change(s)"); // recorded, then written
    }

    [Fact]
    public async Task What_scripts_did_is_recorded_with_secrets_redacted()
    {
        using var recorder = Start();
        var exchange = Exchange("https://example.com/orders", "{\"id\":1}"u8.ToArray());
        exchange.Request.SecretValues.Add("s3cr3t-value");
        var scripts = new ScriptResults { Ran = 2, Error = new ScriptError("request 'Orders'", "KeyError: 's3cr3t-value'", "Traceback (most recent call last):\nKeyError: 's3cr3t-value'") };
        scripts.Tests.Add(new ScriptTest("status is 200", true, null, "request 'Orders'"));
        scripts.Tests.Add(new ScriptTest("has a name", false, "AssertionError: got s3cr3t-value", "request 'Orders'"));
        scripts.Logs.Add(new ScriptLog("test", "request 'Orders'", "token is s3cr3t-value"));
        scripts.VariablesSet.Add("environment.token");
        exchange.Scripts = scripts;

        recorder.Record(exchange);
        await recorder.WhenWritten();
        var recorded = Entries(ReadHar(recorder.Save(Path.Combine(_root, "scripts.har")).Path))[0]!["_termrest"]!["scripts"]!;

        Assert.Equal(2, recorded["ran"]!.GetValue<int>());
        Assert.Equal([("status is 200", true), ("has a name", false)], recorded["tests"]!.AsArray().Select(t => (t!["name"]!.GetValue<string>(), t["passed"]!.GetValue<bool>())));
        Assert.Equal(["environment.token"], recorded["variablesSet"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.StartsWith("[test · request 'Orders'] token is ", recorded["output"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("request 'Orders': KeyError", recorded["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("Traceback", recorded["traceback"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", recorded.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_recording_is_a_valid_har_file()
    {
        using var recorder = Start();

        var har = ReadHar(recorder.Save(Path.Combine(_root, "empty.har")).Path);

        Assert.Empty(Entries(har));
        Assert.Equal(0, har["log"]!["_termrest"]!["requests"]!.GetValue<int>());
    }

    [Fact]
    public void Extra_names_from_the_workspace_settings_are_redacted()
    {
        using var recorder = Start("Tenant-Key");
        var exchange = Exchange("https://example.com/t?tenant_key=t-1234", "{\"tenantKey\": \"t-1234\"}"u8.ToArray());
        exchange.SentHeaders.Add(new("X-Tenant-Key", "t-1234"));

        recorder.Record(exchange);
        var entry = Entries(ReadHar(recorder.Save(Path.Combine(_root, "extra.har")).Path))[0]!;

        Assert.Equal("[REDACTED]", Header(entry["request"]!["headers"], "X-Tenant-Key"));
        Assert.Equal("https://example.com/t?tenant_key=[REDACTED]", entry["request"]!["url"]!.GetValue<string>());
        Assert.Equal("{\"tenantKey\": \"[REDACTED]\"}", entry["response"]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unsaved_recording_is_found_and_saved_after_termrest_ended()
    {
        var recorder = Start();
        recorder.Record(Exchange("https://example.com/first"));
        recorder.Record(Exchange("https://example.com/second"));
        await recorder.WhenWritten();
        Assert.Empty(RequestRecorder.FindUnsaved(_root)); // still recording: in use

        recorder.Dispose(); // TermRest ended without saving …
        File.AppendAllText(Path.Combine(recorder.TempDirectory, RequestRecorder.SpoolFileName), "{\n  \"startedDateTime\": \"cut off whi"); // … while writing a third request
        var empty = Start();
        empty.Dispose(); // nothing recorded: nothing to recover

        var unsaved = Assert.Single(RequestRecorder.FindUnsaved(_root));
        Assert.Equal((recorder.TempDirectory, 2, "tests"), (unsaved.TempDirectory, unsaved.Requests, unsaved.WorkspaceName));
        Assert.False(Directory.Exists(empty.TempDirectory));

        using var recovered = RequestRecorder.Recover(unsaved);
        Assert.Equal((true, false, 2), (recovered.IsRecovered, recovered.IsRecording, recovered.Count));
        recovered.Record(Exchange("https://example.com/ignored"));
        var har = ReadHar(recovered.Save(Path.Combine(_root, "recovered.har")).Path);

        Assert.Equal("https://example.com/first https://example.com/second", string.Join(' ', Entries(har).Select(e => e!["request"]!["url"]!.GetValue<string>())));
        Assert.True(har["log"]!["_termrest"]!["recovered"]!.GetValue<bool>());
        Assert.Empty(RequestRecorder.FindUnsaved(_root));
    }

    [Theory]
    [InlineData(true, null, null, false, "system")] // Windows and macOS: the user's own temporary folder
    [InlineData(false, null, null, false, "system")] // Linux with /tmp on disk
    [InlineData(false, null, null, true, "/var/tmp")] // Linux with /tmp in memory (tmpfs)
    [InlineData(false, null, "/scratch", true, "system")] // $TMPDIR set: the user's choice
    [InlineData(true, "configured", null, false, "configured")] // $TERMREST_TEMP_DIR
    public void The_temporary_folder_suits_the_platform(bool privateSystemTemp, string? configured, string? tmpDir, bool tmpInMemory, string expected)
    {
        var systemTemp = Path.Combine(_root, "system");
        var custom = Path.Combine(_root, "custom");

        var chosen = RequestRecorder.ChooseTempRoot(privateSystemTemp, configured is null ? null : custom, tmpDir, systemTemp, _ => tmpInMemory, _ => true);

        Assert.Equal(expected switch { "system" => systemTemp, "configured" => custom, _ => expected }, chosen);
        Assert.Equal(systemTemp, RequestRecorder.ChooseTempRoot(false, null, null, systemTemp, _ => true, _ => false)); // no /var/tmp
    }

    public void Dispose() => _pki.Dispose();
}
