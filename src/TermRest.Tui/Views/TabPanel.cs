using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace TermRest.Tui.Views;

/// <summary>
/// Tabbed pages that look like Terminal.Gui's <c>Tabs</c> (rounded tab headers above a framed page) at a fraction of the
/// cost. <c>Tabs</c> keeps every page on screen, stacked, and lays out and draws all of them on each redraw, merging
/// every page's border through a line canvas: it was the most expensive part of the window and slowed down every
/// click and drag. Here only the selected page is visible and the frame is drawn directly.
/// </summary>
/// <remarks>
/// Click a tab to open it. From the keyboard the header row is a tab stop: Left/Right (Home/End) switch tabs, and Down
/// or Enter moves into the page. Each page remembers which of its controls had the focus.
/// </remarks>
internal sealed class TabPanel : View
{
    /// <summary>Rows above a page: the tab tops, the titles, and the line the selected tab opens into.</summary>
    internal const int HeaderHeight = 3;

    private readonly TabHeader _header;
    private readonly List<View> _pages = [];
    private readonly Dictionary<View, View> _lastFocused = [];
    private View? _value;

    public TabPanel()
    {
        CanFocus = true;
        _header = new TabHeader(this) { X = 0, Y = 0, Width = Dim.Fill(), Height = HeaderHeight };
        Add(_header);
    }

    /// <summary>Raised after another page was selected.</summary>
    public event EventHandler<ValueChangedEventArgs<View?>>? ValueChanged;

    /// <summary>The pages, in tab order. A page's <see cref="View.Title"/> labels its tab.</summary>
    public IReadOnlyList<View> Pages => _pages;

    /// <summary>Position of the selected page in <see cref="Pages"/> (-1 without pages).</summary>
    public int SelectedIndex => _value is null ? -1 : _pages.IndexOf(_value);

    /// <summary>The selected page. Setting it moves the focus along only if the previous page had it.</summary>
    public View? Value
    {
        get => _value;
        set => Select(value, focusPage: false);
    }

    /// <summary>Adds pages; the first page added is selected.</summary>
    public void AddTabs(params View[] pages)
    {
        foreach (var page in pages)
        {
            page.X = 1;
            page.Y = HeaderHeight;
            page.Width = Dim.Fill(1);
            page.Height = Dim.Fill(1);
            page.Visible = false;
            page.HasFocusChanging += (_, e) =>
            {
                if (e.CurrentValue && !e.NewValue && page.MostFocused is { } focused && focused != page)
                {
                    _lastFocused[page] = focused;
                }
            };
            _pages.Add(page);
            Add(page);
        }

        if (_value is null && _pages.Count > 0)
        {
            Select(_pages[0], focusPage: false);
        }

        _header.SetNeedsDraw();
    }

    /// <summary>Selects <paramref name="page"/>; with <paramref name="focusPage"/> also moves the focus into it.</summary>
    internal void Select(View? page, bool focusPage)
    {
        if (page is null || !_pages.Contains(page))
        {
            return;
        }

        var previous = _value;
        if (page == previous)
        {
            if (focusPage && !page.HasFocus)
            {
                FocusPage(page);
            }

            return;
        }

        _value = page;
        page.Visible = true;

        // Move the focus before hiding the previous page: hiding a focused view sends the focus elsewhere.
        if (focusPage || previous?.HasFocus == true)
        {
            FocusPage(page);
        }

        previous?.Visible = false;
        _header.SetNeedsDraw();
        ValueChanged?.Invoke(this, new ValueChangedEventArgs<View?>(previous, page));
    }

    /// <summary>Selects the page <paramref name="offset"/> tabs away, wrapping around (keyboard navigation).</summary>
    internal void SelectNext(int offset)
    {
        if (_pages.Count == 0)
        {
            return;
        }

        var index = SelectedIndex;
        Select(_pages[((Math.Max(0, index) + offset) % _pages.Count + _pages.Count) % _pages.Count], focusPage: false);
    }

    internal void FocusSelectedPage()
    {
        if (_value is not null)
        {
            FocusPage(_value);
        }
    }

    private void FocusPage(View page)
    {
        if (_lastFocused.TryGetValue(page, out var control) && IsShownWithin(control, page) && control.SetFocus())
        {
            return;
        }

        page.SetFocus();
    }

    private static bool IsShownWithin(View view, View page)
    {
        for (var current = view; current is not null; current = current.SuperView)
        {
            if (!current.Visible || !current.Enabled)
            {
                return false;
            }

            if (current == page)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The frame around the page: its sides and bottom (the header draws the top).</summary>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width < 2 || height <= HeaderHeight)
        {
            return true;
        }

        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (var y = HeaderHeight; y < height - 1; y++)
        {
            AddStr(0, y, "│");
            AddStr(width - 1, y, "│");
        }

        AddStr(0, height - 1, "╰" + new string('─', width - 2) + "╯");
        return true;
    }

    /// <summary>
    /// The tab row, three lines high:
    /// <code>
    /// ╭──────╭───────╮────╮
    /// │Params│Headers│Auth│
    /// ├──────╯       ╰────┴────────╮
    /// </code>
    /// Tabs up to the selected one show their left corner, the ones after it their right corner, and the line below is
    /// open under the selected tab, which reads as the front-most card.
    /// </summary>
    private sealed class TabHeader : View
    {
        private readonly TabPanel _panel;
        private int _scroll;

