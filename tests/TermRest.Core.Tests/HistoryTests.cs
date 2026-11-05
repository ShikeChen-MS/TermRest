using System.Text.Json.Nodes;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Model;
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

        // The echo server reflects the request: the stored body has the secret masked too.
        var storedBody = System.Text.Encoding.UTF8.GetString(runner.History.ReadBody(entry)!);
        Assert.Contains("s3cr3t-token-value", System.Text.Encoding.UTF8.GetString(exchange.Response.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-token-value", storedBody, StringComparison.Ordinal);
        Assert.Contains("id-***", storedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-token-value", File.ReadAllText(runner.History.FilePath), StringComparison.Ordinal);
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
}
