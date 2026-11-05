using Terminal.Gui.App;
using Terminal.Gui.Input;
using TermRest.Core.Workspace;

namespace TermRest.Tui;

public sealed class TuiOptions
{
    /// <summary>Force a Terminal.Gui driver (windows, unix, dotnet, ansi). Null picks the best one for the platform.</summary>
    public string? Driver { get; set; }

    public bool? StrictSslOverride { get; set; }
}

/// <summary>Entry point for the full-screen UI.</summary>
public static class TuiApp
{
    /// <summary>Runs the UI until the user quits. Re-opens when the user switches workspace folders.</summary>
    public static int Run(string workspacePath, TuiOptions? options = null)
    {
        options ??= new TuiOptions();
        ConfigureKeyBindings();

        var path = workspacePath;
        while (true)
        {
            var workspace = ApiWorkspace.Open(path);
            using var runner = new RequestRunner(workspace) { StrictSslOverride = options.StrictSslOverride };
            using var app = Application.Create();
            app.Init(options.Driver);
            using var window = new MainWindow(workspace, runner);
            app.Run(window);
            if (window.NextWorkspacePath is not { } next)
            {
                return 0;
            }

            path = next;
        }
    }

    /// <summary>
    /// F5 is Terminal.Gui's "refresh screen" key by default; TermRest uses it to send (like Postman), so free it up.
    /// </summary>
    internal static void ConfigureKeyBindings()
    {
        if (Application.DefaultKeyBindings?.ContainsKey(Command.Refresh) == true)
        {
            Application.RemoveDefaultKeyBinding(Command.Refresh);
        }
    }

    /// <summary>Creates the main window without running it (used by tests).</summary>
    internal static MainWindow CreateWindow(ApiWorkspace workspace, RequestRunner runner) => new(workspace, runner);
}
