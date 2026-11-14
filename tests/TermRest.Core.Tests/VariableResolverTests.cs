using TermRest.Core.Model;
using TermRest.Core.Variables;
using Xunit;

namespace TermRest.Core.Tests;

public class VariableResolverTests
{
    private static VariableResolver Create() => new(
    [
        new DictionaryScope("local", new Dictionary<string, string> { ["user"] = "local-user" }),
        new EntryListScope("environment", [
            new KeyValueEntry { Key = "baseUrl", Value = "https://{{host}}/api" },
            new KeyValueEntry { Key = "user", Value = "env-user" },
            new KeyValueEntry { Key = "disabled", Value = "nope", Enabled = false },
        ]),
        new EntryListScope("collection", [new KeyValueEntry { Key = "host", Value = "example.com" }, new KeyValueEntry { Key = "disabled", Value = "collection-value" }]),
    ]);

    [Fact]
    public void Higher_scopes_win_and_disabled_values_are_skipped()
    {
        var resolver = Create();

        Assert.Equal("local-user", resolver.Resolve("{{user}}"));
        Assert.Equal("collection-value", resolver.Resolve("{{disabled}}"));
        Assert.True(resolver.TryResolve("host", out var host, out var scope));
        Assert.Equal(("example.com", "collection"), (host, scope));
    }

    [Fact]
    public void Nested_references_are_resolved()
    {
        Assert.Equal("https://example.com/api/users", Create().Resolve("{{baseUrl}}/users"));
    }

    [Fact]
    public void Unknown_variables_are_kept_and_reported()
    {
        var unresolved = new HashSet<string>();

        var result = Create().Resolve("{{baseUrl}}/{{missing}}/{{alsoMissing}}", unresolved);

        Assert.Equal("https://example.com/api/{{missing}}/{{alsoMissing}}", result);
        Assert.Equal(["alsoMissing", "missing"], unresolved.Order());
    }

    [Fact]
    public void Self_references_terminate()
    {
        var resolver = new VariableResolver([new DictionaryScope("x", new Dictionary<string, string> { ["loop"] = "{{loop}}" })]);

        Assert.Equal("{{loop}}", resolver.Resolve("{{loop}}"));
    }

    [Fact]
    public void Values_that_reference_themselves_several_times_stop_growing()
    {
        // Each pass would multiply the text by 8 (8^10 references after ten passes): the expansion stops at the limit,
        // and the references left are reported as unresolved.
        var resolver = new VariableResolver([new DictionaryScope("x", new Dictionary<string, string>
        {
            ["baseUrl"] = string.Concat(Enumerable.Repeat("{{baseUrl}}", 8)),
            ["host"] = "example.com",
        })]);
        var unresolved = new HashSet<string>();

        var result = resolver.Resolve("https://{{host}}/{{baseUrl}}/ping", unresolved);

        Assert.InRange(result.Length, VariableResolver.MaxGrowth / 2, VariableResolver.MaxGrowth + 100);
        Assert.StartsWith("https://example.com/", result, StringComparison.Ordinal);
        Assert.EndsWith("/ping", result, StringComparison.Ordinal);
        Assert.Equal(["baseUrl"], unresolved);
    }

    [Fact]
    public void A_value_too_long_to_insert_is_left_unresolved_and_others_are_not()
    {
        var resolver = new VariableResolver([new DictionaryScope("x", new Dictionary<string, string>
        {
            ["upload"] = new('A', VariableResolver.MaxGrowth + 100),
            ["id"] = "42",
        })]);
        var unresolved = new HashSet<string>();

        Assert.Equal("{\"file\":\"{{upload}}\",\"id\":42}", resolver.Resolve("{\"file\":\"{{upload}}\",\"id\":{{id}}}", unresolved));
        Assert.Equal(["upload"], unresolved);
    }

    [Fact]
    public void Dynamic_variables_produce_values()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero));
        var resolver = new VariableResolver([], time);

        Assert.True(Guid.TryParse(resolver.Resolve("{{$guid}}"), out _));
        Assert.Equal("1767323045", resolver.Resolve("{{$timestamp}}"));
        Assert.Equal("2026-01-02T03:04:05.678Z", resolver.Resolve("{{$isoTimestamp}}"));
        Assert.InRange(int.Parse(resolver.Resolve("{{$randomInt}}"), System.Globalization.CultureInfo.InvariantCulture), 0, 1000);
        Assert.NotEqual(resolver.Resolve("{{$guid}}"), resolver.Resolve("{{$guid}}"));
    }

    [Fact]
    public void A_variable_takes_the_place_of_a_dynamic_one_of_its_name()
    {
        // As in Postman (its dynamic variables are looked up last) and in scripts' replaceIn: a script that signs
        // {{$timestamp}} after setting it signs what the request sends.
        var resolver = new VariableResolver([new DictionaryScope("local", new Dictionary<string, string> { ["$timestamp"] = "1700000000" })]);

        Assert.Equal("1700000000", resolver.Resolve("{{$timestamp}}"));
        Assert.True(resolver.TryResolve("$timestamp", out _, out var scope));
        Assert.Equal("local", scope);
        Assert.True(resolver.TryResolve("$guid", out _, out scope));
        Assert.Equal("dynamic", scope);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
