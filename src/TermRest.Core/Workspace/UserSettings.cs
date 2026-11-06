using System.Text.Json;
using TermRest.Core.IO;

namespace TermRest.Core.Workspace;

/// <summary>
/// Preferences of the user, shared by every workspace: <c>settings.json</c> in the user's TermRest configuration
/// folder (<see cref="DefaultDirectory"/>), next to the <c>themes</c> folder that holds custom color themes.
/// </summary>
public sealed class UserSettings
{
    /// <summary>Environment variable that moves the configuration folder (also handy for tests and portable setups).</summary>
    public const string DirectoryVariable = "TERMREST_CONFIG_DIR";

    /// <summary>Color theme of the full-screen UI; null uses the default theme (the terminal's own colors).</summary>
    public string? Theme { get; set; }

    /// <summary>
    /// The Python interpreter chosen for each workspace folder to run its scripts. It is kept with the user's settings,
    /// not in the workspace, so a cloned repository cannot choose a program for TermRest to run.
    /// </summary>
    public Dictionary<string, string>? PythonInterpreters { get; set; }

    /// <summary>The Python interpreter chosen for the workspace in <paramref name="workspaceRoot"/>, if any.</summary>
    public string? GetPythonInterpreter(string workspaceRoot) =>
        PythonInterpreters?.FirstOrDefault(p => SameFolder(p.Key, workspaceRoot)).Value is { Length: > 0 } interpreter ? interpreter : null;

    /// <summary>Chooses the Python interpreter for the workspace in <paramref name="workspaceRoot"/>; null or empty forgets it.</summary>
    public void SetPythonInterpreter(string workspaceRoot, string? interpreter)
    {
        foreach (var key in PythonInterpreters?.Keys.Where(k => SameFolder(k, workspaceRoot)).ToList() ?? [])
        {
            PythonInterpreters!.Remove(key);
        }

        if (!string.IsNullOrWhiteSpace(interpreter))
        {
            PythonInterpreters ??= new Dictionary<string, string>(StringComparer.Ordinal);
            PythonInterpreters[NormalizeFolder(workspaceRoot)] = interpreter.Trim();
        }
        else if (PythonInterpreters is { Count: 0 })
        {
            PythonInterpreters = null;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(NormalizeFolder(a), NormalizeFolder(b), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeFolder(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path; // an odd entry in a hand-edited file
        }
    }

    /// <summary>
    /// The configuration folder: <c>$TERMREST_CONFIG_DIR</c>, else <c>%APPDATA%\TermRest</c> (Windows),
    /// <c>~/Library/Application Support/TermRest</c> (macOS) or <c>$XDG_CONFIG_HOME/termrest</c> (<c>~/.config/termrest</c>).
    /// </summary>
    public static string DefaultDirectory()
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } custom)
        {
            return Path.GetFullPath(custom);
        }

        if (OperatingSystem.IsWindows())
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming.Length > 0 ? roaming : Path.GetTempPath(), "TermRest");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0)
        {
            return Path.Combine(Path.GetTempPath(), "termrest-config");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "TermRest");
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(config) ? Path.Combine(home, ".config") : config, "termrest");
    }

    /// <summary>Folder with the user's custom color themes (<c>*.json</c>).</summary>
    public static string ThemesDirectory(string? directory = null) => Path.Combine(directory ?? DefaultDirectory(), "themes");

    /// <summary>Reads <c>settings.json</c> from <paramref name="directory"/> (default: <see cref="DefaultDirectory"/>); defaults when missing or invalid.</summary>
    public static UserSettings Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? DefaultDirectory(), "settings.json");
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        try
        {
            var (text, _) = TextFile.Read(path);
            return JsonSerializer.Deserialize(text, CoreJsonContext.Default.UserSettings) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    /// <summary>Writes <c>settings.json</c> to <paramref name="directory"/> (default: <see cref="DefaultDirectory"/>).</summary>
    public void Save(string? directory = null)
    {
        directory ??= DefaultDirectory();
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(this, CoreJsonContext.Default.UserSettings).Replace("\r\n", "\n", StringComparison.Ordinal);
        TextFile.WriteAtomic(Path.Combine(directory, "settings.json"), json, new TextFileInfo(false, true, false));
    }
}
