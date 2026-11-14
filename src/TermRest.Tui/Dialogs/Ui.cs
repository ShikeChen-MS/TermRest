using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.IO;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Small helpers for common modal interactions.</summary>
internal static class Ui
{
    public static void Error(IApplication app, string title, string message) =>
        MessageBox.ErrorQuery(app, title, message, "OK");

    public static void Info(IApplication app, string title, string message) =>
        MessageBox.Query(app, title, message, "OK");

    /// <summary>
    /// Runs <paramref name="save"/>; false, after saying why, when a file could not be written (or held settings that
    /// could not be read, which saving would lose).
    /// </summary>
    public static bool TrySave(IApplication app, Action save)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error(app, "Save failed", ex.Message);
            return false;
        }
    }

    /// <summary>A file to save: <c>Save(overwriteChanged)</c>. Saves without a name are not listed as saved.</summary>
    public sealed record SaveItem(string? Name, Action<bool> Save);

    /// <summary>
    /// Saves each item on its own, so one that cannot be saved does not keep the others from being saved, and says once
    /// why any could not be. Files another program changed since they were read (a git pull, Postman, an editor) are
    /// overwritten only when the user says so (Cancel is the default). Returns the names saved and the names left
    /// unsaved because they changed on disk. Errors other than disk and permission problems are bugs: their details are
    /// logged where unexpected errors go (<see cref="TuiApp.LogUnexpectedError"/>).
    /// </summary>
    public static (List<string> Saved, List<string> Changed) SaveEach(IApplication app, string rootPath, IEnumerable<SaveItem> items)
    {
        var saved = new List<string>();
        var failed = new List<string>();
        var changed = new List<(SaveItem Item, IReadOnlyList<string> Paths)>();
        string? log = null;

        bool TryOne(SaveItem item, bool overwrite)
        {
            try
            {
                item.Save(overwrite);
                if (item.Name is not null)
                {
                    saved.Add(item.Name);
                }

                return true;
            }
            catch (FileChangedException ex) when (!overwrite)
            {
                changed.Add((item, ex.Paths));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(item.Name is null ? ex.Message : $"{item.Name}: {ex.Message}");
                log = LogError(ex) ?? log;
            }

            return false;
        }

        foreach (var item in items)
        {
            TryOne(item, overwrite: false);
        }

        if (changed.Count > 0 && ConfirmOverwrite(app, rootPath, changed.SelectMany(c => c.Paths)))
        {
            var overwriting = changed.ToList();
            changed.Clear();
            foreach (var (item, _) in overwriting)
            {
                TryOne(item, overwrite: true);
            }
        }

        if (failed.Count > 0)
        {
            Error(app, "Save failed", string.Join("\n\n", failed) + (log is null ? "" : $"\n\nDetails were written to {log}"));
        }

        return (saved, changed.Select(c => c.Item.Name).OfType<string>().ToList());
    }

    /// <summary>Logs an unexpected error; returns the log's path, or null when it could not be written either.</summary>
    private static string? LogError(Exception exception)
    {
        try
        {
            return TuiApp.LogUnexpectedError(exception);
        }
        catch (Exception)
        {
            return null; // the error is shown all the same
        }
    }

    private static bool ConfirmOverwrite(IApplication app, string rootPath, IEnumerable<string> paths)
    {
        const int Shown = 8;
        var all = paths.Select(p => Path.GetRelativePath(rootPath, p)).ToList();
        var files = string.Join("\n", all.Take(Shown)) + (all.Count > Shown ? $"\n…and {all.Count - Shown} more" : "");
        return MessageBox.Query(app, "Changed on disk",
            $"These files changed on disk since TermRest read them (a git pull, Postman, an editor):\n\n{files}\n\n" +
            "Saving would undo those changes. To keep them instead, choose Cancel and load them again: pick a JSON collection " +
            "again with File › Import Postman collection…, or quit without saving and start TermRest again. " +
            "Your unsaved changes to them are then lost.",
            "Overwrite", "Cancel") == 0;
    }

    /// <summary>
    /// Shows text as written: left-aligned, line by line, so columns and "label: value" lists line up (a message box
    /// centers each line). Lines wider than the screen wrap at a space or path separator, indented. Underscores are
    /// shown as they are, not taken as hotkey markers. A text taller than the screen scrolls (wheel, arrows, PgDn).
    /// </summary>
    public static void ShowText(IApplication app, string title, string text)
    {
        var width = Math.Max(20, (app.Driver?.Cols ?? 120) - 8);
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').SelectMany(line => Wrap(line, width)).ToList();
        using var dialog = new Dialog { Title = title };

        // As large as the text, with a column for the scroll bar a taller text shows, and at most as large as the screen; a
        // screen made narrower wraps the text again.
        dialog.Width = Dim.Auto(
            minimumContentDim: Math.Max(lines.Max(line => line.GetColumns()) + 3, title.GetColumns() + 4),
            maximumContentDim: Dim.Percent(100) - Dim.Func(_ => dialog.GetAdornmentsThickness().Horizontal));
        dialog.Height = Dim.Auto(
            minimumContentDim: lines.Count + 1,
            maximumContentDim: Dim.Percent(100) - Dim.Func(_ => dialog.GetAdornmentsThickness().Vertical));
        var area = new TextArea { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(), ReadOnly = true, WordWrap = true };
        area.SetText(string.Join('\n', lines));
        dialog.Add(area);
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
                // Not inside a character: half of one can't be shown.
                cut = width > 1 && char.IsHighSurrogate(line[width - 1]) ? width - 1 : width;
            }

            yield return line[..cut].TrimEnd();
            line = indent + line[cut..].TrimStart();
        }

        yield return line;
    }

    /// <summary>
    /// <paramref name="text"/> with U+FFFD for each character outside the BMP (most emoji), for Terminal.Gui views that
    /// cut text by UTF-16 index and fail on half of a pair: the tree draws each UTF-16 unit of a line in a cell of its own,
    /// and a list scrolled sideways cuts its rows at the column scrolled to.
    /// </summary>
    public static string Drawable(string text)
    {
        if (!text.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            return text;
        }

        var drawable = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            drawable.Append(rune.IsBmp ? (char)rune.Value : '\uFFFD');
        }

        return drawable.ToString();
    }

    public static bool Confirm(IApplication app, string title, string message, string yes = "Yes", string no = "No") =>
        MessageBox.Query(app, title, message, no, yes) == 1;

    /// <summary>
    /// For <see cref="View.HotKeySpecifier"/>: no hotkey, so underscores show as they are. Names, paths and messages
    /// must be shown so: else the first underscore (dev_local) is hidden and the letter after it becomes a key that
    /// presses something. A frame or dialog sizes its title as it is set: set this before such a title.
    /// </summary>
    public static readonly System.Text.Rune NoHotKey = new(0xFFFF);

    /// <summary>
    /// Makes a status bar or menu item show its title as written (see <see cref="NoHotKey"/>): the item, and the
    /// command view that shows its title.
    /// </summary>
    public static T AsWritten<T>(T shortcut)
        where T : Shortcut
    {
        shortcut.HotKeySpecifier = NoHotKey;
        shortcut.CommandView?.HotKeySpecifier = NoHotKey;
        return shortcut;
    }

    /// <summary>A label that shows <paramref name="text"/> as written: underscores (HTTPS_PROXY) are not hotkey markers.</summary>
    public static Label Hint(string text, Pos x, Pos y) => new() { Text = text, X = x, Y = y, HotKeySpecifier = NoHotKey };

    /// <summary>Asks for a single line of text, without the spaces around it (spaces alone are no name). Returns null when cancelled.</summary>
    public static string? PromptText(IApplication app, string title, string label, string initial = "", int width = 60)
    {
        using var dialog = new Dialog { Title = title };

        // The label may hold a name: a collection's, a folder's, a theme's.
        var caption = new Label { Text = label, X = 1, Y = 1, HotKeySpecifier = NoHotKey };
        var field = new TextField { X = 1, Y = 2, Width = width, Text = initial };
        dialog.Add(caption, field);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });
        app.Run(dialog);
        return dialog.Canceled ? null : field.Text.Trim();
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
