using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Core.Certificates;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>End-to-end tests against in-process Kestrel servers (HTTP, HTTPS and mutual TLS).</summary>
public sealed class HttpExecutorTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static PreparedRequest Prepare(HttpRequestSpec spec, ExecutionDefaults? defaults = null, ClientCertificateProvider? certificates = null, AuthSpec? auth = null, VariableResolver? variables = null) =>
        new RequestPreparer(variables ?? VariableResolver.Empty, certificates, defaults ?? new ExecutionDefaults()).Prepare(spec, auth ?? spec.Auth, AppContext.BaseDirectory);

    private static JsonObject Json(HttpExchange exchange)
    {
        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        return (JsonObject)PostmanJson.Parse(Encoding.UTF8.GetString(exchange.Response!.Body));
    }

    [Fact]
    public async Task Sends_method_query_headers_and_json_body()
    {
        await using var server = await EchoServer.StartAsync();
        var spec = new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo/items?a=1&b={{b}}"),
            Headers = [new() { Key = "X-Test", Value = "{{who}}" }, new() { Key = "X-Off", Value = "no", Enabled = false }],
            Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"name\":\"{{who}}\"}" },
        };
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(spec.Url, []);
        spec.QueryParams.Add(new KeyValueEntry { Key = "disabled", Value = "x", Enabled = false });
        var variables = new VariableResolver([new DictionaryScope("t", new Dictionary<string, string> { ["b"] = "two", ["who"] = "ada" })]);

        var exchange = await new HttpExecutor().SendAsync(Prepare(spec, variables: variables), _ct);

        var echo = Json(exchange);
        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.Equal("POST", echo["method"].GetString());
        Assert.Equal("/echo/items", echo["path"].GetString());
        Assert.Equal("?a=1&b=two", echo["rawQuery"].GetString());
        Assert.Equal("ada", echo["headers"]!["x-test"].GetString());
        Assert.Null(echo["headers"]!["x-off"]);
        Assert.StartsWith("application/json", echo["headers"]!["content-type"].GetString(), StringComparison.Ordinal);
        Assert.Equal("{\"name\":\"ada\"}", echo["body"].GetString());
        Assert.StartsWith("TermRest/", echo["headers"]!["user-agent"].GetString(), StringComparison.Ordinal);
        Assert.NotNull(exchange.Timing.Connect);
        Assert.NotNull(exchange.Timing.Total);
    }

    [Fact]
    public async Task Sends_urlencoded_multipart_and_file_bodies()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "upload.txt");
        await File.WriteAllTextAsync(file, "file-content", _ct);
        var executor = new HttpExecutor();

        var urlencoded = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.UrlEncoded, UrlEncoded = [new() { Key = "role", Value = "admin & owner" }, new() { Key = "x", Value = "1", Enabled = false }] },
        }), _ct));
        Assert.Equal("role=admin+%26+owner", urlencoded["body"].GetString());

        var multipart = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "text", Value = "hello" }, new() { Key = "doc", Type = "file", Src = file }] },
        }), _ct));
        Assert.StartsWith("multipart/form-data", multipart["headers"]!["content-type"].GetString(), StringComparison.Ordinal);
        Assert.Contains("file-content", multipart["body"].GetString(), StringComparison.Ordinal);
        Assert.Contains("filename=upload.txt", multipart["body"].GetString(), StringComparison.Ordinal);

        var binary = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "PUT",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file },
        }), _ct));
        Assert.Equal("file-content", binary["body"].GetString());
        Assert.Equal("application/octet-stream", binary["headers"]!["content-type"].GetString());
    }

    [Fact]
    public async Task Typed_body_files_are_checked_when_sent_and_get_their_content_type()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "order.json");
        await File.WriteAllTextAsync(file, "{\"item\": \"book\"}", _ct);
        var executor = new HttpExecutor();
        HttpRequestSpec Spec(params KeyValueEntry[] headers) => new()
        {
            Method = "POST",
            Url = server.Url("echo"),
            Headers = [.. headers],
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file, FileLanguage = "json" },
        };

        var sent = Json(await executor.SendAsync(Prepare(Spec()), _ct));
        Assert.Equal("{\"item\": \"book\"}", sent["body"].GetString());
        Assert.Equal("application/json", sent["headers"]!["content-type"].GetString());
        var patch = Json(await executor.SendAsync(Prepare(Spec(new KeyValueEntry { Key = "Content-Type", Value = "application/merge-patch+json" })), _ct));
        Assert.Equal("application/merge-patch+json", patch["headers"]!["content-type"].GetString());
        Assert.Contains("--data-binary '@" + file + "' \\\n  -H 'Content-Type: application/json'", CurlExporter.Export(Prepare(Spec())), StringComparison.Ordinal);

        // Edited into invalid JSON after it was chosen: it is checked again when sent, and not sent.
        await File.WriteAllTextAsync(file, "{\"item\": book}", _ct);
        var prepared = Prepare(Spec());
        var refused = await executor.SendAsync(prepared, _ct);
        Assert.Null(refused.Response);
        Assert.Null(refused.Timing.Connect);
        Assert.Equal("Not sent: order.json is not valid JSON: 'b' is an invalid start of a value (line 1, column 10).", refused.ErrorMessage);
        Assert.Single(prepared.Errors); // so history and recordings leave it out
    }

    [Fact]
    public async Task Applies_auth_helpers()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var spec = new HttpRequestSpec { Url = server.Url("echo") };

        var basic = new AuthSpec { Type = "basic" };
        basic.Set("username", "ada");
        basic.Set("password", "secret");
        Assert.Equal("Basic YWRhOnNlY3JldA==", Json(await executor.SendAsync(Prepare(spec, auth: basic), _ct))["headers"]!["authorization"].GetString());

        var apikey = new AuthSpec { Type = "apikey" };
        apikey.Set("key", "api_key");
        apikey.Set("value", "k1");
        apikey.Set("in", "query");
        Assert.Equal("k1", Json(await executor.SendAsync(Prepare(spec, auth: apikey), _ct))["query"]!["api_key"].GetString());

        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "t0k");
        Assert.Equal("Bearer t0k", Json(await executor.SendAsync(Prepare(spec, auth: bearer), _ct))["headers"]!["authorization"].GetString());
    }

    [Fact]
    public async Task Follows_redirects_and_keeps_cookies()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();

        var redirected = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("redirect") }), _ct);
        Assert.Equal("true", Json(redirected)["query"]!["redirected"].GetString());

        var notFollowed = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("redirect"), Settings = new RequestSettings { FollowRedirects = false } }), _ct);
        Assert.Equal(302, notFollowed.Response!.StatusCode);

        await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("set-cookie") }), _ct);
        Assert.Contains("session=abc123", Json(await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }), _ct))["headers"]!["cookie"].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_status_codes_timeouts_cancellation_and_connection_errors()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();

        Assert.Equal(503, (await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("status/503") }), _ct)).Response!.StatusCode);

        var timedOut = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("slow") }, new ExecutionDefaults { Timeout = TimeSpan.FromMilliseconds(200) }), _ct);
        Assert.Contains("timed out", timedOut.ErrorMessage, StringComparison.Ordinal);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.True((await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("slow") }), cancel.Token)).Cancelled);

        var refused = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = "http://127.0.0.1:1/" }), _ct);
        Assert.Null(refused.Response);
        Assert.False(string.IsNullOrEmpty(refused.ErrorMessage));

        var invalid = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = "{{undefinedBase}}/x" }), _ct);
        Assert.Contains("not a valid", invalid.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("undefinedBase", invalid.Request.UnresolvedVariables);
    }

    [Fact]
    public async Task A_stalled_tls_handshake_is_reported_as_a_connect_timeout()
    {
        // The OS completes the TCP handshake for the listener's backlog, but nothing ever answers the TLS ClientHello.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var executor = new HttpExecutor { ConnectTimeout = TimeSpan.FromSeconds(1) };

        var exchange = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = $"https://127.0.0.1:{port}/" }), _ct);

        Assert.Null(exchange.Response);
        Assert.False(exchange.Cancelled);
        Assert.Contains($"TLS handshake with 127.0.0.1:{port} did not complete within", exchange.ErrorMessage, StringComparison.Ordinal);
        Assert.NotNull(exchange.Timing.Connect);
    }

    [Fact]
    public async Task Requests_that_cannot_be_built_report_an_error_instead_of_throwing()
    {
        var executor = new HttpExecutor();

        var badMethod = await executor.SendAsync(Prepare(new HttpRequestSpec { Method = "{{verb}}", Url = "http://127.0.0.1:1/" }), _ct);
        Assert.Contains("is not a valid HTTP method", badMethod.ErrorMessage, StringComparison.Ordinal);

        var badProxy = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = "http://127.0.0.1:1/" }, new ExecutionDefaults { Proxy = "http://bad proxy:x" }), _ct);
        Assert.Null(badProxy.Response);
        Assert.False(string.IsNullOrEmpty(badProxy.ErrorMessage));
    }

    [Fact]
    public async Task Reloading_certificates_does_not_break_a_request_that_already_selected_one()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        workspace.Settings.CaBundle = _pki.CaPemPath;
        workspace.Certificates.Entries.Add(new CertificateEntry
        {
            Name = "client",
            Matches = [$"https://localhost:{server.BaseUri.Port}/*"],
            Pfx = new FileSource { Src = _pki.ClientPfxPath },
            Passphrase = TestPki.Passphrase,
        });
        using var runner = new RequestRunner(workspace);
        var prepared = runner.Prepare(PostmanCollection.Create("t"), null, new HttpRequestSpec { Url = server.Url("echo") });
        Assert.NotNull(prepared.ClientCertificate);

        runner.ReloadCertificates(); // the user edits certificates (F8) while this request is in flight

        var exchange = await runner.SendAsync(prepared, _ct);
        Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
        Assert.Equal(_pki.Client.Thumbprint, exchange.Tls.Single().ClientCertificate?.Thumbprint);
        Assert.Equal(_pki.Client.Thumbprint, prepared.ClientCertificate.Summary.Thumbprint);
    }

    [Fact]
    public async Task Untrusted_server_certificate_is_rejected_unless_trusted_or_verification_is_off()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server);
        var executor = new HttpExecutor();
        var spec = new HttpRequestSpec { Url = server.Url("echo") };

        var strict = await executor.SendAsync(Prepare(spec), _ct);
        Assert.Null(strict.Response);
        Assert.Contains("rejected", strict.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Assert.Single(strict.Tls).Accepted);

        var insecure = await executor.SendAsync(Prepare(spec, new ExecutionDefaults { StrictSsl = false }), _ct);
        Assert.Equal(200, insecure.Response!.StatusCode);
        Assert.Contains("turned off", insecure.Tls.Single().AcceptReason, StringComparison.Ordinal);

        var perRequest = new HttpRequestSpec { Url = server.Url("echo"), Settings = new RequestSettings { StrictSsl = false } };
        Assert.Equal(200, (await executor.SendAsync(Prepare(perRequest), _ct)).Response!.StatusCode);

        var trusted = await executor.SendAsync(Prepare(spec, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }), _ct);
        Assert.Equal(200, trusted.Response!.StatusCode);
        var tls = trusted.Tls.Single();
        Assert.True(tls.Accepted);
        Assert.Contains("CA bundle", tls.AcceptReason, StringComparison.Ordinal);
        Assert.NotEqual(System.Security.Authentication.SslProtocols.None, tls.Protocol);
        Assert.Equal("CN=localhost", tls.ServerChain[0].Subject);
    }

    [Theory]
    [InlineData("pem")]
    [InlineData("pem-encrypted")]
    [InlineData("pfx")]
    [InlineData("pem-chain")]
    public async Task Mutual_tls_presents_the_matching_client_certificate(string kind)
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        var entry = kind switch
        {
            "pem" => new CertificateEntry { Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } },
            "pem-chain" => new CertificateEntry { Cert = new FileSource { Src = "client-chain.pem" }, Key = new FileSource { Src = "client.key" } },
            "pem-encrypted" => new CertificateEntry { Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.enc.key" }, Passphrase = "{{keyPass}}" },
            _ => new CertificateEntry { Pfx = new FileSource { Src = "client.pfx" }, Passphrase = TestPki.Passphrase },
        };
        entry.Name = kind;
        entry.Matches = [$"https://localhost:{server.BaseUri.Port}/*"];
        var registry = new CertificateRegistry(_pki.Directory) { Entries = [entry] };
        using var provider = new ClientCertificateProvider(registry);
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["keyPass"] = TestPki.Passphrase })]);
        var defaults = new ExecutionDefaults { CaBundlePath = _pki.CaPemPath };

        var prepared = Prepare(new HttpRequestSpec { Url = server.Url("echo") }, defaults, provider, variables: variables);
        Assert.Empty(prepared.Warnings);
        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        var echo = Json(exchange);
        Assert.Equal(_pki.Client.Thumbprint, echo["clientCertificateThumbprint"].GetString());
        var tls = exchange.Tls.Single();
        Assert.True(tls.ClientCertificate is not null, tls.Describe());
        Assert.Equal(_pki.Client.Thumbprint, tls.ClientCertificate.Thumbprint);
        Assert.Contains("matched", tls.ClientCertificateOrigin, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutual_tls_without_a_certificate_fails_with_a_clear_error()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }), _ct);

        var diagnostics = exchange.Response is null ? "" : Encoding.UTF8.GetString(exchange.Response.Body) + " | " + string.Join(" | ", exchange.Tls.Select(t => t.Describe()));
        Assert.True(exchange.Response is null, diagnostics);
        Assert.False(string.IsNullOrEmpty(exchange.ErrorMessage));
    }

    [Fact]
    public async Task Request_pinned_certificate_is_used()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory));
        var spec = new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Certificate = new CertificateSpec { Name = "pinned", CertSrc = _pki.ClientCertPemPath, KeySrc = _pki.ClientKeyPemPath },
        };

        var exchange = await new HttpExecutor().SendAsync(Prepare(spec, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

        Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
    }

    /// <summary>
    /// Imports the client certificate into the Windows CurrentUser\My store with a NON-EXPORTABLE key, uses it by
    /// thumbprint, then removes it. Opt-in because it modifies the user's certificate store.
    /// </summary>
    [Fact]
    public async Task Mutual_tls_with_non_exportable_key_from_the_windows_store()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("TERMREST_STORE_TESTS") == "1",
            "Set TERMREST_STORE_TESTS=1 on Windows to run (temporarily adds a certificate to CurrentUser\\My).");

        using var stored = X509CertificateLoader.LoadPkcs12FromFile(_pki.ClientPfxPath, TestPki.Passphrase, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(stored);
        try
        {
            await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
            var registry = new CertificateRegistry(_pki.Directory)
            {
                Entries = [new CertificateEntry { Name = "store", Matches = ["https://localhost/*"], Store = new StoreSource { Thumbprint = stored.Thumbprint } }],
            };
            using var provider = new ClientCertificateProvider(registry);

            var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

            Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            {
                using var fromStore = CertificateLoader.LoadFromStore(new StoreSource { Thumbprint = stored.Thumbprint });
                fromStore.Certificate.Export(X509ContentType.Pkcs12);
            });
        }
        finally
        {
            store.Remove(stored);
        }
    }

    [Fact]
    public async Task Body_formatter_pretty_prints_json_and_xml()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();

        var json = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }), _ct);
        var pretty = BodyFormatter.Format(json.Response!.Body, json.Response.ContentType);
        Assert.Contains("\n  \"method\": \"GET\"", pretty, StringComparison.Ordinal);
        Assert.Equal("json", BodyFormatter.Language(json.Response.Body, json.Response.ContentType));

        var xml = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("xml") }), _ct);
        Assert.Contains("\n  <item id=\"1\">x</item>", BodyFormatter.Format(xml.Response!.Body, xml.Response.ContentType), StringComparison.Ordinal);

        Assert.StartsWith("00000000  00 01 ff", BodyFormatter.Format([0, 1, 255], "application/octet-stream"), StringComparison.Ordinal);
    }

    [Fact]
    public void Curl_export_quotes_values()
    {
        var prepared = Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://api.example.com/x?q=it's",
            Headers = [new() { Key = "Accept", Value = "application/json" }],
            Body = new RequestBody { Mode = BodyMode.Raw, Raw = "{\"a\":\"it's\"}", RawLanguage = "json" },
            Settings = new RequestSettings { StrictSsl = false },
        });

        var curl = CurlExporter.Export(prepared, multiline: false);

        Assert.Equal(
            "curl -X POST 'https://api.example.com/x?q=it'\\''s' -H 'Accept: application/json' --data-raw '{\"a\":\"it'\\''s\"}' -H 'Content-Type: application/json' --insecure --location",
            curl);
    }

    public void Dispose() => _pki.Dispose();
}
