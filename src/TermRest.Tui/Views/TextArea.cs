using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace TermRest.Tui.Views;

#pragma warning disable CS0618 // TextView is superseded by tui-cs/Editor; switch once Terminal.Gui.Editor ships for Terminal.Gui 2.5.

/// <summary>
/// Multi-line editor/viewer used for bodies, scripts and response text. Isolates Terminal.Gui's (obsolete) TextView so
/// it can be swapped for the Terminal.Gui.Editor control (syntax highlighting, folding) without touching callers.
/// </summary>
internal sealed class TextArea : TextView
{
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
        base.OnContentsChanged();
        if (!_settingText)
        {
            UserEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Text with the editor's line endings normalized to LF.</summary>
    public string GetText() => Text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

#pragma warning restore CS0618
