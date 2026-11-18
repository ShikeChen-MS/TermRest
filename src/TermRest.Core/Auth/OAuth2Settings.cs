using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Core.Certificates;
using TermRest.Core.Json;
using TermRest.Core.Model;

namespace TermRest.Core.Auth;

/// <summary>An OAuth 2.0 grant (Postman's <c>grant_type</c>).</summary>
public enum OAuth2Grant
{
    AuthorizationCode,
    AuthorizationCodeWithPkce,
    ClientCredentials,
    Password,
    Implicit,
}

/// <summary>How the client authenticates to the token endpoint (Postman's <c>client_authentication</c>).</summary>
public enum OAuth2ClientAuthentication
{
    /// <summary>HTTP Basic with the client ID and secret (Postman's <c>header</c>, its default).</summary>
    BasicHeader,

    /// <summary>The client ID and secret in the form (Postman's <c>body</c>).</summary>
    Body,

    /// <summary>
    /// A JWT signed with the private key of a registered certificate (<c>private_key_jwt</c> of OpenID Connect, RFC 7523):
    /// a TermRest addition.
    /// </summary>
    PrivateKeyJwt,
}

/// <summary>Where an extra parameter of a token, authorization or refresh request goes (Postman's <c>send_as</c>).</summary>
public enum OAuth2ParameterTarget
{
    Body,
    Header,
    Url,
}

/// <summary>An extra parameter of a token, authorization or refresh request (Postman's advanced options), resolved.</summary>
public sealed record OAuth2Parameter(string Key, string Value, OAuth2ParameterTarget Target);

/// <summary>
/// The settings of an OAuth 2.0 auth helper (Postman's <c>oauth2</c> parameters) a token is fetched with, their variables
/// resolved: see <see cref="Read"/>. Postman's keys, and TermRest's for a client assertion signed with a certificate
/// (<c>client_authentication: private_key_jwt</c>): <c>x-clientAssertionCertificate</c> (the name of a certificate in the
/// workspace's registry), <c>x-clientAssertionAlgorithm</c>, <c>x-clientAssertionKeyId</c>, <c>x-clientAssertionChain</c>
/// and <c>x-clientAssertionAudience</c>.
/// </summary>
public sealed class OAuth2Settings
{
    /// <summary>The values of <c>client_authentication</c>: Postman's, and TermRest's <c>private_key_jwt</c>.</summary>
    public static readonly string[] ClientAuthenticationValues = ["header", "body", "private_key_jwt"];

    /// <summary>The values of <c>grant_type</c> (Postman's), in the order of <see cref="OAuth2Grant"/>.</summary>
    public static readonly string[] GrantTypes = ["authorization_code", "authorization_code_with_pkce", "client_credentials", "password_credentials", "implicit"];

    private OAuth2Settings()
    {
    }

    public OAuth2Grant Grant { get; private init; }

    public string AccessTokenUrl { get; private init; } = "";

    public string AuthUrl { get; private init; } = "";

    /// <summary>The callback URL; empty: TermRest listens on a loopback port of its choosing (see <see cref="LoopbackRedirect"/>).</summary>
    public string RedirectUri { get; private init; } = "";

    public string ClientId { get; private init; } = "";

    public string ClientSecret { get; private init; } = "";

    public string Scope { get; private init; } = "";

    public string Username { get; private init; } = "";

    public string Password { get; private init; } = "";

    /// <summary>The state sent with the authorization request; empty: a random one.</summary>
    public string State { get; private init; } = "";

    /// <summary>The PKCE code verifier; empty: a random one.</summary>
    public string CodeVerifier { get; private init; } = "";

    /// <summary>The PKCE code challenge method: <c>S256</c> or <c>plain</c>.</summary>
    public string ChallengeMethod { get; private init; } = "S256";

    public OAuth2ClientAuthentication ClientAuthentication { get; private init; }

    /// <summary>The name of the registered certificate that signs the client assertion.</summary>
    public string AssertionCertificate { get; private init; } = "";

    /// <summary>The JWS algorithm of the client assertion (RS256, PS256, ES256 …); empty: chosen for the key (see <see cref="ClientAssertion"/>).</summary>
    public string AssertionAlgorithm { get; private init; } = "";

    /// <summary>The <c>kid</c> of the client assertion's header; empty: none.</summary>
    public string AssertionKeyId { get; private init; } = "";

    /// <summary>Whether the client assertion's header holds the certificate chain (<c>x5c</c>).</summary>
    public bool AssertionChain { get; private init; }

    /// <summary>The audience of the client assertion; empty: the URL of the token endpoint it goes to.</summary>
    public string AssertionAudience { get; private init; } = "";

