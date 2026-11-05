using System.Text;
using TermRest.Core.Model;

namespace TermRest.Core.Postman;

/// <summary>Components of a URL split the way Postman's collection format stores them.</summary>
public sealed class ParsedUrl
{
    public string? Protocol { get; init; }

    public string? AuthUser { get; init; }

    public string? AuthPassword { get; init; }

    public List<string> Host { get; init; } = [];

    public string? Port { get; init; }

    public List<string> Path { get; init; } = [];

    public List<(string Key, string? Value)> Query { get; init; } = [];

    public string? Hash { get; init; }
}

/// <summary>
/// Postman-style URL handling. Raw URLs may contain <c>{{variables}}</c> anywhere, so all splitting
/// ignores separators that appear inside double braces.
/// </summary>
public static class PostmanUrl
{
    public static ParsedUrl Parse(string raw)
    {
        var s = raw.TrimStart();

        string? hash = null;
        var hashIndex = IndexOutsideBraces(s, '#');
        if (hashIndex >= 0)
        {
            hash = s[(hashIndex + 1)..];
            s = s[..hashIndex];
        }

        string? queryString = null;
        var queryIndex = IndexOutsideBraces(s, '?');
        if (queryIndex >= 0)
        {
            queryString = s[(queryIndex + 1)..];
            s = s[..queryIndex];
        }

        string? protocol = null;
        var protocolIndex = s.IndexOf("://", StringComparison.Ordinal);
        if (protocolIndex > 0 && !s[..protocolIndex].Contains('/', StringComparison.Ordinal))
        {
            protocol = s[..protocolIndex];
            s = s[(protocolIndex + 3)..];
        }

        var slashIndex = IndexOutsideBraces(s, '/');
        var authority = slashIndex >= 0 ? s[..slashIndex] : s;
        var pathString = slashIndex >= 0 ? s[(slashIndex + 1)..] : null;

        string? user = null;
        string? password = null;
        var atIndex = LastIndexOutsideBraces(authority, '@');
        if (atIndex >= 0)
        {
            var credentials = authority[..atIndex];
            authority = authority[(atIndex + 1)..];
            var colon = credentials.IndexOf(':', StringComparison.Ordinal);
            user = colon >= 0 ? credentials[..colon] : credentials;
            password = colon >= 0 ? credentials[(colon + 1)..] : null;
        }

        string? port = null;
        List<string> host;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            var hostPart = close >= 0 ? authority[..(close + 1)] : authority;
            if (close >= 0 && close + 1 < authority.Length && authority[close + 1] == ':')
            {
                port = authority[(close + 2)..];
            }

            host = [hostPart];
        }
        else
        {
            var colonIndex = LastIndexOutsideBraces(authority, ':');
            if (colonIndex >= 0)
            {
                port = authority[(colonIndex + 1)..];
                authority = authority[..colonIndex];
            }

            host = authority.Length == 0 ? [] : SplitOutsideBraces(authority, '.');
        }

        var path = pathString is null ? [] : SplitOutsideBraces(pathString, '/');

        var query = new List<(string, string?)>();
        if (!string.IsNullOrEmpty(queryString))
        {
            foreach (var part in SplitOutsideBraces(queryString, '&'))
            {
                var eq = IndexOutsideBraces(part, '=');
                query.Add(eq >= 0 ? (part[..eq], part[(eq + 1)..]) : (part, null));
            }
        }

