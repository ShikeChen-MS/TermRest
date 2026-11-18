using System.Net;
using System.Text;
using TermRest.Core.Model;
using TermRest.Core.Variables;

namespace TermRest.Core.Auth;

/// <summary>The effect of an auth helper on an outgoing request.</summary>
public sealed class AppliedAuth
{
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    public List<KeyValuePair<string, string>> QueryParameters { get; } = [];

    /// <summary>Credentials answered on a 401 challenge (Digest, NTLM, Negotiate).</summary>
    public NetworkCredential? Credential { get; set; }

    /// <summary>
    /// The challenge schemes <see cref="Credential"/> answers (Digest; NTLM and Negotiate): never Basic, which would send
    /// the password in clear text.
    /// </summary>
    public string[] CredentialSchemes { get; set; } = [];

    public string Description { get; set; } = "No auth";

    public List<string> Warnings { get; } = [];

    /// <summary>Resolved credentials (passwords, tokens, key values) that must not end up in history or logs.</summary>
    public List<string> Secrets { get; } = [];

    /// <summary>
    /// OAuth 2.0 settings to fetch the access token with before the request is sent (see <see cref="OAuth2Tokens"/>); the
    /// token is added then. Null for other auth, and for an OAuth 2.0 helper that has a token set but no URLs to fetch one.
    /// </summary>
    public OAuth2Settings? OAuth2 { get; set; }
}

public static class AuthApplier
{
    public static AppliedAuth Apply(AuthSpec? auth, VariableResolver variables, ISet<string> unresolved)
    {
        var result = new AppliedAuth();
        if (auth is null || auth.Type == "noauth")
        {
            return result;
        }

        string Get(string key) => variables.Resolve(auth.Get(key) ?? "", unresolved);

        switch (auth.Type)
        {
            case "basic":
            {
                var user = Get("username");
                var password = Get("password");
                var token = BasicToken(user, password);
                result.Headers.Add(new("Authorization", $"Basic {token}"));

                // The description leaves out the user name, which can be the credential itself: an API key sent with an empty
                // or dummy password, as Stripe and Freshdesk take theirs (curl -u sk_test_…:). Without a password, it is a secret.
                result.Description = "Basic";
                result.Secrets.Add(password);
                result.Secrets.Add(token);
                if (password.Length == 0)
                {
                    result.Secrets.Add(user);
                }

                break;
            }

            case "bearer":
            {
                var token = Get("token");
                result.Secrets.Add(token);
                result.Description = "Bearer token";
                if (token.Length == 0)
                {
                    // As in Postman: no header then (an Authorization header the request has stays), as for a token that a
                    // login request's script sets later.
                    result.Warnings.Add("Bearer token is empty: the request was sent without it.");
                    break;
                }

                result.Headers.Add(new("Authorization", $"Bearer {token}"));
                break;
            }

            case "apikey":
            {
                var key = Get("key");
                var value = Get("value");
                result.Secrets.Add(value);
                var location = auth.Get("in") ?? "header";
                if (key.Length == 0)
                {
                    result.Warnings.Add("API key name is empty; nothing was added.");
                }
                else if (string.Equals(location, "query", StringComparison.OrdinalIgnoreCase))
                {
                    result.QueryParameters.Add(new(key, value));
                }
                else
                {
                    result.Headers.Add(new(key, value));
                }

                result.Description = $"API key ({key} in {location})";
                break;
            }

            case "digest":
            case "ntlm":
            {
                var user = Get("username");
                var password = Get("password");
                var domain = auth.Type == "ntlm" ? Get("domain") : "";
                result.Secrets.Add(password);
                result.Credential = string.IsNullOrEmpty(domain) ? new NetworkCredential(user, password) : new NetworkCredential(user, password, domain);
                result.CredentialSchemes = auth.Type == "ntlm" ? ["NTLM", "Negotiate"] : ["Digest"];
                result.Description = auth.Type == "ntlm" ? $"NTLM ({(domain.Length > 0 ? domain + "\\" : "")}{user})" : $"Digest ({user})";
                break;
            }

            case "oauth2":
            {
                // With URLs to fetch a token from, one is fetched (or the one fetched earlier taken) when the request is sent.
                if (OAuth2Settings.Read(auth, value => variables.Resolve(value, unresolved), out var notFetched) is { } settings)
                {
                    result.OAuth2 = settings;
                    result.Secrets.AddRange(settings.Secrets);
                    result.Description = $"OAuth 2.0, {settings.GrantName}";
                    break;
                }

                var token = Get("accessToken");
                result.Secrets.Add(token);
                if (token.Length == 0)
                {
                    result.Warnings.Add($"OAuth 2.0: there is no access token, and none is fetched: {notFetched}. The request was sent without one.");
                    break;
                }

                var prefix = OAuth2Settings.HeaderPrefixOf(auth, value => variables.Resolve(value, unresolved));
                if (string.Equals(auth.Get("addTokenTo"), "queryParams", StringComparison.OrdinalIgnoreCase))
                {
                    result.QueryParameters.Add(new("access_token", token));
                }
                else
                {
                    result.Headers.Add(new("Authorization", string.IsNullOrEmpty(prefix) ? token : $"{prefix} {token}"));
                }

                result.Description = "OAuth 2.0 access token";
                break;
            }

            default:
                result.Warnings.Add($"Auth type '{AuthSpec.DisplayName(auth.Type)}' is not supported yet; the request was sent without it.");
                result.Description = $"{AuthSpec.DisplayName(auth.Type)} (unsupported)";
                break;
        }

        return result;
    }

    /// <summary>The credentials of an <c>Authorization: Basic</c> header (RFC 7617, in UTF-8).</summary>
    internal static string BasicToken(string user, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
}
