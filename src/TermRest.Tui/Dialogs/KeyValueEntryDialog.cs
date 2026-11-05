using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Model;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Edits a single key/value row (with form-data file and environment secret options).</summary>
internal static class KeyValueEntryDialog
{
    public static KeyValueEntry? Show(IApplication app, string title, KeyValueEntry entry, KeyValueKind kind)
    {
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(70) };
        var y = 1;
        var key = Ui.AddRow(dialog, "Key", new TextField { Text = entry.Key, ReadOnly = kind == KeyValueKind.PathVariables }, y);
        y += 2;
        var value = Ui.AddRow(dialog, "Value", new TextField { Text = entry.Value ?? "", Secret = entry.IsSecret }, y);
        y += 2;
        var description = Ui.AddRow(dialog, "Description", new TextField { Text = entry.Description ?? "" }, y);
        y += 2;
        var enabled = Ui.AddRow(dialog, "Enabled", new CheckBox { Text = "", Value = entry.Enabled ? CheckState.Checked : CheckState.UnChecked, Width = 6 }, y);
        y += 2;

        OptionSelector? partType = null;
        TextField? files = null;
        TextField? contentType = null;
        CheckBox? secret = null;
        if (kind == KeyValueKind.FormData)
        {
            partType = Ui.AddRow(dialog, "Type", new OptionSelector { Labels = ["Text", "File"], Orientation = Orientation.Horizontal, Value = entry.IsFile ? 1 : 0 }, y);
            y += 2;
            files = Ui.AddRow(dialog, "File(s)", new TextField { Text = (entry.Src ?? "").Replace('\n', ';'), Width = Dim.Fill(14) }, y);
            var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = y };
            browse.Accepted += (_, _) =>
            {
                if (Ui.PickFile(app, "Choose file") is { } path)
                {
                    files.Text = path;
                    partType.Value = 1;
                }
            };
            dialog.Add(browse);
            y += 2;
            contentType = Ui.AddRow(dialog, "Content-Type", new TextField { Text = entry.ContentType ?? "" }, y);
            y += 2;
        }
        else if (kind == KeyValueKind.Variables)
        {
            secret = Ui.AddRow(dialog, "Secret", new CheckBox { Text = "mask value", Value = entry.IsSecret ? CheckState.Checked : CheckState.UnChecked, Width = 14 }, y);
            secret.ValueChanged += (_, _) => value.Secret = secret.Value == CheckState.Checked;
            y += 2;
        }

        dialog.Add(new Label { Text = "Values may use {{variables}}.", X = 1, Y = y });
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });
        app.Run(dialog);
        if (dialog.Canceled)
        {
            return null;
        }

        entry.Key = key.Text.Trim();
        entry.Value = value.Text;
        entry.Description = string.IsNullOrWhiteSpace(description.Text) ? null : description.Text;
        entry.Enabled = enabled.Value == CheckState.Checked;
        if (partType is not null)
        {
            entry.Type = partType.Value == 1 ? "file" : "text";
            entry.Src = entry.IsFile ? string.Join('\n', files!.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : null;
            entry.ContentType = string.IsNullOrWhiteSpace(contentType!.Text) ? null : contentType.Text.Trim();
        }
        else if (secret is not null)
        {
            entry.Type = secret.Value == CheckState.Checked ? "secret" : "default";
        }

        return entry.Key.Length == 0 && string.IsNullOrEmpty(entry.Value) ? null : entry;
    }
}

/// <summary>Postman-style bulk edit: one <c>key:value</c> per line, <c>//</c> prefix for disabled rows.</summary>
internal static class BulkEditDialog
{
    public static List<KeyValueEntry>? Show(IApplication app, string noun, IReadOnlyList<KeyValueEntry> entries)
    {
        using var dialog = new Dialog { Title = $"Bulk edit {noun}s", Width = Dim.Percent(80), Height = Dim.Percent(70) };
        dialog.Add(new Label { Text = "One per line as key:value. Prefix a line with // to disable it.", X = 1, Y = 0 });
        var text = new TextArea { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        text.SetText(Format(entries));
        dialog.Add(text);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Apply" });
        app.Run(dialog);
        return dialog.Canceled ? null : Parse(text.GetText(), entries);
    }

    public static string Format(IEnumerable<KeyValueEntry> entries) =>
        string.Join('\n', entries.Select(e => $"{(e.Enabled ? "" : "//")}{e.Key}:{(e.IsFile ? e.Src : e.Value)}"));

    /// <summary>Parses bulk text, keeping descriptions and unknown JSON of rows whose key did not change.</summary>
    public static List<KeyValueEntry> Parse(string text, IReadOnlyList<KeyValueEntry> previous)
    {
        var result = new List<KeyValueEntry>();
        var pool = previous.ToList();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var enabled = true;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                enabled = false;
                line = line.TrimStart()[2..];
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var key = (colon >= 0 ? line[..colon] : line).Trim();
            var value = colon >= 0 ? line[(colon + 1)..] : "";
            var match = pool.FirstOrDefault(p => p.Key == key);
            KeyValueEntry entry;
            if (match is not null)
            {
                pool.Remove(match);
                entry = match.Clone();
            }
            else
            {
                entry = new KeyValueEntry { Key = key };
            }

            entry.Enabled = enabled;
            if (entry.IsFile)
            {
                entry.Src = value.Trim();
            }
            else
            {
                entry.Value = value;
            }

            result.Add(entry);
        }

        return result;
    }
}
