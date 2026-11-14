using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Scripting;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;
using TermRest.Tui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermRest.Tui.Dialogs;

/// <summary>
/// Color theme picker: every theme is previewed live on the whole UI while moving through the list. The choice is saved
/// for the user (all workspaces) or for this workspace only.
/// </summary>
internal static class ThemeDialog
{
    private static readonly string[] SampleMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    /// <summary>A script with every kind of script color.</summary>
    private const string SampleScript = "# Script colors\n[int]$expected = 200\nif ($tr.Response.Status -eq $expected) { Write-Host \"ok\" -NoNewline }";

    /// <summary>Returns the theme chosen, or null when cancelled (the previous theme is restored then).</summary>
    public static ColorTheme? Show(IApplication app, ApiWorkspace workspace, string? userDirectory = null)
    {
        var original = ThemeCatalog.Current;
        var errors = new List<string>();
        var themes = ThemeCatalog.Load(workspace.RootPath, errors, userDirectory).ToList();
        var userThemes = UserSettings.ThemesDirectory(userDirectory);

        using var dialog = new Dialog { Title = "Color theme", Width = Dim.Percent(80), Height = Dim.Percent(85) };
        var names = new ObservableCollection<string>();
        var list = new ListView { X = 1, Y = 1, Width = 28, Height = Dim.Fill(4) };
        list.SetSource(names);
        var details = new TextArea { X = Pos.Right(list) + 2, Y = 1, Width = Dim.Fill(1), Height = 8, ReadOnly = true, WordWrap = true, ScrollBars = false };

        // The main-area colors with the method, status and script colors of the theme (the dialog itself shows dialog colors).
        var preview = new FrameView { Title = "Preview", X = Pos.Right(list) + 2, Y = Pos.Bottom(details), Width = Dim.Fill(1), Height = Dim.Fill(4), SchemeName = "Base" };
        var methods = SampleMethods
            .Select(m => new Label { Text = m, Y = 0 })
            .ToArray();
        for (var i = 0; i < methods.Length; i++)
        {
            methods[i].X = i == 0 ? 1 : Pos.Right(methods[i - 1]) + 2;
        }

        var success = new Label { Text = "200 OK", X = 1, Y = 2, SchemeName = ThemeCatalog.SuccessScheme };
        var warning = new Label { Text = "404 Not Found", X = Pos.Right(success) + 3, Y = 2, SchemeName = ThemeCatalog.WarningScheme };
        var failure = new Label { Text = "500 Internal Server Error", X = Pos.Right(warning) + 3, Y = 2, SchemeName = "Error" };
        var field = new TextField { Text = "An input field", X = 1, Y = 4, Width = 24 };
        var disabled = new Label { Text = "Disabled text", X = Pos.Right(field) + 3, Y = 4, Enabled = false };
        var button = new Button { Text = "Button", X = 1, Y = 6 };
        var script = new TextArea { X = 1, Y = 8, Width = Dim.Fill(1), Height = 3, CanFocus = false, ScrollBars = false, SyntaxLanguage = ScriptLanguage.PowerShell };
        script.SetText(SampleScript);
        preview.Add([.. methods, success, warning, failure, field, disabled, button, script]);

        var workspaceOnly = new CheckBox
        {
            Text = "Use only in this workspace (saved in .termrest/settings.json)",
            X = 1,
            Y = Pos.AnchorEnd(3),
            Value = workspace.Settings.Theme is null ? CheckState.UnChecked : CheckState.Checked,
        };
        var folderLabel = new Label { Text = "Custom themes:", X = 1, Y = Pos.AnchorEnd(2) };
        var folder = new TextField { Text = userThemes, ReadOnly = true, X = Pos.Right(folderLabel) + 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1) };

        ColorTheme Selected() => themes[Math.Clamp(list.Value ?? 0, 0, themes.Count - 1)];

        void Fill(string? select)
        {
            names.Clear();
            foreach (var theme in themes)
            {
                names.Add(Ui.Drawable(theme.IsBuiltIn ? theme.Name : $"{theme.Name} *"));
            }

            list.Value = Math.Max(0, themes.FindIndex(t => string.Equals(t.Name, select, StringComparison.OrdinalIgnoreCase)));
            Preview(Selected());
        }

