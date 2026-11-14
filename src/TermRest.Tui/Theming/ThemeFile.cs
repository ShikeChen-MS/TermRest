using System.Text.Json;
using System.Text.Json.Serialization;
using Terminal.Gui.Drawing;
using TermRest.Core.IO;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace TermRest.Tui.Theming;

/// <summary>
/// A theme file (<c>*.json</c>): a <see cref="Base"/> theme with some of its palette colors replaced (every color of the
/// UI is derived from the palette) and, for full control, exact colors for single scheme roles.
/// </summary>
/// <example>
/// <code>
/// {
///   "name": "Production",
///   "base": "Dark",
///   "palette": { "bar": "#5c0000", "barText": "White", "selection": "DarkRed" },
///   "schemes": { "Base": { "Focus": "white on #8b0000 bold" } }
/// }
/// </code>
/// </example>
internal sealed class ThemeFile
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The theme this one starts from (built in or another custom theme); default: Default.</summary>
    public string? Base { get; set; }

    /// <summary>Palette colors by name (<see cref="ThemePalette.Keys"/>): Terminal.Gui color names, #RRGGBB or None.</summary>
    public Dictionary<string, string>? Palette { get; set; }

    /// <summary>Scheme → role → attribute (<c>"white on #1e1e2e bold"</c>), applied after the palette.</summary>
    public Dictionary<string, Dictionary<string, string>>? Schemes { get; set; }

    /// <summary>Reads every <c>*.json</c> file in <paramref name="directory"/> (sorted by name); unreadable files are reported.</summary>
    public static List<(ThemeFile File, string Path)> ReadDirectory(string directory, ICollection<string> errors)
    {
        var files = new List<(ThemeFile, string)>();
        if (!Directory.Exists(directory))
        {
            return files;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var (text, _) = TextFile.Read(path);
                if (JsonSerializer.Deserialize(text, ThemeJsonContext.Default.ThemeFile) is { } file)
                {
                    files.Add((file, path));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        return files;
    }

    /// <summary>The theme this file describes, on top of <paramref name="baseTheme"/>. Invalid entries are reported and skipped.</summary>
    public ColorTheme ToTheme(string path, ColorTheme baseTheme, ICollection<string> errors)
    {
        var fileName = System.IO.Path.GetFileName(path);
        var palette = baseTheme.Palette;
        var surfaceChanged = false;
        foreach (var (key, value) in Palette ?? [])
        {
            var entry = ThemePalette.Keys.FirstOrDefault(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry.Key is null)
            {
                errors.Add($"{fileName}: unknown palette color '{key}' (known: {string.Join(", ", ThemePalette.Keys.Select(k => k.Key))})");
                continue;
            }

            if (!TryParseColor(value, out var color))
            {
                errors.Add($"{fileName}: palette color {key}: '{value}' is not a color (use a name such as DarkBlue, #RRGGBB, or None)");
                continue;
            }

            // Status, method and script colors only affect their own schemes and the script editors; the others replace
            // the base theme's schemes.
            surfaceChanged |= !entry.Get(palette).Equals(color)
                              && entry.Key is not ("success" or "warning" or "get" or "post" or "put" or "patch" or "delete" or "otherMethods")
                              && !ThemePalette.IsSyntaxKey(entry.Key);
            palette = entry.With(palette, color);
        }

        var schemes = surfaceChanged
            ? palette.ToSchemes()
            : baseTheme.Schemes.ToDictionary(p => p.Key, p => new Scheme(p.Value), StringComparer.Ordinal);

        // A status scheme follows its color: until that changes, the base's stays as it is, with the roles a custom base sets.
        if (!palette.Success.Equals(baseTheme.Palette.Success))
        {
            schemes[ThemeCatalog.SuccessScheme] = palette.StatusScheme(palette.Success);
        }

        if (!palette.Warning.Equals(baseTheme.Palette.Warning))
        {
            schemes[ThemeCatalog.WarningScheme] = palette.StatusScheme(palette.Warning);
        }

        foreach (var (schemeKey, roles) in Schemes ?? [])
        {
            var schemeName = SchemeName(schemeKey);
            if (schemeName is null || !schemes.TryGetValue(schemeName, out var scheme))
            {
                errors.Add($"{fileName}: unknown scheme '{schemeKey}' (known: Base, Menu, Dialog, Error, Accent, Success, Warning)");
                continue;
            }

            if (roles is null)
            {
                errors.Add($"{fileName}: {schemeKey}: no roles given (known: {string.Join(", ", ThemeableRoles)})");
                continue;
            }

            // Roles are set in the order Terminal.Gui derives them from each other (Focus from Normal, Highlight and ReadOnly
            // from Editable …), whatever their order in the file, so that setting one never changes another the file sets.
            // Mistakes are listed in the file's order.
            var problems = new List<(int Index, string Text)>();
            var known = new List<(int Index, VisualRole Role, string Key, string Value)>();
            foreach (var (index, (roleKey, value)) in roles.Index())
            {
                if (!Enum.TryParse<VisualRole>(roleKey, ignoreCase: true, out var role) || !IsThemeableRole(role))
                {
                    problems.Add((index, $"{fileName}: {schemeKey}: unknown role '{roleKey}' (known: {string.Join(", ", ThemeableRoles)})"));
                    continue;
                }

                known.Add((index, role, roleKey, value));
            }

            foreach (var (index, role, roleKey, value) in known.OrderBy(k => Array.IndexOf(ThemeableRoles, k.Role)))
            {
                if (!TryParseAttribute(value, scheme.GetAttributeForRole(role), out var attribute, out var problem))
                {
                    problems.Add((index, $"{fileName}: {schemeKey}.{roleKey}: {problem}"));
                    continue;
                }

                scheme = WithRole(scheme, role, attribute);
            }

            foreach (var (_, text) in problems.OrderBy(p => p.Index))
            {
                errors.Add(text);
            }

            schemes[schemeName] = scheme;
        }

        return new ColorTheme
        {
            Name = string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? $"Custom theme based on {baseTheme.Name}" : Description.Trim(),
            FilePath = path,
            Palette = palette,
            Schemes = schemes,
            TerminalGuiTheme = baseTheme.TerminalGuiTheme,
        };
    }

    /// <summary>
    /// A theme file that reproduces <paramref name="theme"/>, with all its palette colors to edit. A built-in theme's copy is
    /// based on it. A custom theme's copy needs no other theme file (its base may be in a workspace that other workspaces
    /// don't have): it is based on the built-in theme of the same Terminal.Gui theme and sets the scheme roles in which the
    /// custom theme differs from what its palette gives there.
    /// </summary>
    public static ThemeFile CopyOf(ColorTheme theme, string name)
    {
        var copy = new ThemeFile
        {
            Name = name,
            Description = $"Based on {theme.Name}",
            Base = theme.Name,
            Palette = ThemePalette.Keys.ToDictionary(k => k.Key, k => FormatColor(k.Get(theme.Palette))),
        };
        if (theme.IsBuiltIn)
        {
            return copy;
        }

        var builtIn = ThemeCatalog.Find(ThemeCatalog.BuiltIn, theme.TerminalGuiTheme) ?? ThemeCatalog.BuiltIn[0];
        copy.Base = builtIn.Name;
        var computed = copy.ToTheme(name, builtIn, []).Schemes;
        var schemes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (schemeName, source) in theme.Schemes)
        {
            if (!computed.TryGetValue(schemeName, out var scheme))
            {
                continue;
            }

            // Loading sets the roles in the order written, each on the scheme the ones before it left: compare with that.
            var roles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var role in ThemeableRoles)
            {
                var attribute = source.GetAttributeForRole(role);
                if (scheme.GetAttributeForRole(role) != attribute)
                {
                    roles[role.ToString()] = FormatAttribute(attribute);
                    scheme = WithRole(scheme, role, attribute);
                }
            }

            if (roles.Count > 0)
            {
                schemes[FileSchemeName(schemeName)] = roles;
            }
        }

        copy.Schemes = schemes.Count > 0 ? schemes : null;
        return copy;
    }

    public string ToJson() => JsonSerializer.Serialize(this, ThemeJsonContext.Default.ThemeFile).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    public static bool TryParseColor(string? text, out Color color)
    {
        color = Color.None;
        try
        {
            if (string.IsNullOrWhiteSpace(text) || !Color.TryParse(text.Trim(), out Color? parsed) || parsed is not { } value)
            {
                return false;
            }

            color = value;
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException)
        {
            return false; // Terminal.Gui's TryParse throws for some colors, such as rgb(256,0,0)
        }
    }

    /// <summary>
    /// Parses <c>"&lt;foreground&gt; [on &lt;background&gt;] [style …]"</c>, e.g. <c>"white on #1e1e2e bold underline"</c> or
    /// <c>"on DarkRed"</c>. Parts left out keep <paramref name="fallback"/>'s colors and style; <c>none</c> as a style clears it.
    /// </summary>
    public static bool TryParseAttribute(string? text, Attribute fallback, out Attribute attribute, out string? problem)
    {
        attribute = fallback;
        problem = null;
        var tokens = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (tokens.Length == 0)
        {
            problem = "empty; use \"<foreground> on <background> [bold|italic|underline|faint|reverse|strikethrough|blink]\"";
            return false;
        }

        var foreground = fallback.Foreground;
        var background = fallback.Background;
        var i = 0;

        // "none" is both a color (the terminal's own) and a style: before "on" it is the foreground color.
        var startsWithStyle = TryParseStyle(tokens[0], out _) && !(tokens.Length > 1 && tokens[1].Equals("on", StringComparison.OrdinalIgnoreCase));
        if (!tokens[0].Equals("on", StringComparison.OrdinalIgnoreCase) && !startsWithStyle)
        {
            if (!TryParseColor(tokens[0], out foreground))
            {
                problem = $"'{tokens[0]}' is not a color (use a name such as DarkBlue, #RRGGBB, or None)";
                return false;
            }

            i++;
        }

        if (i < tokens.Length && tokens[i].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= tokens.Length || !TryParseColor(tokens[i + 1], out background))
            {
                problem = i + 1 < tokens.Length ? $"'{tokens[i + 1]}' is not a color" : "a color is missing after 'on'";
                return false;
            }

            i += 2;
        }

        var style = fallback.Style;
        var styleGiven = false;
        for (; i < tokens.Length; i++)
        {
            if (!TryParseStyle(tokens[i], out var part))
            {
                problem = $"'{tokens[i]}' is not a style (bold, faint, italic, underline, blink, reverse, strikethrough or none)";
                return false;
            }

            style = styleGiven ? style | part : part;
            styleGiven = true;
        }

        attribute = new Attribute(foreground, background, style);
        return true;
    }

    private static bool TryParseStyle(string text, out TextStyle style) =>
        Enum.TryParse(text, ignoreCase: true, out style) && !int.TryParse(text, out _);

    /// <summary>A color as <see cref="TryParseColor"/> reads it back exactly: a name, #RRGGBB or None (#AARRGGBB if partly transparent).</summary>
    public static string FormatColor(Color color)
    {
        var text = color.ToString();
        return TryParseColor(text, out var parsed) && parsed.Equals(color) ? text : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// An attribute as <see cref="TryParseAttribute"/> reads it back exactly, whatever the fallback, e.g.
    /// <c>"White on DarkRed bold"</c> or <c>"None on None none"</c>.
    /// </summary>
    public static string FormatAttribute(Attribute attribute)
    {
        var styles = Enum.GetValues<TextStyle>().Where(s => s != TextStyle.None && attribute.Style.HasFlag(s)).ToList();
        var style = styles.Count == 0 ? "none" : string.Join(' ', styles.Select(s => s.ToString().ToLowerInvariant()));
        return $"{FormatColor(attribute.Foreground)} on {FormatColor(attribute.Background)} {style}";
    }

    // In the order Terminal.Gui derives roles from each other (Focus from Normal, Highlight and ReadOnly from Editable …), so
    // that a copy, setting roles in this order, never changes a role it set or checked before.
    private static readonly VisualRole[] ThemeableRoles =
    [
        VisualRole.Normal, VisualRole.HotNormal, VisualRole.Focus, VisualRole.HotFocus, VisualRole.Active, VisualRole.HotActive,
        VisualRole.Editable, VisualRole.Highlight, VisualRole.ReadOnly, VisualRole.Disabled,
    ];

    private static bool IsThemeableRole(VisualRole role) => ThemeableRoles.Contains(role);

    private static string? SchemeName(string key) => key.ToLowerInvariant() switch
    {
        "base" => "Base",
        "menu" => "Menu",
        "dialog" => "Dialog",
        "error" => "Error",
        "accent" => "Accent",
        "success" => ThemeCatalog.SuccessScheme,
        "warning" => ThemeCatalog.WarningScheme,
        _ => null,
    };

    private static string FileSchemeName(string scheme) => scheme switch
    {
        ThemeCatalog.SuccessScheme => "Success",
        ThemeCatalog.WarningScheme => "Warning",
        _ => scheme,
    };

    private static Scheme WithRole(Scheme scheme, VisualRole role, Attribute attribute) => role switch
    {
        VisualRole.Normal => scheme with { Normal = attribute },
        VisualRole.HotNormal => scheme with { HotNormal = attribute },
        VisualRole.Focus => scheme with { Focus = attribute },
        VisualRole.HotFocus => scheme with { HotFocus = attribute },
        VisualRole.Active => scheme with { Active = attribute },
        VisualRole.HotActive => scheme with { HotActive = attribute },
        VisualRole.Highlight => scheme with { Highlight = attribute },
        VisualRole.Editable => scheme with { Editable = attribute },
        VisualRole.ReadOnly => scheme with { ReadOnly = attribute },
        VisualRole.Disabled => scheme with { Disabled = attribute },
        _ => scheme,
    };
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ThemeFile))]
internal sealed partial class ThemeJsonContext : JsonSerializerContext;
