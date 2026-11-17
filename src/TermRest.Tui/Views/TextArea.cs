using System.Runtime.CompilerServices;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Tui.Syntax;
using TermRest.Tui.Theming;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace TermRest.Tui.Views;

#pragma warning disable CS0618 // TextView is superseded by tui-cs/Editor; switch once Terminal.Gui.Editor ships for Terminal.Gui 2.5.

/// <summary>
/// Multi-line editor/viewer used for bodies, scripts and response text. Isolates Terminal.Gui's (obsolete) TextView so
/// it can be swapped for the Terminal.Gui.Editor control (folding) without touching callers. With a
/// <see cref="SyntaxLanguage"/>, it colors the script's syntax in the theme's script colors.
/// </summary>
internal sealed class TextArea : TextView
{
    /// <summary>Script colors closer than this to the editor's background are drawn in its text color instead.</summary>
    private const double MinimumContrast = 1.5;

    private readonly List<SyntaxKind[]> _lineKinds = [];
    private readonly List<LexState> _lineEnds = [];
    private readonly Attribute[] _syntaxAttributes = new Attribute[Enum.GetValues<SyntaxKind>().Length];
    private string? _syntaxLanguage;
    private SyntaxLexer? _lexer;
    private int _linesLexed;
    private char[] _chars = [];
    private int[] _cellStarts = [];
    private SyntaxKind[] _charKinds = [];

    /// <summary>While words wrap: TextView's whole text, against which rows are matched to find the lines they are parts of.</summary>
    private string? _wrappedText;

    /// <summary>Where the first row not matched yet starts in <see cref="_wrappedText"/>.</summary>
    private int _wrappedAt;

    /// <summary>For each row matched so far, whether a line ends with it.</summary>
    private readonly List<bool> _rowEndsLine = [];

    /// <summary>The rows did not add up to the text: each row is then colored as a line.</summary>
    private bool _rowsUnmatched;

    /// <summary>Whether words wrapped, and at which width, when rows were matched to lines (TextView wraps again when it changes).</summary>
    private (bool Wrap, int Width) _rowsLayout;

    /// <summary>The text <see cref="Find"/> looks for, or null.</summary>
    private string? _findText;

    private bool _findMatchCase;

    /// <summary>Where <see cref="_findText"/> is, in order.</summary>
    private readonly List<Match> _matches = [];

    /// <summary>The match selected by <see cref="SelectMatch"/>, or -1.</summary>
    private int _currentMatch = -1;

    /// <summary>The columns of the row being drawn that matches cover.</summary>
    private readonly List<(int From, int To)> _rowMatches = [];

    public TextArea()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        WordWrap = false;
        ScrollBars = true;

        // Tab and Shift+Tab move to the next and previous field. (Turning TabKeyAddsTab off instead would also make the
        // tabs in the text 0 columns wide.)
        KeyBindings.Remove(Key.Tab);
        KeyBindings.Remove(Key.Tab.WithShift);

        // TextView's Ctrl+L (a color picker) and Ctrl+N (cursor down) would hide the window's Focus URL bar and New request.
        KeyBindings.Remove(Key.L.WithCtrl);
        KeyBindings.Remove(Key.N.WithCtrl);

        // TextView builds a selection's text by adding each line to all the text before it, to copy it and once more to
        // see that something is selected: seconds for a large selection. These build it in one pass.
        AddCommand(Command.Copy, () => CopySelection(cut: false));
        AddCommand(Command.Cut, () => CopySelection(cut: true));

        // TextView's undo and redo leave a selection on, its start maybe past the text they shorten: the next key would fail.
        AddCommand(Command.Undo, () => EndingTheSelection(Undo));
        AddCommand(Command.Redo, () => EndingTheSelection(Redo));

        // After each move or edit, TextView builds the selected text anew to see whether it may offer completions, a step
        // it skips while it draws: seconds for each Shift+arrow on a large selection. TermRest offers no completions and
        // TextArea draws by itself, so TextView's "drawing" flag stays set. (A Terminal.Gui without that field is only
        // slower.)
        try
        {
            TextViewIsDrawing(this) = true;
        }
        catch (MissingFieldException)
        {
        }

        Autocomplete = new Completions(this);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDrawing")]
    private static extern ref bool TextViewIsDrawing(TextView view);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_copiedText")]
    private static extern ref string? TextViewCopiedText(TextView view);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_copiedCellsList")]
    private static extern ref List<List<Cell>> TextViewCopiedLines(TextView view);

    /// <summary>
    /// Makes TextView paste the text pasted. It keeps the lines it copied, to insert them in place of a pasted text equal to
    /// the copied one, though they are lines of the text itself (a copy without a selection keeps the line it is on): the
    /// pasted lines would then be the same lines, which change together, or a line edited or emptied since.
    /// </summary>
    private void ForgetCopiedLines()
    {
        try
        {
            TextViewCopiedText(this) = null;
            TextViewCopiedLines(this) = [];
        }
        catch (MissingFieldException)
        {
        }
    }