        void Preview(ColorTheme theme)
        {
            ThemeCatalog.Apply(theme);
            for (var i = 0; i < methods.Length; i++)
            {
                methods[i].SetScheme(new Scheme(new Attribute(theme.Palette.MethodColor(methods[i].Text), theme.Palette.Background, TextStyle.Bold)));
            }

            var source = theme.IsBuiltIn ? "Built-in theme." : $"Custom theme: {theme.FilePath}";
            var problems = errors.Count == 0 ? "" : $"\n\n⚠ Problems in theme files:\n{string.Join('\n', errors.Select(e => "• " + e))}";
            details.SetText($"{theme.Name}\n{theme.Description}\n{source}{problems}");
            app.LayoutAndDraw(forceRedraw: true);
        }

        list.ValueChanged += (_, e) =>
        {
            if (e.NewValue is int index && index >= 0 && index < themes.Count && themes[index] != ThemeCatalog.Current)
            {
                Preview(themes[index]);
            }
        };

        var copy = new Button { Text = "Copy as custom theme…", X = 1, Y = Pos.AnchorEnd(1) };
        Ui.OnPress(copy, () =>
        {
            var from = Selected();
            if (Ui.PromptText(app, "Copy as custom theme", $"Name of the new theme (a copy of {from.Name} you can edit)", ThemeCatalog.FreeName(themes, $"My {from.Name}")) is not { Length: > 0 } name)
            {
                return;
            }

            if (ThemeCatalog.FindCustom(themes, name) is { } taken)
            {
                Ui.Error(app, "Copy failed", $"A custom theme called '{taken.Name}' exists already ({taken.FilePath}): choose another name.");
                return;
            }

            try
            {
                var path = ThemeCatalog.WriteCopy(from, name, userThemes);
                errors.Clear();
                themes = [.. ThemeCatalog.Load(workspace.RootPath, errors, userDirectory)];
                Fill(name.Trim());
                Ui.Info(app, "Custom theme created",
                    $"{path}\n\nEdit its palette colors (names such as DarkBlue, #RRGGBB, or None) in any editor, then press Reload here.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ui.Error(app, "Copy failed", ex.Message);
            }
        });
        var reload = new Button { Text = "Reload theme files", X = Pos.Right(copy) + 1, Y = Pos.AnchorEnd(1) };
        Ui.OnPress(reload, () =>
        {
            var name = Selected().Name;
            errors.Clear();
            themes = [.. ThemeCatalog.Load(workspace.RootPath, errors, userDirectory)];
            Fill(name);
        });

        dialog.Add(new Label { Text = "Themes (* custom)", X = 1, Y = 0 }, list, details, preview, workspaceOnly, folderLabel, folder, copy, reload);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Use theme" });
        Fill(original.Name);
        list.SetFocus();
        ColorTheme? chosen = null;
        try
        {
            app.Run(dialog);
            chosen = dialog.Canceled ? null : Selected();
        }
        finally
        {
            // Cancelled, or ended by an unexpected error: the theme shown before comes back, not the one previewed last.
            ThemeCatalog.Apply(chosen ?? original);
        }

        if (chosen is not null)
        {
            Ui.TrySave(app, () => Save(workspace, chosen, workspaceOnly.Value == CheckState.Checked, userDirectory));
        }

        return chosen;
    }

    /// <summary>Remembers the theme for this workspace, or for the user (dropping this workspace's own choice).</summary>
    internal static void Save(ApiWorkspace workspace, ColorTheme theme, bool workspaceOnly, string? userDirectory = null)
    {
        var name = string.Equals(theme.Name, ThemeCatalog.DefaultName, StringComparison.OrdinalIgnoreCase) ? null : theme.Name;
        if (workspaceOnly)
        {
            workspace.Settings.Theme = theme.Name;
            workspace.SaveSettings();
            return;
        }

        var user = UserSettings.Load(userDirectory);
        user.Theme = name;
        user.Save(userDirectory);
        if (workspace.Settings.Theme is not null)
        {
            workspace.Settings.Theme = null;
            workspace.SaveSettings();
        }
    }
}
