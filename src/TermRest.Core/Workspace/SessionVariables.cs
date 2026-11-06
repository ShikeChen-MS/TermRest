using TermRest.Core.Postman;

namespace TermRest.Core.Workspace;

/// <summary>
/// Variables set by scripts during this session: Postman's "current values". They take precedence over the values in
/// the environment, collection and globals files, which scripts never change (so a token a script fetched does not end
/// up in a file under version control). A null value means a script unset the variable.
/// </summary>
public sealed class SessionVariables
{
    private readonly Dictionary<string, Dictionary<string, string?>> _environments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string?>> _collections = new(StringComparer.Ordinal);

    /// <summary>Global variables set by scripts.</summary>
    public Dictionary<string, string?> Globals { get; } = new(StringComparer.Ordinal);

    /// <summary>Values set by scripts so far, in every scope.</summary>
    public int Count => Globals.Count + _environments.Values.Sum(v => v.Count) + _collections.Values.Sum(v => v.Count);

    /// <summary>The values set for <paramref name="environment"/> (created on first use).</summary>
    public Dictionary<string, string?> For(PostmanEnvironment environment) => GetOrAdd(_environments, Key(environment));

    /// <summary>The values set for the variables of <paramref name="collection"/> (created on first use).</summary>
    public Dictionary<string, string?> For(PostmanCollection collection) => GetOrAdd(_collections, Key(collection));

    public IReadOnlyDictionary<string, string?>? Find(PostmanEnvironment environment) => _environments.GetValueOrDefault(Key(environment));

    public IReadOnlyDictionary<string, string?>? Find(PostmanCollection collection) => _collections.GetValueOrDefault(Key(collection));

    /// <summary>Forgets every value set by scripts: the files' values apply again.</summary>
    public void Clear()
    {
        Globals.Clear();
        _environments.Clear();
        _collections.Clear();
    }

    private static string Key(PostmanEnvironment environment) => environment.FilePath ?? "name:" + environment.Name;

    private static string Key(PostmanCollection collection) => collection.FilePath ?? "name:" + collection.Name;

    private static Dictionary<string, string?> GetOrAdd(Dictionary<string, Dictionary<string, string?>> scopes, string key)
    {
        if (!scopes.TryGetValue(key, out var values))
        {
            scopes[key] = values = new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        return values;
    }
}
