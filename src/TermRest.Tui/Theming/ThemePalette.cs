using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermRest.Tui.Theming;

/// <summary>
/// The colors of a theme. Every color the UI uses is derived from these (see <see cref="ToSchemes"/>), so a custom theme
/// only needs a few of them. <see cref="Color.None"/> stands for the terminal's own color.
/// </summary>
public sealed record ThemePalette
{
    /// <summary>Background of the main area.</summary>
    public Color Background { get; init; } = Color.None;

    /// <summary>Text of the main area.</summary>
    public Color Text { get; init; } = Color.None;

    /// <summary>Hints, read-only and disabled text.</summary>
    public Color Muted { get; init; } = Color.None;

    /// <summary>Hot keys (the underlined letters) and other highlights.</summary>
    public Color Accent { get; init; } = Color.None;

    /// <summary>Background of the focused item.</summary>
    public Color Selection { get; init; } = Color.None;

    /// <summary>Text of the focused item.</summary>
    public Color SelectionText { get; init; } = Color.None;

    /// <summary>Background of text fields and tables, and of items under the mouse.</summary>
    public Color Input { get; init; } = Color.None;

    /// <summary>Background of the menu bar, menus and the status bar.</summary>
    public Color Bar { get; init; } = Color.None;

    /// <summary>Text of the menu bar, menus and the status bar.</summary>
    public Color BarText { get; init; } = Color.None;

    /// <summary>Background of dialogs.</summary>
    public Color Dialog { get; init; } = Color.None;

    /// <summary>Text of dialogs.</summary>
    public Color DialogText { get; init; } = Color.None;

    /// <summary>Successful responses (1xx–3xx).</summary>
    public Color Success { get; init; } = Color.None;

    /// <summary>Client errors (4xx).</summary>
    public Color Warning { get; init; } = Color.None;

    /// <summary>Server errors (5xx), failed requests and error messages.</summary>
    public Color Error { get; init; } = Color.None;

    /// <summary>The GET method label in the collection tree.</summary>
    public Color Get { get; init; } = Color.None;

    /// <summary>The POST method label.</summary>
    public Color Post { get; init; } = Color.None;

    /// <summary>The PUT method label.</summary>
    public Color Put { get; init; } = Color.None;

    /// <summary>The PATCH method label.</summary>
    public Color Patch { get; init; } = Color.None;

    /// <summary>The DELETE method label.</summary>
    public Color Delete { get; init; } = Color.None;

    /// <summary>Other method labels (HEAD, OPTIONS …).</summary>
    public Color OtherMethods { get; init; } = Color.None;

    /// <summary>The palette's colors by their name in theme files (camelCase).</summary>
    internal static IReadOnlyList<(string Key, Func<ThemePalette, Color> Get, Func<ThemePalette, Color, ThemePalette> With)> Keys { get; } =
    [
        ("background", p => p.Background, (p, c) => p with { Background = c }),
        ("text", p => p.Text, (p, c) => p with { Text = c }),
        ("muted", p => p.Muted, (p, c) => p with { Muted = c }),
        ("accent", p => p.Accent, (p, c) => p with { Accent = c }),
        ("selection", p => p.Selection, (p, c) => p with { Selection = c }),
        ("selectionText", p => p.SelectionText, (p, c) => p with { SelectionText = c }),
        ("input", p => p.Input, (p, c) => p with { Input = c }),
        ("bar", p => p.Bar, (p, c) => p with { Bar = c }),
        ("barText", p => p.BarText, (p, c) => p with { BarText = c }),
        ("dialog", p => p.Dialog, (p, c) => p with { Dialog = c }),
        ("dialogText", p => p.DialogText, (p, c) => p with { DialogText = c }),
        ("success", p => p.Success, (p, c) => p with { Success = c }),
        ("warning", p => p.Warning, (p, c) => p with { Warning = c }),
        ("error", p => p.Error, (p, c) => p with { Error = c }),
        ("get", p => p.Get, (p, c) => p with { Get = c }),
        ("post", p => p.Post, (p, c) => p with { Post = c }),
        ("put", p => p.Put, (p, c) => p with { Put = c }),
        ("patch", p => p.Patch, (p, c) => p with { Patch = c }),
        ("delete", p => p.Delete, (p, c) => p with { Delete = c }),
        ("otherMethods", p => p.OtherMethods, (p, c) => p with { OtherMethods = c }),
    ];

