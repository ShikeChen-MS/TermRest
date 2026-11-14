using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Model;
using TermRest.Tui.Dialogs;

namespace TermRest.Tui.Views;

internal enum KeyValueKind
{
    QueryParams,
    PathVariables,
    Headers,
    UrlEncoded,
    FormData,
    Variables,
}

/// <summary>
/// Mouse-friendly key/value grid: a checkbox column toggles rows on/off (click or Space), double-click or Enter edits a
/// row, Delete removes it, and "Bulk edit" switches to Postman's <c>key:value</c> text format.
/// </summary>
internal sealed class KeyValueTable : View
{
    private readonly KeyValueKind _kind;
    private readonly TableView _table;
    private readonly EntryRows _rows;
    private readonly CheckBoxTableSourceWrapperByObject<KeyValueEntry> _checkBoxes;
    private readonly Button _add;
    private readonly Button _remove;
    private List<KeyValueEntry> _entries = [];

    public KeyValueTable(KeyValueKind kind)
    {
        _kind = kind;
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            FullRowSelect = true,
            MultiSelect = false,
        };
        _table.Style.ExpandLastColumn = true;
        _table.Style.ShowHorizontalBottomLine = true;
        _table.Style.AlwaysShowHeaders = true;
        // Enter or a double-click edits the row, handled here: a dialog holding the table (Manage environments) stays open.
        _table.Accepting += (_, e) =>
        {
            e.Handled = true;
            EditSelected();
        };
        _table.KeyDown += OnTableKeyDown;
        _table.MouseEvent += OnTableMouseEvent;

        // TableView's Ctrl+N (down) would hide the window's New request; Ctrl+P (up) goes with it.
        _table.KeyBindings.Remove(Key.N.WithCtrl);
        _table.KeyBindings.Remove(Key.P.WithCtrl);

        // The checkbox column handles the table's Space key for as long as the table lives (Terminal.Gui never unsubscribes
        // it), so there is one, over rows that follow the entries. Clicks on it are handled here (OnTableMouseEvent).
        _rows = new EntryRows(this);
        _checkBoxes = new CheckBoxTableSourceWrapperByObject<KeyValueEntry>(_table, _rows, e => e.Enabled, SetEnabled);

        _add = new Button { Text = "+ Add", X = 0, Y = Pos.AnchorEnd(1), NoDecorations = true, ShadowStyle = null };
        _add.Accepted += (_, _) => AddEntry();
        var edit = new Button { Text = "Edit", X = Pos.Right(_add) + 2, Y = Pos.AnchorEnd(1), NoDecorations = true, ShadowStyle = null };
        edit.Accepted += (_, _) => EditSelected();
        _remove = new Button { Text = "Remove", X = Pos.Right(edit) + 2, Y = Pos.AnchorEnd(1), NoDecorations = true, ShadowStyle = null };
        _remove.Accepted += (_, _) => RemoveSelected();
        var bulk = new Button { Text = "Bulk edit", X = Pos.Right(_remove) + 2, Y = Pos.AnchorEnd(1), NoDecorations = true, ShadowStyle = null };
        bulk.Accepted += (_, _) => BulkEdit();
        Add(_table, _add, edit, _remove, bulk);

        if (kind == KeyValueKind.PathVariables)
        {
            // Path variable names come from :name segments in the URL.
            _add.Visible = false;
            _remove.Visible = false;
            bulk.Visible = false;
        }

