using System.Collections.ObjectModel;
using System.Globalization;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.History;
using TermRest.Tui.Dialogs;

namespace TermRest.Tui.Views;

/// <summary>
/// The history pane: sent requests, newest first, as <c>time status method path</c> rows with a filter box.
/// Click or arrow to an entry to see its request and response; right-click (or Shift+F10) for more actions.
/// </summary>
internal sealed class HistoryView : View
{
    private readonly TextField _filter;
    private readonly ListView _list;
    private readonly List<HistoryEntry> _all = [];
    private readonly List<HistoryEntry> _shown = [];
    private ObservableCollection<string> _rows = [];
    private string _emptyText = "No requests sent yet.";
    private bool _refreshing;

    public HistoryView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        var label = new Label { Text = "Filter:", X = 0, Y = 0 };
        _filter = new TextField { X = Pos.Right(label) + 1, Y = 0, Width = Dim.Fill() };
        _filter.TextChanged += (_, _) => ApplyFilter();

        _list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _list.SetSource(_rows);

        // ListView's Ctrl+N (down) would hide the window's New request; Ctrl+P (up) goes with it.
        _list.KeyBindings.Remove(Key.N.WithCtrl);
        _list.KeyBindings.Remove(Key.P.WithCtrl);
        _list.ValueChanged += (_, e) =>
        {
            // Only the user's selection opens an entry; refreshing the list does not replace the editor's request.
            if (!_refreshing && Entry(e.NewValue) is { } entry)
            {
                EntrySelected?.Invoke(this, entry);
            }
        };
        _list.Accepted += (_, _) =>
        {
            if (Selected is { } entry)
            {
                EntryActivated?.Invoke(this, entry);
            }
        };
        _list.MouseEvent += (_, mouse) =>
        {
            if (mouse.Position is not { } position)
            {
                return;
            }

            var row = _list.Viewport.Y + position.Y;
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) && row == _list.SelectedItem && Entry(row) is { } clicked)
            {
                EntrySelected?.Invoke(this, clicked); // clicking the selected row opens it again
            }
            else if (mouse.Flags.HasFlag(MouseFlags.RightButtonClicked))
            {
                if (row >= 0 && row < _shown.Count)
                {
                    _list.SelectedItem = row;
                }

                ContextMenuRequested?.Invoke(this, mouse.ScreenPosition);
                mouse.Handled = true;
            }
        };
        _list.KeyDown += (_, key) =>
        {
            if (key == Key.F10.WithShift)
            {
                ContextMenuRequested?.Invoke(this, null);
                key.Handled = true;
            }
            else if (key == Key.Delete && Selected is { } entry)
            {
                DeleteRequested?.Invoke(this, entry);
                key.Handled = true;
            }
        };

        Add(label, _filter, _list);
    }

    /// <summary>An entry was selected (click, arrow keys).</summary>
    public event EventHandler<HistoryEntry>? EntrySelected;

    /// <summary>Enter or double-click on an entry.</summary>
    public event EventHandler<HistoryEntry>? EntryActivated;

    public event EventHandler<System.Drawing.Point?>? ContextMenuRequested;

    public event EventHandler<HistoryEntry>? DeleteRequested;

    public HistoryEntry? Selected => Entry(_list.SelectedItem);

    public int Count => _all.Count;

    /// <summary>Entries listed at most: as many as the history keeps (it deletes older ones, and their response bodies).</summary>
    public int Limit { get; set; } = int.MaxValue;

    public IReadOnlyList<string> Rows => _rows;

    public void SetEntries(IEnumerable<HistoryEntry> entries, string emptyText)
    {
        _all.Clear();
        _all.AddRange(entries.Take(Limit));
        _emptyText = emptyText;
        ApplyFilter();
    }

    /// <summary>Lists a new entry first; the oldest beyond <see cref="Limit"/> go, as they go from the history.</summary>
    public void Prepend(HistoryEntry entry)
    {
        _all.Insert(0, entry);
        if (_all.Count > Limit)
        {
            _all.RemoveRange(Limit, _all.Count - Limit);
        }

        ApplyFilter();
    }

    public void Remove(HistoryEntry entry)
    {
        _all.Remove(entry);
        ApplyFilter();
    }

    public void FocusList() => _list.SetFocus();

    private HistoryEntry? Entry(int? row) => row is { } index && index >= 0 && index < _shown.Count ? _shown[index] : null;

    private void ApplyFilter()
    {
        var filter = _filter.Text.Trim();
        var previous = Selected;
        _refreshing = true;
        try
        {
            _shown.Clear();
            _shown.AddRange(filter.Length == 0 ? _all : _all.Where(e => Matches(e, filter)));

            // New rows each time: the list measures all its rows again after each row added to them.
            _rows = _shown.Count == 0 ? [filter.Length == 0 ? _emptyText : "No match."] : new ObservableCollection<string>(_shown.Select(Format));
            _list.SetSource(_rows);
            if (_shown.Count > 0)
            {
                _list.SelectedItem = previous is null ? 0 : Math.Max(0, _shown.IndexOf(previous));
            }
            else
            {
                // No row is selected, which would scroll the list to it: back to the top, where the message is.
                _list.Viewport = _list.Viewport with { X = 0, Y = 0 };
            }
        }
        finally
        {
            _refreshing = false;
        }

        SetNeedsDraw();
    }

    private static bool Matches(HistoryEntry entry, string filter) =>
        entry.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Method.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (entry.Status?.ToString(CultureInfo.InvariantCulture) ?? "ERR").StartsWith(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>A compact row: time (date when not today), status, method and the URL without scheme and host.</summary>
    internal static string Format(HistoryEntry entry)
    {
        var local = entry.Time.ToLocalTime();
        var time = local.Date == DateTime.Today ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("MM-dd", CultureInfo.InvariantCulture);
        var status = entry.Status?.ToString(CultureInfo.InvariantCulture) ?? "ERR";
        var target = Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : entry.Url;
        return Ui.Drawable($"{time} {status,-3} {entry.Method,-6} {target}");
    }
}
