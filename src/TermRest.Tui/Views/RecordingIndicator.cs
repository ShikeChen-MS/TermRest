using System.Globalization;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using TermRest.Tui.Theming;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermRest.Tui.Views;

/// <summary>
/// The record button at the right end of the menu bar. Idle it reads <c>● Record F12</c> with a red dot; while recording
/// it turns into a red badge with a blinking dot, the elapsed time and the number of requests recorded:
/// <c>● REC 01:23 · 3 requests · Stop F12</c>. A click starts or stops recording.
/// </summary>
internal sealed class RecordingIndicator : View
{
    private DateTimeOffset? _since;
    private int _requests;
    private bool _dotVisible = true;

    public RecordingIndicator()
    {
        X = Pos.AnchorEnd();
        Y = 0;
        Height = 1;
        CanFocus = false;
        SchemeName = "Menu";
        MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                Clicked?.Invoke(this, EventArgs.Empty);
                mouse.Handled = true;
            }
        };
        Update();
    }

    /// <summary>The user clicked the button.</summary>
    public event EventHandler? Clicked;

    /// <summary>Whether it shows a recording in progress.</summary>
    public bool IsRecording => _since is not null;

    /// <summary>The text shown, without colors (for tests).</summary>
    public string Caption => string.Concat(Segments().Select(s => s.Text));

    /// <summary>Shows the idle button.</summary>
    public void ShowIdle()
    {
        _since = null;
        _requests = 0;
        _dotVisible = true;
        Update();
    }

    /// <summary>Shows the recording badge: since when, and how many requests so far.</summary>
    public void ShowRecording(DateTimeOffset since, int requests)
    {
        _since = since;
        _requests = requests;
        Update();
    }

    /// <summary>Advances the clock and blinks the dot (call about twice a second while recording).</summary>
    public void Tick()
    {
        _dotVisible = !_dotVisible;
        Update();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var x = 0;
        foreach (var (text, attribute) in Segments())
        {
            SetAttribute(attribute);
            AddStr(x, 0, text);
            x += text.Length;
        }

        return true;
    }

    private void Update()
    {
        var width = Caption.Length;
        if (Width is not DimAbsolute { Size: var current } || current != width)
        {
            Width = width;
        }

        SetNeedsDraw();
    }

    private List<(string Text, Attribute Attribute)> Segments()
    {
        var bar = GetAttributeForRole(VisualRole.Normal);
        var key = GetAttributeForRole(VisualRole.HotNormal);
        var red = ThemeCatalog.Current.Palette.Error;
        if (red.Equals(Color.None) || red.Equals(bar.Background))
        {
            red = Color.Red;
        }

        if (_since is not { } since)
        {
            return
            [
                (" ", bar),
                ("●", new Attribute(red, bar.Background, TextStyle.Bold)),
                (" Record ", bar),
                ("F12", new Attribute(key.Foreground, key.Background, key.Style & ~TextStyle.Underline)),
                (" ", bar),
            ];
        }

        // White or black on red, whichever reads better.
        var badge = new Attribute(red.IsDarkColor() ? Color.White : Color.Black, red, TextStyle.Bold);
        var elapsed = DateTimeOffset.Now - since;
        var clock = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        var dot = _dotVisible ? "●" : " ";
        var narrow = (SuperView?.Viewport.Width ?? 120) < 100;
        var requests = narrow ? _requests.ToString(CultureInfo.InvariantCulture) : _requests == 1 ? "1 request" : $"{_requests} requests";
        return
        [
            ($" {dot} REC {clock} · {requests} · Stop ", badge),
            (narrow ? "" : "F12 ", badge),
            (" ", bar),
        ];
    }
}