    /// <summary>The certificate that signs the client assertion, loaded when the request was prepared (see <see cref="Http.RequestPreparer"/>).</summary>
    public LoadedCertificate? SigningCertificate { get; set; }

    /// <summary>Where a refresh token is redeemed; empty: <see cref="AccessTokenUrl"/>.</summary>
    public string RefreshTokenUrl { get; private init; } = "";

    public IReadOnlyList<OAuth2Parameter> TokenRequestParams { get; private init; } = [];

    public IReadOnlyList<OAuth2Parameter> AuthRequestParams { get; private init; } = [];

    public IReadOnlyList<OAuth2Parameter> RefreshRequestParams { get; private init; } = [];

    /// <summary>The token goes in the <c>access_token</c> query parameter rather than the Authorization header.</summary>
    public bool AddToQuery { get; private init; }

    /// <summary>What precedes the token in the Authorization header (Bearer); empty: the token alone.</summary>
    public string HeaderPrefix { get; private init; } = "Bearer";

    /// <summary>
    /// What identifies the token these settings fetch (a hash of everything that goes into it, secrets too): tokens are
    /// cached under it. The state, code verifier and where the token goes don't count.
    /// </summary>
    public string CacheKey => field ??= ComputeCacheKey();

    public bool IsAuthorizationCode => Grant is OAuth2Grant.AuthorizationCode or OAuth2Grant.AuthorizationCodeWithPkce;

    public string GrantName => Describe(Grant);

    /// <summary>The credentials among them (the client secret, the password), which are kept out of recordings and logs.</summary>
    public IEnumerable<string> Secrets => new[] { ClientSecret, Password }.Where(s => s.Length > 0);

    public static string Describe(OAuth2Grant grant) => grant switch
    {
        OAuth2Grant.AuthorizationCode => "authorization code",
        OAuth2Grant.AuthorizationCodeWithPkce => "authorization code with PKCE",
        OAuth2Grant.ClientCredentials => "client credentials",
        OAuth2Grant.Password => "password",
        _ => "implicit",
    };

    /// <summary>The grant a <c>grant_type</c> names; Postman's default (authorization code) for none, null for one it has not.</summary>
    public static OAuth2Grant? ParseGrant(string? grantType) => grantType?.Trim().ToLowerInvariant() switch
    {
        null or "" or "authorization_code" => OAuth2Grant.AuthorizationCode,
        "authorization_code_with_pkce" => OAuth2Grant.AuthorizationCodeWithPkce,
        "client_credentials" => OAuth2Grant.ClientCredentials,
        "password_credentials" or "password" => OAuth2Grant.Password,
        "implicit" => OAuth2Grant.Implicit,
        _ => null,
    };

