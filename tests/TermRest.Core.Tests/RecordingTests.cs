using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Core.Auth;
using TermRest.Core.Certificates;
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
        using var runner = new RequestRunner(ApiWorkspace.Open(_root)); // the workspace's file
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
        Assert.Equal("text/plain", file["contentType"]!.GetValue<string>()); // as sent: the type of its extension
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
        using var runner = new RequestRunner(ApiWorkspace.Open(_root)); // the workspace's file
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
    public async Task A_decoded_response_is_recorded_with_the_size_received_and_the_bytes_saved()
    {
        await using var server = await EchoServer.StartAsync();
        var data = Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("{\"id\":1}", 100)) + "]");
        using var gzip = new MemoryStream();
        using (var encoder = new GZipStream(gzip, CompressionLevel.Fastest, leaveOpen: true))
        {
            encoder.Write(data);
        }

        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        using var recorder = Start();
        foreach (var path in (string[])[$"bytes/{Convert.ToHexString(gzip.ToArray())}?type=application/json&encoding=gzip", $"bytes/{Convert.ToHexString(data)}?type=application/json"])
        {
            recorder.Record(await new HttpExecutor().SendAsync(preparer.Prepare(new HttpRequestSpec { Url = server.Url(path) }, null, _root), _ct));
        }

        var entries = Entries(ReadHar(recorder.Save(Path.Combine(_root, "sizes.har")).Path));

        var decoded = entries[0]!["response"]!;
        var content = decoded["content"]!;
        Assert.Equal("gzip", Header(decoded["headers"], "Content-Encoding"));
        Assert.Equal(gzip.Length, decoded["bodySize"]!.GetValue<long>());
        Assert.Equal((data.Length, data.Length - gzip.Length), (content["size"]!.GetValue<long>(), content["compression"]!.GetValue<long>()));
        Assert.Equal(Encoding.UTF8.GetString(data), content["text"]!.GetValue<string>());
        var plain = entries[1]!["response"]!;
        Assert.Equal((data.Length, data.Length), (plain["bodySize"]!.GetValue<long>(), plain["content"]!["size"]!.GetValue<long>()));
        Assert.Null(plain["content"]!["compression"]);
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
    public async Task The_password_of_a_proxy_written_with_a_space_before_it_is_redacted()
    {
        // A hand-edited .termrest/settings.json whose proxy the user allowed; nothing listens there, so the request fails.
        const string Proxy = " http://ada:Pr0xy-s3cret@127.0.0.1:1";
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        workspace.Settings.Proxy = Proxy;
        using var runner = new RequestRunner(workspace) { AllowedProxy = Proxy };
        using var recorder = Start();
        runner.Recorder = recorder;

        var exchange = await runner.SendAsync(PostmanCollection.Create("API"), null, new HttpRequestSpec { Url = "http://api.example.com/x" }, _ct);

        Assert.Null(exchange.Response);
        var path = recorder.Save(Path.Combine(_root, "proxy.har")).Path;
        Assert.DoesNotContain("Pr0xy-s3cret", await File.ReadAllTextAsync(path, _ct), StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:1", Entries(ReadHar(path))[0]!["_termrest"]!["settings"]!["proxy"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_a_test_script_keeps_is_redacted_in_the_response_it_came_from()
    {
        var python = TestPython.Require();
        await using var server = await EchoServer.StartAsync();
        const string Key = "9944b09199c62bcf9418ad846dd0e4bbdfc6ee4b";
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJpZCI6NDJ9.c2lnbmF0dXJlLXZhbHVl";
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "session", Value = "", Type = "secret" }]);
        workspace.ActiveEnvironment = environment;
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        runner.SetPythonInterpreter(python);
        using var recorder = Start();
        runner.Recorder = recorder;

        // A login answered as Django REST Framework ({"key": …}) and Strapi ({"jwt": …}) do: neither name says it is a credential.
        var login = Encoding.UTF8.GetBytes($"{{\"key\":\"{Key}\",\"jwt\":\"{Jwt}\"}}");
        var exchange = await runner.SendAsync(PostmanCollection.Create("API"), null, new HttpRequestSpec
        {
            Url = server.Url($"bytes/{Convert.ToHexString(login)}?type=application/json"),
            Scripts = new ScriptSet("", "body = tr.response.json()\ntr.environment.set('session', body['key'])\ntr.environment.set('jwtToken', body['jwt'])", ScriptLanguage.Python),
        }, _ct);
        Assert.True(exchange.Scripts!.Succeeded, exchange.Scripts.Describe());

        var path = recorder.Save(Path.Combine(_root, "login.har")).Path;

        var text = await File.ReadAllTextAsync(path, _ct);
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Jwt, text, StringComparison.Ordinal);
        Assert.Equal("{\"key\":\"[REDACTED]\",\"jwt\":\"[REDACTED]\"}", Entries(ReadHar(path))[0]!["response"]!["content"]!["text"]!.GetValue<string>());

        // The history masks the value of the variable typed secret, as it masks secrets known before sending.
        var entry = Assert.Single(runner.History.Load());
        Assert.StartsWith("{\"key\":\"***\"", Encoding.UTF8.GetString(runner.History.ReadBody(entry)!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_value_sent_for_a_secret_variable_is_redacted_whichever_scope_it_comes_from()
    {
        await using var server = await EchoServer.StartAsync();
        const string CliKey = "cli-function-key-4f9a2b";
        const string VaultValue = "vault-value-8c1d3e";
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("prod");
        environment.SetValues(
        [
            new KeyValueEntry { Key = "functionKey", Value = "", Type = "secret" },
            new KeyValueEntry { Key = "signing", Value = "{{fromVault}}", Type = "secret" },
        ]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        collection.SetVariables([new KeyValueEntry { Key = "fromVault", Value = VaultValue }]);
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        runner.LocalVariables["functionKey"] = CliKey; // termrest send … -e prod --var functionKey=…
        using var recorder = Start();
        runner.Recorder = recorder;

        // Neither name says it is a credential: only their type does.
        var exchange = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = server.Url("echo/orders") + "?code={{functionKey}}&ref={{signing}}" }, _ct);

        Assert.Equal($"?code={CliKey}&ref={VaultValue}", JsonNode.Parse(exchange.Response!.Body)!["rawQuery"]!.GetValue<string>());
        var entry = Assert.Single(runner.History.Load());
        Assert.EndsWith("/echo/orders?code=***&ref=***", entry.Url, StringComparison.Ordinal);
        var har = await File.ReadAllTextAsync(recorder.Save(Path.Combine(_root, "secret-variables.har")).Path, _ct);
        foreach (var secret in (string[])[CliKey, VaultValue])
        {
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(runner.History.ReadBody(entry)!), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, har, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_secret_sent_in_a_url_is_redacted_as_the_url_holds_it()
    {
        await using var server = await EchoServer.StartAsync();
        const string Secret = "P@ss^word|1"; // the URL escapes ^ and | but not @: neither URL encoding of the value
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("prod");
        environment.SetValues([new KeyValueEntry { Key = "pass", Value = Secret, Type = "secret" }]);
        workspace.ActiveEnvironment = environment;
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        using var recorder = Start();
        runner.Recorder = recorder;

        var exchange = await runner.SendAsync(workspace.CreateCollection("API"), null, new HttpRequestSpec { Url = server.Url("echo/login") + "?user=ada&pass={{pass}}" }, _ct);

        Assert.Equal("?user=ada&pass=P@ss%5Eword%7C1", JsonNode.Parse(exchange.Response!.Body)!["rawQuery"]!.GetValue<string>());
        var har = await File.ReadAllTextAsync(recorder.Save(Path.Combine(_root, "url-form.har")).Path, _ct);
        var stored = Encoding.UTF8.GetString(runner.History.ReadBody(Assert.Single(runner.History.Load()))!);
        foreach (var form in (string[])[Secret, "P@ss%5Eword%7C1"])
        {
            Assert.DoesNotContain(form, har, StringComparison.Ordinal);
            Assert.DoesNotContain(form, stored, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_secret_with_a_hash_or_a_question_mark_is_redacted_as_the_url_holds_it()
    {
        await using var server = await EchoServer.StartAsync();
        const string Secret = "P@ss#w?rd 1";
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("prod");
        environment.SetValues([new KeyValueEntry { Key = "pass", Value = Secret, Type = "secret" }]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        using var recorder = Start();
        runner.Recorder = recorder;

        // Its '#' is sent as %23, and in a path variable its '?' as %3F: neither URL encoding of the value gives these forms.
        var query = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = server.Url("echo/login") + "?user=ada&pass={{pass}}" }, _ct);
        var path = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = server.Url("echo/keys/:id"), PathVariables = [new() { Key = "id", Value = "{{pass}}" }] }, _ct);

        Assert.Equal("?user=ada&pass=P@ss%23w?rd%201", JsonNode.Parse(query.Response!.Body)!["rawQuery"]!.GetValue<string>());
        Assert.Equal(server.BaseUri + "echo/keys/P@ss%23w%3Frd%201", path.Request.Uri!.AbsoluteUri);
        var entries = runner.History.Load();
        Assert.Equal([server.Url("echo/keys/***"), server.Url("echo/login") + "?user=ada&pass=***"], entries.Select(e => e.Url));
        var har = await File.ReadAllTextAsync(recorder.Save(Path.Combine(_root, "hash-form.har")).Path, _ct);
        var stored = string.Concat(entries.Select(e => Encoding.UTF8.GetString(runner.History.ReadBody(e)!)));
        foreach (var form in (string[])[Secret, "P@ss%23w?rd", "P@ss%23w%3Frd"])
        {
            Assert.DoesNotContain(form, har, StringComparison.Ordinal);
            Assert.DoesNotContain(form, stored, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_sent_as_the_basic_user_name_is_in_neither_the_recording_nor_a_report_nor_the_history(bool inUrl)
    {
        // Stripe's documented curl takes the secret key as the user name, without a password (-u sk_test_…:, imported as Basic
        // auth); in the URL, it is its user info. The response quotes the key, as an error message might. (Stripe's sample test
        // key, split so that secret scanning doesn't take it for a real one.)
        const string Key = "sk_test_" + "4eC39HqLyjWDarjtT1zdp7dc";
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        using var recorder = Start();
        runner.Recorder = recorder;
        var url = server.Url($"bytes/{Convert.ToHexString(Encoding.UTF8.GetBytes($"{{\"error\":\"No such key: {Key}\"}}"))}?type=application/json");
        var auth = new AuthSpec { Type = "basic" };
        auth.Set("username", Key);
        auth.Set("password", "");
        var spec = inUrl ? new HttpRequestSpec { Url = url.Replace("://", $"://{Key}:@", StringComparison.Ordinal) } : new HttpRequestSpec { Url = url, Auth = auth };

        var exchange = await runner.SendAsync(workspace.CreateCollection("API"), null, spec, _ct);

        Assert.Contains(Key, Encoding.UTF8.GetString(exchange.Response!.Body), StringComparison.Ordinal);
        var path = recorder.Save(Path.Combine(_root, "stripe.har")).Path;
        var report = new Redactor().ForReport(exchange); // as termrest run --report-json redacts its report
        var entry = Assert.Single(runner.History.Load());
        foreach (var text in (string[])
        [
            await File.ReadAllTextAsync(path, _ct),
            report.Url(exchange.Request.Url, "url") + report.Text($"expected '{Key}' to be revoked", "tests"),
            await File.ReadAllTextAsync(runner.History.FilePath, _ct),
            Encoding.UTF8.GetString(runner.History.ReadBody(entry)!),
        ])
        {
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        }

        Assert.Equal(inUrl ? "Basic (from the URL)" : "Basic", Entries(ReadHar(path))[0]!["_termrest"]!["auth"]!.GetValue<string>());
        Assert.True(HistoryEntry.HasMaskedCredentials(entry.ReadSpec())); // sending it again asks for the key
    }

    [Fact]
    public void A_secret_is_redacted_where_json_escapes_it_as_python_or_go_does()
    {
        // Python's json.dumps (Flask, Django, httpbin, TermRest's Python scripts) writes non-ASCII characters as lower-case
        // \u escapes; Go's encoding/json writes < > & as \u003c \u003e \u0026.
        using var recorder = Start();
        var exchange = Exchange("https://example.com/login", """{"form":{"pin":"Gr\u00fc\u00dfe-2024"},"go":"k3y\u003c\u0026\u003ev4lue"}"""u8.ToArray(), method: "POST");
        exchange.Request.Body = new PreparedBody { Kind = PreparedBodyKind.Text, Text = """{"pin": "Gr\u00fc\u00dfe-2024"}""", ContentType = "application/json" };
        exchange.Request.SecretValues.UnionWith(["Grüße-2024", "k3y<&>v4lue"]);

        recorder.Record(exchange);
        var entry = Assert.Single(Entries(ReadHar(recorder.Save(Path.Combine(_root, "escaped.har")).Path)))!;

        Assert.Equal("""{"pin": "[REDACTED]"}""", entry["request"]!["postData"]!["text"]!.GetValue<string>());
        Assert.Equal("""{"form":{"pin":"[REDACTED]"},"go":"[REDACTED]"}""", entry["response"]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_body_in_a_charset_dotnet_does_not_support_is_recorded_as_text()
    {
        using var recorder = Start();
        recorder.Record(Exchange("https://example.com/utf7", [.. "Hi Mom -+Jjo--! "u8, 0xE9], "text/plain; charset=utf-7"));

        var har = ReadHar(recorder.Save(Path.Combine(_root, "utf7.har")).Path);

        Assert.Null(recorder.LastError);
        Assert.Equal("Hi Mom -+Jjo--! \uFFFD", Assert.Single(Entries(har))!["response"]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_body_in_a_legacy_code_page_is_recorded_as_the_text_it_is()
    {
        using var recorder = Start();
        recorder.Record(Exchange("https://example.com/legacy", [.. "caf"u8, 0xE9], "text/plain; charset=windows-1252"));

        var har = ReadHar(recorder.Save(Path.Combine(_root, "legacy.har")).Path);

        Assert.Equal("café", Assert.Single(Entries(har))!["response"]!["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_text_body_whose_byte_order_mark_says_utf16_or_utf32_is_recorded_as_text_with_its_secrets_redacted()
    {
        const string Secret = "s3cr3t-wide-value";
        using var recorder = Start();
        foreach (var encoding in (Encoding[])[Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32])
        {
            // No charset: only the mark says how the echoed secret is encoded.
            var exchange = Exchange("https://example.com/wide", [.. encoding.Preamble, .. encoding.GetBytes($"<echo>{Secret}</echo>")], "text/xml");
            exchange.Request.SecretValues.Add(Secret);
            recorder.Record(exchange);
        }

        var path = recorder.Save(Path.Combine(_root, "wide.har")).Path;

        Assert.All(Entries(ReadHar(path)), entry =>
        {
            var content = entry!["response"]!["content"]!;
            Assert.Null(content["encoding"]);
            Assert.Equal("<echo>[REDACTED]</echo>", content["text"]!.GetValue<string>());
        });
        Assert.DoesNotContain(Secret, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_utf16_body_without_a_byte_order_mark_is_recorded_in_the_byte_order_its_text_shows()
    {
        // RFC 2781 lets an unmarked "UTF-16" be either byte order: a SOAP fault, say, that echoes the token.
        const string Secret = "s3cr3t-token-value";
        using var recorder = Start();
        foreach (var encoding in (Encoding[])[Encoding.BigEndianUnicode, Encoding.Unicode])
        {
            var exchange = Exchange("https://example.com/soap", encoding.GetBytes($"<faultstring>Token {Secret} is not valid</faultstring>"), "text/xml; charset=\"UTF-16\"");
            exchange.Request.SecretValues.Add(Secret);
            recorder.Record(exchange);
        }

        var path = recorder.Save(Path.Combine(_root, "unmarked.har")).Path;

        Assert.All(Entries(ReadHar(path)), entry =>
            Assert.Equal("<faultstring>Token [REDACTED] is not valid</faultstring>", entry!["response"]!["content"]!["text"]!.GetValue<string>()));
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
    public async Task Names_added_while_recording_are_redacted_in_the_requests_recorded_from_then_on()
    {
        var recorder = Start("ssn");
        var before = Exchange("https://example.com/before");
        before.SentHeaders.Add(new("X-Tenant-Key", "t-123456"));
        recorder.Record(before);
        await recorder.WhenWritten();

        Assert.Equal(["Tenant-Key"], recorder.AddRedactNames(["SSN", "Tenant-Key"]));
        Assert.Empty(recorder.AddRedactNames(["tenant_key"])); // the same name
        var after = Exchange("https://example.com/after");
        after.SentHeaders.Add(new("X-Tenant-Key", "t-123456"));
        recorder.Record(after);
        await recorder.WhenWritten();
        recorder.Dispose(); // TermRest ended: the recording recovered next time lists the names too

        using var recovered = RequestRecorder.Recover(Assert.Single(RequestRecorder.FindUnsaved(_root)));
        var har = ReadHar(recovered.Save(Path.Combine(_root, "added.har")).Path);

        Assert.Equal(["t-123456", "[REDACTED]"], Entries(har).Select(e => Header(e!["request"]!["headers"], "X-Tenant-Key")));
        Assert.Equal(["ssn", "Tenant-Key"], har["log"]!["_termrest"]!["redaction"]!["extraNames"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Variables_named_like_a_name_removed_from_the_settings_while_recording_stay_redacted()
    {
        const string tenant = "contoso-7f3a9b2c";
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        workspace.Settings.RecordingRedact = ["tenant-id"];
        using var runner = new RequestRunner(workspace);
        runner.LocalVariables["base"] = server.BaseUri.ToString().TrimEnd('/');
        runner.LocalVariables["tenantId"] = tenant; // not typed "secret": redacted for its name
        var collection = PostmanCollection.Create("Recorded");
        using var recorder = Start("tenant-id");
        runner.Recorder = recorder;
        var spec = new HttpRequestSpec { Url = "{{base}}/echo/tenants/{{tenantId}}/users" };

        await runner.SendAsync(runner.Prepare(collection, null, spec), _ct);
        workspace.Settings.RecordingRedact = null; // removed in the settings: the recording goes on redacting it
        await runner.SendAsync(runner.Prepare(collection, null, spec), _ct);
        var path = recorder.Save(Path.Combine(_root, "removed.har")).Path;

        Assert.DoesNotContain(tenant, await File.ReadAllTextAsync(path, _ct), StringComparison.Ordinal);
        Assert.Equal(
            [$"{server.BaseUri}echo/tenants/[REDACTED]/users", $"{server.BaseUri}echo/tenants/[REDACTED]/users"],
            Entries(ReadHar(path)).Select(e => e!["request"]!["url"]!.GetValue<string>()));
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
        Assert.Equal((recorder.TempDirectory, "tests"), (unsaved.TempDirectory, unsaved.WorkspaceName));
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

    [Fact]
    public void Secrets_are_redacted_by_name_in_json_that_is_cut_off_not_utf8_or_labelled_as_text()
    {
        using var recorder = Start();
        recorder.Record(Exchange("https://example.com/cut", Encoding.UTF8.GetBytes("{\"access_token\":\"tok-Cut-12345678\",\"note\":\"café")[..^1]));
        recorder.Record(Exchange("https://example.com/latin1", [.. "{\"name\":\"Caf"u8, 0xE9, .. "\",\"access_token\":\"tok-Latin-12345678\"}"u8]));
        recorder.Record(Exchange("https://example.com/html", "{\"access_token\":\"tok-Html-12345678\",\"token_type\":\"Bearer\"}"u8.ToArray(), "text/html"));

        // After a UTF-8 byte order mark: JSON from a file saved with one (as PHP echoes it, as text/html or with no type).
        byte[] bom = [0xEF, 0xBB, 0xBF];
        recorder.Record(Exchange("https://example.com/bom", [.. bom, .. "{\"access_token\":\"tok-Bom-12345678\"}"u8], "text/html; charset=UTF-8"));
        recorder.Record(Exchange("https://example.com/bom-unlabelled", [.. bom, .. " [{\"access_token\":\"tok-Bare-12345678\"}]"u8], null));
        recorder.Record(Exchange("https://example.com/bom-json", [.. bom, .. "{\"access_token\":\"tok-Json-12345678\"}"u8]));

        var path = recorder.Save(Path.Combine(_root, "text.har")).Path;

        foreach (var entry in Entries(ReadHar(path)))
        {
            var content = entry!["response"]!["content"]!;
            Assert.Null(content["encoding"]);
            Assert.Contains("\"access_token\":\"[REDACTED]\"", content["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("tok-", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_raw_form_data_body_is_recorded_as_text_without_the_values_of_parts_named_like_credentials()
    {
        // A browser's "Copy as cURL" of a login form, imported: a raw body, and the Content-Type with its boundary.
        const string Body = "------WebKitFormBoundaryX\r\nContent-Disposition: form-data; name=\"password\"\r\n\r\nHunter2-Secret\r\n"
            + "------WebKitFormBoundaryX\r\nContent-Disposition: form-data; name=\"credentials\"; filename=\"credentials.json\"\r\nContent-Type: application/json\r\n\r\n{\"type\":\"service_account\"}\r\n"
            + "------WebKitFormBoundaryX--\r\n";
        using var recorder = Start();
        var login = Exchange("https://example.com/login", "{\"echo\":\"Hunter2-Secret\"}"u8.ToArray(), method: "POST");
        login.Request.Body = new PreparedBody { Kind = PreparedBodyKind.Text, Text = Body };
        login.SentHeaders.Add(new("Content-Type", "multipart/form-data; boundary=----WebKitFormBoundaryX"));
        recorder.Record(login);
        recorder.Record(Exchange("https://example.com/me?pw=Hunter2-Secret"));

        var path = recorder.Save(Path.Combine(_root, "form-data.har")).Path;

        Assert.DoesNotContain("Hunter2-Secret", File.ReadAllText(path), StringComparison.Ordinal);
        var entries = Entries(ReadHar(path));
        var postData = entries[0]!["request"]!["postData"]!;
        Assert.Null(postData["_encoding"]);
        Assert.Equal(Body.Replace("Hunter2-Secret", "[REDACTED]", StringComparison.Ordinal), postData["text"]!.GetValue<string>()); // the file as sent
        Assert.Contains(entries[0]!["_termrest"]!["redacted"]!.AsArray(), n => n!["in"]!.GetValue<string>() == "request.postData" && n["name"]?.GetValue<string>() == "password");
        Assert.Equal("{\"echo\":\"[REDACTED]\"}", entries[0]!["response"]!["content"]!["text"]!.GetValue<string>());
        Assert.Equal("https://example.com/me?pw=[REDACTED]", entries[1]!["request"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_text_body_that_is_not_utf8_at_all_is_written_as_replacement_characters()
    {
        var recorder = Start(); // not disposed if writing hangs: disposing would wait for it
        recorder.Record(Exchange("https://example.com/garbage", Enumerable.Repeat((byte)0x80, 200_000).ToArray(), "text/plain"));

        await recorder.WhenWritten().WaitAsync(TimeSpan.FromSeconds(30), _ct);
        var content = Entries(ReadHar(recorder.Save(Path.Combine(_root, "garbage.har")).Path))[0]!["response"]!["content"]!;

        Assert.Equal(new string('\uFFFD', 200_000), content["text"]!.GetValue<string>());
    }

    [Fact]
    public void Only_private_folders_are_recovered_and_damaged_details_are_ignored()
    {
        string Leftover(string details, bool shared = false)
        {
            var directory = Path.Combine(_root, RequestRecorder.DirectoryPrefix + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                // As TermRest makes them (0700), or readable by others (0755): possibly another user's, in a shared /tmp.
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                mode |= shared ? UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute : 0;
                Directory.CreateDirectory(directory, mode);
                File.SetUnixFileMode(directory, mode);
            }

            File.WriteAllText(Path.Combine(directory, RequestRecorder.SpoolFileName), "{\n}\n");
            File.WriteAllText(Path.Combine(directory, RequestRecorder.InfoFileName), details);
            return directory;
        }

        string[] damaged = ["{\"startedAt\": 1}", "{\"startedAt\": \"2026-11-05T13:29:29-07:00\", \"processId\": \"42\"}", "[]", "{"];
        var expected = damaged.Select(details => Leftover(details)).ToList();
        if (!OperatingSystem.IsWindows())
        {
            Leftover("{}", shared: true);
        }

        Assert.Equal(expected.Order(), RequestRecorder.FindUnsaved(_root).Select(u => u.TempDirectory).Order());
    }

    [Fact]
    public void The_temporary_file_is_read_in_pieces_and_finding_a_recording_stops_at_its_first_request()
    {
        var first = "{\n  \"a\": \"" + new string('x', 65_523) + "\"\n}"; // its last line break is the first byte of the second read
        var second = "{\n  \"b\": 1\n}";
        var spool = Encoding.UTF8.GetBytes(first + "\n" + second + "\n{\n  \"cut\": \"off" + new string(' ', 400_000));
        using var stream = new MemoryStream(spool);

        Assert.Equal([(0L, 65_536L), (65_537L, (long)second.Length)], RequestRecorder.Scan(stream).Select(e => (e.Offset, e.Length)));

        Assert.Single(RequestRecorder.Scan(stream, max: 1));
        Assert.True(stream.Position < spool.Length / 2, $"{stream.Position} of {spool.Length} bytes read.");
    }

    [Fact]
    public void Recording_waits_when_requests_are_sent_faster_than_they_are_written()
    {
        using var recorder = Start();
        var body = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(body);
        var most = 0;
        for (var i = 0; i < 12; i++)
        {
            recorder.Record(Exchange($"https://example.com/{i}", body, "application/octet-stream"));
            most = Math.Max(most, recorder.PendingWrites);
        }

        Assert.True(most <= RequestRecorder.MaxPendingWrites, $"{most} requests waited to be written.");
        Assert.Equal(12, recorder.Count);
        recorder.Discard();
    }

    public void Dispose() => _pki.Dispose();
}
