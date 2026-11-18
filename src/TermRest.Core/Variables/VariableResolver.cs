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
/// repeated up to <see cref="MaxDepth"/> times, and adds at most <see cref="MaxGrowth"/> characters. Unknown variables
/// are left as-is, like Postman does.
/// </summary>
public sealed partial class VariableResolver
{
    public const int MaxDepth = 10;

    /// <summary>
    /// How many characters resolving a text may add to it. A value that references itself (or others that reference it)
    /// several times would multiply the text with every pass: a reference whose value would take the text past this
    /// limit stays unresolved, and no further pass is made.
    /// </summary>
    public const int MaxGrowth = 4 * 1024 * 1024;

    // What {{$randomFirstName}} and the like pick from; scripts get them with each message (their replaceIn resolves the same names).
    internal static readonly string[] FirstNames = ["Ada", "Alan", "Grace", "Linus", "Margaret", "Ken", "Barbara", "Dennis"];
    internal static readonly string[] LastNames = ["Lovelace", "Turing", "Hopper", "Torvalds", "Hamilton", "Thompson", "Liskov", "Ritchie"];
    internal static readonly string[] Words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];

    private readonly IReadOnlyList<IVariableScope> _scopes;
    private readonly TimeProvider _time;
    private readonly bool _literal;

    public VariableResolver(IEnumerable<IVariableScope> scopesHighestPrecedenceFirst, TimeProvider? time = null)
    {
        _scopes = scopesHighestPrecedenceFirst.ToList();
        _time = time ?? TimeProvider.System;
    }

    private VariableResolver(bool literal)
        : this([]) => _literal = literal;

    public static VariableResolver Empty { get; } = new([]);

    /// <summary>
    /// Replaces nothing, not even dynamic variables ({{$guid}}): what a request that a script sends gets, as in Postman, whose
    /// pm.sendRequest sends {{references}} as written (scripts replace them with replaceIn). They count as unresolved.
    /// </summary>
    public static VariableResolver Literal { get; } = new(literal: true);

    public IReadOnlyList<IVariableScope> Scopes => _scopes;

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public static IEnumerable<string> FindReferences(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        // Compared before they are copied: a text that MaxGrowth cut short holds the same names many times.
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lookup = seen.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var match in ReferencePattern().EnumerateMatches(text))
        {
            var name = text.AsSpan(match.Index + 2, match.Length - 4);
            if (!lookup.Contains(name))
            {
                var copy = name.ToString();
                names.Add(copy);
                seen.Add(copy);
            }
        }

        return names;
    }

    /// <summary>
    /// Replaces all resolvable references, as long as the text grows by at most <see cref="MaxGrowth"/> characters. Names
    /// that cannot be resolved, or that are left at that limit, are added to <paramref name="unresolved"/>.
    /// </summary>
    public string Resolve(string? text, ISet<string>? unresolved = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
        {
            return text ?? "";
        }

        if (_literal)
        {
            if (unresolved is not null)
            {
                foreach (var name in FindReferences(text))
                {
                    unresolved.Add(name);
                }
            }

            return text;
        }

        var limit = (long)text.Length + MaxGrowth;
        var current = text;
        var full = false;
        for (var depth = 0; depth < MaxDepth && !full; depth++)
        {
            var builder = new StringBuilder();
            var end = 0;
            var length = (long)current.Length;
            var replaced = false;
            string? name = null;
            foreach (var match in ReferencePattern().EnumerateMatches(current))
            {
                // The same name again (as in a text that grew to the limit) is not copied again.
                var reference = current.AsSpan(match.Index + 2, match.Length - 4);
                name = name is not null && reference.SequenceEqual(name) ? name : reference.ToString();
                if (!TryResolve(name, out var value, out _))
                {
                    continue;
                }

                value ??= "";
                if (length + value.Length - match.Length > limit)
                {
                    full = true; // left as it is; references after it that fit are still replaced
                    continue;
                }

                length += value.Length - match.Length;
                builder.Append(current, end, match.Index - end).Append(value);
                end = match.Index + match.Length;
                replaced = true;
            }

            if (!replaced)
            {
                break;
            }

            current = builder.Append(current, end, current.Length - end).ToString();
            if (!current.Contains("{{", StringComparison.Ordinal))
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

    /// <summary>
    /// Looks up a single variable (without nested resolution) and reports the scope it came from. A variable takes the place
    /// of a dynamic one of the same name (<c>$timestamp</c> set by a script), as in Postman and in scripts' replaceIn.
    /// </summary>
    public bool TryResolve(string name, out string? value, out string? scopeName)
    {
        if (_literal)
        {
            (value, scopeName) = (null, null);
            return false;
        }

        foreach (var scope in _scopes)
        {
            if (scope.TryGetValue(name, out value))
            {
                scopeName = scope.Name;
                return true;
            }
        }

        if (name.StartsWith('$') && TryDynamic(name, out value))
        {
            scopeName = "dynamic";
            return true;
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
