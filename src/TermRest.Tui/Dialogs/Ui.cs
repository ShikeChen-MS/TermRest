using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Small helpers for common modal interactions.</summary>
internal static class Ui
{
    public static void Error(IApplication app, string title, string message) =>
        MessageBox.ErrorQuery(app, title, message, "OK");

    public static void Info(IApplication app, string title, string message) =>
        MessageBox.Query(app, title, message, "OK");

    /// <summary>
    /// Shows text as written: left-aligned, line by line, so columns and "label: value" lists line up (a message box
    /// centers each line). Lines wider than the screen wrap at a space or path separator, indented. Underscores are
    /// shown as they are, not taken as hotkey markers.
    /// </summary>
    public static void ShowText(IApplication app, string title, string text)
    {
        var width = Math.Max(20, (app.Driver?.Cols ?? 120) - 8);
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').SelectMany(line => Wrap(line, width));
        using var dialog = new Dialog { Title = title };
        dialog.Add(new Label
        {
            Text = string.Join('\n', lines),
            X = 1,
            Y = 1,
            TextAlignment = Alignment.Start,
            HotKeySpecifier = new System.Text.Rune(0xFFFF),
        });
        dialog.AddButton(new Button { Text = "OK" });
        app.Run(dialog);
    }

    /// <summary>Breaks <paramref name="line"/> into pieces of at most <paramref name="width"/> characters.</summary>
    internal static IEnumerable<string> Wrap(string line, int width)
    {
        // Continuation lines are indented a little more than the line, but never so much that nothing fits.
        var indent = new string(' ', Math.Min(line.Length - line.TrimStart(' ').Length + 2, width / 4));
        while (line.Length > width)
        {
            // After the last space or path separator that fits; a hard cut when there is none in the second half.
            var cut = line.LastIndexOfAny([' ', '\\', '/'], width - 1) + 1;
            if (cut <= width / 2)
            {
                cut = width;
            }

            yield return line[..cut].TrimEnd();
            line = indent + line[cut..].TrimStart();
        }

        yield return line;
    }

    public static bool Confirm(IApplication app, string title, string message, string yes = "Yes", string no = "No") =>
        MessageBox.Query(app, title, message, no, yes) == 1;

    /// <summary>Asks for a single line of text. Returns null when cancelled.</summary>
    public static string? PromptText(IApplication app, string title, string label, string initial = "", int width = 60)
    {
        using var dialog = new Dialog { Title = title };
        var caption = new Label { Text = label, X = 1, Y = 1 };
        var field = new TextField { X = 1, Y = 2, Width = width, Text = initial };
        dialog.Add(caption, field);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });
        app.Run(dialog);
        return dialog.Canceled ? null : field.Text;
    }

    /// <summary>Shows a file picker. Returns the chosen path or null.</summary>
    public static string? PickFile(IApplication app, string title, string? startDirectory = null, bool directory = false, params (string Name, string[] Extensions)[] filters)
    {
        using var dialog = new OpenDialog
        {
            Title = title,
            AllowsMultipleSelection = false,
            OpenMode = directory ? OpenMode.Directory : OpenMode.File,
        };

        if (startDirectory is not null && Directory.Exists(startDirectory))
        {
            dialog.Path = startDirectory.EndsWith(Path.DirectorySeparatorChar) ? startDirectory : startDirectory + Path.DirectorySeparatorChar;
        }

        if (filters.Length > 0)
        {
            dialog.AllowedTypes = [.. filters.Select(f => (IAllowedType)new AllowedType(f.Name, f.Extensions)), new AllowedTypeAny()];
        }

        app.Run(dialog);
        return dialog.Canceled || dialog.FilePaths.Count == 0 ? null : dialog.FilePaths[0];
    }

    /// <summary>Shows a save-file picker. Returns the chosen path or null.</summary>
    public static string? PickSaveFile(IApplication app, string title, string? startDirectory = null)
    {
        using var dialog = new SaveDialog { Title = title };
        if (startDirectory is not null && Directory.Exists(startDirectory))
        {
            dialog.Path = startDirectory.EndsWith(Path.DirectorySeparatorChar) ? startDirectory : startDirectory + Path.DirectorySeparatorChar;
        }

        app.Run(dialog);
        return dialog.Canceled || string.IsNullOrEmpty(dialog.FileName) ? null : dialog.Path;
    }

    public static bool CopyToClipboard(IApplication? app, string text) =>
        app?.Clipboard is { IsSupported: true } clipboard && clipboard.TrySetClipboardData(text);

    /// <summary>Label + field row helper for forms.</summary>
    public static T AddRow<T>(View container, string label, T field, int y, int labelWidth = 18)
        where T : View
    {
        container.Add(new Label { Text = label, X = 1, Y = y, Width = labelWidth });
        field.X = labelWidth + 2;
        field.Y = y;
        if (field.Width is null or DimAbsolute { Size: 0 })
        {
            field.Width = Dim.Fill(1);
        }

        container.Add(field);
        return field;
    }

    /// <summary>
    /// Runs <paramref name="action"/> when a button inside a dialog is pressed, and only that: a press the button does
    /// not handle would also press the dialog's default button (Save, OK) and close it.
    /// </summary>
    public static void OnPress(Button button, Action action) => button.Accepting += (_, e) =>
    {
        e.Handled = true;
        action();
    };
}