    /// <inheritdoc/>
    protected override bool OnPaste(string text)
    {
        ForgetCopiedLines();
        return base.OnPaste(WithoutLoneSurrogates(text));
    }

    /// <summary>Runs <paramref name="edit"/> with the selection ended first, unless the text can't change.</summary>
    private bool EndingTheSelection(Func<bool> edit)
    {
        if (!ReadOnly)
        {
            IsSelecting = false;
        }

        return edit();
    }

    /// <summary>
    /// TextView's completions (TermRest has none). TextView offers them each mouse event right after it measured the
    /// selected text, which <see cref="OnMouseEvent"/> hides from that measure: this shows it again.
    /// </summary>
    private sealed class Completions(TextArea area) : TextViewAutocomplete
    {
        /// <summary>
        /// A key no input produces. TextView takes this key to reopen completions (by default Shift+Space, which would then
        /// type no space); keys compare by key code alone, so <see cref="Key.Empty"/> would also take text that comes with no
        /// key (from an IME, or as a kitty terminal's <c>CSI 0;;text u</c>).
        /// </summary>
        public override Key Reopen { get; set; } = new(KeyCode.SpecialMask);

        public override bool OnMouseEvent(Mouse mouse, bool fromHost = false)
        {
            area.ShowHiddenSelection();
            return base.OnMouseEvent(mouse, fromHost);
        }
    }

    /// <summary>Raised with the new text after the user changed it (not for programmatic <see cref="SetText"/>).</summary>
    public event EventHandler<string>? UserEdited
    {
        add
        {
            _text ??= GetText();
            _userEdited += value;
        }

        remove => _userEdited -= value;
    }

    private EventHandler<string>? _userEdited;
    private bool _settingText;
    private bool _contentsChanged;

    /// <summary>Whether <see cref="OnMouseEvent"/> hid the selection, until TextView measured it.</summary>
    private bool _selectionHidden;

    /// <summary>The text as last set or reported by <see cref="UserEdited"/>; kept only while something listens.</summary>
    private string? _text;

    /// <summary>The language whose syntax is colored (a script language, or a body's: json, xml, html); null for plain text.</summary>
    public string? SyntaxLanguage
    {
        get => _syntaxLanguage;
        set
        {
            if (value == _syntaxLanguage)
            {
                return;
            }

            _syntaxLanguage = value;
            _lexer = SyntaxLexer.For(value);
            ForgetColors();
            SetNeedsDraw();
        }
    }

