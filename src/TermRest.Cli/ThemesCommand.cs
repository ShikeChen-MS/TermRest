using System.CommandLine;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;

namespace TermRest.Cli;

/// <summary><c>termrest themes</c>: list the UI's color themes, choose one, or start a custom theme from one.</summary>
internal static class ThemesCommand
{
    public static Command Create()
    {
        var command = new Command("themes",
            "List the color themes of the full-screen UI (built in, and custom JSON theme files you can edit). " +
            "Choose one here, in the UI (Tools > Color theme), or for one session with --theme or $TERMREST_THEME.");
        var workspace = WorkspaceOption();
        command.Options.Add(workspace);
        command.SetAction(parseResult => List(Root(parseResult.GetValue(workspace))));

        var use = new Command("use", "Use a theme from now on: for all workspaces, or with --only-here for this workspace only.");
        var useName = new Argument<string>("theme") { Description = "Theme name (see 'termrest themes')" };
        var useWorkspace = WorkspaceOption();
        var onlyHere = new Option<bool>("--only-here") { Description = "Save the choice in the workspace's .termrest/settings.json instead of your user settings" };
        use.Arguments.Add(useName);
        use.Options.Add(useWorkspace);
        use.Options.Add(onlyHere);
        use.SetAction(parseResult => Use(Root(parseResult.GetValue(useWorkspace)), parseResult.GetValue(useName)!, parseResult.GetValue(onlyHere)));
        command.Subcommands.Add(use);

        var copy = new Command("copy", "Create a custom theme file from a theme, to edit its colors.");
        var from = new Argument<string>("theme") { Description = "The theme to start from" };
        var name = new Argument<string>("name") { Description = "Name of the new theme" };
        var copyWorkspace = WorkspaceOption();
        var shared = new Option<bool>("--in-workspace") { Description = "Create it in the workspace's .termrest/themes (shared with the workspace) instead of your themes folder" };
        copy.Arguments.Add(from);
        copy.Arguments.Add(name);
        copy.Options.Add(copyWorkspace);
        copy.Options.Add(shared);
        copy.SetAction(parseResult => Copy(Root(parseResult.GetValue(copyWorkspace)), parseResult.GetValue(from)!, parseResult.GetValue(name)!, parseResult.GetValue(shared)));
        command.Subcommands.Add(copy);
        return command;
    }

    private static Option<string?> WorkspaceOption() =>
        new("--workspace", "-w") { Description = "Workspace folder (default: current directory): its own themes and theme choice count too" };

    private static string Root(string? workspace) => ApiWorkspace.RootFor(workspace ?? Environment.CurrentDirectory);

    private static IReadOnlyList<ColorTheme> Load(string root)
    {
        var errors = new List<string>();
        var themes = ThemeCatalog.Load(Directory.Exists(root) ? root : null, errors);
        foreach (var error in errors)
        {
            Output.Warning(error);
        }

        return themes;
    }

    private static int List(string root)
    {
        var themes = Load(root);
        var settings = Directory.Exists(root) ? ApiWorkspace.ReadSettings(root) : new WorkspaceSettings();
        var user = UserSettings.Load();
        if (user.LoadError is { } problem)
        {
            Output.Warning(problem);
        }

        var selected = ThemeCatalog.SelectedName(null, settings, user) ?? ThemeCatalog.DefaultName;
        var nameWidth = themes.Max(t => t.Name.Length) + 2;
        var sourceWidth = themes.Max(t => t.SourceDescription.Length) + 2;
        foreach (var theme in themes)
        {
            var mark = string.Equals(theme.Name, selected, StringComparison.OrdinalIgnoreCase) ? "●" : " ";
            Output.Info($"{mark} {theme.Name.PadRight(nameWidth)}{theme.SourceDescription.PadRight(sourceWidth)}{theme.Description}");
        }

        Output.Info("");
        Output.Info($"In use: {selected}{Origin(settings, user)}");
        Output.Info($"Custom themes: {UserSettings.ThemesDirectory()}");
        Output.Info($"  and for this workspace: {ThemeCatalog.WorkspaceThemesDirectory(root)}");
        Output.Info("Choose with 'termrest themes use <name>', in the UI (Tools > Color theme), or for one session with --theme <name>.");
        return 0;
    }

    private static string Origin(WorkspaceSettings settings, UserSettings user) =>
        Environment.GetEnvironmentVariable(ThemeCatalog.EnvironmentVariable) is { Length: > 0 } ? $" (from ${ThemeCatalog.EnvironmentVariable})"
        : settings.Theme is not null ? " (chosen for this workspace)"
        : user.Theme is not null ? " (your choice for all workspaces)"
        : "";

    private static int Use(string root, string name, bool onlyHere)
    {
        if (ThemeCatalog.Find(Load(root), name) is not { } theme)
        {
            Output.Error($"Color theme '{name}' was not found; see 'termrest themes'.");
            return 2;
        }

        if (onlyHere)
        {
            try
            {
                var workspace = ApiWorkspace.Open(root, CollectionLoading.None);
                workspace.Settings.Theme = theme.Name;
                workspace.SaveSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Output.Error(ex.Message); // also when the settings file could not be read: it is not saved over
                return 1;
            }

            Output.Status($"✓ {theme.Name} is used in {root}", true);
            return 0;
        }

        var user = UserSettings.Load();
        user.Theme = string.Equals(theme.Name, ThemeCatalog.DefaultName, StringComparison.OrdinalIgnoreCase) ? null : theme.Name;
        try
        {
            user.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Error(ex.Message);
            return 1;
        }

        Output.Status($"✓ {theme.Name} is used in all workspaces", true);
        if (Directory.Exists(root) && ApiWorkspace.ReadSettings(root).Theme is { } own)
        {
            Output.Info($"  This workspace keeps its own theme ({own}); change it with --only-here.");
        }

        return 0;
    }

    private static int Copy(string root, string from, string name, bool inWorkspace)
    {
        var themes = Load(root);
        if (ThemeCatalog.Find(themes, from) is not { } theme)
        {
            Output.Error($"Color theme '{from}' was not found; see 'termrest themes'.");
            return 2;
        }

        if (ThemeCatalog.FindCustom(themes, name) is { } taken)
        {
            Output.Error($"A custom theme called '{taken.Name}' exists already ({taken.FilePath}): choose another name.");
            return 2;
        }

        var directory = inWorkspace ? ThemeCatalog.WorkspaceThemesDirectory(root) : UserSettings.ThemesDirectory();
        try
        {
            var path = ThemeCatalog.WriteCopy(theme, name, directory);
            Output.Status($"✓ Created {path}", true);
            Output.Info("  Edit its palette colors (names such as DarkBlue, #RRGGBB, or None), then pick it in Tools > Color theme");
            Output.Info($"  or with 'termrest themes use \"{name.Trim()}\"'.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Error(ex.Message);
            return 1;
        }
    }
}
