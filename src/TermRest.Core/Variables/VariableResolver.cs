using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TermRest.Core.Model;

namespace TermRest.Core.Variables;

public interface IVariableScope
{
    string Name { get; }

    bool TryGetValue(string key, out string? value);
}

/// <summary>A scope over Postman key/value entries (environment, collection variables, globals). Disabled entries are ignored.</summary>
public sealed class EntryListScope(string name, IEnumerable<KeyValueEntry> entries) : IVariableScope
{
    private readonly Dictionary<string, string?> _values = entries
        .Where(e => e.Enabled && e.Key.Length > 0)
        .GroupBy(e => e.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    public string Name { get; } = name;

    public bool TryGetValue(string key, out string? value) => _values.TryGetValue(key, out value);
}

public sealed class DictionaryScope(string name, IDictionary<string, string> values) : IVariableScope
{
    public string Name { get; } = name;

    public IDictionary<string, string> Values { get; } = values;

    public bool TryGetValue(string key, out string? value)
    {
        if (Values.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }

        value = null;
        return false;
    }
}

/// <summary>
/// A scope whose values are covered by values set in this session (by scripts). A value set to null hides the
/// variable; lower scopes then apply, as in Postman when a script unsets a variable.
/// </summary>
public sealed class OverlayScope(IVariableScope inner, IReadOnlyDictionary<string, string?>? overlay) : IVariableScope
{
    public string Name => inner.Name;

    public bool TryGetValue(string key, out string? value)
    {
        if (overlay is not null && overlay.TryGetValue(key, out value))
        {
            return value is not null;
        }

        return inner.TryGetValue(key, out value);
    }
}

/// <summary>
/// Resolves Postman <c>{{variable}}</c> references. Scopes are searched in order (highest precedence first:
/// local/CLI overrides, environment, collection, globals). Values may reference other variables; resolution is
/// repeated up to <see cref="MaxDepth"/> times. Unknown variables are left as-is, like Postman does.
/// </summary>
public sealed partial class VariableResolver
{
    public const int MaxDepth = 10;

    private static readonly string[] FirstNames = ["Ada", "Alan", "Grace", "Linus", "Margaret", "Ken", "Barbara", "Dennis"];
    private static readonly string[] LastNames = ["Lovelace", "Turing", "Hopper", "Torvalds", "Hamilton", "Thompson", "Liskov", "Ritchie"];
    private static readonly string[] Words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];

    private readonly IReadOnlyList<IVariableScope> _scopes;
    private readonly TimeProvider _time;

    public VariableResolver(IEnumerable<IVariableScope> scopesHighestPrecedenceFirst, TimeProvider? time = null)
    {
        _scopes = scopesHighestPrecedenceFirst.ToList();
        _time = time ?? TimeProvider.System;
    }

    public static VariableResolver Empty { get; } = new([]);

    public IReadOnlyList<IVariableScope> Scopes => _scopes;

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public static IEnumerable<string> FindReferences(string? text) =>
        string.IsNullOrEmpty(text) ? [] : ReferencePattern().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    /// <summary>Replaces all resolvable references. Names that cannot be resolved are added to <paramref name="unresolved"/>.</summary>
    public string Resolve(string? text, ISet<string>? unresolved = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
        {
            return text ?? "";
        }

        var current = text;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var replaced = false;
            current = ReferencePattern().Replace(current, match =>
            {
                var name = match.Groups[1].Value;
                if (TryResolve(name, out var value, out _))
                {
                    replaced = true;
                    return value ?? "";
                }

                return match.Value;
            });

            if (!replaced || !current.Contains("{{", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (unresolved is not null)
        {
            foreach (var name in FindReferences(current))
            {
                unresolved.Add(name);
            }
        }

        return current;
    }

    /// <summary>Looks up a single variable (without nested resolution) and reports the scope it came from.</summary>
    public bool TryResolve(string name, out string? value, out string? scopeName)
    {
        if (name.StartsWith('$') && TryDynamic(name, out value))
        {
            scopeName = "dynamic";
            return true;
        }

        foreach (var scope in _scopes)
        {
            if (scope.TryGetValue(name, out value))
            {
                scopeName = scope.Name;
                return true;
            }
        }

        value = null;
        scopeName = null;
        return false;
    }

    private bool TryDynamic(string name, out string? value)
    {
        var now = _time.GetUtcNow();
        value = name switch
        {
            "$guid" or "$randomUUID" => Guid.NewGuid().ToString(),
            "$timestamp" => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "$isoTimestamp" => now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            "$randomInt" => RandomNumberGenerator.GetInt32(0, 1001).ToString(CultureInfo.InvariantCulture),
            "$randomBoolean" => (RandomNumberGenerator.GetInt32(2) == 1 ? "true" : "false"),
            "$randomAlphaNumeric" => RandomString(1),
            "$randomHexadecimal" => RandomNumberGenerator.GetInt32(16).ToString("x", CultureInfo.InvariantCulture),
            "$randomFirstName" => Pick(FirstNames),
            "$randomLastName" => Pick(LastNames),
            "$randomFullName" => $"{Pick(FirstNames)} {Pick(LastNames)}",
            "$randomUserName" => $"{Pick(FirstNames).ToLowerInvariant()}.{Pick(LastNames).ToLowerInvariant()}{RandomNumberGenerator.GetInt32(100)}",
            "$randomEmail" => $"{Pick(FirstNames).ToLowerInvariant()}.{Pick(LastNames).ToLowerInvariant()}@example.com",
            "$randomWord" => Pick(Words),
            "$randomPassword" => RandomString(15),
            _ => null,
        };
        return value is not null;
    }

    private static string Pick(string[] values) => values[RandomNumberGenerator.GetInt32(values.Length)];

    private static string RandomString(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            builder.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        }

        return builder.ToString();
    }
}
