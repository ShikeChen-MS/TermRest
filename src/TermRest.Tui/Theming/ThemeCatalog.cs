using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using TermRest.Core.Workspace;

namespace TermRest.Tui.Theming;

/// <summary>
/// The color themes of the full-screen UI and the one in use. Built in are Terminal.Gui's themes (Default, Dark, Light,
/// TurboPascal 5 …) and TermRest's palettes (Dracula, Nord, Solarized …). Custom themes are JSON files (see
/// <see cref="ThemeFile"/>) in the user's themes folder (<see cref="UserSettings.ThemesDirectory"/>) or in a workspace's
/// <c>.termrest/themes</c> folder; a custom theme replaces a built-in one with the same name.
/// </summary>
public static class ThemeCatalog
{
    public const string DefaultName = "Default";

    /// <summary>Overrides the configured theme for one session (like the <c>--theme</c> option).</summary>
    public const string EnvironmentVariable = "TERMREST_THEME";

    /// <summary>Scheme of successful response statuses (registered with every theme).</summary>
    public const string SuccessScheme = "TermRest.Success";

    /// <summary>Scheme of 4xx response statuses (registered with every theme).</summary>
    public const string WarningScheme = "TermRest.Warning";

    private static readonly string[] TerminalGuiSchemeNames = ["Base", "Menu", "Dialog", "Error", "Accent"];

    /// <summary>Terminal.Gui's themes as shipped, captured before TermRest replaces any of their schemes.</summary>
    private static readonly Lazy<Dictionary<string, Dictionary<string, Scheme>>> TerminalGuiThemes = new(CaptureTerminalGuiThemes);

    private static readonly Lazy<IReadOnlyList<ColorTheme>> BuiltInThemes = new(CreateBuiltInThemes);

    private static ColorTheme? _current;

    /// <summary>The built-in themes, <see cref="DefaultName"/> first.</summary>
    public static IReadOnlyList<ColorTheme> BuiltIn => BuiltInThemes.Value;

    /// <summary>The theme in use (Default until one is applied).</summary>
    public static ColorTheme Current => _current ?? BuiltIn[0];

    /// <summary>Raised after a theme was applied.</summary>
    public static event EventHandler? Changed;

    /// <summary>The workspace's own themes folder (<c>.termrest/themes</c>), shared with the workspace.</summary>
    public static string WorkspaceThemesDirectory(string workspaceRoot) => Path.Combine(workspaceRoot, ApiWorkspace.ConfigDirectoryName, "themes");

    /// <summary>
    /// Built-in themes plus the theme files of the user (<paramref name="userDirectory"/>: the configuration folder,
    /// default <see cref="UserSettings.DefaultDirectory"/>) and of the workspace. Problems are added to <paramref name="errors"/>.
    /// </summary>
    public static IReadOnlyList<ColorTheme> Load(string? workspaceRoot, ICollection<string> errors, string? userDirectory = null)
    {
        var files = ThemeFile.ReadDirectory(UserSettings.ThemesDirectory(userDirectory), errors);
        if (workspaceRoot is not null)
        {
            files.AddRange(ThemeFile.ReadDirectory(WorkspaceThemesDirectory(workspaceRoot), errors));
        }

        var themes = new List<ColorTheme>(BuiltIn);

        // A theme can start from another custom theme: resolve them in dependency order.
        var pending = files.Select(f => (f.File, f.Path, Name: NameOf(f.File, f.Path))).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FindIndex(p => p.File.Base is not { Length: > 0 } baseName
                                               || (Find(themes, baseName) is not null && !pending.Any(other => other != p && Same(other.Name, baseName))));
            if (ready < 0)
            {
                var missing = pending.FindIndex(p => Find(themes, p.File.Base) is null);
                if (missing >= 0)
                {
                    // Missing or circular bases: start those from Default.
                    var (file, path, _) = pending[missing];
                    errors.Add($"{Path.GetFileName(path)}: base theme '{file.Base}' was not found; using {DefaultName}");
                    file.Base = DefaultName;
                    continue;
                }

                // Every base exists, but each waits for another file that replaces it (as after copying Default as
                // "Default" twice): build them in file order, each on the theme as it is so far.
                ready = 0;
            }

            var (readyFile, readyPath, _) = pending[ready];
            pending.RemoveAt(ready);
            var baseTheme = Find(themes, readyFile.Base) ?? BuiltIn[0];
            var theme = readyFile.ToTheme(readyPath, baseTheme, errors);
            var existing = themes.FindIndex(t => Same(t.Name, theme.Name));
            if (existing >= 0)
            {
                themes[existing] = theme;
            }
            else
            {
                themes.Add(theme);
            }
        }