    /// <summary>
    /// The settings of <paramref name="auth"/> (an <c>oauth2</c> auth helper) a token is fetched with, their values resolved
    /// by <paramref name="resolve"/>: a token is fetched when its URLs are set (the access token URL; for authorization
    /// code, the auth URL too). Null when they are not, as when Postman writes the helper with a token pasted in it, or for
    /// the implicit grant: <paramref name="notFetched"/> says why, and the token set in it (<c>accessToken</c>) is used.
    /// Only the settings its grant uses are resolved, so others name no unresolved variables.
    /// </summary>
    public static OAuth2Settings? Read(AuthSpec auth, Func<string?, string> resolve, out string notFetched)
    {
        string Trimmed(string key) => resolve(auth.Get(key)).Trim();

        var grantType = auth.Get("grant_type");
        if (ParseGrant(grantType) is not { } grant)
        {
            notFetched = $"its grant type '{grantType}' is not one TermRest knows";
            return null;
        }

        if (grant == OAuth2Grant.Implicit)
        {
            // Its token comes back in the URL's fragment, where the browser keeps it: RFC 9700 retires it.
            notFetched = "the implicit grant is not supported (the OAuth 2.0 Security Best Current Practice retires it): use authorization code with PKCE";
            return null;
        }

        var code = grant is OAuth2Grant.AuthorizationCode or OAuth2Grant.AuthorizationCodeWithPkce;
        var tokenUrl = Trimmed("accessTokenUrl");
        var authUrl = code ? Trimmed("authUrl") : "";
        if (tokenUrl.Length == 0 || (code && authUrl.Length == 0))
        {
            notFetched = tokenUrl.Length == 0 ? "its access token URL is empty" : "its auth URL is empty";
            return null;
        }

        notFetched = "";
        var clientAuthentication = auth.Get("client_authentication")?.Trim().ToLowerInvariant() switch
        {
            "body" => OAuth2ClientAuthentication.Body,
            "private_key_jwt" => OAuth2ClientAuthentication.PrivateKeyJwt,
            _ => OAuth2ClientAuthentication.BasicHeader,
        };
        var signed = clientAuthentication == OAuth2ClientAuthentication.PrivateKeyJwt;
        var pkce = grant == OAuth2Grant.AuthorizationCodeWithPkce;
        var password = grant == OAuth2Grant.Password;
        var settings = new OAuth2Settings
        {
            Grant = grant,
            AccessTokenUrl = tokenUrl,
            AuthUrl = authUrl,
            RedirectUri = code ? Trimmed("redirect_uri") : "",
            ClientId = Trimmed("clientId"),
            ClientSecret = signed ? "" : resolve(auth.Get("clientSecret")),
            Scope = Trimmed("scope"),
            Username = password ? resolve(auth.Get("username")) : "",
            Password = password ? resolve(auth.Get("password")) : "",
            State = code ? resolve(auth.Get("state")) : "",
            CodeVerifier = pkce ? Trimmed("code_verifier") : "",
            ChallengeMethod = pkce && string.Equals(auth.Get("challengeAlgorithm")?.Trim(), "plain", StringComparison.OrdinalIgnoreCase) ? "plain" : "S256",
            ClientAuthentication = clientAuthentication,
            AssertionCertificate = signed ? Trimmed("x-clientAssertionCertificate") : "",
            AssertionAlgorithm = signed ? Trimmed("x-clientAssertionAlgorithm").ToUpperInvariant() : "",
            AssertionKeyId = signed ? Trimmed("x-clientAssertionKeyId") : "",
            AssertionChain = signed && string.Equals(auth.Get("x-clientAssertionChain")?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            AssertionAudience = signed ? Trimmed("x-clientAssertionAudience") : "",
            RefreshTokenUrl = Trimmed("refreshTokenUrl"),
            TokenRequestParams = ReadParameters(auth, "tokenRequestParams", resolve),
            AuthRequestParams = code ? ReadParameters(auth, "authRequestParams", resolve) : [],
            RefreshRequestParams = ReadParameters(auth, "refreshRequestParams", resolve),
            AddToQuery = string.Equals(auth.Get("addTokenTo"), "queryParams", StringComparison.OrdinalIgnoreCase),
            HeaderPrefix = HeaderPrefixOf(auth, resolve),
        };
        return settings;
    }

    /// <summary>
    /// What precedes the token in the Authorization header: <c>headerPrefix</c> without the white space around it (Postman
    /// writes "Bearer " too), Bearer when it is not set.
    /// </summary>
    public static string HeaderPrefixOf(AuthSpec auth, Func<string?, string> resolve) =>
        auth.Get("headerPrefix") is { } prefix ? resolve(prefix).Trim() : "Bearer";

    /// <summary>
    /// The enabled extra parameters in <paramref name="key"/> (a list of <c>{key, value, enabled, send_as}</c>, as Postman
    /// writes them), resolved; those without a key are left out.
    /// </summary>
    private static List<OAuth2Parameter> ReadParameters(AuthSpec auth, string key, Func<string?, string> resolve)
    {
        var parameters = new List<OAuth2Parameter>();
        if (auth.Params.FirstOrDefault(p => p.Key == key)?.Value is not JsonArray list)
        {
            return parameters;
        }

        foreach (var item in list.OfType<JsonObject>())
        {
            var name = resolve(item["key"].GetString()).Trim();
            if (item["enabled"].GetBool() == false || name.Length == 0)
            {
                continue;
            }

            var target = item["send_as"].GetString() switch
            {
                "request_header" => OAuth2ParameterTarget.Header,
                "request_url" => OAuth2ParameterTarget.Url,
                _ => OAuth2ParameterTarget.Body,
            };
            parameters.Add(new OAuth2Parameter(name, resolve(item["value"].GetString()), target));
        }

        return parameters;
    }

    private string ComputeCacheKey()
    {
        var text = new StringBuilder();
        void Add(string value) => text.Append(value.Length).Append(':').Append(value).Append('|');

        foreach (var value in (string[])[Grant.ToString(), AccessTokenUrl, AuthUrl, RedirectUri, ClientId, ClientSecret, Scope, Username, Password,
                     ClientAuthentication.ToString(), AssertionCertificate, AssertionAlgorithm, AssertionKeyId, AssertionChain ? "chain" : "", AssertionAudience,
                     RefreshTokenUrl])
        {
            Add(value);
        }

        foreach (var (name, parameters) in (ReadOnlySpan<(string, IReadOnlyList<OAuth2Parameter>)>)[("token", TokenRequestParams), ("auth", AuthRequestParams), ("refresh", RefreshRequestParams)])
        {
            Add(name);
            foreach (var parameter in parameters)
            {
                Add(parameter.Target.ToString());
                Add(parameter.Key);
                Add(parameter.Value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
