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
        _table.Accepted += (_, _) => EditSelected();
        _table.KeyDown += OnTableKeyDown;

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

        var source = new EnumerableTableSource<KeyValueEntry>(_entries, columns);
        _table.Table = new CheckBoxTableSourceWrapperByObject<KeyValueEntry>(_table, source, e => e.Enabled, (e, enabled) =>
        {
            if (e.Enabled != enabled)
            {
                e.Enabled = enabled;
                RaiseChanged();
            }
        });
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
        if (key == Key.Delete || key == Key.DeleteChar)
        {
            RemoveSelected();
            key.Handled = true;
        }
        else if (key == Key.InsertChar || key == Key.N.WithCtrl)
        {
            AddEntry();
            key.Handled = true;
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

    private void RemoveSelected()
    {
        if (_kind == KeyValueKind.PathVariables || Selected is not { } entry)
        {
            return;
        }

        var index = _entries.IndexOf(entry);
        _entries.RemoveAt(index);
        Rebind();
        if (_entries.Count > 0)
        {
            _table.SetSelection(0, Math.Min(index, _entries.Count - 1), false);
        }

        RaiseChanged();
    }

    private void BulkEdit()
    {
        if (App is null)
        {
            return;
        }

        var edited = BulkEditDialog.Show(App, Noun, _entries);
        if (edited is null)
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
}
