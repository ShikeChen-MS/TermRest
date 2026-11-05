using TermRest.Core.Model;
using TermRest.Core.Postman;
using Xunit;

namespace TermRest.Core.Tests;

public class PostmanUrlTests
{
    [Fact]
    public void Parses_a_full_url()
    {
        var url = PostmanUrl.Parse("https://user:pw@api.example.com:8443/v1/:tenant/items/?flag&a=1&b=x=y#frag");

        Assert.Equal("https", url.Protocol);
        Assert.Equal("user", url.AuthUser);
        Assert.Equal("pw", url.AuthPassword);
        Assert.Equal(["api", "example", "com"], url.Host);
        Assert.Equal("8443", url.Port);
        Assert.Equal(["v1", ":tenant", "items", ""], url.Path);
        Assert.Equal([("flag", (string?)null), ("a", "1"), ("b", "x=y")], url.Query);
        Assert.Equal("frag", url.Hash);
    }

    [Theory]
    [InlineData("{{baseUrl}}/users", new[] { "{{baseUrl}}" }, new[] { "users" })]
    [InlineData("{{base.url}}/a/b", new[] { "{{base.url}}" }, new[] { "a", "b" })]
    [InlineData("localhost:3000/api", new[] { "localhost" }, new[] { "api" })]
    [InlineData("{{host}}", new[] { "{{host}}" }, new string[0])]
    public void Splits_host_and_path_outside_variables(string raw, string[] host, string[] path)
    {
        var url = PostmanUrl.Parse(raw);

        Assert.Equal(host, url.Host);
        Assert.Equal(path, url.Path);
    }

    [Fact]
    public void Separators_inside_variables_are_ignored()
    {
        var url = PostmanUrl.Parse("{{base}}/search?q={{a?b&c=d}}&n=1");

        Assert.Equal([("q", (string?)"{{a?b&c=d}}"), ("n", "1")], url.Query);
    }

    [Fact]
    public void Syncing_params_from_url_keeps_disabled_rows_in_place()
    {
        List<KeyValueEntry> current =
        [
            new() { Key = "a", Value = "1", Description = "first" },
            new() { Key = "off", Value = "x", Enabled = false },
            new() { Key = "b", Value = "2" },
        ];

        var synced = PostmanUrl.SyncQueryFromRaw("http://h/p?a=10&b=20&c=30", current);

        Assert.Equal(["a=10", "off=x", "b=20", "c=30"], synced.Select(e => $"{e.Key}={e.Value}"));
        Assert.Equal("first", synced[0].Description);
        Assert.False(synced[1].Enabled);
    }

    [Fact]
    public void Syncing_params_keeps_metadata_with_its_key_when_params_are_removed_reordered_or_edited()
    {
        var limitSource = new System.Text.Json.Nodes.JsonObject { ["x-custom"] = "keep me" };
        List<KeyValueEntry> current =
        [
            new() { Key = "page", Value = "1", Description = "Page number" },
            new() { Key = "limit", Value = "10", Description = "Max items", Source = limitSource },
        ];
        static IEnumerable<string> Rows(List<KeyValueEntry> rows) => rows.Select(e => $"{e.Key}={e.Value}:{e.Description}");

        var removed = PostmanUrl.SyncQueryFromRaw("https://x/api?limit=10", current);
        Assert.Equal(["limit=10:Max items"], Rows(removed));
        Assert.Same(limitSource, removed[0].Source);

        Assert.Equal(["limit=10:Max items", "page=1:Page number"], Rows(PostmanUrl.SyncQueryFromRaw("https://x/api?limit=10&page=1", current)));
        Assert.Equal(["page=2:Page number", "limit=10:Max items"], Rows(PostmanUrl.SyncQueryFromRaw("https://x/api?page=2&limit=10", current)));
        Assert.Equal(["pg=1:Page number", "limit=10:Max items"], Rows(PostmanUrl.SyncQueryFromRaw("https://x/api?pg=1&limit=10", current)));
        Assert.Equal(["page=1:Page number", "limit=10:Max items", "q=x:"], Rows(PostmanUrl.SyncQueryFromRaw("https://x/api?page=1&limit=10&q=x", current)));

        List<KeyValueEntry> duplicates = [new() { Key = "tag", Value = "a", Description = "first" }, new() { Key = "tag", Value = "b", Description = "second" }];
        Assert.Equal(["tag=b:second"], Rows(PostmanUrl.SyncQueryFromRaw("https://x/api?tag=b", duplicates)));
    }

    [Fact]
    public void Syncing_url_from_params_rebuilds_the_query_and_keeps_the_fragment()
    {
        List<KeyValueEntry> query =
        [
            new() { Key = "a", Value = "1" },
            new() { Key = "off", Value = "x", Enabled = false },
            new() { Key = "flag", Value = null },
        ];

        Assert.Equal("http://h/p?a=1&flag#top", PostmanUrl.SyncRawFromQuery("http://h/p?old=1#top", query));
        Assert.Equal("http://h/p", PostmanUrl.SyncRawFromQuery("http://h/p?old=1", []));
    }

    [Fact]
    public void Path_variables_follow_the_url()
    {
        List<KeyValueEntry> current = [new() { Key = "id", Value = "42" }, new() { Key = "gone", Value = "x" }];

        var synced = PostmanUrl.SyncPathVariables("{{base}}/users/:id/orders/:orderId", current);

        Assert.Equal(["id=42", "orderId="], synced.Select(e => $"{e.Key}={e.Value}"));
        Assert.Equal("http://h/users/42/x", PostmanUrl.SubstitutePathVariables("http://h/users/:id/:other", n => n == "id" ? "42" : "x"));
    }
}
