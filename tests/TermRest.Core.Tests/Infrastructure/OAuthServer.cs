using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermRest.Core.Json;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>
/// An OAuth 2.0 authorization server for tests. <c>/authorize</c> issues authorization codes (redirecting the browser to
/// the redirect URI); <c>/token</c> issues tokens for the client credentials, password, authorization code (with PKCE)
/// and refresh token grants, checking how the client authenticates: Basic, its secret in the form, or a client assertion
/// that <see cref="AssertionCertificate"/>'s key signed. <c>/echo</c> reflects a request's Authorization header and query,
/// as the API the tokens are for.
/// </summary>
public sealed class OAuthServer : IAsyncDisposable
{
    public const string ClientId = "termrest-client";
    public const string ClientSecret = "client-s3cret-value";
    public const string Username = "ada";
    public const string Password = "ada-pa55word";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _refreshTokens = new(StringComparer.Ordinal);
    private int _issued;

    private OAuthServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    /// <summary>The token requests it received.</summary>
    public ConcurrentQueue<TokenRequest> TokenRequests { get; } = new();

    /// <summary>The client assertions it verified: their header and claims.</summary>
    public ConcurrentQueue<(JsonObject Header, JsonObject Claims)> Assertions { get; } = new();

    /// <summary>The certificate whose key signs client assertions; null: they are refused.</summary>
    public X509Certificate2? AssertionCertificate { get; set; }

    /// <summary>The <c>expires_in</c> of the tokens it issues; null: none.</summary>
    public int? ExpiresIn { get; set; } = 3600;

    public bool IssueRefreshTokens { get; set; } = true;

    /// <summary>The access tokens it issued, in order.</summary>
    public ConcurrentQueue<string> AccessTokens { get; } = new();

    public string Url(string path) => new Uri(BaseUri, path).ToString();

    /// <summary>Forgets the refresh tokens it issued, as a server does when they expire or are revoked.</summary>
    public void RevokeRefreshTokens() => _refreshTokens.Clear();

    public static async Task<OAuthServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        OAuthServer? server = null;
        app.MapGet("/authorize", (HttpContext ctx) => server!.Authorize(ctx));

        // Typed: a lambda that takes the HttpContext and returns a task would be a RequestDelegate, its result never written.
        app.MapPost("/token", (Func<HttpContext, Task<IResult>>)(ctx => server!.TokenAsync(ctx)));
        app.MapPost("/moved", (HttpContext ctx) => Results.Redirect(server!.Url("token"), preserveMethod: true));
        app.MapGet("/echo", (HttpContext ctx) => Results.Json(new JsonObject
        {
            ["authorization"] = ctx.Request.Headers.Authorization.ToString(),
            ["query"] = ctx.Request.QueryString.Value,
        }));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new OAuthServer(app, new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/"));
        return server;
    }

    private IResult Authorize(HttpContext ctx)
    {
        var query = ctx.Request.Query;
        var redirect = query["redirect_uri"].ToString();
        if (query["client_id"] != ClientId || redirect.Length == 0)
        {
            return Results.BadRequest("unknown client or no redirect_uri");
        }

        string Back(string parameters) => $"{redirect}{(redirect.Contains('?', StringComparison.Ordinal) ? "&" : "?")}{parameters}&state={Uri.EscapeDataString(query["state"].ToString())}";
        if (query["response_type"] != "code")
        {
            return Results.Redirect(Back("error=unsupported_response_type"));
        }

        if (query["login_hint"] == "nobody")
        {
            return Results.Redirect(Back("error=access_denied&error_description=" + Uri.EscapeDataString("The user said no.")));
        }

        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = new IssuedCode(redirect, query["code_challenge"].ToString(), query["code_challenge_method"].ToString(), query["scope"].ToString());
        return Results.Redirect(Back("code=" + code));
    }

