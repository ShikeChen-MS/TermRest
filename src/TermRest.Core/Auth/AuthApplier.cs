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

    public string Description { get; set; } = "No auth";

    public List<string> Warnings { get; } = [];

    /// <summary>Resolved credentials (passwords, tokens, key values) that must not end up in history or logs.</summary>
    public List<string> Secrets { get; } = [];
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
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                result.Headers.Add(new("Authorization", $"Basic {token}"));
                result.Description = $"Basic ({user})";
                result.Secrets.Add(password);
                result.Secrets.Add(token);
                break;
            }

            case "bearer":
            {
                var token = Get("token");
                result.Secrets.Add(token);
                result.Headers.Add(new("Authorization", $"Bearer {token}"));
                result.Description = "Bearer token";
                if (token.Length == 0)
                {
                    result.Warnings.Add("Bearer token is empty.");
                }

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
                result.Description = auth.Type == "ntlm" ? $"NTLM ({(domain.Length > 0 ? domain + "\\" : "")}{user})" : $"Digest ({user})";
                break;
            }

            case "oauth2":
            {
                var token = Get("accessToken");
                result.Secrets.Add(token);
                if (token.Length == 0)
                {
                    result.Warnings.Add("OAuth 2.0: no access token is set. Paste a token in the Auth tab (token acquisition is not implemented yet).");
                    break;
                }

                var prefix = auth.Get("headerPrefix") is { } p ? variables.Resolve(p, unresolved) : "Bearer";
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
}
