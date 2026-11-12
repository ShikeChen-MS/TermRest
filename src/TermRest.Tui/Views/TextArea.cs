using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Tui.Syntax;
using TermRest.Tui.Theming;
using Attribute = Terminal.Gui.Drawing.Attribute;

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
        TabKeyAddsTab = false;
        WordWrap = false;
        ScrollBars = true;
    }

    /// <summary>Raised after the user edits the text (not for programmatic <see cref="SetText"/>).</summary>
    public event EventHandler? UserEdited;

    private bool _settingText;

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
            Text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            InsertionPoint = System.Drawing.Point.Empty;
            ClearHistoryChanges();
        }
        finally
        {
            _settingText = false;
        }
    }

    public override void OnContentsChanged()
    {
        _linesLexed = 0;
        base.OnContentsChanged();
        if (!_settingText)
        {
            UserEdited?.Invoke(this, EventArgs.Empty);
        }
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

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (Highlighting)
        {
            LexLinesInView();
            var editable = GetAttributeForRole(VisualRole.Editable);
            var palette = ThemeCatalog.Current.Palette;
            for (var kind = 0; kind < _syntaxAttributes.Length; kind++)
            {
                var color = palette.SyntaxColor((SyntaxKind)kind);
                var readable = !color.Equals(Color.None)
                               && (editable.Background.Equals(Color.None) || ThemePalette.Contrast(color, editable.Background) >= MinimumContrast);
                _syntaxAttributes[kind] = new Attribute(readable ? color : editable.Foreground, editable.Background, editable.Style);
            }
        }

        return base.OnDrawingContent(context);
    }

    protected override void OnDrawNormalColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (Highlighting && idxRow < _linesLexed && idxCol < _lineKinds[idxRow].Length)
        {
            SetAttribute(_syntaxAttributes[(int)_lineKinds[idxRow][idxCol]]);
            return;
        }

        base.OnDrawNormalColor(line, idxCol, idxRow);
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