    private async Task<IResult> TokenAsync(HttpContext ctx)
    {
        var form = (await ctx.Request.ReadFormAsync()).ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal);
        var headers = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal);
        TokenRequests.Enqueue(new TokenRequest(form, headers, ctx.Request.QueryString.Value ?? ""));

        // How the client authenticated: confidential (Basic, secret, assertion) or public (its ID alone).
        var confidential = false;
        string? client;
        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..])).Split(':', 2);
            if (pair is not [ClientId, ClientSecret])
            {
                return Error(401, "invalid_client", "Client authentication failed.");
            }

            (client, confidential) = (pair[0], true);
        }
        else if (form.GetValueOrDefault("client_assertion_type") is { } assertionType)
        {
            if (assertionType != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer" || Verify(form.GetValueOrDefault("client_assertion")) is not { } subject)
            {
                return Error(401, "invalid_client", "The client assertion is not valid.");
            }

            (client, confidential) = (subject, true);
        }
        else if (form.GetValueOrDefault("client_secret") is { } secret)
        {
            if (form.GetValueOrDefault("client_id") != ClientId || secret != ClientSecret)
            {
                return Error(401, "invalid_client", "Client authentication failed.");
            }

            (client, confidential) = (ClientId, true);
        }
        else
        {
            client = form.GetValueOrDefault("client_id");
        }

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "client_credentials" when confidential:
                return Issue(form.GetValueOrDefault("scope"));
            case "client_credentials":
                return Error(401, "invalid_client", "The client did not authenticate.");
            case "password" when form.GetValueOrDefault("username") == Username && form.GetValueOrDefault("password") == Password:
                return Issue(form.GetValueOrDefault("scope"));
            case "password":
                return Error(400, "invalid_grant", "Wrong user name or password.");
            case "authorization_code":
            {
                if (client != ClientId || !_codes.TryRemove(form.GetValueOrDefault("code") ?? "", out var issued) || issued.RedirectUri != form.GetValueOrDefault("redirect_uri"))
                {
                    return Error(400, "invalid_grant", "Unknown code, or another redirect_uri.");
                }

                var verifier = form.GetValueOrDefault("code_verifier");
                if (issued.Challenge.Length > 0
                    ? verifier is null || issued.Challenge != (issued.Method == "plain" ? verifier : Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))))
                    : !confidential)
                {
                    return Error(400, "invalid_grant", "PKCE verification failed.");
                }

                return Issue(issued.Scope);
            }

            case "refresh_token" when _refreshTokens.TryRemove(form.GetValueOrDefault("refresh_token") ?? "", out _):
                return Issue(form.GetValueOrDefault("scope"));
            case "refresh_token":
                return Error(400, "invalid_grant", "The refresh token expired.");
            default:
                return Error(400, "unsupported_grant_type", null);
        }
    }

    /// <summary>A client assertion's subject when its signature and claims are good; null otherwise.</summary>
    private string? Verify(string? assertion)
    {
        if (AssertionCertificate is not { } certificate || assertion?.Split('.') is not [var header, var claims, var signature])
        {
            return null;
        }

        var head = (JsonObject)JsonNode.Parse(Base64Url.DecodeFromChars(header))!;
        var payload = (JsonObject)JsonNode.Parse(Base64Url.DecodeFromChars(claims))!;
        var algorithm = head["alg"].GetString() ?? "";
        var hash = algorithm.EndsWith("384", StringComparison.Ordinal) ? HashAlgorithmName.SHA384 : algorithm.EndsWith("512", StringComparison.Ordinal) ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
        var data = Encoding.ASCII.GetBytes($"{header}.{claims}");
        var bytes = Base64Url.DecodeFromChars(signature);
        using var rsa = certificate.GetRSAPublicKey();
        using var ecdsa = certificate.GetECDsaPublicKey();
        var valid = algorithm[0] switch
        {
            'R' => rsa?.VerifyData(data, bytes, hash, RSASignaturePadding.Pkcs1) == true,
            'P' => rsa?.VerifyData(data, bytes, hash, RSASignaturePadding.Pss) == true,
            'E' => ecdsa?.VerifyData(data, bytes, hash) == true,
            _ => false,
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!valid || payload["aud"].GetString() != Url("token") || payload["iss"].GetString() != payload["sub"].GetString()
            || payload["exp"]!.GetValue<long>() <= now || payload["nbf"]!.GetValue<long>() > now + 60
            || head["x5t#S256"].GetString() != Base64Url.EncodeToString(certificate.GetCertHash(HashAlgorithmName.SHA256)))
        {
            return null;
        }

        Assertions.Enqueue((head, payload));
        return payload["sub"].GetString();
    }

    private IResult Issue(string? scope)
    {
        var number = Interlocked.Increment(ref _issued);
        var token = new JsonObject { ["access_token"] = $"at-{number}-{Guid.NewGuid():N}", ["token_type"] = "Bearer" };
        AccessTokens.Enqueue(token["access_token"]!.GetValue<string>());
        if (ExpiresIn is { } seconds)
        {
            token["expires_in"] = seconds;
        }

        if (IssueRefreshTokens)
        {
            var refreshToken = $"rt-{number}-{Guid.NewGuid():N}";
            _refreshTokens[refreshToken] = true;
            token["refresh_token"] = refreshToken;
        }

        if (!string.IsNullOrEmpty(scope))
        {
            token["scope"] = scope;
        }

        return Results.Content(token.ToJsonString(), "application/json");
    }

    private static IResult Error(int status, string error, string? description) =>
        Results.Content(new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString(), "application/json", statusCode: status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>A token request as it came: its form, its headers (names in lower case) and its query.</summary>
    public sealed record TokenRequest(Dictionary<string, string> Form, Dictionary<string, string> Headers, string Query);

    private sealed record IssuedCode(string RedirectUri, string Challenge, string Method, string Scope);
}
