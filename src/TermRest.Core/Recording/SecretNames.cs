namespace TermRest.Core.Recording;

/// <summary>Where a name was found; some short names only mean a credential in a URL.</summary>
public enum NameKind
{
    Header,
    QueryParameter,
    Field,
}

/// <summary>
/// Recognizes names of headers, URL parameters, body fields and variables that hold credentials: Authorization,
/// Cookie, X-API-Key, api_key, password, client_secret, access_token, X-Amz-Signature … Names are compared without
/// case, separators and trailing digits, so <c>X-API-Key</c>, <c>api_key</c> and <c>apiKey</c> are the same name.
/// </summary>
public static class SecretNames
{
    // Normalized names (lowercase ASCII letters and digits) that contain one of these hold a secret …
    private static readonly string[] Containing = ["password", "passwd", "passphrase", "secret", "credential", "privatekey", "connectionstring"];

    // … or end with one of these ("token_type" and "token_url" don't; "access_token" and "X-CSRF-Token" do).
    private static readonly string[] Ending =
    [
        "token", "authorization", "cookie", "cookies", "apikey", "accesskey", "secretkey", "subscriptionkey", "functionskey",
        "masterkey", "sessionkey", "signingkey", "accountkey", "sharedkey", "authkey", "sessionid", "sessid", "signature",
        "assertion", "codeverifier", "pwd",
    ];

    // Short names that are credentials only in URLs: Google API keys (?key=), Azure SAS signatures (?sig=) and Firebase
    // ID tokens or database secrets (?auth=).
    private static readonly string[] QueryOnly = ["key", "sig", "auth"];

    // Names that look like credentials but never hold one (CORS says whether credentials are allowed: "true").
    private static readonly string[] NotSecret = ["accesscontrolallowcredentials"];

    /// <summary>Lowercase ASCII letters and digits of <paramref name="name"/>, without trailing digits (X-Hub-Signature-256 → xhubsignature).</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        while (length > 0 && char.IsAsciiDigit(buffer[length - 1]))
        {
            length--;
        }

        return new string(buffer[..length]);
    }

    /// <summary>The normalized form of extra names to redact (from the workspace settings).</summary>
    public static HashSet<string> NormalizeAll(IEnumerable<string>? names) =>
        (names ?? []).Select(Normalize).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether values named <paramref name="name"/> are credentials. <paramref name="extraNames"/> (normalized) are
    /// matched like the built-in endings: "tenantkey" matches X-Tenant-Key and tenant_key. They win over the names known
    /// not to be credentials (Access-Control-Allow-Credentials), which only change the built-in rules.
    /// </summary>
    public static bool IsSecret(string? name, NameKind kind, IReadOnlySet<string>? extraNames = null)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (extraNames is not null && extraNames.Any(extra => normalized.EndsWith(extra, StringComparison.Ordinal)))
        {
            return true;
        }

        if (NotSecret.Contains(normalized, StringComparer.Ordinal))
        {
            return false;
        }

        if (kind == NameKind.QueryParameter && QueryOnly.Contains(normalized, StringComparer.Ordinal))
        {
            return true;
        }

        return Containing.Any(part => normalized.Contains(part, StringComparison.Ordinal))
               || Ending.Any(end => normalized.EndsWith(end, StringComparison.Ordinal));
    }
}
