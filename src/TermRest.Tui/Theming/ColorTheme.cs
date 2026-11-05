using Terminal.Gui.Drawing;

namespace TermRest.Tui.Theming;

/// <summary>
/// A color theme of the full-screen UI: built in (Terminal.Gui's themes and TermRest's palettes) or a theme file
/// (see <see cref="ThemeCatalog"/>).
/// </summary>
public sealed class ColorTheme
{
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>The theme file; null for built-in themes.</summary>
    public string? FilePath { get; init; }

    public bool IsBuiltIn => FilePath is null;

    /// <summary>The theme's colors (for themes defined by their schemes: the colors those schemes use).</summary>
    public required ThemePalette Palette { get; init; }

    /// <summary>The Terminal.Gui schemes to register, by name.</summary>
    public required IReadOnlyDictionary<string, Scheme> Schemes { get; init; }

    /// <summary>The Terminal.Gui theme switched to first: its border and shadow styles apply.</summary>
    public string TerminalGuiTheme { get; init; } = ThemeCatalog.DefaultName;

    /// <summary>Where the theme comes from, for lists: "built in" or the file name.</summary>
    public string SourceDescription => FilePath is null ? "built in" : Path.GetFileName(FilePath);

    public override string ToString() => Name;
}
