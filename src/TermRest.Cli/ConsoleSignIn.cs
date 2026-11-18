using TermRest.Core.Auth;

namespace TermRest.Cli;

/// <summary>
/// Signing in for an OAuth 2.0 authorization code on the command line: the browser opens the sign-in page (its URL is
/// printed too, for a browser elsewhere), and returns the code to TermRest's loopback redirect URI, or the user pastes the
/// URL it ended on (a redirect URI elsewhere, or a browser on another machine, as over SSH). All on stderr: stdout is
/// the response's.
/// </summary>
internal sealed class ConsoleSignIn : IOAuth2Interaction
{
    /// <summary>A line being read from stdin; one at a time, kept for the next sign-in when this one ended without it.</summary>
    private Task<string?>? _reading;

    public async Task<string?> SignInAsync(OAuth2SignIn signIn, CancellationToken cancellationToken)
    {
        var interactive = !Console.IsInputRedirected;
        var opened = SystemBrowser.TryOpen(signIn.AuthorizationUrl);
        var url = signIn.AuthorizationUrl.AbsoluteUri;
        Output.Info(opened ? $"Sign in in the browser that opened, or at:\n  {url}" : $"Sign in at:\n  {url}");
        if (signIn.Redirect is not { } redirect)
        {
            if (!interactive)
            {
                throw new OAuth2Exception($"OAuth 2.0: after the sign-in, the browser goes to {signIn.RedirectUri}, which is not a loopback address TermRest " +
                                          "listens on, and there is no terminal to paste the URL it ends on.");
            }

            Output.Info($"The browser then goes to {signIn.RedirectUri}: paste the URL it ends on (its address bar) and press Enter.");
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null || line.Trim().Length > 0)
                {
                    return line;
                }
            }
        }

        if (!interactive)
        {
            // In CI nobody signs in: no need to wait for it.
            if (!opened)
            {
                throw new OAuth2Exception("OAuth 2.0: signing in needs a browser, and TermRest could not open one (nor is there a terminal to paste the URL it ends on).");
            }

            Output.Info($"Waiting for the browser to return to {signIn.RedirectUri} …");
            return await redirect.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Output.Info($"Waiting for the browser to return to {signIn.RedirectUri} … (with a browser on another machine, paste the URL it ends on and press Enter)");
        while (true)
        {
            var reading = _reading ??= Task.Run(Console.In.ReadLine, CancellationToken.None);
            if (await Task.WhenAny((Task)redirect, reading).WaitAsync(cancellationToken).ConfigureAwait(false) != reading)
            {
                return await redirect.ConfigureAwait(false);
            }

            _reading = null;
            if (await reading.ConfigureAwait(false) is not { } line)
            {
                return await redirect.WaitAsync(cancellationToken).ConfigureAwait(false); // stdin closed: the browser it is
            }

            if (line.Trim().Length > 0)
            {
                return line;
            }
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var reading = _reading ??= Task.Run(Console.In.ReadLine, CancellationToken.None);
        var line = await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        _reading = null;
        return line;
    }
}
