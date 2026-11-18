using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Core.Auth;
using TermRest.Core.Certificates;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>OAuth 2.0 tokens fetched for requests (grants, client authentication, sign-in, caching, refresh) from a test authorization server.</summary>
public sealed class OAuth2Tests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static AuthSpec OAuth2(OAuthServer server, string grant, params (string Key, string Value)[] settings)
    {
        var auth = new AuthSpec { Type = "oauth2" };
        auth.Set("grant_type", grant);
        auth.Set("accessTokenUrl", server.Url("token"));
        auth.Set("clientId", OAuthServer.ClientId);
        auth.Set("clientSecret", OAuthServer.ClientSecret);
        foreach (var (key, value) in settings)
        {
            auth.Set(key, value);
        }

        return auth;
    }

    /// <summary>Adds extra parameters (<c>tokenRequestParams</c> …) as Postman writes them.</summary>
    private static void AddParameters(AuthSpec auth, string key, params (string Key, string Value, string SendAs, bool Enabled)[] parameters) =>
        auth.Params.Add(new AuthParam
        {
            Key = key,
            Type = "any",
            Value = new JsonArray([.. parameters.Select(p => (JsonNode)new JsonObject { ["key"] = p.Key, ["value"] = p.Value, ["enabled"] = p.Enabled, ["send_as"] = p.SendAs })]),
        });

    private static (ApiWorkspace Workspace, PostmanCollection Collection) NewWorkspace()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        return (workspace, workspace.CreateCollection("API"));
    }

    private static HttpRequestSpec Echo(OAuthServer server, AuthSpec auth) => new() { Url = server.Url("echo"), Auth = auth };

    private static string? Authorization(HttpExchange exchange)
    {
        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        return JsonNode.Parse(exchange.Response!.Body)!["authorization"].GetString();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task A_client_credentials_token_is_fetched_once_with_basic_auth_and_kept_out_of_the_history()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };
        var spec = Echo(server, OAuth2(server, "client_credentials", ("scope", "orders.read orders.write")));

        var first = await runner.SendAsync(collection, null, spec, _ct);
        var second = await runner.SendAsync(collection, null, spec, _ct);

        var token = Assert.Single(server.AccessTokens);
        Assert.Equal($"Bearer {token}", Authorization(first));
        Assert.Equal($"Bearer {token}", Authorization(second));
        var request = Assert.Single(server.TokenRequests);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OAuthServer.ClientId}:{OAuthServer.ClientSecret}")), request.Headers["authorization"]);
        Assert.Equal("application/json", request.Headers["accept"]);
        Assert.Equal(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "orders.read orders.write" }, request.Form);
        Assert.StartsWith($"OAuth 2.0, client credentials: new token from {server.Url("token")} (200 in ", first.Request.AuthDescription, StringComparison.Ordinal);
        Assert.StartsWith($"OAuth 2.0, client credentials: token from {server.Url("token")}, fetched ", second.Request.AuthDescription, StringComparison.Ordinal);
        Assert.EndsWith(", expires in 60 min", second.Request.AuthDescription, StringComparison.Ordinal);

        // Token requests are not recorded; the requests that are have the token and the client secret masked.
        var entries = runner.History!.Load();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal("Bearer ***", entry.SentHeaders.Single(h => h.Key == "Authorization").Value));
        var recorded = File.ReadAllText(runner.History.FilePath) + Encoding.UTF8.GetString(runner.History.ReadBody(entries[0])!);
        Assert.DoesNotContain(token, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthServer.ClientSecret, recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_password_grant_can_send_the_client_secret_in_the_form()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.LocalVariables["password"] = OAuthServer.Password;
        var auth = OAuth2(server, "password_credentials", ("username", OAuthServer.Username), ("password", "{{password}}"), ("client_authentication", "body"));

        var exchange = await runner.SendAsync(collection, null, Echo(server, auth), _ct);

        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", Authorization(exchange));
        var request = Assert.Single(server.TokenRequests);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = OAuthServer.Username,
            ["password"] = OAuthServer.Password,
            ["client_id"] = OAuthServer.ClientId,
            ["client_secret"] = OAuthServer.ClientSecret,
        }, request.Form);
        Assert.Contains(OAuthServer.Password, exchange.Request.SecretValues);
        Assert.Contains(OAuthServer.ClientSecret, exchange.Request.SecretValues);
    }

    [Fact]
    public async Task The_token_goes_in_the_query_or_after_the_header_prefix_as_the_auth_says()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);

        var inQuery = await runner.SendAsync(collection, null,
            new HttpRequestSpec { Url = server.Url("echo?access_token=old&page=2"), Auth = OAuth2(server, "client_credentials", ("addTokenTo", "queryParams")) }, _ct);

        var token = Assert.Single(server.AccessTokens);
        Assert.Equal("", Authorization(inQuery));
        Assert.Equal($"?page=2&access_token={token}", JsonNode.Parse(inQuery.Response!.Body)!["query"].GetString());
        Assert.Equal("The auth helper replaced the 'access_token' query parameter.", Assert.Single(inQuery.Request.Warnings));
        Assert.Equal(server.Url("echo?page=2&access_token=***"), inQuery.Request.Redact(inQuery.Request.Url));

        // Where the token goes is not part of what it is fetched with: the same token goes after another prefix
        // ("Token ", which Postman writes with its space), in place of the request's own Authorization header.
        var inHeader = await runner.SendAsync(collection, null, new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Headers = [new() { Key = "Authorization", Value = "Basic abc" }],
            Auth = OAuth2(server, "client_credentials", ("headerPrefix", "Token ")),
        }, _ct);

        Assert.Equal($"Token {token}", Authorization(inHeader));
        Assert.Equal("The auth helper replaced the 'Authorization' header.", Assert.Single(inHeader.Request.Warnings));
        Assert.Single(server.TokenRequests);
    }

    [Fact]
    public async Task Extra_token_request_parameters_go_in_the_form_a_header_or_the_url_and_replace_those_of_the_same_name()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        runner.LocalVariables["audience"] = "https://api.example.com";
        var auth = OAuth2(server, "client_credentials", ("scope", "read"));
        AddParameters(auth, "tokenRequestParams",
            ("audience", "{{audience}}", "request_body", true),
            ("scope", "write", "request_body", true),
            ("X-Tenant", "contoso", "request_header", true),
            ("api-version", "2", "request_url", true),
            ("ignored", "x", "request_body", false));

        var exchange = await runner.SendAsync(collection, null, Echo(server, auth), _ct);

        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", Authorization(exchange));
        var request = Assert.Single(server.TokenRequests);
        Assert.Equal(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["audience"] = "https://api.example.com", ["scope"] = "write" }, request.Form);
        Assert.Equal(("contoso", "?api-version=2"), (request.Headers["x-tenant"], request.Query));
    }

    [Theory]
    [InlineData("S256", false)]
    [InlineData("plain", true)]
    public async Task Authorization_code_with_pkce_signs_in_with_the_browser_and_receives_the_code_on_a_loopback_redirect(string method, bool registeredRedirect)
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        var browser = new TestBrowser();
        using var runner = new RequestRunner(workspace) { OAuth2Interaction = browser };
        var redirect = registeredRedirect ? $"http://localhost:{FreePort()}/callback" : "";

        // A public client: no secret, as apps that sign users in with PKCE are.
        var auth = OAuth2(server, "authorization_code_with_pkce", ("authUrl", server.Url("authorize")), ("clientSecret", ""), ("challengeAlgorithm", method),
            ("redirect_uri", redirect), ("scope", "openid profile"));

        var exchange = await runner.SendAsync(collection, null, Echo(server, auth), _ct);

        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", Authorization(exchange));
        var authorization = OAuth2Response.ParseForm(Assert.Single(browser.Opened).Query.TrimStart('?'));
        var redirectUri = authorization["redirect_uri"];
        if (registeredRedirect)
        {
            Assert.Equal(redirect, redirectUri);
        }
        else
        {
            Assert.Matches(@"^http://127\.0\.0\.1:\d+/$", redirectUri);
        }

        Assert.Equal(("code", OAuthServer.ClientId, "openid profile", method), (authorization["response_type"], authorization["client_id"], authorization["scope"], authorization["code_challenge_method"]));
        Assert.True(authorization["state"].Length >= 20);
        var request = Assert.Single(server.TokenRequests);
        Assert.Equal(("authorization_code", redirectUri, OAuthServer.ClientId), (request.Form["grant_type"], request.Form["redirect_uri"], request.Form["client_id"]));
        Assert.True(request.Form["code_verifier"].Length >= 43);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.Contains("Signed in", browser.Page, StringComparison.Ordinal);
        Assert.StartsWith("OAuth 2.0, authorization code with PKCE: new token from", exchange.Request.AuthDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_uri_elsewhere_takes_the_url_the_browser_ended_on_and_checks_its_state()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace) { OAuth2Interaction = new TestBrowser() };
        AuthSpec SignIn(string scope) =>
            OAuth2(server, "authorization_code", ("authUrl", server.Url("authorize")), ("redirect_uri", "https://oauth.pstmn.io/v1/callback"), ("state", "my-state"), ("scope", scope));

        var exchange = await runner.SendAsync(collection, null, Echo(server, SignIn("a")), _ct);

        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", Authorization(exchange));
        var request = Assert.Single(server.TokenRequests);
        Assert.Equal(("authorization_code", "https://oauth.pstmn.io/v1/callback"), (request.Form["grant_type"], request.Form["redirect_uri"]));
        Assert.StartsWith("Basic ", request.Headers["authorization"], StringComparison.Ordinal);

        // A response to another sign-in (its state differs) is refused, as is the server's refusal to sign the user in.
        runner.OAuth2Interaction = new TestBrowser { Tamper = url => url.Replace("state=my-state", "state=forged", StringComparison.Ordinal) };
        var forged = await runner.SendAsync(collection, null, Echo(server, SignIn("b")), _ct);
        Assert.Null(forged.Response);
        Assert.Equal("OAuth 2.0: the authorization response is not the one to this sign-in (its state differs), so no token was asked for.", forged.ErrorMessage);

        runner.OAuth2Interaction = new TestBrowser();
        var denied = SignIn("c");
        AddParameters(denied, "authRequestParams", ("login_hint", "nobody", "request_url", true));
        Assert.Equal("OAuth 2.0: the authorization server did not sign you in: access_denied: The user said no.",
            (await runner.SendAsync(collection, null, Echo(server, denied), _ct)).ErrorMessage);
        Assert.Single(server.TokenRequests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/callback?code=abc&state=s1")]
    [InlineData("?code=abc&state=s1")]
    [InlineData("code=abc&state=s1")]
    [InlineData("https://app.example.com/callback#state=s1&code=abc")]
    [InlineData("  https://oauth.pstmn.io/v1/callback?state=s1&code=abc&session_state=x\n")]
    public void An_authorization_response_is_read_from_the_url_or_the_query_pasted(string pasted) =>
        Assert.Equal("abc", OAuth2Tokens.ReadAuthorizationResponse(pasted, "s1"));

    [Fact]
    public async Task A_sign_in_times_out_or_is_cancelled_and_needs_a_way_to_sign_in()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace) { OAuth2Interaction = new TestBrowser { Wait = true } };
        runner.OAuth2Tokens.SignInTimeout = TimeSpan.FromMilliseconds(300);
        var spec = Echo(server, OAuth2(server, "authorization_code_with_pkce", ("authUrl", server.Url("authorize"))));

        var timedOut = await runner.SendAsync(collection, null, spec, _ct);

        Assert.Matches(@"^OAuth 2\.0: the browser did not return to http://127\.0\.0\.1:\d+/ within 0\.3 seconds, so no token was asked for\.$", timedOut.ErrorMessage);

        runner.OAuth2Tokens.SignInTimeout = TimeSpan.FromMinutes(5);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
        Assert.True((await runner.SendAsync(collection, null, spec, cancel.Token)).Cancelled);

        runner.OAuth2Interaction = null;
        Assert.Equal("OAuth 2.0: the authorization code with PKCE grant has you sign in with a browser, which TermRest can't do here.",
            (await runner.SendAsync(collection, null, spec, _ct)).ErrorMessage);
        Assert.Empty(server.TokenRequests);
    }

    [Fact]
    public async Task An_expiring_token_is_refreshed_and_when_its_refresh_is_refused_a_new_one_is_fetched()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        var time = new ManualTime(DateTimeOffset.UtcNow);
        runner.OAuth2Tokens.Time = time;
        var auth = OAuth2(server, "password_credentials", ("username", OAuthServer.Username), ("password", OAuthServer.Password), ("scope", "profile"),
            ("refreshTokenUrl", server.Url("token?refreshing=1")));
        AddParameters(auth, "refreshRequestParams", ("X-Refresh", "yes", "request_header", true));
        var spec = Echo(server, auth);

        await runner.SendAsync(collection, null, spec, _ct);
        time.Now += TimeSpan.FromMinutes(59.5); // within a minute of expiring: refreshed before it goes
        var refreshed = await runner.SendAsync(collection, null, spec, _ct);

        Assert.Equal(2, server.AccessTokens.Count);
        Assert.Equal($"Bearer {server.AccessTokens.Last()}", Authorization(refreshed));
        var refresh = server.TokenRequests.Last();
        Assert.Equal(("refresh_token", "profile", "?refreshing=1", "yes"), (refresh.Form["grant_type"], refresh.Form["scope"], refresh.Query, refresh.Headers["x-refresh"]));
        Assert.StartsWith($"OAuth 2.0, password: token refreshed at {server.Url("token")} (200 in ", refreshed.Request.AuthDescription, StringComparison.Ordinal);

        // Revoked: a new token is asked for, and a warning says why.
        server.RevokeRefreshTokens();
        time.Now += TimeSpan.FromHours(2);
        var fetched = await runner.SendAsync(collection, null, spec, _ct);

        Assert.Equal(3, server.AccessTokens.Count);
        Assert.Equal($"Bearer {server.AccessTokens.Last()}", Authorization(fetched));
        Assert.Equal("password", server.TokenRequests.Last().Form["grant_type"]);
        Assert.Equal($"OAuth 2.0: the token request to {server.Url("token")} failed: 400 Bad Request — invalid_grant: The refresh token expired. A new token was asked for instead.",
            Assert.Single(fetched.Request.Warnings));
    }

    [Theory]
    [InlineData("RSA", "", "RS256")]
    [InlineData("RSA", "ps256", "PS256")]
    [InlineData("EC", "", "ES256")]
    public async Task A_client_assertion_signed_with_a_registered_certificate_authenticates_the_client(string keyType, string algorithm, string signedWith)
    {
        await using var server = await OAuthServer.StartAsync();
        var directory = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(directory);
        var collection = workspace.CreateCollection("API");
        using var certificate = CreateSigner(keyType, directory);
        server.AssertionCertificate = certificate;

        // Registered without URL patterns: it signs, and goes to no server.
        workspace.Certificates.Entries.Add(new CertificateEntry { Name = "Signer", Cert = new FileSource { Src = "signer.pem" }, Key = new FileSource { Src = "signer.key" } });
        using var runner = new RequestRunner(workspace);
        var auth = OAuth2(server, "client_credentials", ("client_authentication", "private_key_jwt"), ("x-clientAssertionCertificate", "signer"),
            ("x-clientAssertionAlgorithm", algorithm), ("x-clientAssertionKeyId", "key-1"), ("x-clientAssertionChain", "true"));

        var exchange = await runner.SendAsync(collection, null, Echo(server, auth), _ct);

        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", Authorization(exchange));
        var request = Assert.Single(server.TokenRequests);
        Assert.Equal(["client_assertion", "client_assertion_type", "client_id", "grant_type"], request.Form.Keys.Order(StringComparer.Ordinal));
        Assert.False(request.Headers.ContainsKey("authorization"));
        var (header, claims) = Assert.Single(server.Assertions);
        Assert.Equal((signedWith, "JWT", "key-1"), (header["alg"].GetString(), header["typ"].GetString(), header["kid"].GetString()));
        Assert.Equal(Convert.ToBase64String(certificate.RawData), Assert.Single(header["x5c"]!.AsArray())!.GetString());
        Assert.Equal((OAuthServer.ClientId, OAuthServer.ClientId, server.Url("token")), (claims["iss"].GetString(), claims["sub"].GetString(), claims["aud"].GetString()));
        Assert.Equal(300, claims["exp"]!.GetValue<long>() - claims["iat"]!.GetValue<long>());
        Assert.True(Guid.TryParse(claims["jti"].GetString(), out _));
    }

    [Fact]
    public async Task A_token_that_cannot_be_had_stops_the_request()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace) { History = new HistoryStore(Path.Combine(TestFiles.NewTempDirectory(), "history"), 10) };

        var refused = await runner.SendAsync(collection, null, Echo(server, OAuth2(server, "client_credentials", ("clientSecret", "wrong-secret"))), _ct);
        var redirected = await runner.SendAsync(collection, null, Echo(server, OAuth2(server, "client_credentials", ("accessTokenUrl", server.Url("moved")))), _ct);
        var unsigned = await runner.SendAsync(collection, null,
            Echo(server, OAuth2(server, "client_credentials", ("client_authentication", "private_key_jwt"), ("x-clientAssertionCertificate", "nope"))), _ct);

        Assert.Equal($"OAuth 2.0: the token request to {server.Url("token")} failed: 401 Unauthorized — invalid_client: Client authentication failed.", refused.ErrorMessage);
        Assert.Equal($"OAuth 2.0: the token endpoint {server.Url("moved")} answered 307 Temporary Redirect (to {server.Url("token")}): token requests, which carry the client's credentials, are not redirected.",
            redirected.ErrorMessage);
        Assert.Equal("OAuth 2.0: the workspace has no client certificate named 'nope' to sign the client assertion with (Tools › Client certificates…).", unsigned.ErrorMessage);
        Assert.All((HttpExchange[])[refused, redirected, unsigned], exchange => Assert.Null(exchange.Response));
        Assert.Single(server.TokenRequests); // the refused one: the redirect was not followed, and the assertion never signed
        Assert.Empty(runner.History!.Load());
    }

    [Fact]
    public async Task Without_urls_to_fetch_a_token_from_the_token_set_in_the_auth_is_sent()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);

        // As Postman writes the helper with a token pasted in it: an authorization code grant without its URLs.
        var pasted = new AuthSpec { Type = "oauth2" };
        pasted.Set("grant_type", "authorization_code");
        pasted.Set("accessTokenUrl", "");
        pasted.Set("authUrl", "");
        pasted.Set("accessToken", "pasted-token-value");
        pasted.Set("headerPrefix", "Bearer ");
        var exchange = await runner.SendAsync(collection, null, Echo(server, pasted), _ct);

        Assert.Equal("Bearer pasted-token-value", Authorization(exchange));
        Assert.Equal("OAuth 2.0 access token", exchange.Request.AuthDescription);
        Assert.Empty(exchange.Request.Warnings);

        // The implicit grant is not fetched (the OAuth 2.0 Security Best Current Practice retires it).
        var unsent = await runner.SendAsync(collection, null, Echo(server, OAuth2(server, "implicit", ("authUrl", server.Url("authorize")))), _ct);
        Assert.Equal("", Authorization(unsent));
        Assert.StartsWith("OAuth 2.0: there is no access token, and none is fetched: the implicit grant is not supported", Assert.Single(unsent.Request.Warnings), StringComparison.Ordinal);
        Assert.Empty(server.TokenRequests);
    }

    [Fact]
    public async Task A_curl_command_gets_the_token_fetched_earlier_and_a_new_one_can_be_asked_for()
    {
        await using var server = await OAuthServer.StartAsync();
        var (workspace, collection) = NewWorkspace();
        using var runner = new RequestRunner(workspace);
        var spec = Echo(server, OAuth2(server, "client_credentials"));

        var before = runner.Prepare(collection, null, spec);
        Assert.False(runner.AddCachedToken(before));
        Assert.DoesNotContain("Authorization", CurlExporter.Export(before), StringComparison.Ordinal);

        await runner.SendAsync(collection, null, spec, _ct);
        var after = runner.Prepare(collection, null, spec);
        Assert.True(runner.AddCachedToken(after));
        Assert.Contains($"Authorization: Bearer {Assert.Single(server.AccessTokens)}", CurlExporter.Export(after), StringComparison.Ordinal);

        // Asked for (the Auth tab's Get new token), or after the one fetched is forgotten (its Clear).
        var fresh = await runner.GetTokenAsync(runner.Prepare(collection, null, spec), fetchNew: true, _ct);
        Assert.Equal((OAuth2TokenOrigin.Fetched, server.AccessTokens.Last()), (fresh.Origin, fresh.Token.AccessToken));
        Assert.True(runner.OAuth2Tokens.Forget(after.OAuth2!));
        Assert.Null(runner.OAuth2Tokens.Find(after.OAuth2!));
        await runner.SendAsync(collection, null, spec, _ct);
        Assert.Equal(3, server.AccessTokens.Count);
    }

    private static X509Certificate2 CreateSigner(string keyType, string directory)
    {
        using AsymmetricAlgorithm key = keyType == "EC" ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : RSA.Create(2048);
        var request = key is ECDsa ec
            ? new CertificateRequest("CN=TermRest assertion signer", ec, HashAlgorithmName.SHA256)
            : new CertificateRequest("CN=TermRest assertion signer", (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        File.WriteAllText(Path.Combine(directory, "signer.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "signer.key"), key.ExportPkcs8PrivateKeyPem());
        return X509CertificateLoader.LoadCertificate(certificate.RawData);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A browser for sign-ins: it opens the authorization request and follows the server's redirect, as a user who signs in.</summary>
    private sealed class TestBrowser : IOAuth2Interaction
    {
        public List<Uri> Opened { get; } = [];

        /// <summary>The page the loopback redirect URI answered with.</summary>
        public string? Page { get; private set; }

        /// <summary>Changes the URL the server redirects to (forges a response).</summary>
        public Func<string, string>? Tamper { get; init; }

        /// <summary>Never signs in: waits for the redirect (which then times out) or the cancellation.</summary>
        public bool Wait { get; init; }

        public async Task<string?> SignInAsync(OAuth2SignIn signIn, CancellationToken cancellationToken)
        {
            Opened.Add(signIn.AuthorizationUrl);
            if (Wait)
            {
                return signIn.Redirect is { } redirect ? await redirect.WaitAsync(cancellationToken) : null;
            }

            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
            using var authorization = await client.GetAsync(signIn.AuthorizationUrl, cancellationToken);
            var location = authorization.Headers.Location?.ToString() ?? throw new InvalidOperationException(await authorization.Content.ReadAsStringAsync(cancellationToken));
            location = Tamper?.Invoke(location) ?? location;
            if (signIn.Redirect is null)
            {
                return location; // the user pastes the URL the browser ended on
            }

            using var callback = await client.GetAsync(location, cancellationToken);
            Page = await callback.Content.ReadAsStringAsync(cancellationToken);
            return await signIn.Redirect;
        }
    }
}
