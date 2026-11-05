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
            app.Run(window, ex => ReportUnexpectedError(app, ex));
            if (window.NextWorkspacePath is not { } next)
            {
                return 0;
            }

            path = next;
        }
    }

    /// <summary>Keeps the UI alive after an unexpected exception: shows it and logs the details.</summary>
    private static bool ReportUnexpectedError(IApplication app, Exception exception)
    {
        var log = Path.Combine(Path.GetTempPath(), "termrest-errors.log");
        try
        {
            File.AppendAllText(log, $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
            Dialogs.Ui.Error(app, "Unexpected error", $"{exception.Message}\n\nDetails were written to {log}");
        }
        catch (Exception)
        {
            // Never let error reporting take the application down.
        }

        return true;
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

    /// <summary>
    /// Renders the UI once into an in-memory screen (ANSI driver, no terminal needed) and returns it as text.
    /// Used as a smoke test of the published binary on every platform, e.g. in CI.
    /// </summary>
    public static string Snapshot(string workspacePath, int width = 120, int height = 36, string? requestPath = null, bool send = false)
    {
        ConfigureKeyBindings();
        var workspace = ApiWorkspace.Open(workspacePath);
        using var runner = new RequestRunner(workspace);
        using var app = Application.Create();
        app.Init(Terminal.Gui.Drivers.DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        using var window = new MainWindow(workspace, runner);
        if (requestPath is not null)
        {
            var node = workspace.Collections.Select(c => c.FindRequest(requestPath)).FirstOrDefault(n => n is not null)
                       ?? throw new ArgumentException($"Request '{requestPath}' was not found.", nameof(requestPath));
            window.ShowRequest(node);
        }

        if (send)
        {
            window.SendAndShowAsync().GetAwaiter().GetResult();
        }

        var token = app.Begin(window);
        app.LayoutAndDraw(forceRedraw: true);
        var screen = app.Driver.ToString() ?? "";
        if (token is not null)
        {
            app.End(token);
        }

        return screen;
    }
}
