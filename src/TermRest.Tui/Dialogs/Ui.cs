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
}
