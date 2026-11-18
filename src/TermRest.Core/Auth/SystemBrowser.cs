using System.ComponentModel;
using System.Diagnostics;

namespace TermRest.Core.Auth;

/// <summary>Opens a URL in the user's default browser.</summary>
public static class SystemBrowser
{
    /// <summary>
    /// Opens <paramref name="url"/> (an http or https URL only) in the default browser: through the shell on Windows, with
    /// <c>open</c> on macOS, and elsewhere with the commands in <c>BROWSER</c>, <c>xdg-open</c>, or on WSL <c>wslview</c>
    /// or Windows' <c>explorer.exe</c>. False when none could: the user then opens it (there is no browser over SSH).
    /// What the commands write goes nowhere, so that it doesn't land in the full-screen UI.
    /// </summary>
    public static bool TryOpen(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var text = url.AbsoluteUri;
        if (OperatingSystem.IsWindows())
        {
            return Start(new ProcessStartInfo(text) { UseShellExecute = true }, wait: false);
        }

        // The URL is the script's argument ($1), never part of the script.
        var commands = new List<(string Script, bool AnyExitCode)>();
        if (OperatingSystem.IsMacOS())
        {
            commands.Add(("open \"$1\"", false));
        }
        else
        {
            foreach (var browser in (Environment.GetEnvironmentVariable("BROWSER") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                commands.Add((browser.Contains("%s", StringComparison.Ordinal) ? browser.Replace("%s", "\"$1\"", StringComparison.Ordinal) : $"{browser} \"$1\"", false));
            }

            commands.Add(("xdg-open \"$1\"", false));
            if (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null)
            {
                commands.Add(("wslview \"$1\"", false));
                commands.Add(("explorer.exe \"$1\"", true)); // which exits with 1 when it opened it
            }
        }

        foreach (var (script, anyExitCode) in commands)
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var argument in (string[])["-c", $"{script} </dev/null >/dev/null 2>&1", "sh", text])
            {
                start.ArgumentList.Add(argument);
            }

            if (Start(start, wait: true, anyExitCode))
            {
                return true;
            }
        }

        return false;
    }

    /// <param name="wait">
    /// Wait a little for it to exit, and take an exit code other than 0 as a failure (not found, no browser); one still
    /// running then is opening the browser (some <c>xdg-open</c>s wait for the browser to close).
    /// </param>
    private static bool Start(ProcessStartInfo start, bool wait, bool anyExitCode = false)
    {
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return !wait; // the shell opened it in a running browser
            }

            return !wait || !process.WaitForExit(TimeSpan.FromSeconds(3)) || anyExitCode || process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
