using System.Text;
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
        var value = new TextInput(dialog, "Value", entry.Value, ref y);
        value.Mask(entry.IsSecret);
        var description = new TextInput(dialog, "Description", entry.Description, ref y);
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
            Ui.OnPress(browse, () =>
            {
                if (Ui.PickFile(app, "Choose file") is { } path)
                {
                    files.Text = path;
                    partType.Value = 1;
                }
            });
            dialog.Add(browse);
            y += 2;
            contentType = Ui.AddRow(dialog, "Content-Type", new TextField { Text = entry.ContentType ?? "" }, y);
            y += 2;
        }
        else if (kind == KeyValueKind.Variables)
        {
            secret = Ui.AddRow(dialog, "Secret", new CheckBox { Text = "mask value", Value = entry.IsSecret ? CheckState.Checked : CheckState.UnChecked, Width = 14 }, y);
            secret.ValueChanged += (_, _) => value.Mask(secret.Value == CheckState.Checked);
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

        // Fields left alone keep what they had: line breaks, or no value at all (?flag).
        entry.Key = key.Text.Trim();
        if (value.Edited)
        {
            entry.Value = value.Text;
        }

        if (description.Edited)
        {
            entry.Description = string.IsNullOrWhiteSpace(description.Text) ? null : description.Text;
        }

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

    /// <summary>
    /// A value's editor: a text field, or a text area for a value with line breaks or tabs (a text field keeps its first
    /// line only, without tabs). <see cref="Edited"/> tells whether the user changed it.
    /// </summary>
    private sealed class TextInput
    {
        private readonly TextField? _field;
        private readonly TextArea? _area;
        private readonly Action<bool> _mask;
        private readonly string _shown;

        public TextInput(View dialog, string label, string? text, ref int y)
        {
            if (text is not null && text.AsSpan().ContainsAny('\r', '\n', '\t'))
            {
                var area = _area = Ui.AddRow(dialog, label, new TextArea { Width = Dim.Fill(1), Height = 5 }, y);
                area.SetText(text);

                // A text area cannot mask its text: a secret's is hidden.
                var hidden = new Label { Text = "•••••• (untick Secret to see it)", X = area.X, Y = y, Visible = false };
                dialog.Add(hidden);
                _mask = masked => (area.Visible, hidden.Visible) = (!masked, masked);
                y += 6;
            }
            else
            {
                var field = _field = Ui.AddRow(dialog, label, new TextField { Text = text ?? "" }, y);
                _mask = masked => field.Secret = masked;
                y += 2;
            }

            _shown = Text;
        }

        public string Text => _field?.Text ?? _area!.GetText();

        public bool Edited => Text != _shown;

        /// <summary>Masks the value (a secret).</summary>
        public void Mask(bool masked) => _mask(masked);
    }
}

/// <summary>
/// Postman-style bulk edit: one <c>key:value</c> per line, <c>//</c> prefix for disabled rows. A value with line breaks
/// shows them as <c>\n</c> (and its backslashes as <c>\\</c>), files are separated by <c>;</c> as in the row dialog,
/// and secret values are masked.
/// </summary>
internal static class BulkEditDialog
{
    /// <summary>What a secret value shows, as in the table: left as it is, the value is kept.</summary>
    internal const string SecretMask = "••••••";