    public void SetText(string text)
    {
        _settingText = true;
        try
        {
            // Every line break becomes LF, as when pasted: Terminal.Gui would drop the character before the next LF from a
            // line with a lone CR. Setting the text also moves the cursor to the start and clears the undo history.
            var normalized = WithoutLoneSurrogates(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
            Text = normalized;
            _text = _userEdited is null ? null : AsHeld(normalized);
        }
        finally
        {
            _settingText = false;
        }
    }

    /// <summary>
    /// <paramref name="text"/>, just set, as the editor holds it: Terminal.Gui keeps each character composed (NFC), so a
    /// decomposed é (as macOS file names have it) or the ohm sign comes back changed. Read back only when it can be.
    /// </summary>
    private string AsHeld(string text)
    {
        try
        {
            if (text.IsNormalized())
            {
                return text;
            }
        }
        catch (ArgumentException)
        {
            // Normalizing refuses some characters (U+FFFE): Terminal.Gui keeps those as they are, and composes the rest.
        }

        return GetText();
    }

    /// <summary>
    /// <paramref name="text"/> with U+FFFD in place of each half of a surrogate pair found alone (as a cut by UTF-16 index
    /// leaves one), which Terminal.Gui fails on when it loads the text; typing inserts U+FFFD for one too. Copies the text
    /// only when it holds one.
    /// </summary>
    private static string WithoutLoneSurrogates(string text)
    {
        var span = text.AsSpan();
        var at = span.IndexOfAnyInRange('\uD800', '\uDFFF');
        while (at >= 0 && char.IsHighSurrogate(span[at]) && at + 1 < span.Length && char.IsLowSurrogate(span[at + 1]))
        {
            var next = span[(at + 2)..].IndexOfAnyInRange('\uD800', '\uDFFF');
            at = next < 0 ? -1 : at + 2 + next;
        }

        return at < 0 ? text : string.Create(text.Length, (Text: text, First: at), static (chars, state) =>
        {
            state.Text.CopyTo(chars);
            for (var i = state.First; i < chars.Length; i++)
            {
                if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                {
                    i++;
                }
                else if (char.IsSurrogate(chars[i]))
                {
                    chars[i] = '\uFFFD';
                }
            }
        });
    }

    public override void OnContentsChanged()
    {
        ForgetColors();
        FindAgain();
        _contentsChanged = true;
        base.OnContentsChanged();
        if (_settingText || _userEdited is not { } edited)
        {
            return;
        }

        // Terminal.Gui also reports keys that change nothing (Backspace at the start, Delete at the end…).
        var text = GetText();
        if (text != _text)
        {
            _text = text;
            edited(this, text);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Terminal.Gui 2.5 deletes words (Ctrl+Backspace, Ctrl+Delete) and cuts to the start or end of a line
    /// (Ctrl+Shift+Backspace, Ctrl+K) without <see cref="OnContentsChanged"/>, so their keys run those commands here and
    /// report the change.
    /// </remarks>
    protected override bool OnKeyDown(Key key)
    {
        // TextView first offers each key to autocomplete, after measuring the selected text (which it builds anew: seconds
        // for a large selection). There is nothing to offer unless suggestions were generated, and TermRest has none.
        if (Autocomplete.Suggestions.Count > 0 && base.OnKeyDown(key))
        {
            return true;
        }

        if (ReadOnly || !KeyBindings.TryGet(key, out var binding)
            || !binding.Commands.Any(c => c is Command.KillWordLeft or Command.KillWordRight or Command.CutToStartOfLine or Command.CutToEndOfLine))
        {
            return false;
        }

        // They only delete: the text changed when it has fewer characters or lines. A selection ends first, as TextView ends it
        // for Ctrl+Delete: it would stay on, its start past what is left of the text, and the next key would fail on it.
        var size = (Lines, GetAllLines().Sum(line => line.Count));
        _contentsChanged = false;
        IsSelecting = false;
        InvokeCommands(binding.Commands, binding);
        if (!_contentsChanged && (Lines, GetAllLines().Sum(line => line.Count)) != size)
        {
            OnContentsChanged();
        }

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// TextView measures the selected text (building it anew: seconds for a large selection) before it looks at a mouse
    /// event, to offer the event to its completions when nothing is selected. The selection is hidden from that measure:
    /// the completions show it again, before TextView handles the event.
    /// </remarks>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (!IsSelecting)
        {
            return base.OnMouseEvent(mouse);
        }

        _selectionHidden = true;
        IsSelecting = false;
        try
        {
            return base.OnMouseEvent(mouse);
        }
        finally
        {
            ShowHiddenSelection();
        }
    }

    private void ShowHiddenSelection()
    {
        if (_selectionHidden)
        {
            _selectionHidden = false;
            IsSelecting = true;
        }
    }

    /// <summary>
    /// Copies or cuts the selection as TextView does. Without one, TextView's copy takes the current line, and a cut does
    /// nothing (TextView's would take the text from where the last selection started, or from the start, to the cursor);
    /// in an editor whose words wrap, TextView's own cut and copy also keep its undo history and wrapping right.
    /// </summary>
    private bool CopySelection(bool cut)
    {
        // TextView's copy and cut also forget the column that Up and Down keep the cursor in; its handling of a key that
        // nothing took does only that for a Ctrl key.
        base.OnKeyDownNotHandled(Key.C.WithCtrl);
        if (cut && !IsSelecting)
        {
            return true;
        }

        if (!IsSelecting || (WordWrap && !ReadOnly) || SelectionText() is not { } text)
        {
            var done = cut ? Cut() : Copy();
            ForgetCopiedLines(); // they would keep the text's lines in memory too
            return done;
        }

        // On the clipboard first, as TextView does: a cut that can't get there leaves the text as it was.
        App?.Clipboard?.SetClipboardData(text);
        if (cut && !ReadOnly)
        {
            // Deleted as Backspace deletes a selection, from its start: undoing it then puts the cursor back there, as after
            // TextView's cut, which also leaves the selection's start there.
            var forward = Encode(SelectionStartRow, SelectionStartColumn) < Encode(CurrentRow, CurrentColumn);
            if (forward)
            {
                var (start, viewport) = (new Point(SelectionStartColumn, SelectionStartRow), Viewport);
                SetSelectionStart(CurrentRow, CurrentColumn);
                InsertionPoint = start;
                Viewport = viewport;
            }

            DeleteCharLeft();
            if (forward)
            {
                SetSelectionStart(CurrentRow, CurrentColumn);
                IsSelecting = false;
            }
        }
        else if (!ReadOnly)
        {
            // TextView's copy notes that a selection was copied (a paste then goes in at the cursor, not as a line above
            // it) and scrolls to the cursor: it does that at no cost for an empty selection, here kept off the clipboard.
            var (row, column) = (SelectionStartRow, SelectionStartColumn);
            var driver = App?.Driver;
            var clipboard = driver?.Clipboard;
            SetSelectionStart(CurrentRow, CurrentColumn);
            try
            {
                driver?.Clipboard = null;
                Copy();
            }
            finally
            {
                driver?.Clipboard = clipboard;
                SetSelectionStart(row, column);
            }
        }
        else
        {
            // Read-only: TextView's cut only copies, and ends the selection. Its copy scrolls to the cursor, and so does its
            // cut when words wrap (it wraps them again).
            IsSelecting = !cut;
            SetNeedsDraw();
            if (!cut || WordWrap)
            {
                InsertionPoint = new Point(CurrentColumn, CurrentRow);
            }
        }

        ForgetCopiedLines();
        return true;
    }

    /// <summary>Moves the selection's start, without the scrolling to the cursor that TextView does then.</summary>
    private void SetSelectionStart(int row, int column)
    {
        var viewport = Viewport;
        SelectionStartRow = row;
        SelectionStartColumn = column;
        Viewport = viewport;
    }

    /// <summary>
    /// The selected text as TextView's copy makes it: lines joined with <see cref="Environment.NewLine"/>. When words wrap,
    /// rows are parts of lines; null if they do not add up to the text.
    /// </summary>
    private string? SelectionText()
    {
        var (anchor, cursor) = (Encode(SelectionStartRow, SelectionStartColumn), Encode(CurrentRow, CurrentColumn));
        var (start, end) = anchor > cursor ? (cursor, anchor) : (anchor, cursor);
        var (firstRow, firstColumn, lastRow, lastColumn) = ((int)(start >> 32), (int)(uint)start, (int)(end >> 32), (int)(uint)end);
        var selected = new StringBuilder();
        if (!WordWrap)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                var line = GetLine(row);
                if (row > firstRow)
                {
                    selected.Append(Environment.NewLine);
                }

                for (var i = row == firstRow ? firstColumn : 0; i < (row == lastRow ? lastColumn : line.Count); i++)
                {
                    selected.Append(line[i].Grapheme);
                }
            }

            return selected.ToString();
        }

        // The line breaks are where the whole text (TextView's Text, unwrapped) has them.
        var text = Text;
        var at = 0;
        for (var row = 0; row <= lastRow; row++)
        {
            if (row > 0 && text.AsSpan(at).StartsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                at += Environment.NewLine.Length;
                if (row > firstRow)
                {
                    selected.Append(Environment.NewLine);
                }
            }

            var line = GetLine(row);
            for (var i = 0; i < (row == lastRow ? lastColumn : line.Count); i++)
            {
                var grapheme = line[i].Grapheme;
                if (!text.AsSpan(at).StartsWith(grapheme, StringComparison.Ordinal))
                {
                    return null;
                }

                at += grapheme.Length;
                if (row > firstRow || (row == firstRow && i >= firstColumn))
                {
                    selected.Append(grapheme);
                }
            }
        }

        return selected.ToString();
    }

    /// <summary>Text with the editor's line endings normalized to LF.</summary>
    public string GetText() => Text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private bool Highlighting => _lexer is not null;

    /// <inheritdoc/>
    /// <remarks>Setting <see cref="TextView.Text"/> replaces the text without <see cref="OnContentsChanged"/>.</remarks>
    protected override void OnTextChanged()
    {
        ForgetColors();
        FindAgain();
        base.OnTextChanged();
    }

    /// <summary>The colors are worked out again from the first row (the text, its language, or its rows changed).</summary>
    private void ForgetColors()
    {
        _linesLexed = 0;
        _wrappedText = null;
        _wrappedAt = 0;
        _rowEndsLine.Clear();
        _rowsUnmatched = false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Draws as <see cref="TextView"/> does (the same cells, colors and clearing), but only the rows in view: Terminal.Gui
    /// 2.5 goes through every line from the top of the view to the end of the text, which takes seconds for a large
    /// response. Its colors are set here, not by the OnDraw*Color methods, which look up each cell's place in the
    /// unwrapped text when words wrap; the Draw*Color events are not raised.
    /// </remarks>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        // TextView wraps words again (without a change of contents) when the view's width changes: the rows are other parts
        // of the lines then. It keeps the cursor and the selection on the same text, so the match selected stays so.
        var layout = (WordWrap, WordWrap ? Viewport.Width : 0);
        if (layout != _rowsLayout)
        {
            var widthOnly = layout.WordWrap == _rowsLayout.Wrap;
            _rowsLayout = layout;
            ForgetColors();
            FindAgain(keepCurrent: widthOnly);
        }

        var colors = new CellColors
        {
            Editable = GetAttributeForRole(VisualRole.Editable),
            ReadOnly = GetAttributeForRole(VisualRole.ReadOnly),
            Selected = GetAttributeForRole(VisualRole.Active),
            Focus = GetAttributeForRole(VisualRole.Focus),
            Match = MatchColors(),
            Highlighting = Highlighting,
        };
        if (colors.Highlighting)
        {
            LexLinesInView();
            var palette = ThemeCatalog.Current.Palette;
            var text = ReadOnly ? colors.ReadOnly : colors.Editable;
            for (var kind = 0; kind < _syntaxAttributes.Length; kind++)
            {
                var color = palette.SyntaxColor((SyntaxKind)kind);
                var readable = !color.Equals(Color.None)
                               && (text.Background.Equals(Color.None) || ThemePalette.Contrast(color, text.Background) >= MinimumContrast);
                _syntaxAttributes[kind] = new Attribute(readable ? color : text.Foreground, text.Background, text.Style);
            }
        }

        if (IsSelecting)
        {
            var anchor = Encode(SelectionStartRow, SelectionStartColumn);
            var cursor = Encode(CurrentRow, CurrentColumn);
            (colors.SelectionStart, colors.SelectionEnd) = anchor > cursor ? (cursor, anchor) : (anchor, cursor);
        }

        colors.UsedRow = !IsSelecting && !Used && HasFocus ? CurrentRow : -1;
        SetAttributeForRole(Enabled ? VisualRole.Editable : VisualRole.Disabled);
        var viewport = Viewport;
        var row = 0;
        for (var index = viewport.Y; index < Lines && row < viewport.Height; index++, row++)
        {
            DrawLine(GetLine(index), index, row, viewport, colors);
        }

        if (row < viewport.Height)
        {
            SetAttributeForRole(ReadOnly ? VisualRole.ReadOnly : VisualRole.Editable);
            ClearCells(0, row, viewport.Width, viewport.Height);
        }

        return false;
    }

    /// <summary>The colors of a redraw (they do not change while it draws).</summary>
    private struct CellColors
    {
        public Attribute Editable;
        public Attribute ReadOnly;
        public Attribute Selected;
        public Attribute Focus;
        public Attribute Match;
        public bool Highlighting;
        public long SelectionStart;
        public long SelectionEnd;
        public int UsedRow;
    }

    /// <summary>A position as <see cref="TextView"/> compares them: the row in the high half, the column in the low one.</summary>
    private static long Encode(int row, int column) => ((long)(uint)row << 32) | (uint)column;

    /// <summary>Draws line <paramref name="index"/> on <paramref name="row"/> of the view, as <see cref="TextView"/> does.</summary>
    private void DrawLine(List<Cell> line, int index, int row, Rectangle viewport, in CellColors colors)
    {
        var right = viewport.Width;
        var count = line.Count;
        MatchesOnRow(index);

        // Where the view's left edge cuts the line: TextView takes the widths of the first Viewport.X cells (cells, not
        // columns) and starts with the cell that goes past it, at a negative column when it is cut.
        var (col, start, before) = (0, 0, 0);
        if (viewport.X > 0 && count > 0)
        {
            var cells = Math.Min(viewport.X, count);
            var (sum, previous) = (0, 0);
            (col, start, before) = (int.MinValue, cells, 0);
            for (var i = 0; i < cells; i++)
            {
                var grapheme = line[i].Grapheme;
                sum += grapheme == "\t" ? (TabWidth > 0 ? TabWidth - (sum % TabWidth) : 0) : Math.Max(grapheme.GetColumns(), 1);
                if (sum > viewport.X)
                {
                    (col, start, before) = (previous - viewport.X, i, previous);
                    break;
                }

                previous = sum;
            }

            if (col == int.MinValue)
            {
                (col, before) = (sum - viewport.X, sum);
            }
        }

        var wideCutOnTheLeft = false;
        Move(0, row);
        for (var i = start; i < count; i++)
        {
            var text = line[i].Grapheme;
            var cols = text.GetColumns(false);
            SetCellColor(line[i], i, index, colors);
            if (text == "\t")
            {
                cols = TabWidth > 0 ? TabWidth - (before % TabWidth) : 0;
                if (col + cols > right)
                {
                    cols = right - col;
                }

                for (var k = 0; k < cols; k++)
                {
                    if (col + k < right)
                    {
                        AddRune(col + k, row, (Rune)' ');
                    }
                }
            }
            else
            {
                if (col < 0 && cols > 1)
                {
                    wideCutOnTheLeft = true;
                }
                else
                {
                    AddStr(col, row, text);
                }

                cols = Math.Max(cols, 1);
            }

            if (col + cols > viewport.Right)
            {
                break;
            }

            col += cols;
            before += cols;
            if (i + 1 < count && col + line[i + 1].Grapheme.GetColumns() > right)
            {
                break;
            }
        }

        if (wideCutOnTheLeft)
        {
            AddStr(0, row, " ");
        }

        if (col < right)
        {
            // From the left edge only: the columns left of it are clipped, and a short line ends as far left of it as the view
            // is scrolled right (to the end of a long line, maybe a million columns), each a step.
            SetAttributeForRole(ReadOnly ? VisualRole.ReadOnly : VisualRole.Editable);
            ClearCells(Math.Max(col, 0), row, right, row + 1);
        }
    }

    /// <summary>
    /// The color of a cell, chosen as TextView's OnDraw*Color methods do: selected, overwrite cursor, a match of Find,
    /// syntax colors (in read-only text too, where plain text keeps the read-only color), read-only, normal.
    /// </summary>
    private void SetCellColor(Cell cell, int column, int index, in CellColors colors)
    {
        if (IsSelecting && Encode(index, column) is var position && position >= colors.SelectionStart && position <= colors.SelectionEnd - 1)
        {
            var selected = colors.Selected;
            SetAttribute(cell.Attribute is { } own ? new Attribute(selected.Foreground, selected.Background, own.Style) : selected);
        }
        else if (index == colors.UsedRow && column == CurrentColumn)
        {
            var used = cell.Attribute ?? colors.Focus;
            SetAttribute(new Attribute(used.Background, used.Foreground, used.Style));
        }
        else if (InMatch(column))
        {
            SetAttribute(colors.Match);
        }
        else if (colors.Highlighting && index < _linesLexed && column < _lineKinds[index].Length && (!ReadOnly || _lineKinds[index][column] != SyntaxKind.Plain))
        {
            SetAttribute(_syntaxAttributes[(int)_lineKinds[index][column]]);
        }
        else if (ReadOnly)
        {
            var readOnly = cell.Attribute ?? colors.ReadOnly;
            SetAttribute(readOnly.Foreground == readOnly.Background
                ? new Attribute(readOnly.Foreground, readOnly.Background, readOnly.Style)
                : colors.ReadOnly);
        }
        else
        {
            SetAttribute(cell.Attribute ?? colors.Editable);
        }
    }

    private void ClearCells(int left, int top, int right, int bottom)
    {
        for (var row = top; row < bottom; row++)
        {
            Move(left, row);
            for (var col = left; col < right; col++)
            {
                AddRune(col, row, (Rune)' ');
            }
        }
    }

    /// <summary>
    /// Colors the lines not colored yet down to the last one in view (a line's colors depend on the lines above it). When
    /// words wrap, each line is colored whole, and its colors split over the rows it takes.
    /// </summary>
    private void LexLinesInView()
    {
        var last = Math.Min(Lines, Viewport.Y + Viewport.Height);
        while (_linesLexed < last)
        {
            var first = _linesLexed;
            var rows = WordWrap ? RowsOfLine(first) : 1;
            var (length, _) = LoadLine(first, rows);
            Grow(ref _charKinds, length);
            var charKinds = _charKinds.AsSpan(0, length);
            charKinds.Clear();
            var end = _lexer!.Lex(_chars.AsSpan(0, length), charKinds, first == 0 ? LexState.Code : _lineEnds[first - 1]);

            var cell = 0;
            for (var row = first; row < first + rows; row++)
            {
                var count = GetLine(row).Count;
                var kinds = row < _lineKinds.Count && _lineKinds[row].Length == count ? _lineKinds[row] : new SyntaxKind[count];
                for (var i = 0; i < count; i++, cell++)
                {
                    kinds[i] = _cellStarts[cell] < length ? charKinds[_cellStarts[cell]] : SyntaxKind.Plain;
                }

                if (row < _lineKinds.Count)
                {
                    _lineKinds[row] = kinds;
                    _lineEnds[row] = end;
                }
                else
                {
                    _lineKinds.Add(kinds);
                    _lineEnds.Add(end);
                }
            }

            _linesLexed = first + rows;
        }

        if (_linesLexed == Lines && _lineKinds.Count > Lines)
        {
            _lineKinds.RemoveRange(Lines, _lineKinds.Count - Lines);
            _lineEnds.RemoveRange(Lines, _lineEnds.Count - Lines);
        }
    }

    /// <summary>How many rows the line that starts at row <paramref name="first"/> takes, when words wrap.</summary>
    private int RowsOfLine(int first)
    {
        var row = first;
        while (row + 1 < Lines && !RowEndsLine(row))
        {
            row++;
        }

        return row - first + 1;
    }

    /// <summary>
    /// Whether a line ends with <paramref name="row"/>, when words wrap: rows are matched in order to TextView's whole text
    /// (where the lines are), as far as needed. A row whose text is not where the previous one ended ends a line.
    /// </summary>
    private bool RowEndsLine(int row)
    {
        while (_rowEndsLine.Count <= row)
        {
            var next = _rowEndsLine.Count;
            if (_rowsUnmatched)
            {
                _rowEndsLine.Add(true);
                continue;
            }

            _wrappedText ??= Text;
            var at = _wrappedAt;
            foreach (var cell in GetLine(next))
            {
                var grapheme = cell.Grapheme ?? "";
                if (!_wrappedText.AsSpan(at).StartsWith(grapheme, StringComparison.Ordinal))
                {
                    _rowsUnmatched = true;
                    break;
                }

                at += grapheme.Length;
            }

            var ends = _rowsUnmatched || next == Lines - 1 || _wrappedText.AsSpan(at).StartsWith(Environment.NewLine, StringComparison.Ordinal);
            _wrappedAt = ends ? at + Environment.NewLine.Length : at;
            _rowEndsLine.Add(ends);
        }

        return _rowEndsLine[row];
    }

    private static void Grow<T>(ref T[] array, int size)
    {
        if (array.Length < size)
        {
            Array.Resize(ref array, Math.Max(size, array.Length * 2));
        }
    }

    /// <summary>
    /// Reads the line that rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="rows"/> - 1 make into
    /// <see cref="_chars"/>, and where each of its cells (a grapheme) starts into <see cref="_cellStarts"/>.
    /// </summary>
    private (int Length, int Cells) LoadLine(int first, int rows)
    {
        var (length, count) = (0, 0);
        for (var row = first; row < first + rows; row++)
        {
            var cells = GetLine(row);
            Grow(ref _cellStarts, count + cells.Count);
            for (var i = 0; i < cells.Count; i++)
            {
                var grapheme = cells[i].Grapheme ?? "";
                _cellStarts[count + i] = length;
                Grow(ref _chars, length + grapheme.Length);
                grapheme.CopyTo(_chars.AsSpan(length));
                length += grapheme.Length;
            }

            count += cells.Count;
        }

        return (length, count);
    }

    // ----- Find ----------------------------------------------------------------------------

    /// <summary>At most this many matches are found: Find then counts "100000+".</summary>
    internal const int MaxMatches = 100_000;

    /// <summary>A match of Find: from its first cell to the cell after its last (TextView's rows and columns).</summary>
    private readonly record struct Match(int StartRow, int StartColumn, int EndRow, int EndColumn);

    /// <summary>Raised when the matches of <see cref="Find"/> changed: found anew, or after the text or its rows changed.</summary>
    public event EventHandler? MatchesChanged;

    /// <summary>How many matches <see cref="Find"/> found (at most <see cref="MaxMatches"/>).</summary>
    public int MatchCount => _matches.Count;

    /// <summary>Whether there are more matches than <see cref="MatchCount"/>.</summary>
    public bool MoreMatches { get; private set; }

    /// <summary>The match selected (from 0), or -1.</summary>
    public int CurrentMatch => _currentMatch;

    /// <summary>
    /// Highlights each place <paramref name="text"/> is (ignoring case unless <paramref name="matchCase"/>; null or empty for
    /// none), and selects the first match from the one selected, or from the cursor: a longer text keeps to the match it
    /// grew from. The matches are found again when the text or its rows change.
    /// </summary>
    public void Find(string? text, bool matchCase)
    {
        var from = _currentMatch >= 0 ? (_matches[_currentMatch].StartRow, _matches[_currentMatch].StartColumn) : (CurrentRow, CurrentColumn);
        var selected = _currentMatch >= 0 && IsSelecting;
        _findText = string.IsNullOrEmpty(text) ? null : text;
        _findMatchCase = matchCase;
        FindMatches();
        if (_findText is not null && _matches.Count > 0)
        {
            SelectMatch(MatchFrom(from.Item1, from.Item2));
        }
        else if (selected && _findText is not null)
        {
            IsSelecting = false; // the text the match was is no longer looked for
        }
    }

    /// <summary>Selects the next match after the selected one (or the cursor), or the one before it, going round.</summary>
    public void SelectNextMatch(bool backwards = false)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        // The cursor moved since a match was selected: from where it is now.
        if (_currentMatch >= 0 && (CurrentRow, CurrentColumn) != (_matches[_currentMatch].EndRow, _matches[_currentMatch].EndColumn))
        {
            _currentMatch = -1;
        }

        SelectMatch(_currentMatch >= 0 ? _currentMatch + (backwards ? -1 : 1)
            : backwards ? MatchFrom(CurrentRow, CurrentColumn) - 1 : MatchFrom(CurrentRow, CurrentColumn));
    }