    /// <summary>The color of an HTTP method's label.</summary>
    public Color MethodColor(string method) => method.ToUpperInvariant() switch
    {
        "GET" => Get,
        "POST" => Post,
        "PUT" => Put,
        "PATCH" => Patch,
        "DELETE" => Delete,
        _ => OtherMethods,
    };

    /// <summary>
    /// The Terminal.Gui schemes for this palette: Base (main area), Menu (menus and status bar), Dialog, Error, Accent
    /// (secondary panels), plus the status colors of responses.
    /// </summary>
    public Dictionary<string, Scheme> ToSchemes() => new(StringComparer.Ordinal)
    {
        ["Base"] = Surface(Text, Background),
        ["Menu"] = Surface(BarText, Bar),
        ["Dialog"] = Surface(DialogText, Dialog),
        ["Accent"] = Surface(Text, Input),
        ["Error"] = new(new Attribute(Error, Background))
        {
            HotNormal = new Attribute(Error, Background, TextStyle.Bold | TextStyle.Underline),
            Focus = new Attribute(Background, Error),
            HotFocus = new Attribute(Background, Error, TextStyle.Underline),
            Active = new Attribute(Background, Error, TextStyle.Bold),
            HotActive = new Attribute(Background, Error, TextStyle.Bold | TextStyle.Underline),
            Highlight = new Attribute(Background, Error),
            Editable = new Attribute(Text, Input),
            ReadOnly = new Attribute(Muted, Input, TextStyle.Italic),
            Disabled = new Attribute(Muted, Background, TextStyle.Faint),
        },
        [ThemeCatalog.SuccessScheme] = StatusScheme(Success),
        [ThemeCatalog.WarningScheme] = StatusScheme(Warning),
    };

    /// <summary>The scheme of a response status shown in <paramref name="color"/>.</summary>
    internal Scheme StatusScheme(Color color) => new(new Attribute(color, Background, TextStyle.Bold));

    /// <summary>A palette that matches existing schemes (for themes defined by their schemes, like Terminal.Gui's own).</summary>
    internal static ThemePalette FromSchemes(IReadOnlyDictionary<string, Scheme> schemes, ThemePalette statusAndMethods)
    {
        var main = schemes["Base"];
        var menu = schemes["Menu"];
        var dialog = schemes["Dialog"];
        return statusAndMethods with
        {
            Background = main.Normal.Background,
            Text = main.Normal.Foreground,
            Muted = main.Disabled.Foreground,
            Accent = main.HotNormal.Foreground,
            Selection = main.Focus.Background,
            SelectionText = main.Focus.Foreground,
            Input = main.Editable.Background,
            Bar = menu.Normal.Background,
            BarText = menu.Normal.Foreground,
            Dialog = dialog.Normal.Background,
            DialogText = dialog.Normal.Foreground,
            Error = schemes["Error"].Normal.Foreground,
        };
    }

    /// <summary>Text on a background, with the focused item, hot keys, fields and disabled text in palette colors.</summary>
    private Scheme Surface(Color text, Color background) => new(new Attribute(text, background))
    {
        HotNormal = new Attribute(Accent, background, TextStyle.Underline),
        Focus = new Attribute(SelectionText, Selection),
        HotFocus = new Attribute(SelectionText, Selection, TextStyle.Underline),
        Active = new Attribute(SelectionText, Selection, TextStyle.Bold),
        HotActive = new Attribute(SelectionText, Selection, TextStyle.Bold | TextStyle.Underline),
        Highlight = new Attribute(text, Input),
        Editable = new Attribute(Text, Input),
        ReadOnly = new Attribute(Muted, Input, TextStyle.Italic),
        Disabled = new Attribute(Muted, background, TextStyle.Faint),
    };
}