        Rebind();
    }

    /// <summary>Raised after the user changes the rows.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<KeyValueEntry> Entries => _entries;

    /// <summary>Binds the table to <paramref name="entries"/>; edits are made in place on that list.</summary>
    public void SetEntries(List<KeyValueEntry> entries)
    {
        var row = _table.Value?.SelectedCell.Y ?? 0;
        _entries = entries;
        Rebind();
        if (_entries.Count > 0)
        {
            _table.SetSelection(0, Math.Clamp(row, 0, _entries.Count - 1), false);
        }
    }

    private void Rebind()
    {
        var columns = new Dictionary<string, Func<KeyValueEntry, object>>
        {
            ["Key"] = e => e.Key,
            ["Value"] = DisplayValue,
        };
        if (_kind != KeyValueKind.PathVariables || _entries.Any(e => e.Description is not null))
        {
            columns["Description"] = e => e.Description ?? "";
        }

        _rows.SetColumns(columns);
        _table.Table = _checkBoxes;
        _table.Update();
    }

    private string DisplayValue(KeyValueEntry entry)
    {
        if (entry.IsFile)
        {
            return "[file] " + (entry.Src ?? "").Replace('\n', ',');
        }

        if (entry.IsSecret)
        {
            return string.IsNullOrEmpty(entry.Value) ? "" : "••••••";
        }

        return entry.Value ?? "";
    }

    private KeyValueEntry? Selected =>
        _table.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < _entries.Count
            ? _entries[selection.SelectedCell.Y]
            : null;

    private void OnTableKeyDown(object? sender, Key key)
    {
        // Path variables follow the URL's :name segments: rows are neither added nor removed here.
        if (_kind == KeyValueKind.PathVariables)
        {
            return;
        }

        if (key == Key.Delete || key == Key.DeleteChar)
        {
            key.Handled = RemoveSelected();
        }
        else if (key == Key.InsertChar)
        {
            AddEntry();
            key.Handled = true;
        }
    }

    /// <summary>
    /// Each click on a checkbox turns its row on or off (in the header, all rows) and moves the cursor to the row.
    /// Terminal.Gui reports a second quick click only as a double click, which the table would take as Edit, and the
    /// checkbox column alone would leave the cursor where it was.
    /// </summary>
    private void OnTableMouseEvent(object? sender, Mouse mouse)
    {
        if (mouse.Flags is not (MouseFlags.LeftButtonClicked or MouseFlags.LeftButtonDoubleClicked or MouseFlags.LeftButtonTripleClicked)
            || mouse.Position is not { } position)
        {
            return;
        }

        var cell = _table.ScreenToCell(position.X, position.Y, out var header);
        if (header == 0)
        {
            var enabled = !_entries.All(e => e.Enabled);
            foreach (var entry in _entries)
            {
                SetEnabled(entry, enabled);
            }
        }
        else if (cell is { X: 0, Y: var row })
        {
            SetEnabled(_entries[row], !_entries[row].Enabled);
            _table.SetSelection(0, row, false);
            _table.Update();
        }
        else
        {
            return;
        }

        _table.SetNeedsDraw();
        mouse.Handled = true;
    }

    private void SetEnabled(KeyValueEntry entry, bool enabled)
    {
        if (entry.Enabled != enabled)
        {
            entry.Enabled = enabled;
            RaiseChanged();
        }
    }

    private void AddEntry()
    {
        if (_kind == KeyValueKind.PathVariables || App is null)
        {
            return;
        }

        var entry = KeyValueEntryDialog.Show(App, "Add " + Noun, new KeyValueEntry { Type = DefaultType }, _kind);
        if (entry is null)
        {
            return;
        }

        _entries.Add(entry);
        Rebind();
        _table.SetSelection(0, _entries.Count - 1, false);
        RaiseChanged();
    }

    private void EditSelected()
    {
        if (Selected is not { } entry || App is null)
        {
            return;
        }

        var edited = KeyValueEntryDialog.Show(App, "Edit " + Noun, entry.Clone(), _kind);
        if (edited is null || edited.ContentEquals(entry))
        {
            return;
        }

        var index = _entries.IndexOf(entry);
        _entries[index] = edited;
        Rebind();
        _table.SetSelection(0, index, false);
        RaiseChanged();
    }

    /// <summary>Removes the selected row; false when there is none (or rows cannot be removed).</summary>
    private bool RemoveSelected()
    {
        if (_kind == KeyValueKind.PathVariables || Selected is not { } entry)
        {
            return false;
        }

        var index = _entries.IndexOf(entry);
        _entries.RemoveAt(index);
        Rebind();
        if (_entries.Count > 0)
        {
            _table.SetSelection(0, Math.Min(index, _entries.Count - 1), false);
        }

        RaiseChanged();
        return true;
    }

    private void BulkEdit()
    {
        if (App is null)
        {
            return;
        }

        var edited = BulkEditDialog.Show(App, Noun, _entries);
        if (edited is null || KeyValueEntry.ListEquals(_entries, edited))
        {
            return;
        }

        _entries.Clear();
        _entries.AddRange(edited);
        Rebind();
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private string Noun => _kind switch
    {
        KeyValueKind.QueryParams => "query parameter",
        KeyValueKind.PathVariables => "path variable",
        KeyValueKind.Headers => "header",
        KeyValueKind.UrlEncoded or KeyValueKind.FormData => "form field",
        _ => "variable",
    };

    private string? DefaultType => _kind switch
    {
        KeyValueKind.FormData => "text",
        KeyValueKind.Variables => "default",
        _ => null,
    };

    /// <summary>
    /// The table's rows: the current entries, read when they are needed (Terminal.Gui's
    /// <see cref="EnumerableTableSource{T}"/> copies them).
    /// </summary>
    private sealed class EntryRows(KeyValueTable owner) : IEnumerableTableSource<KeyValueEntry>
    {
        private Dictionary<string, Func<KeyValueEntry, object>> _columns = [];

        public string[] ColumnNames { get; private set; } = [];

        public int Columns => ColumnNames.Length;

        public int Rows => owner._entries.Count;

        public object this[int row, int col] => _columns[ColumnNames[col]](owner._entries[row]);

        public void SetColumns(Dictionary<string, Func<KeyValueEntry, object>> columns)
        {
            _columns = columns;
            ColumnNames = [.. columns.Keys];
        }

        public IEnumerable<KeyValueEntry> GetAllObjects() => owner._entries;

        public KeyValueEntry GetObjectOnRow(int row) => owner._entries[row];
    }
}
