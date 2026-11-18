namespace TermRest.Core.Auth;

/// <summary>A sign-in for an authorization code: where the user signs in, and where the browser returns.</summary>
public sealed class OAuth2SignIn
{
    /// <summary>The authorization request: the page of the authorization server the user signs in on.</summary>
    public required Uri AuthorizationUrl { get; init; }

    /// <summary>The redirect URI the browser returns to with the authorization response (the code).</summary>
    public required string RedirectUri { get; init; }

    /// <summary>
    /// Completes with the URL the browser returned to, received on the loopback redirect URI (see
    /// <see cref="LoopbackRedirect"/>); null when the redirect URI is elsewhere, so that the user pastes that URL. It fails
    /// with <see cref="OperationCanceledException"/> when no response came in time.
    /// </summary>
    public Task<string>? Redirect { get; init; }
}

/// <summary>How the user signs in for an OAuth 2.0 authorization code: the full-screen UI and the command line each have theirs.</summary>
public interface IOAuth2Interaction
{
    /// <summary>
    /// Has the user sign in at <see cref="OAuth2SignIn.AuthorizationUrl"/> (in their browser) and returns the URL the
    /// browser returned to with the authorization response: the one <see cref="OAuth2SignIn.Redirect"/> receives, or one the
    /// user pastes (when the browser runs elsewhere, as over SSH). Null when the user gives up.
    /// </summary>
    Task<string?> SignInAsync(OAuth2SignIn signIn, CancellationToken cancellationToken);

    /// <summary>A token request goes to <paramref name="endpoint"/> (the UI can say so while it waits).</summary>
    void RequestingToken(string endpoint)
    {
    }
}
