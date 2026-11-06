using Terminal.Gui.App;
using Terminal.Gui.Input;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;

namespace TermRest.Tui;

public sealed class TuiOptions
{
    /// <summary>Force a Terminal.Gui driver (windows, unix, dotnet, ansi). Null picks the best one for the platform.</summary>
    public string? Driver { get; set; }

    public bool? StrictSslOverride { get; set; }

    /// <summary>Color theme for this session; null uses $TERMREST_THEME, the workspace's theme or the user's.</summary>
    public string? Theme { get; set; }
}

/// <summary>Entry point for the full-screen UI.</summary>
public static class TuiApp
{
    /// <summary>
    /// Main loop passes per second. Terminal.Gui's default of 25 handles input and repaints every 40 ms, which lags
    /// behind the mouse; 60 keeps up while an idle pass stays well under a millisecond.
    /// </summary>
    internal const ushort IterationsPerSecond = 60;

    /// <summary>Runs the UI until the user quits. Re-opens when the user switches workspace folders.</summary>
    public static int Run(string workspacePath, TuiOptions? options = null)
    {
        options ??= new TuiOptions();
        ConfigureKeyBindings();
        Application.MaximumIterationsPerSecond = IterationsPerSecond;
        using var timerResolution = TimerResolution.Request();

        var path = workspacePath;
        var requestedTheme = options.Theme;
        while (true)
        {
            var workspace = ApiWorkspace.Open(path);
            var themeErrors = new List<string>();
            var theme = ResolveTheme(workspace, requestedTheme, themeErrors, out var found);
            if (!found && requestedTheme is not null)
            {
                Console.Error.WriteLine($"Color theme '{requestedTheme}' was not found; see 'termrest themes'.");
                return 2;
            }

            using var runner = new RequestRunner(workspace) { StrictSslOverride = options.StrictSslOverride };
            runner.ConfigureHistory();
            runner.ConfigureScripts();
            using var app = Application.Create();
            app.Init(options.Driver);
            ThemeCatalog.Apply(theme);
            workspace.LoadErrors.AddRange(ThemeProblems(themeErrors));
            using var window = new MainWindow(workspace, runner);
            app.Run(window, ex => ReportUnexpectedError(app, ex));
            if (window.NextWorkspacePath is not { } next)
            {
                return 0;
            }

            path = next;
            requestedTheme = null; // the --theme option applies to the first workspace; later ones use the saved choice
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
    /// Applies the color theme for this session: <paramref name="requested"/>, else $TERMREST_THEME, the workspace's or
    /// the user's theme. Problems (unknown theme, invalid theme files) are added to the workspace's load errors.
    /// </summary>
    public static ColorTheme ApplyTheme(ApiWorkspace workspace, string? requested = null, string? userDirectory = null)
    {
        var errors = new List<string>();
        var theme = ResolveTheme(workspace, requested, errors, out _, userDirectory);
        ThemeCatalog.Apply(theme);
        workspace.LoadErrors.AddRange(ThemeProblems(errors));
        return theme;
    }

    /// <summary>Theme problems as workspace load errors, each saying that it is about the color theme.</summary>
    private static IEnumerable<string> ThemeProblems(IEnumerable<string> errors) =>
        errors.Select(e => e.StartsWith("Color theme", StringComparison.Ordinal) ? e : $"Color theme: {e}");

    /// <summary>The theme to use (Default when the selected one does not exist: <paramref name="found"/> is false then).</summary>
    internal static ColorTheme ResolveTheme(ApiWorkspace workspace, string? requested, List<string> errors, out bool found, string? userDirectory = null)
    {
        var themes = ThemeCatalog.Load(workspace.RootPath, errors, userDirectory);
        var name = ThemeCatalog.SelectedName(requested, workspace.Settings, UserSettings.Load(userDirectory));
        found = name is null || ThemeCatalog.Find(themes, name) is not null;
        return ThemeCatalog.Resolve(name, themes, errors);
    }

    /// <summary>
    /// Renders the UI once into an in-memory screen (ANSI driver, no terminal needed) and returns it as text.
    /// Used as a smoke test of the published binary on every platform, e.g. in CI.
    /// </summary>
    public static string Snapshot(string workspacePath, int width = 120, int height = 36, string? requestPath = null, bool send = false, string? theme = null)
    {
        ConfigureKeyBindings();
        var workspace = ApiWorkspace.Open(workspacePath);
        var themeErrors = new List<string>();
        var colorTheme = ResolveTheme(workspace, theme, themeErrors, out var found);
        if (!found && theme is not null)
        {
            throw new ArgumentException($"Color theme '{theme}' was not found; see 'termrest themes'.", nameof(theme));
        }

        using var runner = new RequestRunner(workspace);
        runner.ConfigureScripts();
        using var app = Application.Create();
        app.Init(Terminal.Gui.Drivers.DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        ThemeCatalog.Apply(colorTheme);
        workspace.LoadErrors.AddRange(ThemeProblems(themeErrors));
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