    /// <summary>Selects match <paramref name="index"/> (going round), with the cursor at its end, and scrolls to it.</summary>
    public void SelectMatch(int index)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _currentMatch = ((index % _matches.Count) + _matches.Count) % _matches.Count;
        var match = _matches[_currentMatch];
        InsertionPoint = new Point(match.EndColumn, match.EndRow);
        SelectionStartRow = match.StartRow;
        SelectionStartColumn = match.StartColumn;
        SetNeedsDraw();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The first match that starts at or after the position (0 past the last: Find goes round).</summary>
    private int MatchFrom(int row, int column)
    {
        var position = Encode(row, column);
        var (low, high) = (0, _matches.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Encode(_matches[middle].StartRow, _matches[middle].StartColumn) < position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low == _matches.Count ? 0 : low;
    }

    /// <summary>Finds the text looked for again, if any: the text or its rows changed.</summary>
    private void FindAgain(bool keepCurrent = false)
    {
        if (_findText is not null)
        {
            FindMatches(keepCurrent);
        }
    }

    /// <summary>
    /// Finds every match, line by line (when words wrap, a line's rows together), each after the one before it. A match
    /// that starts or ends inside a cell (a grapheme: part of an emoji) covers that cell. The match selected stays so when
    /// <paramref name="keepCurrent"/> (the same text, wrapped again).
    /// </summary>
    private void FindMatches(bool keepCurrent = false)
    {
        var current = keepCurrent ? _currentMatch : -1;
        _matches.Clear();
        _currentMatch = -1;
        MoreMatches = false;
        if (_findText is { } text)
        {
            var comparison = _findMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (var first = 0; first < Lines && !MoreMatches;)
            {
                var rows = WordWrap ? RowsOfLine(first) : 1;
                var (length, cells) = LoadLine(first, rows);
                var line = _chars.AsSpan(0, length);

                // Where the cells of the line's rows start: matches are in order, so it only moves on.
                var (row, rowStart, rowCells) = (first, 0, GetLine(first).Count);
                for (var at = 0; at < length;)
                {
                    var found = line[at..].IndexOf(text, comparison);
                    if (found < 0)
                    {
                        break;
                    }

                    var (start, end) = (at + found, at + found + text.Length);
                    var startCell = CellAt(start, cells);
                    while (startCell >= rowStart + rowCells && row + 1 < first + rows)
                    {
                        (row, rowStart, rowCells) = (row + 1, rowStart + rowCells, GetLine(row + 1).Count);
                    }

                    var (startRow, startColumn) = (row, startCell - rowStart);
                    var endCell = CellAt(end - 1, cells) + 1;
                    while (endCell > rowStart + rowCells && row + 1 < first + rows)
                    {
                        (row, rowStart, rowCells) = (row + 1, rowStart + rowCells, GetLine(row + 1).Count);
                    }

                    _matches.Add(new Match(startRow, startColumn, row, endCell - rowStart));
                    if (_matches.Count == MaxMatches)
                    {
                        MoreMatches = true;
                        break;
                    }

                    at = end;
                }

                first += rows;
            }
        }

        _currentMatch = current < _matches.Count ? current : -1;
        SetNeedsDraw();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The cell of the line loaded by <see cref="LoadLine"/> that character <paramref name="index"/> is part of.</summary>
    private int CellAt(int index, int cells)
    {
        var found = Array.BinarySearch(_cellStarts, 0, cells, index);
        return found >= 0 ? found : ~found - 1;
    }

    /// <summary>Notes which columns of row <paramref name="index"/> the matches cover, for <see cref="InMatch"/>.</summary>
    private void MatchesOnRow(int index)
    {
        _rowMatches.Clear();
        if (_matches.Count == 0)
        {
            return;
        }

        // The first match that does not end before the row starts (they end in order too).
        var (low, high) = (0, _matches.Count);
        while (low < high)
        {
            var middle = (low + high) / 2;
            var match = _matches[middle];
            if (match.EndRow < index || (match.EndRow == index && match.EndColumn == 0))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var i = low; i < _matches.Count && _matches[i].StartRow <= index; i++)
        {
            var match = _matches[i];
            _rowMatches.Add((match.StartRow == index ? match.StartColumn : 0, match.EndRow == index ? match.EndColumn : int.MaxValue));
        }
    }

    /// <summary>Whether a match covers <paramref name="column"/> of the row being drawn.</summary>
    private bool InMatch(int column)
    {
        foreach (var (from, to) in _rowMatches)
        {
            if (column >= from && column < to)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The colors of matches: dark or light text (whichever reads better) on the theme's warning color, a highlighter's
    /// yellow in most themes. The selected match has the selection's colors.
    /// </summary>
    private Attribute MatchColors()
    {
        var background = ThemeCatalog.Current.Palette.Warning;
        if (background.Equals(Color.None))
        {
            var focus = GetAttributeForRole(VisualRole.Focus);
            return new Attribute(focus.Foreground, focus.Background, TextStyle.Underline);
        }

        var (black, white) = (new Color(0, 0, 0), new Color(255, 255, 255));
        return new Attribute(ThemePalette.Contrast(black, background) >= ThemePalette.Contrast(white, background) ? black : white, background);
    }
}

#pragma warning restore CS0618