        public TabHeader(TabPanel panel)
        {
            _panel = panel;
            CanFocus = true;
            TabStop = TabBehavior.TabStop;
            MouseEvent += OnMouse;
        }

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.CursorLeft || key == Key.CursorRight)
            {
                _panel.SelectNext(key == Key.CursorLeft ? -1 : 1);
                return true;
            }

            if ((key == Key.Home || key == Key.End) && _panel.Pages.Count > 0)
            {
                _panel.Select(key == Key.Home ? _panel.Pages[0] : _panel.Pages[^1], focusPage: false);
                return true;
            }

            if (key == Key.CursorDown || key == Key.Enter)
            {
                _panel.FocusSelectedPage();
                return true;
            }

            return base.OnKeyDown(key);
        }

        private void OnMouse(object? sender, Mouse mouse)
        {
            if (!mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)
                && !mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased)
                && !mouse.IsSingleDoubleOrTripleClicked)
            {
                return;
            }

            // A tab opens on press, for the quickest response; the release and click that follow are consumed too,
            // so that they do not move the focus back here.
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) && !mouse.Flags.HasFlag(MouseFlags.PositionReport)
                && mouse.Position is { } position && TabAt(position.X + _scroll) is { } page)
            {
                _panel.Select(page, focusPage: true);
            }

            mouse.Handled = true;
        }

        private View? TabAt(int column)
        {
            var edges = Edges();
            for (var i = 0; i < _panel.Pages.Count; i++)
            {
                if (column >= edges[i] && column <= edges[i + 1])
                {
                    return _panel.Pages[i];
                }
            }

            return null;
        }

        /// <summary>The columns of the vertical lines around the titles: tab i spans edges[i] to edges[i + 1].</summary>
        private int[] Edges()
        {
            var pages = _panel.Pages;
            var edges = new int[pages.Count + 1];
            for (var i = 0; i < pages.Count; i++)
            {
                edges[i + 1] = edges[i] + pages[i].Title.Length + 1;
            }

            return edges;
        }

        protected override bool OnDrawingContent(DrawContext? context)
        {
            var pages = _panel.Pages;
            var width = Viewport.Width;
            if (pages.Count == 0 || width <= 0)
            {
                return true;
            }

            var edges = Edges();
            var count = pages.Count;
            var selected = Math.Max(0, _panel.SelectedIndex);
            KeepVisible(edges[selected], edges[selected + 1], edges[count], width);

            var normal = GetAttributeForRole(VisualRole.Normal);
            SetAttribute(normal);
            for (var i = 0; i <= count; i++)
            {
                Put(edges[i], 0, i == 0 ? "╭" : i == count ? "╮" : i <= selected ? "╭" : "╮");
                Put(edges[i], 1, "│");
                if (i < count)
                {
                    for (var x = edges[i] + 1; x < edges[i + 1]; x++)
                    {
                        Put(x, 0, "─");
                    }
                }
            }

            for (var i = 0; i < count; i++)
            {
                SetAttribute(i == selected && HasFocus ? GetAttributeForRole(VisualRole.Focus) : normal);
                var title = pages[i].Title;
                for (var c = 0; c < title.Length; c++)
                {
                    Put(edges[i] + 1 + c, 1, title[c].ToString());
                }
            }

            // The line below the titles, open under the selected tab; its ends turn down into the page's frame.
            SetAttribute(normal);
            var gapStart = edges[selected];
            var gapEnd = edges[selected + 1];
            bool OnLine(int x) => x <= gapStart || x >= gapEnd;
            for (var column = 0; column < width; column++)
            {
                var x = column + _scroll;
                var up = x <= edges[count] && Array.IndexOf(edges, x) >= 0;
                var down = column == 0 || column == width - 1;
                var line = OnLine(x);
                var left = line && column > 0 && OnLine(x - 1);
                var right = line && column < width - 1 && OnLine(x + 1);
                AddStr(column, 2, Junction(up, down, left, right));
            }

            return true;
        }

        private void Put(int x, int row, string glyph)
        {
            var column = x - _scroll;
            if (column >= 0 && column < Viewport.Width)
            {
                AddStr(column, row, glyph);
            }
        }

        /// <summary>Scrolls the tab row when it is wider than the panel, so that the selected tab stays in view.</summary>
        private void KeepVisible(int start, int end, int total, int width)
        {
            if (end - _scroll > width - 1)
            {
                _scroll = end - (width - 1);
            }

            if (start < _scroll)
            {
                _scroll = start;
            }

            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, total - (width - 1)));
        }

        private static string Junction(bool up, bool down, bool left, bool right) => (up, down, left, right) switch
        {
            (true, true, true, true) => "┼",
            (true, true, true, false) => "┤",
            (true, true, false, true) => "├",
            (true, false, true, true) => "┴",
            (false, true, true, true) => "┬",
            (true, false, true, false) => "╯",
            (true, false, false, true) => "╰",
            (false, true, true, false) => "╮",
            (false, true, false, true) => "╭",
            (true, _, false, false) or (_, true, false, false) => "│",
            (false, false, _, _) when left || right => "─",
            _ => " ",
        };
    }
}
