using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Model;
using TermRest.Tui.Views;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>The key/value grid's checkbox column, clicked with the mouse.</summary>
[Collection("TUI")]
public sealed class KeyValueTableTests : IDisposable
{
    private readonly IApplication _app;
    private readonly Window _window = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly KeyValueTable _table = new(KeyValueKind.QueryParams);
    private readonly List<KeyValueEntry> _entries =
    [
        new() { Key = "foo", Value = "bar" },
        new() { Key = "source", Value = "termrest" },
        new() { Key = "debug", Value = "1", Enabled = false },
    ];

    private readonly List<string> _dialogs = [];
    private readonly SessionToken? _token;
    private int _changes;

    public KeyValueTableTests()
    {
        _app = Application.Create();
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(80, 20);
        _table.SetEntries(_entries);
        _table.Changed += (_, _) => _changes++;
        _window.Add(_table);
        _token = _app.Begin(_window);
        _table.SubViews.OfType<TableView>().Single().SetFocus();
        _app.LayoutAndDraw(forceRedraw: true);

        // Runs only while a dialog is open: notes its title and closes it.
        _app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            if (_app.TopRunnableView is Dialog dialog)
            {
                _dialogs.Add(dialog.Title);
                _app.RequestStop();
            }

            return true;
        });
    }

    /// <summary>Where the checkbox of the row (or the header) that shows <paramref name="text"/> is.</summary>
    private Point CheckBoxOf(string text)
    {
        _app.LayoutAndDraw(forceRedraw: true);
        var lines = (_app.Driver!.ToString() ?? "").Split('\n');
        var y = Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));
        Assert.True(y >= 0, $"'{text}' is not on screen");
        return new Point(lines[y].IndexOf(text, StringComparison.Ordinal) - 2, y);
    }

    private KeyValueEntry? Selected =>
        _table.SubViews.OfType<TableView>().Single().Value?.SelectedCell.Y is { } row && row < _table.Entries.Count ? _table.Entries[row] : null;

    [Fact]
    public void Two_quick_clicks_on_a_checkbox_turn_it_on_and_off_again_and_edit_nothing()
    {
        _app.InjectSequence(InputInjectionExtensions.LeftButtonDoubleClick(CheckBoxOf("debug")));
        var (enabled, selected, changes) = (_entries[2].Enabled, Selected?.Key, _changes);
        _app.InjectKey(Key.Delete);

        Assert.Empty(_dialogs); // no Edit query parameter
        Assert.Equal((false, "debug", 2), (enabled, selected, changes));
        Assert.Equal(["foo", "source"], _table.Entries.Select(e => e.Key)); // Delete removed the clicked row
    }

    [Fact]
    public void A_click_on_a_checkbox_moves_the_cursor_to_its_row()
    {
        _app.InjectSequence(InputInjectionExtensions.LeftButtonClick(CheckBoxOf("debug")));
        var (enabled, selected) = (_entries[2].Enabled, Selected?.Key);
        _app.InjectKey(Key.Delete);

        Assert.Equal((true, "debug"), (enabled, selected));
        Assert.Equal(["foo", "source"], _table.Entries.Select(e => e.Key));
    }

    [Fact]
    public void Two_quick_clicks_on_the_header_s_checkbox_toggle_every_row_twice_and_edit_nothing()
    {
        _app.InjectSequence(InputInjectionExtensions.LeftButtonDoubleClick(CheckBoxOf("Key")));

        Assert.Empty(_dialogs);
        Assert.Equal([false, false, false], _entries.Select(e => e.Enabled)); // all on (debug was off), then all off
        Assert.Equal(4, _changes);
    }

    public void Dispose()
    {
        if (_token is not null)
        {
            _app.End(_token);
        }

        _window.Dispose();
        _app.Dispose();
    }
}
