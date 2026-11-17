using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Tui.Theming;
using TermRest.Tui.Views;
using Xunit;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermRest.Tui.Tests;

#pragma warning disable CS0618 // TextView: TextArea is compared with the Terminal.Gui view it draws and edits like.

/// <summary>The text editor/viewer: drawn and edited as Terminal.Gui's TextView, without its costs on large texts.</summary>
[Collection("TUI")]
public sealed class TextAreaTests
{
    /// <summary>A pretty-printed JSON array of about <paramref name="size"/> characters, one property a line.</summary>
    private static string Json(int size)
    {
        var text = new StringBuilder("[\n");
        for (var i = 0; text.Length < size; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {{\n    \"id\": {i},\n    \"name\": \"Item {i}\",\n    \"tags\": [\"alpha\", \"beta\"],\n")
                .Append(CultureInfo.InvariantCulture, $"    \"active\": {(i % 2 == 0 ? "true" : "false")}\n  }},\n");
        }

        return text.Append("  {}\n]").ToString();
    }

    /// <summary>A headless application showing one view in a window.</summary>
    private sealed class Screen : IDisposable
    {
        private readonly Window _window = new() { Width = Dim.Fill(), Height = Dim.Fill() };
        private SessionToken? _token;

        public Screen(int width = 60, int height = 16)
        {
            App = Application.Create();
            App.Init(DriverRegistry.Names.ANSI);
            App.Driver!.SetScreenSize(width, height);
            App.Driver.Clipboard = new FakeClipboard();
        }

        public IApplication App { get; }

        public T Show<T>(T view, string text)
            where T : TextView
        {
            _window.Add(view);
            _token = App.Begin(_window);
            if (view is TextArea area)
            {
                area.SetText(text);
            }
            else
            {
                view.Text = text;
            }

            view.SetFocus();
            App.LayoutAndDraw(forceRedraw: true);
            return view;
        }

        /// <summary>What the view shows, with each cell's colors, after a full redraw.</summary>
        public string Cells(View view)
        {
            App.LayoutAndDraw(forceRedraw: true);
            var contents = App.Driver!.Contents!;
            var frame = view.FrameToScreen();
            var cells = new StringBuilder();
            for (var y = frame.Top; y < frame.Bottom; y++)
            {
                for (var x = frame.Left; x < frame.Right; x++)
                {
                    cells.Append(contents[y, x].Grapheme).Append(contents[y, x].Attribute).Append('|');
                }

                cells.Append('\n');
            }

            return cells.Append(view.Cursor.Position).ToString();
        }

        public void Key(Key key) => App.InjectKey(key);

        public void Mouse(View view, MouseFlags flags, int x = 3, int y = 3) =>
            App.InjectMouse(new Mouse { ScreenPosition = view.ViewportToScreen(new Point(x, y)), Flags = flags });

        public void Dispose()
        {
            if (_token is not null)
            {
                App.End(_token);
            }

            _window.Dispose();
            App.Dispose();
        }
    }

    private static TextView Configure(TextView view, bool readOnly, bool wrap)
    {
        view.X = 1;
        view.Y = 1;
        view.Width = 40;
        view.Height = 10;
        view.ReadOnly = readOnly;
        view.WordWrap = wrap;
        view.ScrollBars = true;
        return view;
    }

    public static TheoryData<string, bool, bool> Views => new()
    {
        { "top", false, false },
        { "top", true, false },
        { "middle", true, false },
        { "end", false, false },
        { "selection", false, false },
        { "selection", true, true },
        { "wide", true, false },
        { "wide", false, true },
        { "scrolled right", true, false },
        { "scrolled far right", true, false },
        { "scrolled far right", false, false },
        { "overwrite", false, false },
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void Draws_what_Terminal_Gui_s_text_view_draws(string what, bool readOnly, bool wrap)
    {
        var text = what switch
        {
            "wide" or "scrolled right" => string.Concat(Enumerable.Repeat("名前: 東京タワー 🎉👍🏽 é x\ty\t\tz 漢字漢字 end\n\tleading tab ab\tc " + new string('w', 50) + "\n", 20)),
            "scrolled far right" => string.Concat(Enumerable.Repeat("short\n\t名前 🎉\n\n" + new string('w', 300) + " end\n", 5)),
            _ => Json(30_000),
        };
        void Arrange(TextView view)
        {
            switch (what)
            {
                case "middle":
                    view.ScrollTo(new Point(0, 150));
                    break;
                case "end":
                    view.MoveEnd();
                    break;
                case "selection":
                    view.InsertionPoint = new Point(9, 6);
                    view.SelectionStartRow = 2;
                    view.SelectionStartColumn = 4;
                    break;
                case "scrolled right":
                    view.ScrollTo(new Point(9, 3));
                    break;
                case "scrolled far right":
                    view.InsertionPoint = new Point(304, 3); // the end of a long line: the other lines end far left of the view
                    Assert.True(view.Viewport.X > 260, $"scrolled to {view.Viewport.X}");
                    break;
                case "overwrite":
                    view.InsertionPoint = new Point(5, 2);
                    view.Used = false;
                    break;
            }
        }

        string Draw(TextView view)
        {
            using var screen = new Screen();
            screen.Show(Configure(view, readOnly, wrap), text);
            Arrange(view);
            return screen.Cells(view);
        }

        var expected = Draw(new TextView());
        var drawn = Draw(new TextArea());

        Assert.Equal(expected, drawn);
    }

    [Fact]
    public void Redrawing_a_large_text_takes_no_longer_than_a_small_one()
    {
        using var screen = new Screen(120, 40);
        screen.Show(new TextArea { ReadOnly = true }, Json(1_000_000));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            screen.App.LayoutAndDraw(forceRedraw: true);
        }

        watch.Stop();
        var shown = screen.App.Driver!.ToString();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"3 redraws took {watch.Elapsed}"); // ~0.05 s; 12 s when every line below was drawn too
        Assert.Contains("\"name\": \"Item 0\",", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Redrawing_a_view_scrolled_to_the_end_of_a_very_long_line_is_quick()
    {
        // A pretty-printed response holding one long string: at its end, the view is far right of where the other lines end.
        var json = new StringBuilder("{\n  \"data\": \"").Append('x', 1_000_000).Append("\",\n");
        for (var i = 0; i < 60; i++)
        {
            json.Append(CultureInfo.InvariantCulture, $"  \"key {i}\": {i},\n");
        }

        using var screen = new Screen(120, 40);
        var area = screen.Show(new TextArea { ReadOnly = true }, json.Append("  \"end\": true\n}").ToString());
        area.InsertionPoint = new Point(0, 1);
        screen.Key(Key.End);
        var watch = Stopwatch.StartNew();
        screen.App.LayoutAndDraw(forceRedraw: true);
        watch.Stop();

        Assert.True(area.Viewport.X > 999_000, $"scrolled to {area.Viewport.X}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"a redraw took {watch.Elapsed}"); // ~0.3 s; 11 s when the short lines were cleared from where they end
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_only_text_is_colored_and_wrapped_lines_keep_their_colors(bool wrap)
    {
        using var screen = new Screen();
        var area = screen.Show(
            new TextArea { X = 1, Y = 1, Width = wrap ? 22 : 50, Height = 8, ReadOnly = true, WordWrap = wrap, SyntaxLanguage = "json" },
            "{\n  \"name\": \"one two three four five\",\n  \"count\": 12\n}");
        AssertColors();
        if (wrap)
        {
            area.Width = 17; // wrapped again, at other places
            AssertColors();
        }

        void AssertColors()
        {
            screen.App.LayoutAndDraw(forceRedraw: true);
            var palette = ThemeCatalog.Current.Palette;
            Assert.Equal(palette.SyntaxVariable, CellAttribute(screen, area, "\"name\"").Foreground);
            Assert.Equal(palette.SyntaxString, CellAttribute(screen, area, "five").Foreground); // on a row after the string's start
            Assert.Equal(palette.SyntaxNumber, CellAttribute(screen, area, "12").Foreground);
            Assert.Equal(area.GetAttributeForRole(VisualRole.ReadOnly), CellAttribute(screen, area, "{"));
        }
    }

    [Fact]
    public void Coloring_a_large_text_whose_words_wrap_is_quick_down_to_its_end()
    {
        using var screen = new Screen(120, 40);
        var area = screen.Show(new TextArea { ReadOnly = true, WordWrap = true, SyntaxLanguage = "json" }, Json(1_000_000));
        var watch = Stopwatch.StartNew();
        area.MoveEnd();
        screen.App.LayoutAndDraw(forceRedraw: true);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"a redraw took {watch.Elapsed}");
        Assert.Equal(ThemeCatalog.Current.Palette.SyntaxVariable, CellAttribute(screen, area, "\"active\"").Foreground);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Find_highlights_each_match_and_selects_one_in_turn_across_wrapped_rows(bool wrap)
    {
        using var screen = new Screen();
        var area = screen.Show(new TextArea { X = 1, Y = 1, Width = wrap ? 11 : 40, Height = 8, ReadOnly = true, WordWrap = wrap },
            "abcdefghijklmnopqrstuvwxyz\nHIJKLM hijklm");
        area.Find("hijklm", matchCase: false);
        var first = (area.MatchCount, area.CurrentMatch, area.SelectedText, Marks());
        var spans = area.SelectionStartRow != area.CurrentRow; // wrapped: the match goes on to the next row
        if (wrap)
        {
            area.Width = 9; // wrapped again, at other places
        }

        area.SelectNextMatch();
        var second = (area.CurrentMatch, area.SelectedText, Marks());
        area.SelectNextMatch(backwards: true);
        area.SelectNextMatch(backwards: true);
        var last = (area.CurrentMatch, area.SelectedText);
        area.Find("HIJKLM", matchCase: true);
        var matchCase = (area.MatchCount, area.CurrentMatch, area.SelectedText);

        Assert.Equal((3, 0, "hijklm", (Selected: 6, Highlighted: 12)), first);
        Assert.Equal(wrap, spans);
        Assert.Equal((1, "HIJKLM", (6, 12)), second);
        Assert.Equal((2, "hijklm"), last); // round from the first to the last
        Assert.Equal((1, 0, "HIJKLM"), matchCase);

        // The cells the selected match and the other matches cover.
        (int Selected, int Highlighted) Marks()
        {
            screen.App.LayoutAndDraw(forceRedraw: true);
            var contents = screen.App.Driver!.Contents!;
            var frame = area.FrameToScreen();
            var (selected, highlighted) = (area.GetAttributeForRole(VisualRole.Active), ThemeCatalog.Current.Palette.Warning);
            var marks = (Selected: 0, Highlighted: 0);
            for (var y = frame.Top; y < frame.Bottom; y++)
            {
                for (var x = frame.Left; x < frame.Right; x++)
                {
                    var attribute = contents[y, x].Attribute!.Value;
                    if (attribute.Background == selected.Background && attribute.Foreground == selected.Foreground)
                    {
                        marks.Selected++;
                    }
                    else if (attribute.Background == highlighted)
                    {
                        marks.Highlighted++;
                    }
                }
            }

            return marks;
        }
    }

    /// <summary>The colors of the first cell of <paramref name="text"/> where the view shows it.</summary>
    private static Attribute CellAttribute(Screen screen, View view, string text)
    {
        var contents = screen.App.Driver!.Contents!;
        var frame = view.FrameToScreen();
        for (var y = frame.Top; y < frame.Bottom; y++)
        {
            var row = new StringBuilder();
            for (var x = frame.Left; x < frame.Right; x++)
            {
                row.Append(contents[y, x].Grapheme);
            }

            var at = row.ToString().IndexOf(text, StringComparison.Ordinal);
            if (at >= 0)
            {
                return contents[y, frame.Left + at].Attribute!.Value;
            }
        }

        Assert.Fail($"'{text}' is not shown:\n{screen.App.Driver}");
        return default;
    }

    [Fact]
    public void A_space_typed_with_Shift_held_is_typed()
    {
        using var screen = new Screen();
        var area = screen.Show(new TextArea(), "");

        screen.Key(Key.A.WithShift);
        screen.Key(Key.Space.WithShift);
        screen.Key(Key.B);

        Assert.Equal("A b", area.GetText());
    }

    [Fact]
    public void Text_that_comes_with_no_key_is_typed_as_Terminal_Gui_s_text_view_types_it()
    {
        // As a kitty-protocol terminal sends text that has no key of its own (from an IME, or Option+a on a Mac): CSI 0;;text u.
        const string Input = "\u001b[0;;229ub\u001b[0;;123u\u001b[0;;34ua\u001b[0;;34u:1\u001b[0;;125u";
        static string Typed(TextView view)
        {
            using var screen = new Screen();
            screen.Show(view, "");
            using var input = new AnsiInputProcessor(new ConcurrentQueue<char>(Input));
            input.KeyDown += (_, key) => screen.App.Keyboard.RaiseKeyDownEvent(key);
            input.ProcessQueue();
            return view.Text;
        }

        var typed = Typed(new TextView { Width = Dim.Fill(), Height = Dim.Fill() });

        Assert.Equal("åb{\"a\":1}", typed);
        Assert.Equal(typed, Typed(new TextArea()));
    }

    [Fact]
    public void Half_of_a_character_set_or_pasted_shows_as_a_replacement_character()
    {
        using var screen = new Screen();
        var area = screen.Show(new TextArea(), "cut in two: \uD83D\n\uDE00 and 😀 whole, \uD83D\uD83D twice");
        var set = area.GetText();
        var shown = screen.App.Driver!.ToString();
        area.MoveEnd();
        screen.App.RaisePasteEvent(" pasted \uDE00");

        Assert.Equal("cut in two: \uFFFD\n\uFFFD and 😀 whole, \uFFFD\uFFFD twice", set);
        Assert.Contains("cut in two: \uFFFD", shown, StringComparison.Ordinal);
        Assert.Equal(set + " pasted \uFFFD", area.GetText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cutting_with_nothing_selected_cuts_nothing(bool readOnly)
    {
        const string Short = "L0\nL1\nL2\nL3\nL4\nL5\nL6\nL7\nL8\nL9";
        var text = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"L{i}"));
        using var screen = new Screen();
        var area = screen.Show(new TextArea { ReadOnly = readOnly }, text);
        screen.App.Clipboard!.SetClipboardData("kept");

        // TextView's cut takes the text from where the last selection started (the start of the text when there was none) to
        // the cursor, and fails partway when that start is past the end of a shorter text set since.
        area.InsertionPoint = new Point(1, 4);
        screen.Key(Key.X.WithCtrl);
        Assert.Equal(text, area.GetText());
        area.InsertionPoint = new Point(0, 150);
        screen.Key(Key.CursorDown.WithShift);
        screen.Key(Key.CursorDown);
        area.SetText(Short);
        area.InsertionPoint = new Point(1, 4);
        screen.Key(Key.X.WithCtrl);

        Assert.Equal(Short, area.GetText());
        Assert.Equal("kept", screen.App.Clipboard.GetClipboardData());
    }

    [Fact]
    public void A_cut_the_clipboard_refuses_leaves_the_text_as_it_was()
    {
        using var screen = new Screen();
        var area = screen.Show(new TextArea(), "L0\nL1\nL2\nL3\nL4");
        var clipboard = new FakeClipboard { FakeException = new System.ComponentModel.Win32Exception(5, "Access is denied.") };
        area.InsertionPoint = new Point(0, 1);
        screen.Key(Key.CursorDown.WithShift);
        screen.Key(Key.CursorDown.WithShift);
        screen.App.Driver!.Clipboard = clipboard;

        _ = Record.Exception(() => screen.Key(Key.X.WithCtrl)); // another program holds the clipboard

        Assert.Equal("L0\nL1\nL2\nL3\nL4", area.GetText());
    }

    [Fact]
    public void A_paste_from_the_terminal_inserts_the_text_pasted_after_a_copy_of_the_same_text()
    {
        using var screen = new Screen();
        var area = screen.Show(new TextArea(), "alpha\nbeta\ngamma");

        // Copied twice without a selection (its line), then pasted by the terminal (a bracketed paste, not Ctrl+V).
        area.InsertionPoint = new Point(0, 1);
        screen.Key(Key.C.WithCtrl);
        screen.Key(Key.C.WithCtrl);
        area.InsertionPoint = new Point(2, 2);
        screen.App.RaisePasteEvent("beta");
        Assert.Equal("alpha\nbeta\ngabetamma", area.GetText());

        // Copied without a selection, then selected and cut: the paste brings it back.
        area.SetText("foo bar\nzzz");
        screen.Key(Key.C.WithCtrl);
        screen.Key(Key.End.WithShift);
        screen.Key(Key.X.WithCtrl);
        screen.App.RaisePasteEvent("foo bar");
        Assert.Equal("foo bar\nzzz", area.GetText());
    }

    [Theory]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Backspace")]
    [InlineData("Ctrl+Shift+Backspace")]
    [InlineData("Ctrl+K")]
    public void Edits_that_shorten_the_text_end_the_selection(string edit)
    {
        using var screen = new Screen();
        var area = screen.Show(new TextArea(), "hello worl");
        area.InsertionPoint = new Point(10, 0);
        screen.Key(Key.D); // something to undo
        for (var i = 0; i < 3; i++)
        {
            screen.Key(Key.CursorLeft.WithShift);
        }

        screen.Key(edit switch
        {
            "Ctrl+Z" => Key.Z.WithCtrl,
            "Ctrl+Backspace" => Key.Backspace.WithCtrl,
            "Ctrl+Shift+Backspace" => Key.Backspace.WithCtrl.WithShift,
            _ => Key.K.WithCtrl,
        });

        // The selection's start would be past what is left of the text, and the next keys would fail on it.
        Assert.False(area.IsSelecting);
        Assert.Null(Record.Exception(() =>
        {
            screen.Key(Key.C.WithCtrl);
            screen.Key(Key.X);
            screen.Key(Key.Backspace);
            screen.Key(Key.Delete);
        }));
    }

    [Fact]
    public void Copying_a_large_selection_and_the_keys_clicks_drags_and_wheel_steps_after_it_are_quick()
    {
        var text = Json(600_000);
        using var screen = new Screen(120, 40);
        var area = screen.Show(new TextArea { ReadOnly = true }, text);
        var steps = new List<(string Step, TimeSpan Took)>();
        void Step(string step, Action act)
        {
            var watch = Stopwatch.StartNew();
            act();
            screen.App.LayoutAndDraw();
            steps.Add((step, watch.Elapsed));
        }

        Step("Ctrl+A", () => screen.Key(Key.A.WithCtrl));
        Step("Ctrl+C", () => screen.Key(Key.C.WithCtrl));
        var copied = screen.App.Clipboard!.GetClipboardData();
        Step("Shift+Up", () => screen.Key(Key.CursorUp.WithShift));
        var extended = (area.SelectionStartRow, area.SelectionStartColumn, area.CurrentRow);
        Step("wheel", () => screen.Mouse(area, MouseFlags.WheeledDown));
        Step("letter", () => screen.Key(Key.X));
        var selected = area.IsSelecting;
        Step("click", () => screen.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(area.ViewportToScreen(new Point(5, 5)))));
        var clickEnded = !area.IsSelecting;

        // Dragging to the bottom edge selects to the end of the text.
        screen.Key(Key.Home.WithCtrl);
        var bottom = area.Viewport.Height - 1;
        Step("press", () => screen.Mouse(area, MouseFlags.LeftButtonPressed, 5, 1));
        Step("drag to the bottom", () => screen.Mouse(area, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, 5, bottom));
        Step("drag on", () => screen.Mouse(area, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, 6, bottom));
        screen.Mouse(area, MouseFlags.LeftButtonReleased, 6, bottom);

        Assert.Equal(text.ReplaceLineEndings(Environment.NewLine), copied);
        Assert.Equal((0, 0, area.Lines - 2), extended);
        Assert.True(selected);
        Assert.True(clickEnded);
        Assert.Equal((true, 1, area.Lines - 1), (area.IsSelecting, area.SelectionStartRow, area.CurrentRow));
        Assert.All(steps, step => Assert.True(step.Took < TimeSpan.FromSeconds(1), $"{step.Step} took {step.Took}")); // ~0.05 s each; 2 to 17 s each before
    }

    public static TheoryData<string, bool, bool> Sequences => new()
    {
        { "forward cut, undo, redo", false, false },
        { "backward cut, undo", false, false },
        { "copy a line, then a selection, paste", false, false },
        { "copy, paste twice", false, false },
        { "copy, up, down", false, false },
        { "copy all", true, true },
        { "copy part", true, true },
        { "cut", true, false },
        { "wheel, letter, click", true, false },
        { "toggled selection, click", false, false },
    };

    [Theory]
    [MemberData(nameof(Sequences))]
    public void Copies_cuts_and_pastes_as_Terminal_Gui_s_text_view_does(string sequence, bool readOnly, bool wrap)
    {
        var text = string.Join('\n', Enumerable.Range(0, 40).Select(i => i % 5 == 2 ? "short" : $"line {i}: the quick brown fox jumps over the lazy dog {i}"));
        IEnumerable<Action<Screen, TextView>> Steps()
        {
            static Action<Screen, TextView> Press(Key key, int times = 1) => (screen, _) =>
            {
                for (var i = 0; i < times; i++)
                {
                    screen.Key(key);
                }
            };

            static Action<Screen, TextView> At(int column, int row) => (_, view) => view.InsertionPoint = new Point(column, row);

            // TextView pastes the cells it copied when the text pasted is the text it copied, as on Linux (no CR for the
            // paste to drop); those cells become lines of the text, which a second paste then reuses as changed (a
            // Terminal.Gui 2.5 bug). TextArea's copy leaves that text empty, so its paste reads the text pasted: the
            // TextView compared does the same.
            static Action<Screen, TextView> Paste() => (_, view) =>
            {
                if (view is not TextArea)
                {
                    CopiedText(view) = null;
                }

                view.InvokeCommand(Command.Paste);
            };

            return sequence switch
            {
                "forward cut, undo, redo" => [At(5, 3), Press(Key.CursorDown.WithShift, 3), Press(Key.CursorRight.WithShift, 4), Press(Key.X.WithCtrl), Press(Key.Z.WithCtrl), Press(Key.Y.WithCtrl)],
                "backward cut, undo" => [At(9, 12), Press(Key.CursorUp.WithShift, 4), Press(Key.X.WithCtrl), Press(Key.Z.WithCtrl)],
                "copy a line, then a selection, paste" => [At(3, 4), Press(Key.C.WithCtrl), At(1, 6), Press(Key.CursorRight.WithShift, 5), Press(Key.C.WithCtrl), At(2, 9), Paste()],
                "copy, paste twice" => [At(1, 6), Press(Key.CursorDown.WithShift, 3), Press(Key.C.WithCtrl), At(2, 20), Paste(), Paste(), Press(Key.Z.WithCtrl)],
                "copy, up, down" => [At(30, 0), Press(Key.CursorDown.WithShift, 2), Press(Key.C.WithCtrl), Press(Key.CursorDown), Press(Key.CursorDown)],
                "copy all" => [Press(Key.A.WithCtrl), Press(Key.C.WithCtrl)],
                "copy part" => [At(3, 4), Press(Key.CursorDown.WithShift, 5), Press(Key.CursorRight.WithShift, 7), Press(Key.C.WithCtrl)],
                "cut" => [At(3, 4), Press(Key.CursorDown.WithShift, 2), Press(Key.X.WithCtrl), Press(Key.CursorDown)],
                "wheel, letter, click" => [Press(Key.A.WithCtrl), (screen, view) => screen.Mouse(view, MouseFlags.WheeledDown), Press(Key.Q),
                    (screen, view) => screen.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(view.ViewportToScreen(new Point(4, 2)))), Press(Key.C.WithCtrl)],
                _ => [At(2, 1), Press(Key.Space.WithCtrl), Press(Key.CursorDown, 2),
                    (screen, view) => screen.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(view.ViewportToScreen(new Point(5, 6)))), Press(Key.C.WithCtrl)],
            };
        }

        string Play(TextView view)
        {
            using var screen = new Screen();
            screen.Show(Configure(view, readOnly, wrap), text);
            var states = new StringBuilder();
            foreach (var step in Steps())
            {
                step(screen, view);
                screen.App.LayoutAndDraw();

                // Where words wrap, TextView's copy may move a position from the start of a row to the end of the one above.
                var where = wrap ? "" : $"{view.InsertionPoint} ({view.SelectionStartColumn},{view.SelectionStartRow}) {view.Viewport}";
                states.Append(CultureInfo.InvariantCulture, $"{view.Text} | {view.IsSelecting} {where} | {screen.App.Clipboard!.GetClipboardData()}\n");
            }

            return states.ToString();
        }

        Assert.Equal(Play(new TextView()), Play(new TextArea()));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_copiedText")]
    private static extern ref string? CopiedText(TextView view);
}

#pragma warning restore CS0618
