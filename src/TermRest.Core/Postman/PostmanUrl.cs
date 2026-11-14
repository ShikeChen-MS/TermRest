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

    /// <summary>Where the user info of <paramref name="raw"/> is (user:password in https://user:password@host), as <see cref="Parse"/> reads it.</summary>
    public static (int Start, int Length)? FindUserInfo(string raw)
    {
        var s = StripQueryAndHash(raw.TrimStart());
        var offset = raw.Length - raw.TrimStart().Length;
        var protocol = s.IndexOf("://", StringComparison.Ordinal);
        if (protocol > 0 && !s[..protocol].Contains('/', StringComparison.Ordinal))
        {
            offset += protocol + 3;
            s = s[(protocol + 3)..];
        }

        var slash = IndexOutsideBraces(s, '/');
        var at = LastIndexOutsideBraces(slash >= 0 ? s[..slash] : s, '@');
        return at < 0 ? null : (offset, at);
    }

    /// <summary>Where the password of <paramref name="raw"/>'s user info is (https://user:password@host), as <see cref="Parse"/> reads it.</summary>
    public static (int Start, int Length)? FindPassword(string raw)
    {
        if (FindUserInfo(raw) is not var (start, length))
        {
            return null;
        }

        var colon = raw.IndexOf(':', start, length);
        return colon < 0 ? null : (colon + 1, start + length - colon - 1);
    }

    /// <summary>
    /// <paramref name="raw"/> with the password of its user info (see <see cref="FindPassword"/>) masked as <c>***</c>; with
    /// <paramref name="isSecret"/>, only a password it says is one.
    /// </summary>
    public static string MaskPassword(string raw, Func<string, bool>? isSecret = null) =>
        FindPassword(raw) is var (start, length) && length > 0 && raw.AsSpan(start, length) is not "***"
        && (isSecret is null || isSecret(raw.Substring(start, length)))
            ? string.Concat(raw.AsSpan(0, start), "***", raw.AsSpan(start + length))
            : raw;

    /// <summary>
    /// <paramref name="raw"/> with the credential of its user info masked as <c>***</c>: the password
    /// (https://user:password@host), or the user name when there is no password (https://user@host, https://user:@host: it
    /// is then the credential); with <paramref name="isSecret"/>, only a value it says is one.
    /// </summary>
    public static string MaskCredentials(string raw, Func<string, bool>? isSecret = null)
    {
        var parsed = Parse(raw);
        if (!string.IsNullOrEmpty(parsed.AuthPassword) || parsed.AuthUser is not { Length: > 0 } user || user == "***" || isSecret?.Invoke(user) == false)
        {
            return MaskPassword(raw, isSecret);
        }

        // The user name starts the authority, after the scheme.
        var start = raw.Length - raw.TrimStart().Length + (parsed.Protocol is { } protocol ? protocol.Length + 3 : 0);
        return raw.AsSpan(start).StartsWith(user, StringComparison.Ordinal)
            ? string.Concat(raw.AsSpan(0, start), "***", raw.AsSpan(start + user.Length))
            : raw;
    }

    /// <summary>Where the path of <paramref name="raw"/> (a URL without query and fragment) starts, as <see cref="Parse"/> reads it: its first '/', or -1.</summary>
    public static int PathStart(string raw)
    {
        var start = raw.IndexOf("://", StringComparison.Ordinal);
        start = start > 0 && !raw.AsSpan(0, start).Contains('/') ? start + 3 : 0;
        var slash = IndexOutsideBraces(raw[start..], '/');
        return slash < 0 ? -1 : start + slash;
    }

    public static string? GetHash(string raw)
    {
        var hashIndex = IndexOutsideBraces(raw, '#');
        return hashIndex >= 0 ? raw[(hashIndex + 1)..] : null;
    }

    public static string BuildQueryString(IEnumerable<KeyValueEntry> entries) =>
        string.Join('&', entries.Where(e => e.Enabled).Select(e => e.Value is null ? e.Key : $"{e.Key}={e.Value}"));

    /// <summary>
    /// Re-derives the parameter table after the URL text changed. Existing rows are matched by key and value, then
    /// by key, then in order (an edited key), so descriptions and unknown properties stay with their parameter when
    /// parameters are removed or reordered. Disabled rows keep their places.
    /// </summary>
    public static List<KeyValueEntry> SyncQueryFromRaw(string raw, IReadOnlyList<KeyValueEntry> current)
    {
        var parsed = Parse(raw).Query;
        var enabled = current.Where(e => e.Enabled).ToList();
        var used = new bool[enabled.Count];
        var matched = new KeyValueEntry?[parsed.Count];

        void Match(Func<KeyValueEntry, (string Key, string? Value), bool> predicate)
        {
            for (var i = 0; i < parsed.Count; i++)
            {
                for (var j = 0; matched[i] is null && j < enabled.Count; j++)
                {
                    if (!used[j] && predicate(enabled[j], parsed[i]))
                    {
                        matched[i] = enabled[j];
                        used[j] = true;
                    }
                }
            }
        }

        Match((row, p) => row.Key == p.Key && row.Value == p.Value);
        Match((row, p) => row.Key == p.Key);
        Match((_, _) => true);

        var rows = new List<KeyValueEntry>(parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var row = matched[i]?.Clone() ?? new KeyValueEntry();
            row.Key = parsed[i].Key;
            row.Value = parsed[i].Value;
            rows.Add(row);
        }

        // Enabled slots are filled in URL order around the disabled rows; extra parameters go at the end.
        var result = new List<KeyValueEntry>(Math.Max(rows.Count, current.Count));
        var next = 0;
        foreach (var entry in current)
        {
            if (!entry.Enabled)
            {
                result.Add(entry.Clone());
            }
            else if (next < rows.Count)
            {
                result.Add(rows[next++]);
            }
        }

        result.AddRange(rows.Skip(next));
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
