using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using TermRest.Core.History;
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
    /// Main loop passes per second while the UI is in use (see <see cref="UiPace"/>). Terminal.Gui's default of 25 handles
    /// input and repaints every 40 ms, which lags behind the mouse; 60 keeps up while an idle pass stays well under a
    /// millisecond.
    /// </summary>
    internal const ushort IterationsPerSecond = 60;

    /// <summary>Runs the UI until the user quits. Re-opens when the user switches workspace folders.</summary>
    public static int Run(string workspacePath, TuiOptions? options = null)
    {
        options ??= new TuiOptions();
        ConfigureKeyBindings();
        ApiWorkspace workspace;
        try
        {
            workspace = ApiWorkspace.Open(workspacePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var requestedTheme = options.Theme;
        while (true)
        {
            var themeErrors = new List<string>();
            var theme = ResolveTheme(workspace, requestedTheme, themeErrors, out var found);
            if (!found && requestedTheme is not null)
            {
                Console.Error.WriteLine($"Color theme '{requestedTheme}' was not found; see 'termrest themes'.");
                return 2;
            }

            using var runner = new RequestRunner(workspace) { StrictSslOverride = options.StrictSslOverride, RequireScriptRuntimes = false };
            runner.ConfigureHistory();
            runner.ConfigureScripts();
            runner.ConfigureProxy();
            runner.ConfigureFiles();
            using var app = Application.Create();
            app.Init(options.Driver);
            ThemeCatalog.Apply(theme);
            workspace.LoadErrors.AddRange(ThemeProblems(themeErrors));
            using var window = new MainWindow(workspace, runner);
            using var pace = new UiPace(app, () => window.IsSending);
            app.Run(window, ex => ReportUnexpectedError(app, ex));
            if (window.NextWorkspace is not { } next)
            {
                return 0;
            }

            workspace = next;
            requestedTheme = null; // the --theme option applies to the first workspace; later ones use the saved choice
        }
    }

    /// <summary>
    /// Keeps the UI alive after an unexpected exception: logs the details and, given a UI (<paramref name="app"/>), shows
    /// the error there.
    /// </summary>
    internal static bool ReportUnexpectedError(IApplication? app, Exception exception)
    {
        try
        {
            string? log;
            try
            {
                log = LogUnexpectedError(exception);
            }
            catch (Exception)
            {
                log = null; // the error is shown all the same
            }

            if (app is not null)
            {
                Dialogs.Ui.Error(app, "Unexpected error", exception.Message + (log is null ? "" : $"\n\nDetails were written to {log}"));
            }
        }
        catch (Exception)
        {
            // Never let error reporting take the application down.
        }

        return true;
    }

    /// <summary>
    /// Appends <paramref name="exception"/> to <c>errors.log</c> in the user's state folder (see
    /// <see cref="HistoryStore.DefaultStateDirectory"/>), which only the user can read; returns the log's path.
    /// </summary>
    internal static string LogUnexpectedError(Exception exception, string? stateDirectory = null)
    {
        var folder = stateDirectory ?? HistoryStore.DefaultStateDirectory();
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder); // in the user's profile: private already
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var log = Path.Combine(folder, "errors.log");
        using var stream = new FileStream(log, options);
        stream.Write(Encoding.UTF8.GetBytes($"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}"));
        return log;
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
        var user = UserSettings.Load(userDirectory);
        if (user.LoadError is { } problem && !workspace.LoadErrors.Contains(problem))
        {
            workspace.LoadErrors.Add(problem); // the defaults apply then, and the file is not saved over
        }

        var name = ThemeCatalog.SelectedName(requested, workspace.Settings, user);
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

        using var runner = new RequestRunner(workspace) { RequireScriptRuntimes = false };
        runner.ConfigureScripts();
        runner.ConfigureProxy();
        runner.ConfigureFiles();
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
