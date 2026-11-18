using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TermRest.Core.Certificates;

namespace TermRest.Core.Auth;

/// <summary>
/// Client assertions (RFC 7523; <c>private_key_jwt</c> of OpenID Connect): a short-lived JWT the client signs with the
/// private key of its certificate, which the token endpoint checks with the certificate (or public key) registered for
/// the client. Okta, Auth0, Keycloak, Ping, Microsoft Entra ID and other servers take them.
/// </summary>
public static class ClientAssertion
{
    /// <summary>The <c>client_assertion_type</c> of a JWT assertion.</summary>
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>How long an assertion is valid: long enough for its token request, too short to be replayed later.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The JWS algorithms assertions are signed with: RSA (PKCS #1 v1.5 and PSS) and ECDSA.</summary>
    public static readonly string[] Algorithms = ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

    /// <summary>
    /// A signed assertion for <paramref name="clientId"/> (its issuer and subject) to <paramref name="audience"/> (the token
    /// endpoint), valid from <paramref name="now"/> for <see cref="Lifetime"/>. Its header names the certificate by its
    /// SHA-256 and SHA-1 thumbprints (<c>x5t#S256</c>, <c>x5t</c>), and <paramref name="keyId"/> (<c>kid</c>) and the
    /// certificate chain (<c>x5c</c>) when asked for. Without an <paramref name="algorithm"/>: RS256 for an RSA key, and for
    /// an EC key the one of its curve (ES256 for P-256 …). Throws <see cref="OAuth2Exception"/> when it cannot sign.
    /// </summary>
    /// <param name="name">The certificate's name in the registry (for messages).</param>
    public static string Create(LoadedCertificate certificate, string name, string clientId, string audience, string? algorithm, string? keyId, bool withChain,
        DateTimeOffset now)
    {
        var leaf = certificate.Certificate;
        try
        {
            using var rsa = leaf.GetRSAPrivateKey();
            using var ecdsa = rsa is null ? leaf.GetECDsaPrivateKey() : null;
            if (rsa is null && ecdsa is null)
            {
                throw new OAuth2Exception($"OAuth 2.0: certificate '{name}' has no RSA or EC private key to sign the client assertion with.");
            }

            var curve = ecdsa?.KeySize switch
            {
                256 => "ES256",
                384 => "ES384",
                521 => "ES512",
                _ => null,
            };
            algorithm = string.IsNullOrWhiteSpace(algorithm) ? rsa is not null ? "RS256" : curve : algorithm.Trim().ToUpperInvariant();
            if (algorithm is null || !Algorithms.Contains(algorithm, StringComparer.Ordinal))
            {
                throw new OAuth2Exception(algorithm is null
                    ? $"OAuth 2.0: the EC key of certificate '{name}' is not on a curve JWS signs with (P-256, P-384 or P-521)."
                    : $"OAuth 2.0: '{algorithm}' is not an algorithm TermRest signs client assertions with ({string.Join(", ", Algorithms)}).");
            }

            if (algorithm[0] == 'E' ? ecdsa is null || curve != algorithm : rsa is null)
            {
                throw new OAuth2Exception(rsa is not null
                    ? $"OAuth 2.0: certificate '{name}' has an RSA key, which signs with RS256, RS384, RS512, PS256, PS384 or PS512, not {algorithm}."
                    : $"OAuth 2.0: certificate '{name}' has an EC key{(curve is null ? "" : $", which signs with {curve}")}, not with {algorithm}.");
            }

            var hash = algorithm[2..] switch
            {
                "256" => HashAlgorithmName.SHA256,
                "384" => HashAlgorithmName.SHA384,
                _ => HashAlgorithmName.SHA512,
            };
            var header = Json(writer =>
            {
                writer.WriteString("alg", algorithm);
                writer.WriteString("typ", "JWT");
                writer.WriteString("x5t#S256", Base64Url.EncodeToString(leaf.GetCertHash(HashAlgorithmName.SHA256)));
                writer.WriteString("x5t", Base64Url.EncodeToString(leaf.GetCertHash()));
                if (!string.IsNullOrWhiteSpace(keyId))
                {
                    writer.WriteString("kid", keyId.Trim());
                }

                if (withChain)
                {
                    writer.WriteStartArray("x5c");
                    writer.WriteStringValue(Convert.ToBase64String(leaf.RawData));
                    foreach (var issuer in certificate.Chain)
                    {
                        if (issuer.Thumbprint != leaf.Thumbprint)
                        {
                            writer.WriteStringValue(Convert.ToBase64String(issuer.RawData));
                        }
                    }

                    writer.WriteEndArray();
                }
            });
            var seconds = now.ToUnixTimeSeconds();
            var payload = Json(writer =>
            {
                writer.WriteString("aud", audience);
                writer.WriteString("iss", clientId);
                writer.WriteString("sub", clientId);
                writer.WriteString("jti", Guid.NewGuid().ToString());
                writer.WriteNumber("iat", seconds);
                writer.WriteNumber("nbf", seconds);
                writer.WriteNumber("exp", seconds + (long)Lifetime.TotalSeconds);
            });
            var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";
            var data = System.Text.Encoding.ASCII.GetBytes(signingInput);

            // JWS wants an ECDSA signature as r and s side by side (IEEE P1363), as SignData gives it.
            var signature = rsa is not null
                ? rsa.SignData(data, hash, algorithm[0] == 'P' ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1)
                : ecdsa!.SignData(data, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
        }
        catch (CryptographicException ex)
        {
            // A key in a store that asks for a PIN, or does not allow signing, among others.
            throw new OAuth2Exception($"OAuth 2.0: certificate '{name}' could not sign the client assertion: {ex.Message}");
        }
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();

        // As other JWT libraries write it: a '+' of a base64 certificate, a '&' of a URL as they are, not as \u002B.
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