        return new ParsedUrl
        {
            Protocol = protocol,
            AuthUser = user,
            AuthPassword = password,
            Host = host,
            Port = port,
            Path = path,
            Query = query,
            Hash = hash,
        };
    }

    /// <summary>The URL without its query string and fragment.</summary>
    public static string StripQueryAndHash(string raw)
    {
        var hashIndex = IndexOutsideBraces(raw, '#');
        var s = hashIndex >= 0 ? raw[..hashIndex] : raw;
        var queryIndex = IndexOutsideBraces(s, '?');
        return queryIndex >= 0 ? s[..queryIndex] : s;
    }

    public static string? GetHash(string raw)
    {
        var hashIndex = IndexOutsideBraces(raw, '#');
        return hashIndex >= 0 ? raw[(hashIndex + 1)..] : null;
    }

    public static string BuildQueryString(IEnumerable<KeyValueEntry> entries) =>
        string.Join('&', entries.Where(e => e.Enabled).Select(e => e.Value is null ? e.Key : $"{e.Key}={e.Value}"));

    /// <summary>Re-derives the parameter table after the URL text changed, keeping disabled rows in place.</summary>
    public static List<KeyValueEntry> SyncQueryFromRaw(string raw, IReadOnlyList<KeyValueEntry> current)
    {
        var parsed = Parse(raw).Query;
        var result = new List<KeyValueEntry>(Math.Max(parsed.Count, current.Count));
        var next = 0;
        foreach (var entry in current)
        {
            if (!entry.Enabled)
            {
                result.Add(entry.Clone());
                continue;
            }

            if (next < parsed.Count)
            {
                var updated = entry.Clone();
                updated.Key = parsed[next].Key;
                updated.Value = parsed[next].Value;
                result.Add(updated);
                next++;
            }
        }

        for (; next < parsed.Count; next++)
        {
            result.Add(new KeyValueEntry { Key = parsed[next].Key, Value = parsed[next].Value });
        }

        return result;
    }

    /// <summary>Rewrites the query part of the URL text from the parameter table.</summary>
    public static string SyncRawFromQuery(string raw, IReadOnlyList<KeyValueEntry> query)
    {
        var builder = new StringBuilder(StripQueryAndHash(raw));
        var queryString = BuildQueryString(query);
        if (queryString.Length > 0 || query.Any(q => q.Enabled))
        {
            builder.Append('?').Append(queryString);
        }

        var hash = GetHash(raw);
        if (hash is not null)
        {
            builder.Append('#').Append(hash);
        }

        return builder.ToString();
    }

    public static IEnumerable<string> PathVariableNames(string raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in Parse(raw).Path)
        {
            if (segment.Length > 1 && segment[0] == ':' && seen.Add(segment[1..]))
            {
                yield return segment[1..];
            }
        }
    }

    /// <summary>Keeps one path-variable row per <c>:name</c> segment, preserving existing values.</summary>
    public static List<KeyValueEntry> SyncPathVariables(string raw, IReadOnlyList<KeyValueEntry> current) =>
        PathVariableNames(raw)
            .Select(name => current.FirstOrDefault(c => c.Key == name)?.Clone() ?? new KeyValueEntry { Key = name, Value = "" })
            .ToList();

    /// <summary>Replaces <c>:name</c> path segments using <paramref name="lookup"/>.</summary>
    public static string SubstitutePathVariables(string urlWithoutQuery, Func<string, string?> lookup)
    {
        var segments = urlWithoutQuery.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 1 && segment[0] == ':' && lookup(segment[1..]) is { } value)
            {
                segments[i] = value;
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>Builds a raw URL from structured parts (used when a file has no <c>url.raw</c>).</summary>
    public static string BuildRaw(string? protocol, IEnumerable<string> host, string? port, IEnumerable<string> path, IEnumerable<KeyValueEntry> query, string? hash)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(protocol))
        {
            builder.Append(protocol).Append("://");
        }

        builder.Append(string.Join('.', host));
        if (!string.IsNullOrEmpty(port))
        {
            builder.Append(':').Append(port);
        }

        var pathList = path.ToList();
        if (pathList.Count > 0)
        {
            builder.Append('/').Append(string.Join('/', pathList));
        }

        var queryString = BuildQueryString(query);
        if (queryString.Length > 0)
        {
            builder.Append('?').Append(queryString);
        }

        if (!string.IsNullOrEmpty(hash))
        {
            builder.Append('#').Append(hash);
        }

        return builder.ToString();
    }

    internal static int IndexOutsideBraces(string s, char c)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{' && i + 1 < s.Length && s[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (s[i] == '}' && i + 1 < s.Length && s[i + 1] == '}' && depth > 0)
            {
                depth--;
                i++;
            }
            else if (depth == 0 && s[i] == c)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndexOutsideBraces(string s, char c)
    {
        var last = -1;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{' && i + 1 < s.Length && s[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (s[i] == '}' && i + 1 < s.Length && s[i + 1] == '}' && depth > 0)
            {
                depth--;
                i++;
            }
            else if (depth == 0 && s[i] == c)
            {
                last = i;
            }
        }

        return last;
    }

    private static List<string> SplitOutsideBraces(string s, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '{' && i + 1 < s.Length && s[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (s[i] == '}' && i + 1 < s.Length && s[i + 1] == '}' && depth > 0)
            {
                depth--;
                i++;
            }
            else if (depth == 0 && s[i] == separator)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }

        parts.Add(s[start..]);
        return parts;
    }
}
