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
    private System.Security.Cryptography.X509Certificates.X509Certificate2Collection? _trust;
    private string? _trustError;

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

        // URL: resolve path variables first, then the rest, then rebuild the query from enabled parameters.
        var pathValues = spec.PathVariables.Where(p => p.Enabled).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => R(g.Last().Value));
        var baseUrl = PostmanUrl.SubstitutePathVariables(PostmanUrl.StripQueryAndHash(spec.Url), name => pathValues.GetValueOrDefault(name));
        baseUrl = R(baseUrl).Trim();
        // The parameter table is the source of truth (it also holds disabled rows). Specs built in code may only
        // have the query in the URL text, so fall back to parsing it.
        var queryEntries = spec.QueryParams.Count > 0
            ? spec.QueryParams
            : PostmanUrl.Parse(spec.Url).Query.Select(q => new KeyValueEntry { Key = q.Key, Value = q.Value }).ToList();
        var query = queryEntries.Where(q => q.Enabled && q.Key.Length + (q.Value?.Length ?? 0) > 0)
            .Select(q => (Key: R(q.Key), Value: q.Value is null ? null : R(q.Value)))
            .ToList();

        // Auth helpers may add headers, query parameters or challenge credentials.
        var auth = AuthApplier.Apply(effectiveAuth, Variables, unresolved);
        prepared.AuthDescription = auth.Description;
        prepared.Credential = auth.Credential;
        prepared.Warnings.AddRange(auth.Warnings);
        prepared.SecretValues.UnionWith(auth.Secrets.Where(s => s.Length > 0));
        query.AddRange(auth.QueryParameters.Select(q => (q.Key, (string?)q.Value)));

        var url = baseUrl;
        if (query.Count > 0)
        {
            url += "?" + string.Join('&', query.Select(q => q.Value is null ? q.Key : $"{q.Key}={q.Value}"));
        }

        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        prepared.Url = url;
        if (url.Length == 0)
        {
            prepared.Errors.Add("Enter a URL.");
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            prepared.Errors.Add($"'{url}' is not a valid http(s) URL.");
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
        }

        prepared.Body = PrepareBody(spec.Body, prepared, baseDirectory, R);

        // TLS and transport settings: request settings override workspace defaults.
        prepared.StrictSsl = spec.Settings.StrictSsl ?? defaults.StrictSsl;
        prepared.FollowRedirects = spec.Settings.FollowRedirects ?? defaults.FollowRedirects;
        prepared.MaxRedirects = spec.Settings.MaxRedirects ?? defaults.MaxRedirects;
        prepared.UseCookies = !(spec.Settings.DisableCookies ?? false);
        prepared.Timeout = defaults.Timeout;
        prepared.Proxy = defaults.Proxy;
        prepared.HttpVersion = defaults.HttpVersion;
        prepared.MaxResponseBytes = defaults.MaxResponseBytes;
        prepared.ExtraTrustedCertificates = LoadTrust(prepared.Warnings);

        if (prepared.Uri is { } target && target.Scheme == Uri.UriSchemeHttps && certificates is not null)
        {
            prepared.ClientCertificate = certificates.Select(target, spec.Certificate, baseDirectory, ResolveSecret, prepared.Warnings);
        }

        if (unresolved.Count > 0)
        {
            prepared.Warnings.Add($"Unresolved variables: {string.Join(", ", unresolved.Select(v => "{{" + v + "}}"))}");
        }

        return prepared;
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

        if (_trust is null && _trustError is null)
        {
            try
            {
                _trust = CertificateLoader.LoadTrustBundle(defaults.CaBundlePath);
            }
            catch (Exception ex) when (ex is CertificateLoadException or IOException or System.Security.Cryptography.CryptographicException)
            {
                _trustError = $"CA bundle '{defaults.CaBundlePath}' not loaded: {ex.Message}";
            }
        }

        if (_trustError is not null)
        {
            warnings.Add(_trustError);
        }

        return _trust ?? EmptyTrust;
    }

    private static PreparedBody PrepareBody(RequestBody body, PreparedRequest prepared, string baseDirectory, Func<string?, string> resolve)
    {
        if (body.Disabled)
        {
            return PreparedBody.None;
        }

        string? explicitType = prepared.GetHeader("Content-Type");
        string FilePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

        switch (body.Mode)
        {
            case BodyMode.Raw:
                return new PreparedBody
                {
                    Kind = PreparedBodyKind.Text,
                    Text = resolve(body.Raw),
                    ContentType = explicitType ?? RequestBody.ContentTypeForLanguage(body.RawLanguage),
                };

            case BodyMode.UrlEncoded:
                return new PreparedBody
                {
                    Kind = PreparedBodyKind.FormUrlEncoded,
                    Fields = body.UrlEncoded.Where(f => f.Enabled && f.Key.Length > 0).Select(f => new KeyValuePair<string, string>(resolve(f.Key), resolve(f.Value))).ToList(),
                    ContentType = explicitType ?? "application/x-www-form-urlencoded",
                };

            case BodyMode.FormData:
            {
                var parts = new List<MultipartPart>();
                foreach (var field in body.FormData.Where(f => f.Enabled && f.Key.Length > 0))
                {
                    if (field.IsFile)
                    {
                        foreach (var src in (field.Src ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var path = FilePath(resolve(src));
                            if (File.Exists(path))
                            {
                                parts.Add(new MultipartPart { Name = resolve(field.Key), FilePath = path, ContentType = field.ContentType });
                            }
                            else
                            {
                                prepared.Errors.Add($"Form-data file '{path}' does not exist.");
                            }
                        }
                    }
                    else
                    {
                        parts.Add(new MultipartPart { Name = resolve(field.Key), Value = resolve(field.Value), ContentType = field.ContentType });
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
                if (string.IsNullOrWhiteSpace(body.FileSrc))
                {
                    prepared.Errors.Add("Select a file for the binary body.");
                    return PreparedBody.None;
                }

                var path = FilePath(resolve(body.FileSrc));
                if (!File.Exists(path))
                {
                    prepared.Errors.Add($"Body file '{path}' does not exist.");
                    return PreparedBody.None;
                }

                return new PreparedBody { Kind = PreparedBodyKind.File, FilePath = path, ContentType = explicitType ?? "application/octet-stream" };
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

    /// <summary>RFC 9110 token: what <see cref="System.Net.Http.HttpMethod"/> accepts (an unresolved {{variable}} is not).</summary>
    private static bool IsHttpToken(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal));
}
