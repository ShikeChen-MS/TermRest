using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TermRest.Core.Http;

namespace TermRest.Core.Auth;

/// <summary>What OAuth 2.0 token requests go out with: who sends them, the request a token is for, and how the user signs in.</summary>
public sealed class OAuth2Context
{
    public required HttpExecutor Executor { get; init; }

    /// <summary>
    /// The request the token is for: token requests trust its CA bundle and go with the client certificate it selects for
    /// their URL (mutual TLS), and their warnings go to it.
    /// </summary>
    public required PreparedRequest Request { get; init; }

    /// <summary>The settings requests go with (SSL verification, proxy, timeout …): token requests go with them too.</summary>
    public required ExecutionDefaults Defaults { get; init; }

    public IOAuth2Interaction? Interaction { get; init; }
}

/// <summary>
/// Fetches OAuth 2.0 access tokens (client credentials, password, and authorization code with or without PKCE, which
/// has the user sign in with a browser), and keeps them in memory, never in a file, until they expire; then they are
/// refreshed with their refresh token if they came with one. The client authenticates with its secret (Basic or in the
/// form) or a client assertion its certificate signs (<see cref="ClientAssertion"/>). Token requests carry the client's
/// credentials: they are neither recorded (history, recordings) nor redirected.
/// </summary>
public sealed class OAuth2Tokens
{
    /// <summary>How long a token request may take when no timeout is set for requests.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>How long a sign-in may take until the browser returns to the loopback redirect URI.</summary>
    public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The token cached for <paramref name="settings"/>, fresh or not; null when none was fetched (or it was forgotten).</summary>
    public OAuth2Token? Find(OAuth2Settings settings)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(settings.CacheKey)?.Token;
        }
    }

    /// <summary>The token cached for <paramref name="settings"/> while it is fresh (see <see cref="OAuth2Token.IsFresh"/>); null otherwise.</summary>
    public OAuth2Token? Peek(OAuth2Settings settings) => Find(settings) is { } token && token.IsFresh(Time.GetUtcNow()) ? token : null;

    /// <summary>Forgets the token cached for <paramref name="settings"/>: the next request fetches a new one. False when there was none.</summary>
    public bool Forget(OAuth2Settings settings)
    {
        lock (_gate)
        {
            if (_entries.GetValueOrDefault(settings.CacheKey) is not { Token: not null } entry)
            {
                return false;
            }

            entry.Token = null;
            return true;
        }
    }

    /// <summary>
    /// The token for <paramref name="settings"/>: the cached one while it is fresh, else one refreshed with its refresh token
    /// or else a new one (<paramref name="fetchNew"/>: a new one in any case). Requests for the same settings wait for each
    /// other, so that they share the token. Throws <see cref="OAuth2Exception"/> when none can be had, and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task<OAuth2Result> GetAsync(OAuth2Settings settings, OAuth2Context context, bool fetchNew = false, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(settings.CacheKey, out var existing))
            {
                _entries[settings.CacheKey] = existing = new Entry();
            }

            entry = existing;
        }

        await entry.Turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = Find(settings);
            if (!fetchNew && cached is not null && cached.IsFresh(Time.GetUtcNow()))
            {
                return new OAuth2Result(cached, OAuth2TokenOrigin.Cached, Display(settings.AccessTokenUrl));
            }

            if (!fetchNew && cached?.RefreshToken is { Length: > 0 } refreshToken)
            {
                try
                {
                    var refreshed = await RefreshAsync(settings, refreshToken, context, cancellationToken).ConfigureAwait(false);
                    Keep(entry, refreshed.Token);
                    return refreshed;
                }
                catch (OAuth2Exception ex)
                {
                    // Expired or revoked: signing in again, or asking for a new token, is what's left.
                    context.Request.Warnings.Add($"{ex.Message} A new token was asked for instead.");
                    Keep(entry, null);
                }
            }

            try
            {
                var fetched = await FetchAsync(settings, context, cancellationToken).ConfigureAwait(false);
                Keep(entry, fetched.Token);
                return fetched;
            }
            catch (OAuth2Exception)
            {
                if (cached is not null && !cached.IsFresh(Time.GetUtcNow()))
                {
                    Keep(entry, null);
                }

                throw;
            }
        }
        finally
        {
            entry.Turn.Release();
        }
    }

    /// <summary>
    /// Adds the token of <paramref name="result"/> to <paramref name="request"/> as its <see cref="PreparedRequest.OAuth2"/>
    /// settings say (the Authorization header, or the <c>access_token</c> query parameter), as the auth helpers add theirs,
    /// and says where it came from in its auth description. The token is one of its secrets.
    /// </summary>
    public void Apply(PreparedRequest request, OAuth2Result result)
    {
        var settings = request.OAuth2 ?? throw new InvalidOperationException("The request fetches no OAuth 2.0 token.");
        var token = result.Token.AccessToken;
        request.OAuth2Token = result;
        request.AddSecretValues([token]);
        request.AuthDescription = result.Describe(settings, Time.GetUtcNow());
        if (token.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            request.Errors.Add("OAuth 2.0: the access token has a line break or NUL character, so it can't be sent.");
            return;
        }

        if (settings.AddToQuery)
        {
            // As the preparer adds an auth helper's parameter: it replaces one of the same name, encoded, at the end.
            var url = request.Url;
            var start = url.IndexOf('?', StringComparison.Ordinal);
            var pairs = start < 0 || start == url.Length - 1 ? [] : url[(start + 1)..].Split('&').ToList();
            if (pairs.RemoveAll(pair => (pair.IndexOf('=', StringComparison.Ordinal) is var equals and >= 0 ? pair[..equals] : pair) == "access_token") > 0)
            {
                request.Warnings.Add("The auth helper replaced the 'access_token' query parameter.");
            }

            var encoded = Uri.EscapeDataString(token);
            request.AddSecretValues([encoded]);
            pairs.Add("access_token=" + encoded);
            request.Url = (start < 0 ? url : url[..start]) + "?" + string.Join('&', pairs);
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                request.Uri = uri;
            }

            return;
        }

        if (request.Headers.RemoveAll(h => string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) > 0)
        {
            request.Warnings.Add("The auth helper replaced the 'Authorization' header.");
        }

        request.Headers.Add(new("Authorization", settings.HeaderPrefix.Length == 0 ? token : $"{settings.HeaderPrefix} {token}"));
        request.AuthHeaderNames.Add("Authorization");
    }

    /// <summary>A URL as messages and the timeline show it: without its query (which can hold a key) and user info.</summary>
    public static string Display(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Display(uri) : url;

    /// <inheritdoc cref="Display(string)"/>
    public static string Display(Uri uri) => $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";

    /// <summary>
    /// The code in an authorization response: the URL the browser returned to (as pasted from its address bar), or its
    /// query alone; servers may put it in the fragment too. Throws <see cref="OAuth2Exception"/> for an error response, one
    /// without a code, and one whose state is not <paramref name="state"/> (not the response to this sign-in: forged).
    /// </summary>
    internal static string ReadAuthorizationResponse(string response, string state)
    {
        response = response.Trim();
        var hash = response.IndexOf('#', StringComparison.Ordinal);
        var query = response.IndexOf('?', StringComparison.Ordinal);
        var end = hash < 0 ? response.Length : hash;
        var queryText = query >= 0 && query < end ? response[(query + 1)..end]
            : query < 0 && hash < 0 && !response.Contains("://", StringComparison.Ordinal) ? response
            : "";
        var fields = OAuth2Response.ParseForm(queryText);
        if (hash >= 0)
        {
            foreach (var (key, value) in OAuth2Response.ParseForm(response[(hash + 1)..]))
            {
                fields.TryAdd(key, value);
            }
        }

        if (fields.GetValueOrDefault("error") is { Length: > 0 } error)
        {
            var description = fields.GetValueOrDefault("error_description");
            throw new OAuth2Exception($"OAuth 2.0: the authorization server did not sign you in: {error}{(string.IsNullOrEmpty(description) ? "" : ": " + description)}");
        }

        if (fields.GetValueOrDefault("state") != state)
        {
            throw new OAuth2Exception("OAuth 2.0: the authorization response is not the one to this sign-in (its state differs), so no token was asked for.");
        }

        return fields.GetValueOrDefault("code") is { Length: > 0 } code
            ? code
            : throw new OAuth2Exception("OAuth 2.0: the authorization response has no code.");
    }

    private void Keep(Entry entry, OAuth2Token? token)
    {
        lock (_gate)
        {
            entry.Token = token;
        }
    }

    private async Task<OAuth2Result> FetchAsync(OAuth2Settings settings, OAuth2Context context, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>();
        switch (settings.Grant)
        {
            case OAuth2Grant.ClientCredentials:
                form.Add(new("grant_type", "client_credentials"));
                AddScope(form, settings);
                break;
            case OAuth2Grant.Password:
                form.Add(new("grant_type", "password"));
                form.Add(new("username", settings.Username));
                form.Add(new("password", settings.Password));
                AddScope(form, settings);
                break;
            case OAuth2Grant.AuthorizationCode or OAuth2Grant.AuthorizationCodeWithPkce:
            {
                var (code, redirectUri, verifier) = await SignInAsync(settings, context, cancellationToken).ConfigureAwait(false);
                form.Add(new("grant_type", "authorization_code"));
                form.Add(new("code", code));
                form.Add(new("redirect_uri", redirectUri));
                if (verifier is not null)
                {
                    form.Add(new("code_verifier", verifier));
                }

                break;
            }

            default:
                throw new OAuth2Exception($"OAuth 2.0: TermRest does not fetch tokens for the {settings.GrantName} grant.");
        }

        return await RequestTokenAsync(settings, settings.AccessTokenUrl, form, settings.TokenRequestParams, null, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Redeems a refresh token (RFC 6749, section 6) for the same scope.</summary>
    private Task<OAuth2Result> RefreshAsync(OAuth2Settings settings, string refreshToken, OAuth2Context context, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "refresh_token"), new("refresh_token", refreshToken) };
        AddScope(form, settings);
        var url = settings.RefreshTokenUrl.Length > 0 ? settings.RefreshTokenUrl : settings.AccessTokenUrl;
        return RequestTokenAsync(settings, url, form, settings.RefreshRequestParams, refreshToken, context, cancellationToken);
    }

    private static void AddScope(List<KeyValuePair<string, string>> form, OAuth2Settings settings)
    {
        if (settings.Scope.Length > 0)
        {
            form.Add(new("scope", settings.Scope));
        }
    }

    /// <summary>
    /// Has the user sign in for an authorization code (RFC 6749, section 4.1; with PKCE, RFC 7636): the browser opens the
    /// authorization request, and returns to the redirect URI, where TermRest listens when it is a loopback address.
    /// </summary>
    private async Task<(string Code, string RedirectUri, string? Verifier)> SignInAsync(OAuth2Settings settings, OAuth2Context context, CancellationToken cancellationToken)
    {
        if (context.Interaction is not { } interaction)
        {
            throw new OAuth2Exception($"OAuth 2.0: the {settings.GrantName} grant has you sign in with a browser, which TermRest can't do here.");
        }

        var state = settings.State.Length > 0 ? settings.State : RandomValue(16);
        string? verifier = null;
        string? challenge = null;
        if (settings.Grant == OAuth2Grant.AuthorizationCodeWithPkce)
        {
            verifier = settings.CodeVerifier.Length > 0 ? settings.CodeVerifier : RandomValue(32);
            challenge = settings.ChallengeMethod == "plain" ? verifier : Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        }

        using var loopback = LoopbackRedirect.Start(settings.RedirectUri);
        var redirectUri = loopback?.RedirectUri ?? settings.RedirectUri;
        var url = AuthorizationUrl(settings, redirectUri, state, challenge);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(SignInTimeout);
        var redirect = loopback?.ReceiveAsync(waiting.Token);
        string? response;
        try
        {
            response = await interaction.SignInAsync(new OAuth2SignIn { AuthorizationUrl = url, RedirectUri = redirectUri, Redirect = redirect }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && waiting.IsCancellationRequested)
        {
            var timeout = SignInTimeout.TotalMinutes >= 1 ? $"{SignInTimeout.TotalMinutes:0.#} minutes" : $"{SignInTimeout.TotalSeconds:0.#} seconds";
            throw new OAuth2Exception($"OAuth 2.0: the browser did not return to {redirectUri} within {timeout}, so no token was asked for.");
        }
        finally
        {
            // A URL pasted instead: the listener stops.
            if (redirect is not null)
            {
                await waiting.CancelAsync().ConfigureAwait(false);
                try
                {
                    await redirect.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        if (response is null)
        {
            throw new OAuth2Exception("OAuth 2.0: signing in was cancelled, so no token was asked for.");
        }

        return (ReadAuthorizationResponse(response, state), redirectUri, verifier);
    }

    /// <summary>The authorization request (RFC 6749, section 4.1.1): the auth URL with the request in its query.</summary>
    private static Uri AuthorizationUrl(OAuth2Settings settings, string redirectUri, string state, string? challenge)
    {
        var parameters = new List<KeyValuePair<string, string>> { new("response_type", "code"), new("client_id", settings.ClientId), new("redirect_uri", redirectUri) };
        AddScope(parameters, settings);
        parameters.Add(new("state", state));
        if (challenge is not null)
        {
            parameters.Add(new("code_challenge", challenge));
            parameters.Add(new("code_challenge_method", settings.ChallengeMethod));
        }

        // An authorization request has only the URL to carry them.
        Override(parameters, settings.AuthRequestParams);
        var text = WithQuery(settings.AuthUrl, parameters);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : throw new OAuth2Exception($"OAuth 2.0: the auth URL '{settings.AuthUrl}' is not an http(s) URL.");
    }

    /// <summary>Sends a token request (RFC 6749, section 3.2) and reads the token it returns.</summary>
    /// <param name="refreshToken">The refresh token redeemed (kept when the response has no new one); null for a new token.</param>
    private async Task<OAuth2Result> RequestTokenAsync(OAuth2Settings settings, string url, List<KeyValuePair<string, string>> form, IReadOnlyList<OAuth2Parameter> extra,
        string? refreshToken, OAuth2Context context, CancellationToken cancellationToken)
    {
        var headers = new List<KeyValuePair<string, string>>();
        AddClientAuthentication(settings, url, form, headers);

        // As in Postman: the extra parameters replace the request's own of the same name.
        Override(form, extra.Where(p => p.Target == OAuth2ParameterTarget.Body));
        var extraHeaders = extra.Where(p => p.Target == OAuth2ParameterTarget.Header).ToList();
        headers.RemoveAll(h => extraHeaders.Any(p => string.Equals(p.Key, h.Key, StringComparison.OrdinalIgnoreCase)));
        headers.AddRange(extraHeaders.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        var query = extra.Where(p => p.Target == OAuth2ParameterTarget.Url).Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();

        var request = BuildRequest(WithQuery(url, query), headers, form, context);
        var endpoint = Display(request.Uri!);
        context.Interaction?.RequestingToken(endpoint);
        var exchange = await context.Executor.SendAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var warning in request.Warnings)
        {
            context.Request.Warnings.Add($"OAuth 2.0 token request: {warning}");
        }

        if (exchange.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (exchange.Response is not { } response)
        {
            throw new OAuth2Exception($"OAuth 2.0: the token request to {endpoint} failed: {exchange.ErrorMessage}");
        }

        var status = $"{response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        if (response.StatusCode is >= 300 and < 400)
        {
            var location = response.GetHeader("Location") is { Length: > 0 } target ? $" (to {Display(target)})" : "";
            throw new OAuth2Exception($"OAuth 2.0: the token endpoint {endpoint} answered {status}{location}: token requests, which carry the client's credentials, are not redirected.");
        }

        var fields = OAuth2Response.ReadFields(response.Body);
        if (response.StatusCode < 300 && fields is not null && OAuth2Token.From(fields, Time.GetUtcNow(), refreshToken) is { } token)
        {
            return new OAuth2Result(token, refreshToken is null ? OAuth2TokenOrigin.Fetched : OAuth2TokenOrigin.Refreshed, endpoint,
                $"{response.StatusCode} in {HttpExchange.Format(exchange.Timing.Total)}");
        }

        var reason = OAuth2Response.DescribeError(response, fields);
        throw new OAuth2Exception(response.StatusCode < 300
            ? $"OAuth 2.0: the token endpoint {endpoint} answered {status} without an access token{(reason.Length > 0 ? ": " + reason : ".")}"
            : $"OAuth 2.0: the token request to {endpoint} failed: {status}{(reason.Length > 0 ? " — " + reason : "")}");
    }

    /// <summary>
    /// How the client authenticates (RFC 6749, section 2.3): a client assertion its certificate signs, its secret in the form,
    /// or (by default) HTTP Basic with its ID and secret, as Postman and curl send them. A client without a secret (a public
    /// one, as with PKCE) names itself in the form instead: some servers refuse Basic with an empty password.
    /// </summary>
    private void AddClientAuthentication(OAuth2Settings settings, string url, List<KeyValuePair<string, string>> form, List<KeyValuePair<string, string>> headers)
    {
        switch (settings.ClientAuthentication)
        {
            case OAuth2ClientAuthentication.PrivateKeyJwt:
                if (settings.SigningCertificate is not { } certificate)
                {
                    throw new OAuth2Exception($"OAuth 2.0: certificate '{settings.AssertionCertificate}', which signs the client assertion, is not loaded.");
                }

                if (settings.ClientId.Length == 0)
                {
                    throw new OAuth2Exception("OAuth 2.0: a client assertion names the client: enter its client ID.");
                }

                form.Add(new("client_id", settings.ClientId));
                form.Add(new("client_assertion_type", ClientAssertion.AssertionType));
                form.Add(new("client_assertion", ClientAssertion.Create(certificate, settings.AssertionCertificate, settings.ClientId,
                    settings.AssertionAudience.Length > 0 ? settings.AssertionAudience : url, settings.AssertionAlgorithm, settings.AssertionKeyId,
                    settings.AssertionChain, Time.GetUtcNow())));
                break;
            case OAuth2ClientAuthentication.Body:
                if (settings.ClientId.Length > 0)
                {
                    form.Add(new("client_id", settings.ClientId));
                }

                if (settings.ClientSecret.Length > 0)
                {
                    form.Add(new("client_secret", settings.ClientSecret));
                }

                break;
            default:
                if (settings.ClientSecret.Length > 0)
                {
                    headers.Add(new("Authorization", "Basic " + AuthApplier.BasicToken(settings.ClientId, settings.ClientSecret)));
                }
                else if (settings.ClientId.Length > 0)
                {
                    form.Add(new("client_id", settings.ClientId));
                }

                break;
        }
    }

    /// <summary>
    /// A token request: a form POSTed with the settings requests go with (SSL verification, proxy, CA bundle), and the
    /// client certificate the request the token is for selects for its URL. No cookies go with it or come from it.
    /// </summary>
    private static PreparedRequest BuildRequest(string url, List<KeyValuePair<string, string>> headers, List<KeyValuePair<string, string>> form, OAuth2Context context)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new OAuth2Exception($"OAuth 2.0: the token URL '{Display(url)}' is not an http(s) URL.");
        }

        if (headers.Any(h => h.Key.Length == 0 || $"{h.Key}{h.Value}".AsSpan().IndexOfAny('\r', '\n', '\0') >= 0))
        {
            throw new OAuth2Exception("OAuth 2.0: a header of the token request is empty or has a line break or NUL character.");
        }

        var defaults = context.Defaults;
        var request = new PreparedRequest
        {
            Method = "POST",
            Url = url,
            Uri = uri,
            Body = new PreparedBody { Kind = PreparedBodyKind.FormUrlEncoded, Fields = form, ContentType = "application/x-www-form-urlencoded" },
            AuthDescription = "OAuth 2.0 client",
            StrictSsl = defaults.StrictSsl,
            ExtraTrustedCertificates = context.Request.ExtraTrustedCertificates,
            FollowRedirects = false,
            UseCookies = false,
            Timeout = defaults.Timeout ?? DefaultTimeout,
            Proxy = defaults.Proxy,
            ProxyCredential = defaults.ProxyCredential,
            HttpVersion = defaults.HttpVersion,
            MaxResponseBytes = Math.Min(defaults.MaxResponseBytes, 1024 * 1024),
        };
        request.Headers.Add(new("Accept", "application/json"));
        request.Headers.AddRange(headers);
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            request.ClientCertificate = context.Request.ClientCertificateSelector?.Invoke(uri);
        }

        return request;
    }

    /// <summary>Removes the parameters of the names <paramref name="extra"/> has, and adds its own (all of them: a name may come more than once).</summary>
    private static void Override(List<KeyValuePair<string, string>> parameters, IEnumerable<OAuth2Parameter> extra)
    {
        var added = extra.ToList();
        parameters.RemoveAll(p => added.Any(e => e.Key == p.Key));
        parameters.AddRange(added.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)));
    }

    /// <summary><paramref name="url"/> with <paramref name="parameters"/> added to its query (before its fragment).</summary>
    private static string WithQuery(string url, List<KeyValuePair<string, string>> parameters)
    {
        if (parameters.Count == 0)
        {
            return url;
        }

        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var (head, fragment) = hash < 0 ? (url, "") : (url[..hash], url[hash..]);
        var separator = !head.Contains('?', StringComparison.Ordinal) ? "?" : head.EndsWith('?') || head.EndsWith('&') ? "" : "&";
        return head + separator + string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")) + fragment;
    }

    /// <summary>A random value for a state or code verifier: base64url, which RFC 7636 allows in a verifier.</summary>
    private static string RandomValue(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    private sealed class Entry
    {
        /// <summary>One request at a time fetches or refreshes the token; those that wait take it then.</summary>
        public SemaphoreSlim Turn { get; } = new(1, 1);

        public OAuth2Token? Token { get; set; }
    }
}