    /// <summary>The rows as edited; null when cancelled, or applied without a change.</summary>
    public static List<KeyValueEntry>? Show(IApplication app, string noun, IReadOnlyList<KeyValueEntry> entries)
    {
        using var dialog = new Dialog { Title = $"Bulk edit {noun}s", Width = Dim.Percent(80), Height = Dim.Percent(70) };
        dialog.Add(
            new Label { Text = "One per line as key:value. Prefix a line with // to disable it.", X = 1, Y = 0 },
            new Label { Text = $"Line breaks in values show as \\n. Leave {SecretMask} to keep a secret value.", X = 1, Y = 1 });
        var text = new TextArea { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        text.SetText(Format(entries));

        // As the editor shows it: Terminal.Gui keeps characters composed (NFC), so the text read back can differ unedited.
        var shown = text.GetText();
        dialog.Add(text);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Apply" });
        while (true)
        {
            app.Run(dialog);
            var edited = dialog.Canceled ? null : text.GetText();
            if (edited is null || edited == shown)
            {
                return null;
            }

            var unclear = new List<string>();
            var parsed = Parse(edited, entries, unclear);
            if (unclear.Count == 0)
            {
                return parsed;
            }

            Ui.Error(app, "Secret values", $"Which secret value {SecretMask} stands for is not clear on these lines: {string.Join(", ", unclear)}.\n" +
                "Type the value, or leave the line as it was (with its key).");
        }
    }

    public static string Format(IEnumerable<KeyValueEntry> entries) =>
        string.Join('\n', entries.Select(e => $"{(e.Enabled ? "" : "//")}{e.Key}:{FormatValue(e)}"));

    /// <summary>
    /// Parses bulk text, keeping the descriptions and unknown JSON of the rows, and the values of rows that read as they
    /// were written. A line is the row it shows as written, else the row with its key; lines whose key changed are the
    /// rows that were in their place when as many rows as lines changed there, and a line still showing a secret's mask
    /// is else the one secret left. The keys of lines that show the mask for no secret go to <paramref name="unclear"/>
    /// (the mask is their value then).
    /// </summary>
    public static List<KeyValueEntry> Parse(string text, IReadOnlyList<KeyValueEntry> previous, List<string>? unclear = null)
    {
        var texts = new List<(bool Enabled, string Text)>();
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

            texts.Add((enabled, line));
        }

        var lines = Split(texts, previous);

        // A row is the one a line shows as written (rows may share a key), else the one with its key.
        var taken = new bool[previous.Count];
        var matches = lines.Select(line => line.Row).ToArray();
        foreach (var row in matches.Where(row => row >= 0))
        {
            taken[row] = true;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = 0; j < previous.Count && matches[i] < 0; j++)
            {
                if (!taken[j] && previous[j].Key == lines[i].Key)
                {
                    matches[i] = j;
                    taken[j] = true;
                }
            }
        }

        // Renamed rows: the lines between two rows that kept their key are the rows that were between them, when there are
        // as many of each (a row removed there and another renamed would not say which is which).
        for (int i = 0, first = 0, after = -1; i <= lines.Count; i++)
        {
            if (i < lines.Count && matches[i] < 0)
            {
                continue;
            }

            var before = i < lines.Count ? matches[i] : previous.Count;
            var rows = Enumerable.Range(after + 1, Math.Max(0, before - after - 1)).Where(j => !taken[j]).ToList();
            if (rows.Count == i - first)
            {
                for (var k = 0; k < rows.Count; k++)
                {
                    matches[first + k] = rows[k];
                    taken[rows[k]] = true;
                }
            }

            first = i + 1;
            after = before;
        }

        // A renamed secret still showing its mask elsewhere: the secret left, when it is the only one.
        var masks = Enumerable.Range(0, lines.Count).Where(i => matches[i] < 0 && lines[i].Value == SecretMask).ToList();
        var secrets = Enumerable.Range(0, previous.Count).Where(j => !taken[j] && Masked(previous[j])).ToList();
        if (masks.Count == 1 && secrets.Count == 1)
        {
            matches[masks[0]] = secrets[0];
        }

        var result = new List<KeyValueEntry>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (enabled, key, value, _) = lines[i];
            var match = matches[i] < 0 ? null : previous[matches[i]];
            var entry = match?.Clone() ?? new KeyValueEntry();
            entry.Key = key;
            entry.Enabled = enabled;

            // A row that reads as it was written keeps its value as it was (line breaks, files, secret).
            if (match is null || value != FormatValue(match))
            {
                if (value.Trim() == SecretMask)
                {
                    unclear?.Add(key);
                }

                if (entry.IsFile)
                {
                    entry.Src = string.Join('\n', value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else
                {
                    entry.Value = match is not null && !match.IsFile && !Masked(match) && HasLineBreaks(match.Value) ? Unescape(value) : value;
                }
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Splits lines into keys and values. Keys may hold ':' themselves (Api:BaseUrl), so a line is split after the key of a
    /// row: the row it shows as written, else the longest key it starts with followed by ':' (of the rows no other line
    /// was split for), else at its first ':'. <c>Row</c> is the row a line shows as written, else -1.
    /// </summary>
    private static List<(bool Enabled, string Key, string Value, int Row)> Split(List<(bool Enabled, string Text)> lines, IReadOnlyList<KeyValueEntry> rows)
    {
        var used = new bool[rows.Count];
        var keys = new (string Key, int ValueStart, int Row)?[lines.Count];
        var written = rows.Select(r => $"{r.Key}:{FormatValue(r)}").ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var row = Enumerable.Range(0, rows.Count).FirstOrDefault(j => !used[j] && written[j] == lines[i].Text, -1);
            if (row >= 0)
            {
                used[row] = true;
                keys[i] = (rows[row].Key, rows[row].Key.Length + 1, row);
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (keys[i] is not null)
            {
                continue;
            }

            var line = lines[i].Text;
            var start = line.Length - line.TrimStart().Length;
            var best = -1;
            for (var j = 0; j < rows.Count; j++)
            {
                var key = rows[j].Key;
                if (!used[j] && key.Length > 0 && (best < 0 || key.Length > rows[best].Key.Length)
                    && line.Length > start + key.Length && line[start + key.Length] == ':' && string.CompareOrdinal(line, start, key, 0, key.Length) == 0)
                {
                    best = j;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                keys[i] = (rows[best].Key, start + rows[best].Key.Length + 1, -1);
            }
        }

        var result = new List<(bool Enabled, string Key, string Value, int Row)>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var (enabled, line) = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            result.Add(keys[i] is { } key ? (enabled, key.Key, line[key.ValueStart..], key.Row)
                : (enabled, (colon >= 0 ? line[..colon] : line).Trim(), colon >= 0 ? line[(colon + 1)..] : "", -1));
        }

        return result;
    }

    private static string FormatValue(KeyValueEntry entry) =>
        entry.IsFile ? (entry.Src ?? "").Replace('\n', ';')
        : Masked(entry) ? SecretMask
        : HasLineBreaks(entry.Value) ? entry.Value!.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        : entry.Value ?? "";

    private static bool Masked(KeyValueEntry entry) => entry.IsSecret && !string.IsNullOrEmpty(entry.Value);

    private static bool HasLineBreaks(string? value) => value is not null && value.AsSpan().ContainsAny('\r', '\n');

    /// <summary>Turns the <c>\n</c>, <c>\r</c> and <c>\\</c> of a value written by <see cref="FormatValue"/> back into what they stand for.</summary>
    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '\\' or 'n' or 'r')
            {
                i++;
                builder.Append(value[i] switch { 'n' => '\n', 'r' => '\r', _ => '\\' });
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }
}
