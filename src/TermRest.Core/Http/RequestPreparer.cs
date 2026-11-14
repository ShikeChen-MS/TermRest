using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TermRest.Core.Auth;
using TermRest.Core.Certificates;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Variables;

namespace TermRest.Core.Http;

/// <summary>Defaults applied to every request (from workspace settings and command-line flags).</summary>
public sealed class ExecutionDefaults
{
    public bool StrictSsl { get; set; } = true;

    public string? CaBundlePath { get; set; }

    public TimeSpan? Timeout { get; set; }

    public bool FollowRedirects { get; set; } = true;

    public int MaxRedirects { get; set; } = 10;

    public string? Proxy { get; set; }

    public long MaxResponseBytes { get; set; } = 50L * 1024 * 1024;

    public string HttpVersion { get; set; } = "1.1";
}

/// <summary>Turns an editable <see cref="HttpRequestSpec"/> into a <see cref="PreparedRequest"/>.</summary>
public sealed class RequestPreparer(VariableResolver variables, ClientCertificateProvider? certificates, ExecutionDefaults defaults)
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2Collection EmptyTrust = [];

    public VariableResolver Variables { get; } = variables;

    /// <param name="spec">The request to prepare.</param>
    /// <param name="effectiveAuth">Auth after resolving "inherit" (see <see cref="PostmanCollection.GetEffectiveAuth"/>).</param>
    /// <param name="baseDirectory">Directory for relative file paths (the collection's folder).</param>
    public PreparedRequest Prepare(HttpRequestSpec spec, AuthSpec? effectiveAuth, string baseDirectory)
    {
        var prepared = new PreparedRequest();
        var unresolved = prepared.UnresolvedVariables;
        string R(string? text) => Variables.Resolve(text, unresolved);

        var method = R(spec.Method).Trim().ToUpperInvariant();
        prepared.Method = method.Length == 0 ? "GET" : method;
        if (!IsHttpToken(prepared.Method))
        {
            prepared.Errors.Add($"'{prepared.Method}' is not a valid HTTP method.");
        }

        // URL: resolve path variables first, then the rest, then rebuild the query from enabled parameters. Values are sent
        // as typed, as Postman sends them, except that a '#' in a value that goes into the path or the query, and a '?' in a
        // path variable, is percent-encoded: there the URL would end its path or query, and silently not send the rest.
        var pathValues = spec.PathVariables.Where(p => p.Enabled).GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => EscapeHash(R(g.Last().Value)).Replace("?", "%3F", StringComparison.Ordinal));
        var baseUrl = PostmanUrl.SubstitutePathVariables(PostmanUrl.StripQueryAndHash(spec.Url), name => pathValues.GetValueOrDefault(name));

        // In the path, the values of {{variables}} too (a '?' in one still starts the query, as in Postman).
        var pathStart = PostmanUrl.PathStart(baseUrl);
        baseUrl = (ResolveAuthority(pathStart < 0 ? baseUrl : baseUrl[..pathStart], R) + (pathStart < 0 ? "" : EscapeHash(R(baseUrl[pathStart..])))).Trim();
        // The parameter table is the source of truth (it also holds disabled rows). Specs built in code may only
        // have the query in the URL text, so fall back to parsing it.
        var queryEntries = spec.QueryParams.Count > 0
            ? spec.QueryParams
            : PostmanUrl.Parse(spec.Url).Query.Select(q => new KeyValueEntry { Key = q.Key, Value = q.Value }).ToList();
        var query = queryEntries.Where(q => q.Enabled && q.Key.Length + (q.Value?.Length ?? 0) > 0)
            .Select(q => (Key: R(q.Key), Value: q.Value is null ? null : R(q.Value)))
            .ToList();

        // Auth helpers may add headers, query parameters or challenge credentials. Of a v3 collection's or folder's credential
        // sets, the one the URL selects.
        effectiveAuth = ChooseCredentialSet(effectiveAuth, WithScheme(baseUrl), prepared.Warnings);
        prepared.AuthUsed = effectiveAuth;
        var auth = AuthApplier.Apply(effectiveAuth, Variables, unresolved);
        prepared.AuthDescription = auth.Description;
        prepared.Credential = auth.Credential;
        prepared.CredentialSchemes = auth.CredentialSchemes;
        prepared.Warnings.AddRange(auth.Warnings);
        prepared.AddSecretValues(auth.Secrets);
        foreach (var (key, value) in auth.QueryParameters)
        {
            // As in Postman, the helper's parameter replaces one of the same name and is encoded: a & would otherwise end the
            // value early, a # end the query, and a + arrive as a space.
            if (query.RemoveAll(q => q.Key == key) > 0)
            {
                prepared.Warnings.Add($"The auth helper replaced the '{key}' query parameter.");
            }

            var encoded = Uri.EscapeDataString(value);
            prepared.AddSecretValues([encoded]); // the form the URL holds
            query.Add((Uri.EscapeDataString(key), encoded));
        }

        var url = baseUrl;
        if (query.Count > 0)
        {
            url += "?" + string.Join('&', query.Select(q => q.Value is null ? EscapeHash(q.Key) : $"{EscapeHash(q.Key)}={EscapeHash(q.Value)}"));
        }

        url = WithScheme(url);
        prepared.Url = url;
        if (url.Length == 0)
        {
            prepared.Errors.Add("Enter a URL.");
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Its password (or a user name without one, then the credential) masked as in the history: only a valid URL's
            // credentials are known as secrets (AddUrlCredentials).
            prepared.Errors.Add($"'{PostmanUrl.MaskCredentials(url)}' is not a valid http(s) URL.");
        }
        else
        {
            prepared.Uri = uri;
        }

        foreach (var header in spec.Headers.Where(h => h.Enabled && h.Key.Trim().Length > 0))
        {
            prepared.Headers.Add(new(R(header.Key).Trim(), R(header.Value)));
        }

        foreach (var header in auth.Headers)
        {
            if (prepared.Headers.RemoveAll(h => string.Equals(h.Key, header.Key, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                prepared.Warnings.Add($"The auth helper replaced the '{header.Key}' header.");
            }

            prepared.Headers.Add(header);
            prepared.AuthHeaderNames.Add(header.Key);
        }

        // Credentials in the URL (https://user:password@host) go as Basic auth, as curl and Postman send them: .NET would
        // leave them out. An Authorization header or an auth helper takes precedence.
        if (prepared.Uri is { UserInfo.Length: > 0 } withCredentials && prepared.GetHeader("Authorization") is null
            && auth.Headers.Count == 0 && auth.QueryParameters.Count == 0 && auth.Credential is null)
        {
            AddUrlCredentials(prepared, withCredentials.UserInfo);
        }

        // A line break in a value (from a variable, a file …) would end the header early or add headers. A trailing one,
        // as in a pasted token, is dropped.
        for (var i = 0; i < prepared.Headers.Count; i++)
        {
            var (key, value) = prepared.Headers[i];
            var trimmed = value.TrimEnd('\r', '\n');
            if (trimmed.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                prepared.Errors.Add($"Header '{key}' contains a line break or NUL character.");
            }
            else if (trimmed.Length < value.Length)
            {
                prepared.Headers[i] = new(key, trimmed);
            }
        }

        prepared.Body = PrepareBody(spec.Body, prepared, baseDirectory, R);

        // TLS and transport settings: request settings override workspace defaults.
        prepared.StrictSsl = spec.Settings.StrictSsl ?? defaults.StrictSsl;
        prepared.FollowRedirects = spec.Settings.FollowRedirects ?? defaults.FollowRedirects;

        // A negative limit (curl's -1 for no limit, as earlier cURL imports kept it) is read as none: the workspace's applies.
        prepared.MaxRedirects = spec.Settings.MaxRedirects is int max and >= 0 ? max : defaults.MaxRedirects;
        prepared.UseCookies = !(spec.Settings.DisableCookies ?? false);
        prepared.Timeout = defaults.Timeout;
        prepared.Proxy = defaults.Proxy;
        prepared.HttpVersion = defaults.HttpVersion;
        prepared.MaxResponseBytes = defaults.MaxResponseBytes;
        prepared.ExtraTrustedCertificates = LoadTrust(prepared.Warnings);

        if (certificates is not null)
        {
            // Redirects may lead elsewhere: each URL gets the certificate selected for it (see HttpExecutor).
            var pinned = spec.Certificate?.Clone();
            var origin = prepared.Uri;
            prepared.ClientCertificateSelector = uri =>
            {
                // A certificate pinned without URL patterns goes to the request's own origin only, like its credentials.
                var own = pinned is { Matches.Count: 0 } && !(origin is not null && uri.Scheme == origin.Scheme && uri.Port == origin.Port
                    && string.Equals(uri.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)) ? null : pinned;
                var warnings = new List<string>();
                var selection = certificates.Select(uri, own, baseDirectory, ResolveSecret, warnings);
                prepared.Warnings.AddRange(warnings.Except(prepared.Warnings));
                return selection;
            };

            if (prepared.Uri is { } target && target.Scheme == Uri.UriSchemeHttps)
            {
                prepared.ClientCertificate = certificates.Select(target, pinned, baseDirectory, ResolveSecret, prepared.Warnings);
            }
        }

        if (unresolved.Count > 0)
        {
            prepared.Warnings.Add($"Unresolved variables: {string.Join(", ", unresolved.Select(v => "{{" + v + "}}"))}");
        }

        return prepared;
    }

    /// <summary>
    /// A URL as it is sent: a "://" names a scheme only before the path, query or fragment (localhost:3000/login?next=http://app/
    /// has none); without one, the URL is http://.
    /// </summary>
    private static string WithScheme(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        return url.Length > 0 && (scheme < 0 || url.AsSpan(0, scheme).IndexOfAny('/', '?', '#') >= 0) ? "http://" + url : url;
    }

    /// <summary>
    /// Of a v3 collection's or folder's credential sets, the one a request to <paramref name="url"/> uses: the first whose
    /// rules all match it (a set without rules matches any URL), as Postman's schema says. A <c>url</c> rule's value is a
    /// URL pattern as for certificates (<c>api.example.com</c>, <c>https://*.example.com/v1/*</c>, <c>*</c>), and may hold
    /// variables; a rule of another kind, or a pattern that isn't one, matches nothing. None matching: no auth (as the
    /// history keeps it, so a request sent from there gets none either), so its credentials never reach a host its rules
    /// leave out, and a warning says so. Other auth is returned as it is.
    /// </summary>
    private AuthSpec? ChooseCredentialSet(AuthSpec? auth, string url, List<string> warnings)
    {
        if (auth?.CredentialSets is not { Count: > 0 } sets)
        {
            return auth;
        }

        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        foreach (var set in sets)
        {
            if (set.Rules.All(rule => uri is not null && rule.Key == "url"
                && UrlMatchPattern.TryParse(Variables.Resolve(rule.Value), out var pattern) && pattern!.IsMatch(uri)))
            {
                return set.Auth;
            }
        }

        var names = string.Join(", ", sets.Select(s => $"'{s.Name}'"));
        warnings.Add($"No credential set of the inherited auth ({names}) has rules that match this URL: it is sent without that auth.");
        return new AuthSpec { Type = "noauth" };
    }

    /// <summary>Resolves a secret reference: <c>env:NAME</c> reads an environment variable, <c>{{var}}</c> a workspace variable.</summary>
    public string? ResolveSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable(value[4..]);
        }

        return Variables.Resolve(value);
    }

    private System.Security.Cryptography.X509Certificates.X509Certificate2Collection? LoadTrust(List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(defaults.CaBundlePath))
        {
            return null;
        }

        try
        {
            // The provider keeps it parsed for the session (a CA bundle can hold hundreds of certificates).
            return certificates is null ? CertificateLoader.LoadTrustBundle(defaults.CaBundlePath) : certificates.LoadTrustBundle(defaults.CaBundlePath);
        }
        catch (CertificateLoadException ex)
        {
            warnings.Add($"CA bundle '{defaults.CaBundlePath}' not loaded: {ex.Message}");
            return EmptyTrust;
        }
    }

    private static PreparedBody PrepareBody(RequestBody body, PreparedRequest prepared, string baseDirectory, Func<string?, string> resolve)
    {
        if (body.Disabled)
        {
            return PreparedBody.None;
        }

        string? explicitType = prepared.GetHeader("Content-Type");

        // A path typed or pasted with spaces around it, as the Body tab finds it.
        string FilePath(string path) => Path.IsPathRooted(path = path.Trim()) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

        // As in Postman, an empty body is no body, sent without a Content-Type (some servers refuse an empty JSON body): raw
        // text that is empty once its variables are replaced, a form without an enabled field (with a key), a file body
        // without a file.
        switch (body.Mode)
        {
            case BodyMode.Raw:
            {
                var text = resolve(body.Raw);
                return text.Length == 0 ? PreparedBody.None : new PreparedBody
                {
                    Kind = PreparedBodyKind.Text,
                    Text = text,
                    ContentType = explicitType ?? RequestBody.ContentTypeForLanguage(body.RawLanguage),
                };
            }

            case BodyMode.UrlEncoded:
            {
                var fields = body.UrlEncoded.Where(f => f.Enabled && f.Key.Length > 0).Select(f => new KeyValuePair<string, string>(resolve(f.Key), resolve(f.Value))).ToList();
                return fields.Count == 0 ? PreparedBody.None : new PreparedBody
                {
                    Kind = PreparedBodyKind.FormUrlEncoded,
                    Fields = fields,
                    ContentType = explicitType ?? "application/x-www-form-urlencoded",
                };
            }

            case BodyMode.FormData:
            {
                var fields = body.FormData.Where(f => f.Enabled && f.Key.Length > 0).ToList();
                if (fields.Count == 0)
                {
                    return PreparedBody.None;
                }

                var parts = new List<MultipartPart>();
                foreach (var field in fields)
                {
                    // Sent as typed, as curl and Postman send it: a line break would end the part's header there.
                    var name = resolve(field.Key);
                    var type = string.IsNullOrWhiteSpace(field.ContentType) ? null : field.ContentType.Trim();
                    if (type is not null && type.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                    {
                        prepared.Errors.Add($"The content type of form-data field '{name}' contains a line break or NUL character.");
                    }

                    if (field.IsFile)
                    {
                        foreach (var src in (field.Src ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var path = FilePath(resolve(src));
                            if (File.Exists(path))
                            {
                                parts.Add(new MultipartPart { Name = name, FilePath = path, ContentType = type });
                            }
                            else
                            {
                                prepared.Errors.Add($"Form-data file '{path}' does not exist.");
                            }
                        }
                    }
                    else
                    {
                        parts.Add(new MultipartPart { Name = name, Value = resolve(field.Value), ContentType = type });
                    }
                }

                if (explicitType is not null)
                {
                    prepared.Warnings.Add("A Content-Type header was set for a form-data body; the generated multipart boundary header is used instead.");
                    prepared.Headers.RemoveAll(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
                }

                return new PreparedBody { Kind = PreparedBodyKind.Multipart, Parts = parts };
            }

            case BodyMode.File:
            {
                var source = resolve(body.FileSrc);
                if (string.IsNullOrWhiteSpace(source))
                {
                    prepared.Warnings.Add("No file is chosen for the body: the request is sent without one.");
                    return PreparedBody.None;
                }

                var path = FilePath(source);
                if (!File.Exists(path))
                {
                    prepared.Errors.Add($"Body file '{path}' does not exist.");
                    return PreparedBody.None;
                }

                // The file is read (and, when typed, checked) only when the request is sent.
                var language = BodyFile.Normalize(body.FileLanguage);
                return new PreparedBody
                {
                    Kind = PreparedBodyKind.File,
                    FilePath = path,
                    FileLanguage = language,
                    ContentType = explicitType ?? (language is null ? "application/octet-stream" : RequestBody.ContentTypeForLanguage(language)),
                };
            }

            case BodyMode.GraphQL:
            {
                var payload = new JsonObject { ["query"] = resolve(body.GraphQLQuery) };
                var variablesText = resolve(body.GraphQLVariables);
                if (!string.IsNullOrWhiteSpace(variablesText))
                {
                    try
                    {
                        payload["variables"] = JsonNode.Parse(variablesText);
                    }
                    catch (JsonException ex)
                    {
                        prepared.Errors.Add($"GraphQL variables are not valid JSON: {ex.Message}");
                    }
                }

                return new PreparedBody
                {
                    Kind = PreparedBodyKind.Text,
                    Text = payload.ToJsonString(),
                    ContentType = explicitType ?? "application/json",
                };
            }

            default:
                return PreparedBody.None;
        }
    }

    /// <summary>Sends the user info of the URL (as <see cref="System.Uri"/> escapes it) as <c>Authorization: Basic</c>.</summary>
    private static void AddUrlCredentials(PreparedRequest prepared, string userInfo)
    {
        var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        var escapedUser = colon < 0 ? userInfo : userInfo[..colon];
        var user = Uri.UnescapeDataString(escapedUser);
        var password = colon < 0 ? "" : userInfo[(colon + 1)..];
        var token = AuthApplier.BasicToken(user, Uri.UnescapeDataString(password));
        prepared.Headers.Add(new("Authorization", "Basic " + token));
        prepared.AuthHeaderNames.Add("Authorization");
        prepared.AuthDescription = "Basic (from the URL)"; // without the user name, as for the auth helper (see AuthApplier)

        // The password also as typed: the history keeps the URL as typed (masked), which System.Uri may escape differently.
        var secrets = new List<string> { password, Uri.UnescapeDataString(password), token };
        var url = prepared.Url;
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = start < 3 ? -1 : url.IndexOf('@', start);
        var separator = end < 0 ? -1 : url.IndexOf(':', start, end - start);
        if (separator >= 0)
        {
            secrets.Add(url[(separator + 1)..end]);
        }

        // Without a password, the user name is the credential (https://sk_test_…@host, as Stripe takes its key): in every form.
        if (password.Length == 0)
        {
            secrets.AddRange([escapedUser, user]);
            if (end >= 0)
            {
                secrets.Add(url[start..(separator >= 0 ? separator : end)]);
            }
        }

        prepared.AddSecretValues(secrets);
    }

    /// <summary>RFC 9110 token: what <see cref="System.Net.Http.HttpMethod"/> accepts (an unresolved {{variable}} is not).</summary>
    private static bool IsHttpToken(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal));

    /// <summary>A path or query value with its '#' percent-encoded (see <see cref="PreparedRequest.AddSecretValues"/>).</summary>
    private static string EscapeHash(string value) => value.Replace("#", "%23", StringComparison.Ordinal);

    /// <summary>
    /// The authority of a URL as typed (scheme://user:password@host:port) with its {{variables}} replaced. As in Postman, the
    /// user info is read before they are, and characters that would end it there are percent-encoded: a '@', '/', '?', '#'
    /// or '\' in a password (or base64 token) would make the URL invalid, or send part of it as the host or path. '%' stays,
    /// so a value already encoded is sent as it is.
    /// </summary>
    private static string ResolveAuthority(string authority, Func<string, string> resolve)
    {
        if (PostmanUrl.FindUserInfo(authority) is not var (start, length))
        {
            return resolve(authority);
        }

        return string.Concat(resolve(authority[..start]), InUserInfo(resolve(authority.Substring(start, length))), resolve(authority[(start + length)..]));
    }

    /// <summary>
    /// <paramref name="value"/> as the user info of a URL holds it (see <see cref="ResolveAuthority"/>): its '@', '/', '?', '#'
    /// and '\' percent-encoded. Secret values are known in this form too (<see cref="PreparedRequest.AddSecretValues"/>).
    /// </summary>
    internal static string InUserInfo(string value)
    {
        if (value.AsSpan().IndexOfAny("@/?#\\") < 0)
        {
            return value;
        }

        var userInfo = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            _ = c switch
            {
                '@' => userInfo.Append("%40"),
                '/' => userInfo.Append("%2F"),
                '?' => userInfo.Append("%3F"),
                '#' => userInfo.Append("%23"),
                '\\' => userInfo.Append("%5C"),
                _ => userInfo.Append(c),
            };
        }

        return userInfo.ToString();
    }
}
