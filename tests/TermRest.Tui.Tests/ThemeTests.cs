using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;
using Xunit;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermRest.Tui.Tests;

/// <summary>Color themes: built-in themes, theme files, choosing and applying (schemes are global, so not in parallel).</summary>
[Collection("TUI")]
public sealed class ThemeTests : IDisposable
{
    private readonly string _userDirectory = TestFiles.NewTempDirectory();

    private static ColorTheme BuiltIn(string name) => ThemeCatalog.Find(ThemeCatalog.BuiltIn, name) ?? throw new Xunit.Sdk.XunitException($"No built-in theme '{name}'.");

    private static Attribute A(string foreground, string background, TextStyle style = TextStyle.None) =>
        new(Color.Parse(foreground), Color.Parse(background), style);

    private string WriteTheme(string fileName, string json)
    {
        var directory = UserSettings.ThemesDirectory(_userDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Built_in_themes_include_terminal_gui_s_themes_and_termrest_palettes()
    {
        var names = ThemeCatalog.BuiltIn.Select(t => t.Name).ToList();

        Assert.Equal(ThemeCatalog.DefaultName, names[0]);
        Assert.Superset(new HashSet<string> { "Dark", "Light", "TurboPascal 5", "Green Phosphor", "8-Bit", "Dracula", "Nord", "Gruvbox Dark", "Monokai", "Solarized Dark", "Solarized Light", "High Contrast" }, names.ToHashSet());
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ThemeCatalog.BuiltIn, t => Assert.True(t.IsBuiltIn && t.Description.Length > 0, t.Name));
        Assert.All(ThemeCatalog.BuiltIn, t => Assert.Superset(new HashSet<string> { "Base", "Menu", "Dialog", "Error", "Accent", ThemeCatalog.SuccessScheme, ThemeCatalog.WarningScheme }, t.Schemes.Keys.ToHashSet()));
    }

    [Fact]
    public void Applying_a_theme_sets_the_colors_of_the_ui_and_switching_back_restores_them()
    {
        try
        {
            ThemeCatalog.Apply(BuiltIn("Dracula"));
            Assert.Equal("Dracula", ThemeCatalog.Current.Name);
            Assert.Equal(A("#f8f8f2", "#282a36"), SchemeManager.GetScheme("Base").Normal);
            Assert.Equal(A("#282a36", "#bd93f9"), SchemeManager.GetScheme("Menu").Focus);
            Assert.Equal(A("#ff79c6", "#21222c", TextStyle.Underline), SchemeManager.GetScheme("Menu").HotNormal);
            Assert.Equal(A("#50fa7b", "#282a36", TextStyle.Bold), SchemeManager.GetScheme(ThemeCatalog.SuccessScheme).Normal);
            Assert.Equal(A("#ffb86c", "#282a36", TextStyle.Bold), SchemeManager.GetScheme(ThemeCatalog.WarningScheme).Normal);
            Assert.Equal(Color.Parse("#ff5555"), ThemeCatalog.Current.Palette.MethodColor("delete"));

            // Terminal.Gui's own themes come back exactly, although TermRest replaced their schemes.
            ThemeCatalog.Apply(BuiltIn("Dark"));
            Assert.Equal(A("LightGray", "None"), SchemeManager.GetScheme("Base").Normal);
            Assert.Equal("Dark", TuiConfigurationBuilder.Shared.ThemeManager.CurrentThemeName);

            ThemeCatalog.Apply(BuiltIn(ThemeCatalog.DefaultName));
            Assert.Equal(A("Charcoal", "LightBlue", TextStyle.Bold), SchemeManager.GetScheme("Menu").Normal);
            Assert.Equal(A("None", "None"), SchemeManager.GetScheme("Base").Normal);
        }
        finally
        {
            ThemeCatalog.Apply(BuiltIn(ThemeCatalog.DefaultName));
        }
    }

    [Fact]
    public void Theme_files_change_palette_colors_and_single_scheme_roles()
    {
        var path = WriteTheme("production.json", """
            {
              // Comments and trailing commas are fine.
              "name": "Production",
              "base": "Nord",
              "palette": { "bar": "#5c0000", "barText": "White", "get": "Lime", },
              "schemes": {
                "Base": { "Focus": "white on DarkRed bold" },
                "success": { "Normal": "on #102030" }
              }
            }
            """);
        var errors = new List<string>();

        var theme = ThemeCatalog.Find(ThemeCatalog.Load(null, errors, _userDirectory), "production")!;

        Assert.Empty(errors);
        Assert.Equal((path, "Custom theme based on Nord", false), (theme.FilePath, theme.Description, theme.IsBuiltIn));
        Assert.Equal(Color.Parse("#5c0000"), theme.Palette.Bar);
        Assert.Equal(Color.Parse("Lime"), theme.Palette.MethodColor("GET"));
        Assert.Equal(BuiltIn("Nord").Palette.Post, theme.Palette.Post);
        Assert.Equal(A("White", "#5c0000"), theme.Schemes["Menu"].Normal);
        Assert.Equal(A("#d8dee9", "#2e3440"), theme.Schemes["Base"].Normal);
        Assert.Equal(A("White", "DarkRed", TextStyle.Bold), theme.Schemes["Base"].Focus);
        Assert.Equal(A("#a3be8c", "#102030", TextStyle.Bold), theme.Schemes[ThemeCatalog.SuccessScheme].Normal);
    }

    [Fact]
    public void A_copy_of_a_theme_looks_the_same_until_it_is_edited()
    {
        var directory = UserSettings.ThemesDirectory(_userDirectory);
        var path = ThemeCatalog.WriteCopy(BuiltIn("Dark"), "My Dark", directory);
        var errors = new List<string>();
        var copy = ThemeCatalog.Find(ThemeCatalog.Load(null, errors, _userDirectory), "My Dark")!;

        Assert.Empty(errors);
        Assert.Equal(Path.Combine(directory, "My Dark.json"), path);
        foreach (var scheme in (string[])["Base", "Menu", "Dialog", "Error", "Accent"])
        {
            Assert.Equal(BuiltIn("Dark").Schemes[scheme], copy.Schemes[scheme]);
        }

        Assert.Equal("Dark", copy.TerminalGuiTheme);
        Assert.Equal(Path.Combine(directory, "My Dark-2.json"), ThemeCatalog.WriteCopy(BuiltIn("Dark"), "My Dark", directory));

        // Named for every OS (a workspace's themes are shared through Git): no | or ?, and no two names differing in case.
        Assert.Equal(Path.Combine(directory, "my dark-3.json"), ThemeCatalog.WriteCopy(BuiltIn("Dark"), "my dark", directory));
        Assert.Equal(Path.Combine(directory, "Dark - contrast-.json"), ThemeCatalog.WriteCopy(BuiltIn("Dark"), "Dark | contrast?", directory));

        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "\"selection\": \"[^\"]*\"", "\"selection\": \"#004080\""));
        var edited = ThemeCatalog.Find(ThemeCatalog.Load(null, errors, _userDirectory), "My Dark")!;
        Assert.Equal(Color.Parse("#004080"), edited.Schemes["Base"].Focus.Background);

