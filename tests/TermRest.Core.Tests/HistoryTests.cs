using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class HistoryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static HistoryStore NewStore(int maxEntries = 500) => new(Path.Combine(TestFiles.NewTempDirectory(), "history"), maxEntries);

    private static HistoryEntry Entry(string url, int? status = 200, bool bodySaved = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Time = new DateTimeOffset(2026, 11, 4, 21, 22, 53, TimeSpan.FromHours(-7)),
        Method = "POST",
        Url = url,
        Item = new JsonObject { ["name"] = "Create item", ["request"] = new JsonObject { ["method"] = "POST", ["url"] = "{{base}}/items" } },
        Collection = "API",
        RequestPath = "Items/Create item",
        Environment = "dev",
        SentHeaders = [new("Accept", "*/*"), new("Authorization", "Bearer ***")],
        Status = status,
        Reason = status is null ? null : "Created",
        HttpVersion = "1.1",
        Size = 12,
        ContentType = "application/json",
        ResponseHeaders = [new("Content-Type", "application/json"), new("Set-Cookie", "a=1"), new("Set-Cookie", "b=2")],
        BodySaved = bodySaved,
        Error = status is null ? "Connection refused" : null,
        Timing = new TimingBreakdown { Dns = TimeSpan.FromMilliseconds(2), Connect = TimeSpan.FromMilliseconds(10), Total = TimeSpan.FromMilliseconds(120), RemoteEndPoint = "127.0.0.1:443" },
    };

    [Fact]
    public void Store_round_trips_entries_newest_first_and_skips_damaged_lines()
    {
        var store = NewStore();
        var created = Entry("https://h/items?a=1&note=line1\nline2");
        var failed = Entry("https://h/down", status: null);

        store.Add(created);
        File.AppendAllText(store.FilePath, "{not json\n\n");
        store.Add(failed);

        var loaded = store.Load();
        Assert.Equal([failed.Id, created.Id], loaded.Select(e => e.Id));
        var entry = loaded[1];
        Assert.Equal((created.Time, created.Method, created.Url), (entry.Time, entry.Method, entry.Url));
        Assert.Equal(("API", "Items/Create item", "dev"), (entry.Collection, entry.RequestPath, entry.Environment));
        Assert.Equal((200, "Created", "1.1", 12L, "application/json"), (entry.Status!.Value, entry.Reason, entry.HttpVersion, entry.Size!.Value, entry.ContentType));
        Assert.Equal(created.SentHeaders, entry.SentHeaders);
        Assert.Equal(created.ResponseHeaders, entry.ResponseHeaders);
        Assert.Equal(TimeSpan.FromMilliseconds(120), entry.Timing.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(10), entry.Timing.Connect);
        Assert.Equal("127.0.0.1:443", entry.Timing.RemoteEndPoint);
        Assert.Equal("{{base}}/items", entry.ReadSpec().Url);
        Assert.Equal("Create item", entry.Name);
        Assert.Equal("Connection refused", loaded[0].Error);
        Assert.Null(loaded[0].Status);
        Assert.Equal(3, File.ReadAllLines(store.FilePath).Count(l => l.Length > 0));
    }

    [Fact]
    public void Store_keeps_response_bodies_and_compacts_to_the_limit()
    {
        var store = NewStore(maxEntries: 3);
        var entries = Enumerable.Range(0, 14).Select(i => Entry($"https://h/{i}", bodySaved: true)).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            store.Add(entries[i], [(byte)i, 0xFF]);
        }

        var loaded = store.Load();
        Assert.Equal(["https://h/13", "https://h/12", "https://h/11"], loaded.Select(e => e.Url));
        Assert.Equal([13, 0xFF], store.ReadBody(loaded[0]));
        Assert.Equal(3, Directory.GetFiles(store.BodiesFolder).Length);
        Assert.Null(store.ReadBody(entries[0]));
    }

    [Fact]
    public void Entries_can_be_deleted_and_cleared()
    {
        var store = NewStore();
        var first = Entry("https://h/1", bodySaved: true);
        var second = Entry("https://h/2", bodySaved: true);
        store.Add(first, [1]);
        store.Add(second, [2]);

        Assert.True(store.Delete(first.Id));
        Assert.False(store.Delete(first.Id));
        Assert.Equal([second.Id], store.Load().Select(e => e.Id));
        Assert.Null(store.ReadBody(first));
        Assert.Equal([2], store.ReadBody(second));

        store.Clear();
        Assert.Empty(store.Load());
        Assert.False(Directory.Exists(store.BodiesFolder));
        store.Add(first, [1]);
        Assert.Single(store.Load());
    }

    [Fact]
    public void A_history_another_program_has_open_is_not_cleared_but_says_why()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: elsewhere a file that is open can be deleted.");
        }

        var store = NewStore();
        var entry = Entry("https://h/1", bodySaved: true);
        store.Add(entry, [1]);

        using (File.Open(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // as Get-Content -Wait follows it
        {
            Assert.Throws<IOException>(store.Clear);
        }

        // Nothing is gone: neither the entries nor their bodies.
        Assert.Equal([entry.Id], store.Load().Select(e => e.Id));
        Assert.Equal([1], store.ReadBody(entry));

        // Once the entries are gone, a body another program has open is left and the reason returned; the next Clear deletes it.
        using (File.Open(Path.Combine(store.BodiesFolder, entry.Id + ".bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsAssignableFrom<IOException>(store.Clear());
        }

        Assert.Empty(store.Load());
        Assert.Null(store.Clear());
        Assert.False(Directory.Exists(store.BodiesFolder));
    }

    [Fact]
    public void Each_workspace_gets_its_own_history_folder()
    {
        var root = TestFiles.NewTempDirectory();
        var stateDirectory = TestFiles.NewTempDirectory();
        var key = HistoryStore.WorkspaceKey(root);

        Assert.Equal(key, HistoryStore.WorkspaceKey(root + Path.DirectorySeparatorChar));
        Assert.NotEqual(key, HistoryStore.WorkspaceKey(TestFiles.NewTempDirectory()));
        Assert.StartsWith(Path.GetFileName(root) + "-", key, StringComparison.Ordinal);

        var store = HistoryStore.ForWorkspace(root, 10, stateDirectory);
        store.Add(Entry("https://h/1"));
        Assert.Equal(Path.Combine(stateDirectory, "history", key), store.Folder);
        Assert.Equal(root + "\n", File.ReadAllText(Path.Combine(store.Folder, "workspace.txt")));
    }

    [Fact]
    public void History_files_are_private_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes only.");
            return;
        }

        var store = NewStore();
        store.Add(Entry("https://h/1", bodySaved: true), [1, 2, 3]);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Directory.GetFiles(store.BodiesFolder).Single()));
    }

    [Fact]
    public async Task Runner_records_sent_requests_with_secrets_masked()
    {
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues(
        [
            new KeyValueEntry { Key = "base", Value = server.Url("echo") },
            new KeyValueEntry { Key = "token", Value = "s3cr3t-token-value", Type = "secret" },
        ]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        var auth = new AuthSpec { Type = "apikey" };
        auth.Set("key", "api_key");
        auth.Set("value", "{{token}}");
        auth.Set("in", "query");
        var node = collection.AddRequest(collection.RootNode, "Echo", new HttpRequestSpec
        {
            Url = "{{base}}?q=1",
            Headers = [new() { Key = "X-Custom", Value = "id-{{token}}" }, new() { Key = "Authorization", Value = "Bearer {{token}}" }],
            Auth = auth,
        });
        using var runner = new RequestRunner(workspace) { History = NewStore() };

        var exchange = await runner.SendAsync(collection, node, node.ReadSpec(), _ct);

        Assert.Equal(200, exchange.Response!.StatusCode);
        var entry = Assert.Single(runner.History.Load());
        Assert.Equal(exchange.HistoryEntry!.Id, entry.Id);
        Assert.EndsWith("/echo?q=1&api_key=***", entry.Url, StringComparison.Ordinal);
        Assert.Equal("Bearer ***", entry.SentHeaders.Single(h => h.Key == "Authorization").Value);
        Assert.Equal("id-***", entry.SentHeaders.Single(h => h.Key == "X-Custom").Value);
        Assert.Equal(("API", "Echo", "dev", 200), (entry.Collection, entry.RequestPath, entry.Environment, entry.Status));
        Assert.Equal("{{base}}?q=1", entry.ReadSpec().Url);
        Assert.Equal("{{token}}", entry.ReadSpec().Auth!.Get("value"));
        Assert.False(entry.CredentialsMasked);
        Assert.False(entry.AuthInherited);

        // Auth inherited from the collection is stored with the entry, and said to be inherited.
        collection.Root["auth"] = PostmanRequestMapper.WriteAuth(auth, PostmanSchemaVersion.V21);
        var inheriting = collection.AddRequest(collection.RootNode, "Inherits", new HttpRequestSpec { Url = "{{base}}" });
        await runner.SendAsync(collection, inheriting, inheriting.ReadSpec(), _ct);
        var inherited = runner.History.Load()[0];
        Assert.True(inherited.AuthInherited);
        Assert.Equal("{{token}}", inherited.ReadSpec().Auth!.Get("value"));
        Assert.True(HistoryEntry.TryParse(PostmanJson.SerializeCompact(inherited.ToJson()), out var reread) && reread.AuthInherited);

        // The echo server reflects the request: the stored body has the secret masked too.
        var storedBody = System.Text.Encoding.UTF8.GetString(runner.History.ReadBody(entry)!);
        Assert.Contains("s3cr3t-token-value", System.Text.Encoding.UTF8.GetString(exchange.Response.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-token-value", storedBody, StringComparison.Ordinal);
        Assert.Contains("id-***", storedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-token-value", File.ReadAllText(runner.History.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_is_masked_as_it_is_sent_without_its_trailing_line_break()
    {
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("dev");

        // A token pasted, or read from a file, with its line break: the header is sent without it.
        environment.SetValues([new KeyValueEntry { Key = "tenant", Value = "tenant-s3cret-42\n", Type = "secret" }]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        var node = collection.AddRequest(collection.RootNode, "Echo", new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Headers = [new() { Key = "X-Tenant", Value = "{{tenant}}" }],
        });
        using var runner = new RequestRunner(workspace) { History = NewStore() };

        var exchange = await runner.SendAsync(collection, node, node.ReadSpec(), _ct);

        Assert.Contains("tenant-s3cret-42", System.Text.Encoding.UTF8.GetString(exchange.Response!.Body), StringComparison.Ordinal);
        var entry = Assert.Single(runner.History!.Load());
        Assert.Equal("***", entry.SentHeaders.Single(h => h.Key == "X-Tenant").Value);
        Assert.DoesNotContain("tenant-s3cret-42", System.Text.Encoding.UTF8.GetString(runner.History.ReadBody(entry)!), StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-s3cret-42", File.ReadAllText(runner.History.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secrets_in_the_user_info_of_a_url_are_masked_as_it_holds_them()
    {
        // An e-mail address as the user name: sent with its '@' encoded, as Postman sends it.
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([
            new KeyValueEntry { Key = "username", Value = "ada@example.com", Type = "secret" },
            new KeyValueEntry { Key = "password", Value = "pa55/word", Type = "secret" },
        ]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { History = NewStore() };

        var exchange = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = $"http://{{{{username}}}}:{{{{password}}}}@localhost:{server.BaseUri.Port}/echo" }, _ct);

        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        Assert.Equal($"http://***:***@localhost:{server.BaseUri.Port}/echo", Assert.Single(runner.History!.Load()).Url);
        Assert.DoesNotContain("example.com", File.ReadAllText(runner.History.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_records_failures_but_not_unsent_or_cancelled_requests()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { History = NewStore(), HistoryMaxBodyBytes = 0 };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var invalid = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = "{{missing}}/x" }, _ct);
        var aborted = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = "http://127.0.0.1:1/" }, cancelled.Token);
        var refused = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = "http://127.0.0.1:1/" }, _ct);

        Assert.Null(invalid.HistoryEntry);
        Assert.True(aborted.Cancelled);
        Assert.Null(aborted.HistoryEntry);
        var entry = Assert.Single(runner.History.Load());
        Assert.Equal(refused.HistoryEntry!.Id, entry.Id);
        Assert.Null(entry.Status);
        Assert.False(string.IsNullOrEmpty(entry.Error));
        Assert.Null(entry.RequestPath);
        Assert.Equal("GET http://127.0.0.1:1/", entry.Name);
    }

    [Fact]
    public async Task A_request_is_kept_with_the_environment_it_was_sent_with()
    {
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var prod = workspace.CreateEnvironment("prod");
        prod.SetValues([new KeyValueEntry { Key = "base", Value = server.Url("echo/prod") }]);
        var sandbox = workspace.CreateEnvironment("partner-sandbox");
        sandbox.SetValues([new KeyValueEntry { Key = "base", Value = server.Url("echo/sandbox") }]);
        workspace.ActiveEnvironment = prod;
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { History = NewStore() };

        // Another environment is chosen (F7) as the request goes out, and while a prepared one is on its way.
        var exchange = await runner.SendAsync(collection, null, new HttpRequestSpec { Url = "{{base}}" }, _ => workspace.ActiveEnvironment = sandbox, _ct);
        workspace.ActiveEnvironment = prod;
        var sending = runner.SendAsync(runner.Prepare(collection, null, new HttpRequestSpec { Url = "{{base}}" }), _ct);
        workspace.ActiveEnvironment = sandbox;
        await sending;

        Assert.EndsWith("/echo/prod", exchange.Request.Url, StringComparison.Ordinal);
        Assert.Equal(["prod", "prod"], runner.History.Load().Select(e => e.Environment));
    }

    [Fact]
    public async Task A_request_is_kept_with_its_collection_s_file_which_tells_collections_of_the_same_name_apart()
    {
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var json = workspace.CreateCollection("API", v3: false);
        var converted = workspace.CreateCollection("API", v3: true);
        using var runner = new RequestRunner(workspace) { History = NewStore() };

        await runner.SendAsync(json, null, new HttpRequestSpec { Url = server.Url("echo") }, _ct);
        await runner.SendAsync(converted, null, new HttpRequestSpec { Url = server.Url("echo") }, _ct);

        Assert.Equal([("API", "postman/collections/API"), ("API", "API.postman_collection.json")], runner.History.Load().Select(e => (e.Collection, e.CollectionFile)));
    }

    [Fact]
    public void Workspace_settings_control_recording()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var stateDirectory = TestFiles.NewTempDirectory();
        using var runner = new RequestRunner(workspace);

        workspace.Settings.HistoryLimit = 42;
        workspace.Settings.HistoryResponseKb = 8;
        runner.ConfigureHistory(stateDirectory);
        Assert.Equal((42, 8 * 1024), (runner.History!.MaxEntries, runner.HistoryMaxBodyBytes));
        Assert.StartsWith(stateDirectory, runner.History.Folder, StringComparison.Ordinal);

        workspace.Settings.HistoryEnabled = false;
        runner.ConfigureHistory(stateDirectory);
        Assert.Null(runner.History);
    }

    [Theory]
    [InlineData(4 * 1024 * 1024)] // 4 GB: 0 bytes in int arithmetic
    [InlineData(int.MaxValue)]
    public void The_largest_history_sizes_keep_every_body_and_entry(int kb)
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        using var runner = new RequestRunner(workspace);
        workspace.Settings.HistoryLimit = int.MaxValue;
        workspace.Settings.HistoryResponseKb = kb;
        runner.ConfigureHistory(TestFiles.NewTempDirectory());
        var history = runner.History!;

        var entry = Record(Sent(new HttpRequestSpec { Url = "https://api.example.com/x" }, "{\"id\":1}"u8.ToArray()), runner.HistoryMaxBodyBytes, out var body);
        history.Add(entry, body);
        File.AppendAllText(history.FilePath, "{not json\n"); // a damaged line, which compacting the history would drop
        history.Add(Entry("https://h/2"));

        Assert.Equal("{\"id\":1}"u8.ToArray(), history.ReadBody(entry));
        Assert.Contains("{not json", File.ReadAllText(history.FilePath), StringComparison.Ordinal);
    }

    /// <summary>A finished exchange of <paramref name="spec"/>, built in code (no server needed).</summary>
    private static HttpExchange Sent(HttpRequestSpec spec, byte[]? body = null, List<KeyValuePair<string, string>>? responseHeaders = null,
        string? contentType = null)
    {
        var request = new PreparedRequest { Url = "https://api.example.com/x", Uri = new Uri("https://api.example.com/x"), Source = new RequestSource(spec, null, null, null) };
        return new HttpExchange(request)
        {
            Response = new ResponseSnapshot { StatusCode = 200, ReasonPhrase = "OK", Headers = responseHeaders ?? [], Body = body ?? [], ContentType = contentType },
        };
    }

    private static HistoryEntry Record(HttpExchange exchange, int maxBodyBytes, out byte[]? body) =>
        HistoryEntry.Create(exchange, exchange.Request.Source!, null, maxBodyBytes, out body);

    [Fact]
    public void Credentials_typed_into_the_request_are_masked_and_variables_are_kept()
    {
        var auth = new AuthSpec { Type = "basic" };
        auth.Set("username", "alice");
        auth.Set("password", "Hunter2Hunter2");
        var spec = new HttpRequestSpec
        {
            Url = "https://api.example.com/x",
            Auth = auth,
            Headers =
            [
                new() { Key = "Authorization", Value = "Bearer eyJ-typed-token" },
                new() { Key = "Cookie", Value = "sid=typed-cookie-value" },
                new() { Key = "X-API-Key", Value = "{{apiKey}}" },
                new() { Key = "Accept", Value = "application/json" },
            ],
            Certificate = new CertificateSpec { PfxSrc = "client.p12", Passphrase = "Typed-Passphrase" },
        };

        var line = PostmanJson.SerializeCompact(Record(Sent(spec), 0, out _).ToJson());

        foreach (var secret in (string[])["Hunter2Hunter2", "eyJ-typed-token", "typed-cookie-value", "Typed-Passphrase"])
        {
            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }

        Assert.True(HistoryEntry.TryParse(line, out var entry));
        Assert.True(entry.CredentialsMasked);
        var stored = entry.ReadSpec();
        Assert.Equal(("alice", "***"), (stored.Auth!.Get("username"), stored.Auth.Get("password")));
        Assert.Equal(["Bearer ***", "sid=***", "{{apiKey}}", "application/json"], stored.Headers.Select(h => h.Value));
        Assert.Equal("***", stored.Certificate!.Passphrase);
        Assert.True(HistoryEntry.HasMaskedCredentials(stored));
        stored.Auth.Set("password", "typed again");
        stored.Headers = [.. stored.Headers.Where(h => !h.Value!.EndsWith("***", StringComparison.Ordinal))];
        stored.Certificate.Passphrase = "typed again";
        Assert.False(HistoryEntry.HasMaskedCredentials(stored));

        // Postman v2.0 maps auth parameters by name.
        spec.Auth = PostmanRequestMapper.ReadAuth(JsonNode.Parse("""{"type":"basic","basic":{"username":"alice","password":"Hunter2Hunter2"}}"""), PostmanSchemaVersion.V20);
        Assert.Equal("***", Record(Sent(spec), 0, out _).ReadSpec().Auth!.Get("password"));

        // A hand-written collection's number is sent as its text, and masked too; a flag holds no credential.
        spec.Auth = PostmanRequestMapper.ReadAuth(JsonNode.Parse("""{"type":"jwt","jwt":[{"key":"secret","value":20250101},{"key":"isSecretBase64Encoded","value":false}]}"""), PostmanSchemaVersion.V21);
        var numbers = Record(Sent(spec), 0, out _).ReadSpec().Auth!;
        Assert.Equal(("***", "false"), (numbers.Get("secret"), numbers.Get("isSecretBase64Encoded")));

        // References hold no secret: they are kept, so the request can be sent again as it is.
        spec.Auth = auth;
        auth.Set("password", "{{password}}");
        spec.Headers = [new() { Key = "Authorization", Value = "Bearer {{token}}" }, new() { Key = "Cookie", Value = "{{cookies}}" }];
        spec.Certificate.Passphrase = "env:CLIENT_PASSPHRASE";
        line = PostmanJson.SerializeCompact(Record(Sent(spec), 0, out _).ToJson());
        Assert.True(HistoryEntry.TryParse(line, out entry));
        Assert.False(entry.CredentialsMasked);
        stored = entry.ReadSpec();
        Assert.Equal("{{password}}", stored.Auth!.Get("password"));
        Assert.Equal(["Bearer {{token}}", "{{cookies}}"], stored.Headers.Select(h => h.Value));
        Assert.Equal("env:CLIENT_PASSPHRASE", stored.Certificate!.Passphrase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_password_typed_into_the_url_is_masked_and_a_reference_is_kept(bool urlWasString)
    {
        var spec = new HttpRequestSpec { Url = "https://ada:pw@api.example.com/me?x=1", UrlWasString = urlWasString };
        var request = new PreparedRequest { Url = spec.Url, Uri = new Uri(spec.Url), Source = new RequestSource(spec, null, null, null) };

        var line = PostmanJson.SerializeCompact(Record(new HttpExchange(request) { Response = new ResponseSnapshot { StatusCode = 200 } }, 0, out _).ToJson());

        Assert.DoesNotContain(":pw@", line, StringComparison.Ordinal);
        Assert.True(HistoryEntry.TryParse(line, out var entry));
        Assert.True(entry.CredentialsMasked);
        Assert.Equal(("https://ada:***@api.example.com/me?x=1", "GET https://ada:***@api.example.com/me"), (entry.Url, entry.Name));
        var stored = entry.ReadSpec();
        Assert.Equal("https://ada:***@api.example.com/me?x=1", stored.Url);
        Assert.True(HistoryEntry.HasMaskedCredentials(stored));
        stored.Url = "https://ada:typed-again@api.example.com/me";
        Assert.False(HistoryEntry.HasMaskedCredentials(stored));

        // A reference holds no secret.
        spec.Url = "https://ada:{{password}}@api.example.com/me";
        entry = Record(Sent(spec), 0, out _);
        Assert.False(entry.CredentialsMasked);
        Assert.Equal(spec.Url, entry.ReadSpec().Url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_user_name_sent_without_a_password_is_masked_as_the_credential_it_is(bool urlWasString)
    {
        // Stripe's documented curl -u sk_test_…: takes the secret key as the user name, without a password; so does a URL's user info.
        const string Key = "sk_test_typed-key";
        var auth = new AuthSpec { Type = "basic" };
        auth.Set("username", Key);
        auth.Set("password", "");
        var v20 = PostmanRequestMapper.ReadAuth(JsonNode.Parse($$$"""{"type":"basic","basic":{"username":"{{{Key}}}"}}"""), PostmanSchemaVersion.V20);
        var specs = (HttpRequestSpec[])
        [
            new() { Url = "https://api.example.com/me", Auth = auth },
            new() { Url = "https://api.example.com/me", Auth = v20 },
            new() { Url = $"https://{Key}:@api.example.com/me", UrlWasString = urlWasString },
            new() { Url = $"https://{Key}@api.example.com/me", UrlWasString = urlWasString },
        ];

        var entries = specs.Select(spec => Record(Sent(spec), 0, out _)).ToList();

        foreach (var entry in entries)
        {
            Assert.DoesNotContain(Key, PostmanJson.SerializeCompact(entry.ToJson()), StringComparison.Ordinal);
            Assert.True(entry.CredentialsMasked);
            Assert.True(HistoryEntry.HasMaskedCredentials(entry.ReadSpec()));
        }

        Assert.Equal(("***", ""), (entries[0].ReadSpec().Auth!.Get("username"), entries[0].ReadSpec().Auth!.Get("password")));
        Assert.Equal(["https://***:@api.example.com/me", "https://***@api.example.com/me"], entries.Skip(2).Select(e => e.ReadSpec().Url));

        // With a password, the user name is no credential; a reference holds no secret.
        auth.Set("password", "{{password}}");
        Assert.Equal(Key, Record(Sent(specs[0]), 0, out _).ReadSpec().Auth!.Get("username"));
        auth.Set("username", "{{stripeKey}}");
        auth.Set("password", "");
        var reference = Record(Sent(new HttpRequestSpec { Url = "https://{{stripeKey}}@api.example.com/me", Auth = auth }), 0, out _);
        Assert.False(reference.CredentialsMasked);
        Assert.Equal(("https://{{stripeKey}}@api.example.com/me", "{{stripeKey}}"), (reference.ReadSpec().Url, reference.ReadSpec().Auth!.Get("username")));
    }

    [Fact]
    public void The_url_a_redirect_led_to_is_masked_like_the_url()
    {
        var spec = new HttpRequestSpec { Url = "https://ada:pw@api.example.com/old" };
        var request = new PreparedRequest { Url = spec.Url, Uri = new Uri(spec.Url), Source = new RequestSource(spec, null, null, null) };
        request.AddSecretValues(["ab&cd #+é", "P@ss^w0rd"]);
        var final = new Uri("https://ada:pw@api.example.com/new?api_key=ab%26cd%20%23%2B%C3%A9&sig=P@ss^w0rd");

        var line = PostmanJson.SerializeCompact(Record(new HttpExchange(request) { Response = new ResponseSnapshot { StatusCode = 200, FinalUri = final } }, 0, out _).ToJson());

        // A password however short, and secret values as the URL holds them (encoded, or escaped by System.Uri).
        Assert.True(HistoryEntry.TryParse(line, out var entry));
        Assert.Equal("https://ada:***@api.example.com/new?api_key=***&sig=***", entry.FinalUrl);
        Assert.DoesNotContain(":pw@", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Headers_that_recordings_redact_by_name_are_masked_too()
    {
        const string SubscriptionKey = "3f8a9c1e7b2d4f60a5e8c3b1d9f7e2a4";
        var exchange = Sent(new HttpRequestSpec
        {
            Url = "https://api.example.com/x",
            Headers =
            [
                new() { Key = "Ocp-Apim-Subscription-Key", Value = SubscriptionKey },
                new() { Key = "X-Functions-Key", Value = "typed-functions-key" },
                new() { Key = "api_key", Value = "{{apiKey}}" },
            ],
        }, responseHeaders: [new("X-Hub-Signature-256", "sha256=response-signature")]);
        exchange.SentHeaders.AddRange([new("Ocp-Apim-Subscription-Key", SubscriptionKey), new("X-Functions-Key", "typed-functions-key"), new("api_key", "resolved-api-key")]);

        var line = PostmanJson.SerializeCompact(Record(exchange, 0, out _).ToJson());

        foreach (var secret in (string[])[SubscriptionKey, "typed-functions-key", "resolved-api-key", "response-signature"])
        {
            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }

        Assert.True(HistoryEntry.TryParse(line, out var entry));
        Assert.True(entry.CredentialsMasked);
        Assert.Equal(["***", "***", "{{apiKey}}"], entry.ReadSpec().Headers.Select(h => h.Value));
        Assert.True(HistoryEntry.HasMaskedCredentials(entry.ReadSpec()));
        Assert.Equal(["***", "***", "***"], entry.SentHeaders.Select(h => h.Value));
    }

    [Fact]
    public void Cookies_are_masked_one_by_one_and_references_with_separators_are_kept()
    {
        var spec = new HttpRequestSpec
        {
            Url = "https://api.example.com/x",
            Headers =
            [
                new() { Key = "Cookie", Value = "session={{sid}}; theme={{theme}}" },
                new() { Key = "Authorization", Value = "Basic {{user}}:{{password}}" },
                new() { Key = "X-Auth-Token", Value = "{{tenant}};{{token}}" },
            ],
        };

        var entry = Record(Sent(spec), 0, out _);

        Assert.False(entry.CredentialsMasked);
        Assert.Equal(["session={{sid}}; theme={{theme}}", "Basic {{user}}:{{password}}", "{{tenant}};{{token}}"], entry.ReadSpec().Headers.Select(h => h.Value));
        Assert.False(HistoryEntry.HasMaskedCredentials(entry.ReadSpec()));

        // Typed cookie values are masked; the names and the references stay.
        spec.Headers = [new() { Key = "Cookie", Value = "session={{sid}}; csrftoken=typed-csrf-value; theme=dark" }];
        var typed = Record(Sent(spec), 0, out _);

        Assert.True(typed.CredentialsMasked);
        var stored = typed.ReadSpec();
        Assert.Equal("session={{sid}}; csrftoken=***; theme=***", stored.Headers[0].Value);
        Assert.True(HistoryEntry.HasMaskedCredentials(stored));
        stored.Headers[0].Value = "session={{sid}}; csrftoken=typed-again; theme=dark";
        Assert.False(HistoryEntry.HasMaskedCredentials(stored));
        stored.Headers[0].Value = "***"; // as entries kept before cookies were masked one by one
        Assert.True(HistoryEntry.HasMaskedCredentials(stored));
    }

    [Theory]
    [InlineData("bearer", "token", null)]
    [InlineData("apikey", "value", "key")]
    [InlineData("digest", "password", "username")]
    [InlineData("oauth2", "accessToken", "tokenName")]
    [InlineData("oauth2", "clientSecret", "clientId")]
    [InlineData("oauth1", "tokenSecret", "consumerKey")]
    [InlineData("awsv4", "secretKey", "region")]
    [InlineData("hawk", "authKey", "authId")]
    [InlineData("jwt", "privateKey", "algorithm")]
    [InlineData("edgegrid", "clientToken", "baseURL")]
    public void Auth_credentials_of_every_type_are_masked(string type, string secret, string? plain)
    {
        var auth = new AuthSpec { Type = type };
        auth.Set(secret, "Typed-Credential-1");
        if (plain is not null)
        {
            auth.Set(plain, "plain-value");
        }

        var entry = Record(Sent(new HttpRequestSpec { Url = "https://api.example.com/x", Auth = auth }), 0, out _);

        Assert.True(entry.CredentialsMasked);
        Assert.Equal("***", entry.ReadSpec().Auth!.Get(secret));
        if (plain is not null)
        {
            Assert.Equal("plain-value", entry.ReadSpec().Auth!.Get(plain));
        }
    }

    [Fact]
    public void Echoed_secrets_are_masked_in_the_stored_body_whatever_its_encoding_and_wherever_it_is_cut()
    {
        const string Secret = "s3cr3t-token-value";
        byte[] Stored(byte[] response, int maxBytes, out bool truncated)
        {
            var exchange = Sent(new HttpRequestSpec { Url = "https://api.example.com/x" }, response);
            exchange.Request.SecretValues.Add(Secret);
            var entry = Record(exchange, maxBytes, out var body);
            truncated = entry.BodyTruncated;
            return body!;
        }

        // UTF-8 cut by the limit inside a character ("é" is two bytes).
        var cut = Stored(Encoding.UTF8.GetBytes($"{{\"echo\":\"{Secret}\",\"text\":\"{new string('é', 40)}\"}}"), 62, out var truncated);
        Assert.StartsWith("{\"echo\":\"***\",\"text\":\"ééé", Encoding.UTF8.GetString(cut), StringComparison.Ordinal);
        Assert.True(truncated);

        // Not UTF-8 at all.
        Assert.Equal(Encoding.Latin1.GetBytes("café *** café"), Stored(Encoding.Latin1.GetBytes($"café {Secret} café"), 1024, out truncated));
        Assert.False(truncated);

        // A secret that the limit would cut is left out entirely.
        Assert.Equal(new string('x', 60), Encoding.UTF8.GetString(Stored(Encoding.UTF8.GetBytes(new string('x', 60) + Secret + "yyyy"), 64, out truncated)));
        Assert.True(truncated);
    }

    [Fact]
    public void Echoed_secrets_are_masked_in_the_stored_body_also_where_they_are_escaped()
    {
        // A base64 secret with "+" and "/", echoed by ASP.NET Core as \u002B and by Go with & < > as \u0026 \u003c \u003e; one
        // with non-ASCII characters, echoed by Python (Flask, Django, httpbin) as lower-case \u escapes, by PHP also with "\/".
        const string Secret = "Ab+cd/ef&g<h>";
        const string Pin = "Grüße/2024";
        var echoed = $$"""{"json":{{JsonSerializer.Serialize(Secret)}},"go":"Ab+cd/ef\u0026g\u003ch\u003e","query":"{{Uri.EscapeDataString(Secret)}}","form":"{{WebUtility.UrlEncode(Secret)}}","slashes":"{{Secret.Replace("/", "\\/", StringComparison.Ordinal)}}","python":"Gr\u00fc\u00dfe/2024","php":"Gr\u00fc\u00dfe\/2024"}""";
        var exchange = Sent(new HttpRequestSpec { Url = "https://api.example.com/x" }, Encoding.UTF8.GetBytes(echoed));
        exchange.Request.SecretValues.UnionWith([Secret, Pin]);

        Record(exchange, 1024, out var body);

        Assert.Equal("""{"json":"***","go":"***","query":"***","form":"***","slashes":"***","python":"***","php":"***"}""", Encoding.UTF8.GetString(body!));
    }

    [Fact]
    public void Echoed_secrets_are_masked_in_utf16_and_utf32_bodies_in_their_encoding()
    {
        const string Secret = "s3cr3t-token-value";
        const string Fault = "<soap:Fault><faultstring>Token s3cr3t-token-value is not valid</faultstring></soap:Fault>";
        byte[] Stored(byte[] response, string? contentType, int maxBytes, out bool truncated, string secret = Secret)
        {
            var exchange = Sent(new HttpRequestSpec { Url = "https://api.example.com/x" }, response, contentType: contentType);
            exchange.Request.SecretValues.Add(secret);
            var entry = Record(exchange, maxBytes, out var body);
            truncated = entry.BodyTruncated;
            return body!;
        }

        (Encoding Encoding, string ContentType, bool Marked)[] bodies =
        [
            (Encoding.Unicode, "text/xml; charset=utf-16", true), // as .NET's SOAP services answer
            (Encoding.BigEndianUnicode, "text/xml; charset=\"UTF-16\"", false), // without a mark, either byte order
            (Encoding.Unicode, "text/xml; charset=UTF-16;", false), // a trailing ";", which .NET can't parse
            (Encoding.BigEndianUnicode, "text/xml", true), // only the mark says it
            (Encoding.UTF32, "application/json; charset=utf-32", false),
            (new UTF32Encoding(bigEndian: true, byteOrderMark: true), "text/plain", true),
        ];
        foreach (var (encoding, contentType, marked) in bodies)
        {
            byte[] Bytes(string text) => marked ? [.. encoding.GetPreamble(), .. encoding.GetBytes(text)] : encoding.GetBytes(text);

            Assert.Equal(Bytes(Fault.Replace(Secret, "***", StringComparison.Ordinal)), Stored(Bytes(Fault), contentType, 1024, out var truncated));
            Assert.False(truncated);
        }

        // A secret that the limit would cut is left out entirely; the body ends between characters.
        var cut = Stored(Encoding.Unicode.GetBytes(new string('x', 30) + Secret + "yyyy"), "text/plain; charset=utf-16le", 64, out var cutShort);
        Assert.Equal(Encoding.Unicode.GetBytes(new string('x', 30)), cut);
        Assert.True(cutShort);

        // The bytes of a secret across characters are other text: "abcd" in UTF-16LE, one byte into these CJK characters.
        var across = Encoding.Unicode.GetBytes("\u6141\u6200\u6300\u6400\u4100");
        Assert.Equal(across, Stored(across, "text/plain; charset=utf-16", 1024, out _, secret: "abcd"));
    }

    [Fact]
    public void A_body_in_a_charset_dotnet_does_not_support_is_shown_as_utf8()
    {
        // .NET has UTF-7 disabled: history --show, the response pane and scripts read such a body as UTF-8.
        byte[] body = [.. "Hi Mom -+Jjo--! "u8, 0xE9];

        Assert.Equal("Hi Mom -+Jjo--! \uFFFD", BodyFormatter.Format(body, "text/plain; charset=utf-7"));
    }

    [Fact]
    public void Response_headers_named_like_secrets_are_masked()
    {
        var entry = Record(Sent(new HttpRequestSpec { Url = "https://api.example.com/x" }, responseHeaders:
        [
            new("X-Subject-Token", "gAAAAABk-subject-token"),
            new("access-token", "devise-token-value"),
            new("Set-Cookie", "sid=abc123456789; Path=/; HttpOnly"),
            new("Content-Type", "application/json"),
        ]), 0, out _);

        Assert.Equal(["***", "***", "sid=***; Path=/; HttpOnly", "application/json"], entry.ResponseHeaders.Select(h => h.Value));
    }

    [Fact]
    public async Task Changes_wait_while_another_process_changes_the_history()
    {
        var store = NewStore();
        var first = Entry("https://h/1");
        store.Add(first);
        var other = new HistoryStore(store.Folder); // e.g. termrest send while the UI compacts the history
        var second = Entry("https://h/2", bodySaved: true);

        Task adding;
        Task deleting;
        using (new FileStream(Path.Combine(store.Folder, HistoryStore.LockFileName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            adding = Task.Run(() => other.Add(second, [2]), _ct);
            deleting = Task.Run(() => other.Delete(first.Id), _ct);
            await Task.Delay(300, _ct);

            Assert.False(adding.IsCompleted || deleting.IsCompleted);
            Assert.Equal([first.Id], store.Load().Select(e => e.Id));
            Assert.Null(store.ReadBody(second));
        }

        await Task.WhenAll(adding, deleting);
        Assert.Equal([second.Id], store.Load().Select(e => e.Id));
        Assert.Equal([2], store.ReadBody(second));
    }
}
