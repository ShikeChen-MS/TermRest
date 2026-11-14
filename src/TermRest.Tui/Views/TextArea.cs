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

    /// <summary>The script language (<see cref="Core.Scripting.ScriptLanguage"/>) whose syntax is colored; null for plain text.</summary>
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
            _linesLexed = 0;
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
        _linesLexed = 0;
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

    /// <summary>Colors are kept by line, so wrapped text (whose rows are parts of lines) is left plain.</summary>
    private bool Highlighting => _lexer is not null && !WordWrap;

    /// <inheritdoc/>
    /// <remarks>Setting <see cref="TextView.Text"/> replaces the text without <see cref="OnContentsChanged"/>.</remarks>
    protected override void OnTextChanged()
    {
        _linesLexed = 0;
        base.OnTextChanged();
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
        var colors = new CellColors
        {
            Editable = GetAttributeForRole(VisualRole.Editable),
            ReadOnly = GetAttributeForRole(VisualRole.ReadOnly),
            Selected = GetAttributeForRole(VisualRole.Active),
            Focus = GetAttributeForRole(VisualRole.Focus),
            Highlighting = Highlighting,
        };
        if (colors.Highlighting)
        {
            LexLinesInView();
            var palette = ThemeCatalog.Current.Palette;
            for (var kind = 0; kind < _syntaxAttributes.Length; kind++)
            {
                var color = palette.SyntaxColor((SyntaxKind)kind);
                var readable = !color.Equals(Color.None)
                               && (colors.Editable.Background.Equals(Color.None) || ThemePalette.Contrast(color, colors.Editable.Background) >= MinimumContrast);
                _syntaxAttributes[kind] = new Attribute(readable ? color : colors.Editable.Foreground, colors.Editable.Background, colors.Editable.Style);
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
    /// The color of a cell, chosen as TextView's OnDraw*Color methods do: selected, overwrite cursor, read-only, script
    /// colors, normal.
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
        else if (ReadOnly)
        {
            var readOnly = cell.Attribute ?? colors.ReadOnly;
            SetAttribute(readOnly.Foreground == readOnly.Background
                ? new Attribute(readOnly.Foreground, readOnly.Background, readOnly.Style)
                : colors.ReadOnly);
        }
        else if (colors.Highlighting && index < _linesLexed && column < _lineKinds[index].Length)
        {
            SetAttribute(_syntaxAttributes[(int)_lineKinds[index][column]]);
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

    /// <summary>Colors the lines not colored yet down to the last one in view (a line's colors depend on the lines above it).</summary>
    private void LexLinesInView()
    {
        var last = Math.Min(Lines, Viewport.Y + Viewport.Height);
        for (; _linesLexed < last; _linesLexed++)
        {
            var row = _linesLexed;
            var cells = GetLine(row);

            // The line's text, and where each cell (a grapheme) starts in it.
            Grow(ref _cellStarts, cells.Count);
            var length = 0;
            for (var i = 0; i < cells.Count; i++)
            {
                var grapheme = cells[i].Grapheme ?? "";
                _cellStarts[i] = length;
                Grow(ref _chars, length + grapheme.Length);
                grapheme.CopyTo(_chars.AsSpan(length));
                length += grapheme.Length;
            }

            Grow(ref _charKinds, length);
            var charKinds = _charKinds.AsSpan(0, length);
            charKinds.Clear();
            var end = _lexer!.Lex(_chars.AsSpan(0, length), charKinds, row == 0 ? LexState.Code : _lineEnds[row - 1]);

            var kinds = row < _lineKinds.Count && _lineKinds[row].Length == cells.Count ? _lineKinds[row] : new SyntaxKind[cells.Count];
            for (var i = 0; i < cells.Count; i++)
            {
                kinds[i] = _cellStarts[i] < length ? charKinds[_cellStarts[i]] : SyntaxKind.Plain;
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

        if (_linesLexed == Lines && _lineKinds.Count > Lines)
        {
            _lineKinds.RemoveRange(Lines, _lineKinds.Count - Lines);
            _lineEnds.RemoveRange(Lines, _lineEnds.Count - Lines);
        }
    }

    private static void Grow<T>(ref T[] array, int size)
    {
        if (array.Length < size)
        {
            Array.Resize(ref array, Math.Max(size, array.Length * 2));
        }
    }
}

#pragma warning restore CS0618