        // A copy of a built-in theme starts from it and lists its palette colors by name or #RRGGBB, and nothing else.
        Assert.All(ThemeCatalog.BuiltIn, theme =>
        {
            var file = ThemeFile.CopyOf(theme, "Copy");
            Assert.Equal((theme.Name, false), (file.Base, file.Schemes is not null));
            Assert.Equal(ThemePalette.Keys.Select(k => k.Get(theme.Palette).ToString()), file.Palette!.Values);
        });
    }

    [Fact]
    public void A_copy_of_a_custom_theme_looks_the_same_without_the_theme_files_it_builds_on()
    {
        // A production theme like the README's, shared in a workspace's .termrest/themes; a theme built on it, which keeps its
        // scheme roles; and one built on a Terminal.Gui theme.
        var workspace = TestFiles.NewTempDirectory();
        var shared = ThemeCatalog.WorkspaceThemesDirectory(workspace);
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Production.json"), """
            {
              "name": "Production",
              "base": "Nord",
              "palette": { "bar": "#5c0000", "barText": "White", "selection": "DarkRed" },
              "schemes": { "Base": { "Focus": "white on #8b0000 bold" }, "Success": { "Normal": "on #102030" } }
            }
            """);
        File.WriteAllText(Path.Combine(shared, "Staging.json"), """
            { "name": "Staging", "base": "Production", "palette": { "get": "Lime" }, "schemes": { "Menu": { "HotNormal": "None on none none" } } }
            """);
        File.WriteAllText(Path.Combine(shared, "Night.json"), """
            { "name": "Night", "base": "Dark", "schemes": { "Dialog": { "Normal": "Yellow on #102030 italic" }, "Warning": { "Disabled": "Orange" } } }
            """);
        var errors = new List<string>();
        var themes = ThemeCatalog.Load(workspace, errors, _userDirectory);
        var elsewhere = TestFiles.NewTempDirectory(); // the configuration folder of someone without that workspace
        string[] names = ["Production", "Staging", "Night"];
        var paths = names.Select(n => ThemeCatalog.WriteCopy(ThemeCatalog.Find(themes, n)!, $"My {n}", UserSettings.ThemesDirectory(elsewhere))).ToList();

        var copies = ThemeCatalog.Load(TestFiles.NewTempDirectory(), errors, elsewhere);

        Assert.Empty(errors);
        foreach (var name in names)
        {
            var (source, copy) = (ThemeCatalog.Find(themes, name)!, ThemeCatalog.Find(copies, $"My {name}")!);
            Assert.Equal((source.Palette, source.TerminalGuiTheme), (copy.Palette, copy.TerminalGuiTheme));
            Assert.Equal(source.Schemes.Keys.Order(), copy.Schemes.Keys.Order());
            Assert.All(source.Schemes, s => Assert.Equal(s.Value, copy.Schemes[s.Key]));
        }

        // The roles the theme sets stay as they are; the others still follow its palette.
        File.WriteAllText(paths[0], System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(paths[0]), "\"selection\": \"[^\"]*\"", "\"selection\": \"#004080\""));
        var edited = ThemeCatalog.Find(ThemeCatalog.Load(null, errors, elsewhere), "My Production")!;
        Assert.Empty(errors);
        Assert.Equal(A("White", "#8b0000", TextStyle.Bold), edited.Schemes["Base"].Focus);
        Assert.Equal(Color.Parse("#004080"), edited.Schemes["Menu"].Focus.Background);
    }

    [Fact]
    public void Roles_a_theme_sets_stay_as_set_whatever_their_order_in_the_file()
    {
        // Terminal.Gui derives the roles a scheme doesn't set from others: Focus from Normal, Highlight and ReadOnly from Editable.
        var workspace = TestFiles.NewTempDirectory();
        var shared = ThemeCatalog.WorkspaceThemesDirectory(workspace);
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Order.json"), """
            { "name": "Order", "schemes": { "Base": { "Highlight": "Red on Blue none", "ReadOnly": "Yellow on Blue none", "Focus": "Black on White none", "Editable": "White on Black none", "Normal": "Gray on Black none" } } }
            """);
        var errors = new List<string>();

        var scheme = ThemeCatalog.Find(ThemeCatalog.Load(workspace, errors, _userDirectory), "Order")!.Schemes["Base"];

        Assert.Empty(errors);
        Assert.Equal(
            (A("Red", "Blue", TextStyle.None), A("Yellow", "Blue", TextStyle.None), A("Black", "White", TextStyle.None), A("White", "Black", TextStyle.None), A("Gray", "Black", TextStyle.None)),
            (scheme.Highlight, scheme.ReadOnly, scheme.Focus, scheme.Editable, scheme.Normal));
    }

    [Fact]
    public void A_theme_keeps_the_status_roles_its_base_sets_until_it_changes_that_status_color()
    {
        var workspace = TestFiles.NewTempDirectory();
        var shared = ThemeCatalog.WorkspaceThemesDirectory(workspace);
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Production.json"), """
            { "name": "Production", "base": "Nord", "schemes": { "Success": { "Normal": "Lime on #102030 bold" }, "Warning": { "Normal": "Gold on #102030 bold" } } }
            """);
        File.WriteAllText(Path.Combine(shared, "Staging.json"), """{ "name": "Staging", "base": "Production", "palette": { "warning": "#ffa500" } }""");
        var errors = new List<string>();

        var themes = ThemeCatalog.Load(workspace, errors, _userDirectory);

        Assert.Empty(errors);
        var (production, staging) = (ThemeCatalog.Find(themes, "Production")!, ThemeCatalog.Find(themes, "Staging")!);
        Assert.Equal(A("Lime", "#102030", TextStyle.Bold), staging.Schemes[ThemeCatalog.SuccessScheme].Normal);
        Assert.Equal(production.Schemes[ThemeCatalog.SuccessScheme], staging.Schemes[ThemeCatalog.SuccessScheme]);
        Assert.Equal(new Attribute(Color.Parse("#ffa500"), staging.Palette.Background, TextStyle.Bold), staging.Schemes[ThemeCatalog.WarningScheme].Normal);
    }

    [Fact]
    public void Built_in_themes_color_scripts_so_they_can_be_read()
    {
        foreach (var theme in ThemeCatalog.BuiltIn.Where(t => t.Name != "8-Bit"))
        {
            var editor = theme.Schemes["Base"].Editable.Background;
            foreach (var color in (Color[])[theme.Palette.SyntaxComment, theme.Palette.SyntaxKeyword])
            {
                Assert.False(color.Equals(Color.None), $"{theme.Name} has no script colors");
                Assert.True(editor.Equals(Color.None) || ThemePalette.Contrast(color, editor) >= 1.5, $"{theme.Name}: {color} on {editor}");
            }
        }
    }

    [Fact]
    public void Theme_files_change_script_colors_without_replacing_the_base_theme_s_schemes()
    {
        WriteTheme("dark-scripts.json", """{ "name": "Dark scripts", "base": "Dark", "palette": { "syntaxKeyword": "Orange", "syntaxComment": "None" } }""");
        var errors = new List<string>();

        var theme = ThemeCatalog.Find(ThemeCatalog.Load(null, errors, _userDirectory), "Dark scripts")!;

        Assert.Empty(errors);
        Assert.Equal((Color.Parse("Orange"), Color.None), (theme.Palette.SyntaxKeyword, theme.Palette.SyntaxComment));
        Assert.Equal(BuiltIn("Dark").Palette.SyntaxString, theme.Palette.SyntaxString);
        Assert.Equal(BuiltIn("Dark").Schemes["Base"], theme.Schemes["Base"]);
    }

    [Fact]
    public void Problems_in_theme_files_are_reported_and_the_rest_still_works()
    {
        WriteTheme("broken.json", "{ \"name\": ");
        WriteTheme("typos.json", """
            {
              "name": "Typos",
              "base": "Nope",
              "palette": { "backgrond": "Black", "text": "not-a-color", "accent": "Yellow" },
              "schemes": { "Sidebar": { "Normal": "White" }, "Base": { "Glow": "White", "Focus": "White on", "HotFocus": "Red blinking" } }
            }
            """);
        var errors = new List<string>();

        var themes = ThemeCatalog.Load(null, errors, _userDirectory);

        Assert.Contains(errors, e => e.Contains("broken.json", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("base theme 'Nope' was not found", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown palette color 'backgrond'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'not-a-color' is not a color", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown scheme 'Sidebar'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown role 'Glow'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("a color is missing after 'on'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'blinking' is not a style", StringComparison.Ordinal));
        var typos = ThemeCatalog.Find(themes, "Typos")!;
        Assert.Equal(Color.Parse("Yellow"), typos.Palette.Accent);
        Assert.Equal(BuiltIn(ThemeCatalog.DefaultName).Palette.Text, typos.Palette.Text);
        Assert.Equal(ThemeCatalog.BuiltIn.Count + 1, themes.Count);
    }

    [Fact]
    public void Out_of_range_colors_and_nulls_in_theme_files_are_reported_too()
    {
        WriteTheme("odd.json", """
            {
              "name": "Odd",
              "palette": { "bar": "rgb(256,0,0)", "accent": "Yellow" },
              "schemes": { "Menu": null, "Base": { "Focus": null, "Normal": "White on rgb(0,0,999)", "HotFocus": "White on Blue" } }
            }
            """);
        var errors = new List<string>();

        var odd = ThemeCatalog.Find(ThemeCatalog.Load(null, errors, _userDirectory), "Odd")!;

        Assert.Equal(4, errors.Count);
        Assert.StartsWith("odd.json: palette color bar: 'rgb(256,0,0)' is not a color", errors[0], StringComparison.Ordinal);
        Assert.StartsWith("odd.json: Menu: no roles given", errors[1], StringComparison.Ordinal);
        Assert.StartsWith("odd.json: Base.Focus: empty", errors[2], StringComparison.Ordinal);
        Assert.Equal("odd.json: Base.Normal: 'rgb(0,0,999)' is not a color", errors[3]);
        Assert.Equal(Color.Parse("Yellow"), odd.Palette.Accent);
        Assert.Equal((Color.Parse("White"), Color.Parse("Blue")), (odd.Schemes["Base"].HotFocus.Foreground, odd.Schemes["Base"].HotFocus.Background));
    }

    [Fact]
    public void Custom_themes_can_build_on_each_other_and_replace_built_in_ones()
    {
        WriteTheme("a-child.json", """{ "name": "Child", "base": "Parent", "palette": { "accent": "Orange" } }""");
        WriteTheme("b-parent.json", """{ "name": "Parent", "base": "Dracula", "palette": { "background": "#101010" } }""");
        WriteTheme("dark.json", """{ "name": "Dark", "base": "Dark", "palette": { "get": "Cyan" } }""");
        var errors = new List<string>();

        var themes = ThemeCatalog.Load(null, errors, _userDirectory);

        Assert.Empty(errors);
        var child = ThemeCatalog.Find(themes, "Child")!;
        Assert.Equal((Color.Parse("#101010"), Color.Parse("Orange"), Color.Parse("#f8f8f2")), (child.Palette.Background, child.Palette.Accent, child.Palette.Text));
        var dark = ThemeCatalog.Find(themes, "Dark")!;
        Assert.False(dark.IsBuiltIn);
        Assert.Equal(Color.Parse("Cyan"), dark.Palette.Get);
        Assert.Equal(BuiltIn("Dark").Schemes["Base"], dark.Schemes["Base"]);
        Assert.Single(themes, t => t.Name == "Dark");
    }

    [Fact(Timeout = 10_000)]
    public async Task Several_theme_files_can_replace_the_theme_they_start_from()
    {
        // As after copying Default as a custom theme named Default twice, and once more into the workspace.
        WriteTheme("Default.json", """{ "name": "Default", "base": "Default", "palette": { "accent": "Orange" } }""");
        WriteTheme("Default-2.json", """{ "name": "Default", "base": "Default", "palette": { "bar": "#5c0000" } }""");
        var workspaceRoot = TestFiles.NewTempDirectory();
        Directory.CreateDirectory(ThemeCatalog.WorkspaceThemesDirectory(workspaceRoot));
        var workspaceTheme = Path.Combine(ThemeCatalog.WorkspaceThemesDirectory(workspaceRoot), "Default.json");
        File.WriteAllText(workspaceTheme, """{ "name": "Default", "base": "Default", "palette": { "get": "Cyan" } }""");
        var errors = new List<string>();

        var themes = await Task.Run(() => ThemeCatalog.Load(workspaceRoot, errors, _userDirectory), TestContext.Current.CancellationToken);

        Assert.Empty(errors);
        var theme = Assert.Single(themes, t => t.Name == ThemeCatalog.DefaultName);
        Assert.Equal(workspaceTheme, theme.FilePath);
        Assert.Equal((Color.Parse("Orange"), Color.Parse("#5c0000"), Color.Parse("Cyan")), (theme.Palette.Accent, theme.Palette.Bar, theme.Palette.Get)); // each starts from the one before
    }

    [Fact(Timeout = 10_000)]
    public async Task A_missing_base_is_reported_for_its_file_and_themes_based_on_that_theme_still_build_on_it()
    {
        WriteTheme("a-child.json", """{ "name": "Child", "base": "Default", "palette": { "accent": "Orange" } }""");
        WriteTheme("default.json", """{ "name": "Default", "base": "Defualt", "palette": { "background": "#101010" } }""");
        var errors = new List<string>();

        var themes = await Task.Run(() => ThemeCatalog.Load(null, errors, _userDirectory), TestContext.Current.CancellationToken);

        Assert.Equal(["default.json: base theme 'Defualt' was not found; using Default"], errors);
        var child = ThemeCatalog.Find(themes, "Child")!;
        Assert.Equal((Color.Parse("#101010"), Color.Parse("Orange")), (child.Palette.Background, child.Palette.Accent));
    }

    [Fact]
    public void The_theme_comes_from_the_command_line_environment_workspace_or_user()
    {
        var workspace = new WorkspaceSettings { Theme = "Nord" };
        var user = new UserSettings { Theme = "Dracula" };
        var previous = Environment.GetEnvironmentVariable(ThemeCatalog.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ThemeCatalog.EnvironmentVariable, null);
            Assert.Equal("Monokai", ThemeCatalog.SelectedName("Monokai", workspace, user));
            Assert.Equal("Nord", ThemeCatalog.SelectedName(null, workspace, user));
            Assert.Equal("Dracula", ThemeCatalog.SelectedName(null, new WorkspaceSettings(), user));
            Assert.Null(ThemeCatalog.SelectedName(null, new WorkspaceSettings(), new UserSettings()));

            Environment.SetEnvironmentVariable(ThemeCatalog.EnvironmentVariable, "Light");
            Assert.Equal("Light", ThemeCatalog.SelectedName(null, workspace, user));
            Assert.Equal("Monokai", ThemeCatalog.SelectedName("Monokai", workspace, user));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThemeCatalog.EnvironmentVariable, previous);
        }

        var errors = new List<string>();
        Assert.Equal(ThemeCatalog.DefaultName, ThemeCatalog.Resolve("Nope", ThemeCatalog.BuiltIn, errors).Name);
        Assert.Contains("Color theme 'Nope' was not found", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void The_chosen_theme_is_saved_for_the_user_or_the_workspace()
    {
        var root = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(root);

        Dialogs.ThemeDialog.Save(workspace, BuiltIn("Nord"), workspaceOnly: true, _userDirectory);
        Assert.Equal("Nord", ApiWorkspace.ReadSettings(root).Theme);
        Assert.Null(UserSettings.Load(_userDirectory).Theme);

        Dialogs.ThemeDialog.Save(workspace, BuiltIn("Dracula"), workspaceOnly: false, _userDirectory);
        Assert.Equal("Dracula", UserSettings.Load(_userDirectory).Theme);
        Assert.Null(ApiWorkspace.ReadSettings(root).Theme); // the workspace now follows the user's choice

        Dialogs.ThemeDialog.Save(workspace, BuiltIn(ThemeCatalog.DefaultName), workspaceOnly: false, _userDirectory);
        Assert.Null(UserSettings.Load(_userDirectory).Theme);
    }

    [Fact]
    public void User_settings_that_cannot_be_read_are_reported_and_not_saved_over()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        Directory.CreateDirectory(_userDirectory);
        var path = Path.Combine(_userDirectory, "settings.json");
        File.WriteAllText(path, "{ \"theme\": ");

        TuiApp.ApplyTheme(workspace, userDirectory: _userDirectory);

        Assert.Contains(workspace.LoadErrors, e => e.StartsWith(path, StringComparison.Ordinal));
        Assert.Throws<IOException>(() => Dialogs.ThemeDialog.Save(workspace, BuiltIn("Nord"), workspaceOnly: false, _userDirectory));
        Assert.Equal("{ \"theme\": ", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("white on blue", "White", "Blue", TextStyle.Italic)]
    [InlineData("#112233 on rgb(1,2,3) bold underline", "#112233", "#010203", TextStyle.Bold | TextStyle.Underline)]
    [InlineData("on DarkRed", "Gray", "DarkRed", TextStyle.Italic)]
    [InlineData("Yellow", "Yellow", "Black", TextStyle.Italic)]
    [InlineData("reverse", "Gray", "Black", TextStyle.Reverse)]
    [InlineData("None on none none", "None", "None", TextStyle.None)]
    [InlineData("none", "Gray", "Black", TextStyle.None)]
    [InlineData("italic bold", "Gray", "Black", TextStyle.Italic | TextStyle.Bold)]
    public void Attributes_are_written_as_colors_and_styles(string text, string foreground, string background, TextStyle style)
    {
        var fallback = A("Gray", "Black", TextStyle.Italic);

        Assert.True(ThemeFile.TryParseAttribute(text, fallback, out var attribute, out var problem), problem);
        Assert.Equal(A(foreground, background, style), attribute);
    }

    [Theory]
    [InlineData("White", "DarkRed", TextStyle.Bold)]
    [InlineData("None", "None", TextStyle.None)]
    [InlineData("#123456", "None", TextStyle.Italic | TextStyle.Underline | TextStyle.Strikethrough)]
    [InlineData("#80FF0000", "#00000000", TextStyle.Faint)] // partly transparent
    public void Attributes_are_written_so_that_they_read_back_the_same(string foreground, string background, TextStyle style)
    {
        var written = A(foreground, background, style);
        var fallback = A("Gray", "Black", TextStyle.Blink | TextStyle.Reverse);

        Assert.True(ThemeFile.TryParseAttribute(ThemeFile.FormatAttribute(written), fallback, out var attribute, out var problem), problem);
        Assert.Equal(written, attribute);
    }

    public void Dispose() => ThemeCatalog.Apply(BuiltIn(ThemeCatalog.DefaultName));
}
