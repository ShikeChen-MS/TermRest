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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