        return themes;
    }

    public static ColorTheme? Find(IEnumerable<ColorTheme> themes, string? name) =>
        name is null ? null : themes.LastOrDefault(t => Same(t.Name, name));

    /// <summary>
    /// The custom theme called <paramref name="name"/>, if there is one. A copy must not take its name: only one theme of a
    /// name is loaded, the one read last, so the other would never show.
    /// </summary>
    public static ColorTheme? FindCustom(IEnumerable<ColorTheme> themes, string name) =>
        themes.LastOrDefault(t => !t.IsBuiltIn && Same(t.Name, name.Trim()));

    /// <summary><paramref name="name"/>, or the first of "name 2", "name 3" … that no custom theme has.</summary>
    public static string FreeName(IReadOnlyList<ColorTheme> themes, string name)
    {
        var candidate = name;
        for (var i = 2; FindCustom(themes, candidate) is not null; i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }

    /// <summary>
    /// The theme to use: <paramref name="requested"/> (the <c>--theme</c> option), else <c>$TERMREST_THEME</c>, else the
    /// workspace's theme, else the user's; null for <see cref="DefaultName"/>.
    /// </summary>
    public static string? SelectedName(string? requested, WorkspaceSettings? workspace, UserSettings? user) =>
        new[] { requested, Environment.GetEnvironmentVariable(EnvironmentVariable), workspace?.Theme, user?.Theme }
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();

    /// <summary>The theme named <paramref name="name"/>, or Default (reporting the missing theme).</summary>
    public static ColorTheme Resolve(string? name, IReadOnlyList<ColorTheme> themes, ICollection<string> errors)
    {
        if (name is null)
        {
            return Find(themes, DefaultName) ?? BuiltIn[0];
        }

        if (Find(themes, name) is { } theme)
        {
            return theme;
        }

        errors.Add($"Color theme '{name}' was not found; using {DefaultName}. Themes: {string.Join(", ", themes.Select(t => t.Name))}");
        return Find(themes, DefaultName) ?? BuiltIn[0];
    }

    /// <summary>Makes <paramref name="theme"/> the UI's colors (views pick them up when they are drawn next).</summary>
    public static void Apply(ColorTheme theme)
    {
        _ = TerminalGuiThemes.Value; // capture Terminal.Gui's schemes before they are first replaced
        var manager = TuiConfigurationBuilder.Shared.ThemeManager;
        if (!Same(manager.CurrentThemeName, theme.TerminalGuiTheme) && !manager.SwitchTheme(theme.TerminalGuiTheme))
        {
            manager.SwitchTheme(DefaultName);
        }

        foreach (var (name, scheme) in theme.Schemes)
        {
            SchemeManager.AddScheme(name, new Scheme(scheme));
        }

        _current = theme;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Applies Default unless a theme was applied already (so TermRest's own schemes always exist).</summary>
    public static void EnsureApplied()
    {
        if (_current is null)
        {
            Apply(BuiltIn[0]);
        }
    }

    /// <summary>
    /// Writes a copy of <paramref name="theme"/> named <paramref name="name"/> to <paramref name="directory"/>; returns the
    /// file. Its name is valid on every OS and unique ignoring case, as a workspace's themes are shared through Git.
    /// </summary>
    public static string WriteCopy(ColorTheme theme, string name, string directory)
    {
        Directory.CreateDirectory(directory);
        var safe = name.Trim().Length == 0 ? "theme" : ApiWorkspace.SafeFileName(name);
        var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var file = safe + ".json";
        for (var i = 2; taken.Contains(file); i++)
        {
            file = $"{safe}-{i}.json";
        }

        var path = Path.Combine(directory, file);
        File.WriteAllText(path, ThemeFile.CopyOf(theme, name.Trim()).ToJson());
        return path;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string NameOf(ThemeFile file, string path) =>
        string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(path) : file.Name.Trim();

    private static Dictionary<string, Dictionary<string, Scheme>> CaptureTerminalGuiThemes()
    {
        var manager = TuiConfigurationBuilder.Shared.ThemeManager;
        var original = manager.CurrentThemeName;
        var themes = new Dictionary<string, Dictionary<string, Scheme>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in manager.ThemeNames)
        {
            if (!manager.SwitchTheme(name))
            {
                continue;
            }

            var schemes = SchemeManager.GetSchemesForCurrentTheme();
            if (TerminalGuiSchemeNames.All(schemes.ContainsKey))
            {
                themes[name] = TerminalGuiSchemeNames.ToDictionary(n => n, n => new Scheme(schemes[n]!), StringComparer.Ordinal);
            }
        }

        manager.SwitchTheme(original);
        if (!themes.ContainsKey(DefaultName))
        {
            var schemes = SchemeManager.GetSchemesForCurrentTheme();
            themes[DefaultName] = TerminalGuiSchemeNames.Where(schemes.ContainsKey).ToDictionary(n => n, n => new Scheme(schemes[n]!), StringComparer.Ordinal);
        }

        return themes;
    }

    private static List<ColorTheme> CreateBuiltInThemes()
    {
        var themes = new List<ColorTheme>();
        foreach (var (name, schemes) in TerminalGuiThemes.Value)
        {
            if (!TerminalGuiSchemeNames.All(schemes.ContainsKey))
            {
                continue;
            }

            var palette = ThemePalette.FromSchemes(schemes, TermRestColors(name, schemes));
            var all = schemes.ToDictionary(p => p.Key, p => new Scheme(p.Value), StringComparer.Ordinal);
            all[SuccessScheme] = palette.StatusScheme(palette.Success);
            all[WarningScheme] = palette.StatusScheme(palette.Warning);
            themes.Add(new ColorTheme { Name = name, Description = DescribeTerminalGuiTheme(name), Palette = palette, Schemes = all, TerminalGuiTheme = name });
        }

        foreach (var (name, description, palette) in Palettes())
        {
            if (themes.All(t => !Same(t.Name, name)))
            {
                themes.Add(new ColorTheme { Name = name, Description = description, Palette = palette, Schemes = palette.ToSchemes() });
            }
        }

        return [.. themes.OrderBy(t => Same(t.Name, DefaultName) ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string DescribeTerminalGuiTheme(string name) => name switch
    {
        DefaultName => "The terminal's own colors, with a light blue menu bar.",
        "Dark" => "Gray tones on the terminal's background.",
        "Light" => "For terminals with a light background.",
        "TurboPascal 5" => "Yellow on blue, like the classic DOS IDE.",
        "Anders" => "White on dark blue.",
        "Green Phosphor" => "A green monochrome CRT.",
        "Amber Phosphor" => "An amber monochrome CRT.",
        "8-Bit" => "Black and white.",
        _ => "A Terminal.Gui theme.",
    };

    /// <summary>TermRest's own colors (response statuses, HTTP methods, scripts) to suit a Terminal.Gui theme.</summary>
    private static ThemePalette TermRestColors(string name, Dictionary<string, Scheme> schemes)
    {
        if (name is "Green Phosphor" or "Amber Phosphor" or "8-Bit")
        {
            var text = schemes["Base"].Normal.Foreground;
            var monochrome = new ThemePalette { Success = text, Warning = text, Get = text, Post = text, Put = text, Patch = text, Delete = text, OtherMethods = text };

            // A phosphor screen has brightness only: comments dim, keywords bright.
            return name == "8-Bit" ? monochrome : monochrome with
            {
                SyntaxComment = schemes["Base"].Disabled.Foreground,
                SyntaxKeyword = C(name == "Green Phosphor" ? "#99ffc2" : "#ffe08a"),
            };
        }

        var background = schemes["Base"].Normal.Background;
        var light = name == "Light" || (!background.Equals(Color.None) && !background.IsDarkColor());
        var colors = light
            ? new ThemePalette
            {
                Success = C("#1b7f2a"), Warning = C("#a35c00"), Get = C("#1b7f2a"), Post = C("#a35c00"), Put = C("#1d4ed8"),
                Patch = C("#7e22ce"), Delete = C("#b91c1c"), OtherMethods = C("DimGray"),
            }
            : new ThemePalette
            {
                Success = C("BrightGreen"), Warning = C("BrightYellow"), Get = C("BrightGreen"), Post = C("BrightYellow"), Put = C("BrightCyan"),
                Patch = C("BrightMagenta"), Delete = C("BrightRed"), OtherMethods = C("Gray"),
            };
        return colors.WithSyntaxOf(SyntaxColors(schemes["Base"].Editable.Background));
    }

    /// <summary>Script colors that are easy to read on an editor's <paramref name="background"/> (None: the terminal's, taken as dark).</summary>
    private static ThemePalette SyntaxColors(Color background) =>
        (background.Equals(Color.None) ? 0 : ThemePalette.Luminance(background)) switch
        {
            // Dark: VS Code's Dark+ colors.
            < 0.05 => new ThemePalette
            {
                SyntaxComment = C("#6a9955"), SyntaxKeyword = C("#569cd6"), SyntaxString = C("#ce9178"), SyntaxNumber = C("#b5cea8"),
                SyntaxVariable = C("#9cdcfe"), SyntaxFunction = C("#dcdcaa"), SyntaxType = C("#4ec9b0"), SyntaxParameter = C("#c586c0"),
            },

            // Mid gray, like Default's and Anders' fields: light colors.
            < 0.4 => new ThemePalette
            {
                SyntaxComment = C("Silver"), SyntaxKeyword = C("Yellow"), SyntaxString = C("#a0ffa0"), SyntaxNumber = C("#ffc0ff"),
                SyntaxVariable = C("#c0ffff"), SyntaxFunction = C("#ffe0a0"), SyntaxType = C("#a0e0ff"), SyntaxParameter = C("#ffd0d0"),
            },

            // Light: VS Code's Light+ colors.
            _ => new ThemePalette
            {
                SyntaxComment = C("#008000"), SyntaxKeyword = C("#0000ff"), SyntaxString = C("#a31515"), SyntaxNumber = C("#098658"),
                SyntaxVariable = C("#001080"), SyntaxFunction = C("#795e26"), SyntaxType = C("#267f99"), SyntaxParameter = C("#af00db"),
            },
        };

    /// <summary>TermRest's palette themes.</summary>
    private static IEnumerable<(string Name, string Description, ThemePalette Palette)> Palettes()
    {
        yield return ("Dracula", "Dark purple, from the Dracula palette.", new ThemePalette
        {
            Background = C("#282a36"), Text = C("#f8f8f2"), Muted = C("#6272a4"), Accent = C("#ff79c6"),
            Selection = C("#bd93f9"), SelectionText = C("#282a36"), Input = C("#343746"),
            Bar = C("#21222c"), BarText = C("#f8f8f2"), Dialog = C("#343746"), DialogText = C("#f8f8f2"),
            Success = C("#50fa7b"), Warning = C("#ffb86c"), Error = C("#ff5555"),
            Get = C("#50fa7b"), Post = C("#ffb86c"), Put = C("#8be9fd"), Patch = C("#bd93f9"), Delete = C("#ff5555"), OtherMethods = C("#f1fa8c"),
            SyntaxComment = C("#6272a4"), SyntaxKeyword = C("#ff79c6"), SyntaxString = C("#f1fa8c"), SyntaxNumber = C("#bd93f9"),
            SyntaxVariable = C("#bd93f9"), SyntaxFunction = C("#50fa7b"), SyntaxType = C("#8be9fd"), SyntaxParameter = C("#ffb86c"),
        });
        yield return ("Gruvbox Dark", "Warm retro colors, from the Gruvbox palette.", new ThemePalette
        {
            Background = C("#282828"), Text = C("#ebdbb2"), Muted = C("#928374"), Accent = C("#fabd2f"),
            Selection = C("#458588"), SelectionText = C("#fbf1c7"), Input = C("#3c3836"),
            Bar = C("#3c3836"), BarText = C("#ebdbb2"), Dialog = C("#3c3836"), DialogText = C("#ebdbb2"),
            Success = C("#b8bb26"), Warning = C("#fabd2f"), Error = C("#fb4934"),
            Get = C("#b8bb26"), Post = C("#fe8019"), Put = C("#83a598"), Patch = C("#d3869b"), Delete = C("#fb4934"), OtherMethods = C("#8ec07c"),
            SyntaxComment = C("#928374"), SyntaxKeyword = C("#fb4934"), SyntaxString = C("#b8bb26"), SyntaxNumber = C("#d3869b"),
            SyntaxVariable = C("#83a598"), SyntaxFunction = C("#8ec07c"), SyntaxType = C("#fabd2f"), SyntaxParameter = C("#fe8019"),
        });
        yield return ("High Contrast", "Maximum contrast: white on black, yellow focus.", new ThemePalette
        {
            Background = C("Black"), Text = C("White"), Muted = C("#b0b0b0"), Accent = C("BrightCyan"),
            Selection = C("BrightYellow"), SelectionText = C("Black"), Input = C("#1c1c1c"),
            Bar = C("Black"), BarText = C("White"), Dialog = C("Black"), DialogText = C("White"),
            Success = C("BrightGreen"), Warning = C("BrightYellow"), Error = C("BrightRed"),
            Get = C("BrightGreen"), Post = C("BrightYellow"), Put = C("BrightCyan"), Patch = C("BrightMagenta"), Delete = C("BrightRed"), OtherMethods = C("White"),
            SyntaxComment = C("#b0b0b0"), SyntaxKeyword = C("#5fd7ff"), SyntaxString = C("#87ff87"), SyntaxNumber = C("#ff87ff"),
            SyntaxVariable = C("#ffaf5f"), SyntaxFunction = C("#ffff5f"), SyntaxType = C("#5fffd7"), SyntaxParameter = C("#d7afff"),
        });
        yield return ("Monokai", "The classic Monokai editor colors.", new ThemePalette
        {
            Background = C("#272822"), Text = C("#f8f8f2"), Muted = C("#75715e"), Accent = C("#e6db74"),
            Selection = C("#66d9ef"), SelectionText = C("#272822"), Input = C("#3e3d32"),
            Bar = C("#3e3d32"), BarText = C("#f8f8f2"), Dialog = C("#3e3d32"), DialogText = C("#f8f8f2"),
            Success = C("#a6e22e"), Warning = C("#e6db74"), Error = C("#f92672"),
            Get = C("#a6e22e"), Post = C("#fd971f"), Put = C("#66d9ef"), Patch = C("#ae81ff"), Delete = C("#f92672"), OtherMethods = C("#e6db74"),
            SyntaxComment = C("#75715e"), SyntaxKeyword = C("#f92672"), SyntaxString = C("#e6db74"), SyntaxNumber = C("#ae81ff"),
            SyntaxVariable = C("#ae81ff"), SyntaxFunction = C("#a6e22e"), SyntaxType = C("#66d9ef"), SyntaxParameter = C("#fd971f"),
        });
        yield return ("Nord", "Cool arctic blues, from the Nord palette.", new ThemePalette
        {
            Background = C("#2e3440"), Text = C("#d8dee9"), Muted = C("#616e88"), Accent = C("#88c0d0"),
            Selection = C("#5e81ac"), SelectionText = C("#eceff4"), Input = C("#3b4252"),
            Bar = C("#3b4252"), BarText = C("#e5e9f0"), Dialog = C("#3b4252"), DialogText = C("#e5e9f0"),
            Success = C("#a3be8c"), Warning = C("#ebcb8b"), Error = C("#bf616a"),
            Get = C("#a3be8c"), Post = C("#ebcb8b"), Put = C("#81a1c1"), Patch = C("#b48ead"), Delete = C("#bf616a"), OtherMethods = C("#8fbcbb"),
            SyntaxComment = C("#616e88"), SyntaxKeyword = C("#81a1c1"), SyntaxString = C("#a3be8c"), SyntaxNumber = C("#b48ead"),
            SyntaxVariable = C("#ebcb8b"), SyntaxFunction = C("#88c0d0"), SyntaxType = C("#8fbcbb"), SyntaxParameter = C("#d08770"),
        });
        yield return ("Solarized Dark", "Ethan Schoonover's Solarized, dark.", new ThemePalette
        {
            Background = C("#002b36"), Text = C("#839496"), Muted = C("#586e75"), Accent = C("#b58900"),
            Selection = C("#268bd2"), SelectionText = C("#fdf6e3"), Input = C("#073642"),
            Bar = C("#073642"), BarText = C("#93a1a1"), Dialog = C("#073642"), DialogText = C("#93a1a1"),
            Success = C("#859900"), Warning = C("#b58900"), Error = C("#dc322f"),
            Get = C("#859900"), Post = C("#b58900"), Put = C("#268bd2"), Patch = C("#6c71c4"), Delete = C("#dc322f"), OtherMethods = C("#2aa198"),
            SyntaxComment = C("#586e75"), SyntaxKeyword = C("#859900"), SyntaxString = C("#2aa198"), SyntaxNumber = C("#d33682"),
            SyntaxVariable = C("#6c71c4"), SyntaxFunction = C("#268bd2"), SyntaxType = C("#b58900"), SyntaxParameter = C("#cb4b16"),
        });
        yield return ("Solarized Light", "Ethan Schoonover's Solarized, light.", new ThemePalette
        {
            Background = C("#fdf6e3"), Text = C("#657b83"), Muted = C("#93a1a1"), Accent = C("#268bd2"),
            Selection = C("#268bd2"), SelectionText = C("#fdf6e3"), Input = C("#eee8d5"),
            Bar = C("#eee8d5"), BarText = C("#586e75"), Dialog = C("#eee8d5"), DialogText = C("#586e75"),
            Success = C("#859900"), Warning = C("#cb4b16"), Error = C("#dc322f"),
            Get = C("#859900"), Post = C("#cb4b16"), Put = C("#268bd2"), Patch = C("#6c71c4"), Delete = C("#dc322f"), OtherMethods = C("#2aa198"),
            SyntaxComment = C("#93a1a1"), SyntaxKeyword = C("#859900"), SyntaxString = C("#2aa198"), SyntaxNumber = C("#d33682"),
            SyntaxVariable = C("#6c71c4"), SyntaxFunction = C("#268bd2"), SyntaxType = C("#b58900"), SyntaxParameter = C("#cb4b16"),
        });
    }

    private static Color C(string text) => Color.Parse(text);
}
