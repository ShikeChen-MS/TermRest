using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Auth;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>
/// Signing in for an OAuth 2.0 authorization code in the full-screen UI. The browser opens the sign-in page, and a dialog
/// waits for it to return to TermRest's loopback redirect URI, closing by itself when it does. It also takes the address
/// the browser ended on, pasted: for a redirect URI elsewhere, or a browser on another machine (as over SSH).
/// </summary>
/// <param name="app">The UI the dialog shows in; null once it is closed.</param>
/// <param name="status">Says that a token is being fetched (called on the UI thread).</param>
/// <param name="openBrowser">Opens the sign-in page in a browser; false when it can't (default: <see cref="SystemBrowser.TryOpen"/>).</param>
internal sealed class SignInDialog(Func<IApplication?> app, Action<string> status, Func<Uri, bool>? openBrowser = null) : IOAuth2Interaction
{
    private readonly Func<Uri, bool> _openBrowser = openBrowser ?? SystemBrowser.TryOpen;

    public Task<string?> SignInAsync(OAuth2SignIn signIn, CancellationToken cancellationToken)
    {
        if (app() is not { } ui)
        {
            return Task.FromResult<string?>(null);
        }

        // Shown from the main loop, whichever thread asks: the token is fetched off it.
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.AddTimeout(TimeSpan.Zero, () =>
        {
            try
            {
                Show(ui, signIn, result, cancellationToken);
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }

            return false;
        });
        return result.Task;
    }

    public void RequestingToken(string endpoint) => app()?.AddTimeout(TimeSpan.Zero, () =>
    {
        status($"Fetching an OAuth 2.0 token from {endpoint} …");
        return false;
    });

    private void Show(IApplication app, OAuth2SignIn signIn, TaskCompletionSource<string?> result, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            result.TrySetCanceled(cancellationToken);
            return;
        }

        var url = signIn.AuthorizationUrl.AbsoluteUri;
        var host = signIn.AuthorizationUrl.Host;
        var opened = _openBrowser(signIn.AuthorizationUrl);
        using var dialog = new Dialog { Title = "Sign in (OAuth 2.0)", Width = Dim.Percent(85) };
        dialog.Add(Ui.Hint(opened ? $"Sign in to {host} in the browser that opened, or at this address:" : $"Open this address in a browser and sign in to {host}:", 1, 1));
        var address = new TextArea { X = 1, Y = 2, Width = Dim.Fill(1), Height = 3, ReadOnly = true, WordWrap = true };
        address.SetText(url);
        var waiting = signIn.Redirect is not null;
        var pasted = new TextField { X = 1, Y = 8, Width = Dim.Fill(1) };

        // The field first: it has the focus (the address is there to be copied).
        dialog.Add(
            pasted,
            address,
            Ui.Hint(waiting ? $"Waiting for the browser to return to {signIn.RedirectUri} …" : $"Once you are signed in, the browser goes to {signIn.RedirectUri}.", 1, 6),
            Ui.Hint(waiting ? "With a browser on another machine, paste the address it ends on:" : "Paste the address it ends on (its address bar), then OK:", 1, 7));
        var copy = new Button { Text = "Copy address" };
        Ui.OnPress(copy, () => Ui.CopyToClipboard(app, url));
        var open = new Button { Text = "Open browser" };
        Ui.OnPress(open, () => _openBrowser(signIn.AuthorizationUrl));
        dialog.AddButton(copy);
        dialog.AddButton(open);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });

        // What ends the wait: the browser came back (or did not in time), or the request was cancelled. Checked from the main
        // loop: Terminal.Gui runs timeouts (this dialog is shown from one) under a lock, so one added from another thread
        // would wait for the dialog to close.
        string? received = null;
        Exception? failed = null;
        var polling = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (signIn.Redirect is { IsCompleted: true } redirect)
            {
                if (redirect.IsCompletedSuccessfully)
                {
                    received = redirect.Result;
                }
                else
                {
                    failed = redirect.Exception?.InnerException ?? new OperationCanceledException();
                }
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            dialog.RequestStop();
            return false;
        });

        pasted.SetFocus();
        try
        {
            // OK with nothing pasted goes on waiting.
            do
            {
                app.Run(dialog);
            }
            while (received is null && failed is null && !cancellationToken.IsCancellationRequested && !dialog.Canceled && pasted.Text.Trim().Length == 0);
        }
        finally
        {
            if (polling is not null)
            {
                app.RemoveTimeout(polling);
            }
        }
        if (received is not null)
        {
            result.TrySetResult(received);
        }
        else if (cancellationToken.IsCancellationRequested)
        {
            result.TrySetCanceled(cancellationToken);
        }
        else if (failed is not null)
        {
            result.TrySetException(failed);
        }
        else
        {
            result.TrySetResult(dialog.Canceled ? null : pasted.Text.Trim());
        }
    }
}
