namespace TermRest.Core.Postman.V3;

/// <summary>File and folder naming rules of the Postman collection format v3 layout.</summary>
internal static class V3Names
{
    public const string RequestSuffix = ".request.yaml";
    public const string ResourcesDirectory = ".resources";
    public const string DefinitionFile = "definition.yaml";
    public const string EnvironmentSuffix = ".environment.yaml";

    /// <summary>Postman keeps file names short; longer names are stored in the file's <c>name</c> key.</summary>
    private const int MaxLength = 64;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A file-system-safe name: characters that are invalid on any OS become "-" (like Postman does).</summary>
    public static string Safe(string name)
    {
        var chars = name.Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c).ToArray();
        var safe = new string(chars).Trim();
        if (safe.Length > MaxLength)
        {
            safe = safe[..MaxLength];
        }

        safe = safe.TrimEnd('.', ' ');
        if (safe.Length == 0 || safe.All(c => c == '.'))
        {
            safe = "untitled";
        }

        return ReservedNames.Contains(safe.Split('.')[0]) ? safe + "-" : safe;
    }

    public static bool IsRequestFile(string path) =>
        path.EndsWith(RequestSuffix, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".request.yml", StringComparison.OrdinalIgnoreCase);

    public static string RequestStem(string path)
    {
        var name = Path.GetFileName(path);
        var suffix = name.EndsWith(".request.yml", StringComparison.OrdinalIgnoreCase) ? ".request.yml".Length : RequestSuffix.Length;
        return name[..^suffix];
    }

    public static string DefinitionPath(string directory) => Path.Combine(directory, ResourcesDirectory, DefinitionFile);

    /// <summary>Where a request's examples and other resources live: <c>.resources/&lt;stem&gt;.resources</c> next to it.</summary>
    public static string ResourcesPath(string requestFile) =>
        Path.Combine(Path.GetDirectoryName(requestFile)!, ResourcesDirectory, RequestStem(requestFile) + ".resources");

    /// <summary>A folder is a v3 collection when it has a collection definition or request files of its own.</summary>
    public static bool IsCollectionDirectory(string directory) =>
        Directory.Exists(directory)
        && (File.Exists(DefinitionPath(directory))
            || Directory.EnumerateFiles(directory, "*.request.y*ml").Any(IsRequestFile));

    /// <summary>The workspace folder for a collection directory: the folder that holds <c>postman/collections</c>.</summary>
    public static string WorkspaceRootFor(string collectionDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(collectionDirectory));
        var parent = Path.GetDirectoryName(full);
        var grandparent = parent is null ? null : Path.GetDirectoryName(parent);
        if (parent is not null && grandparent is not null
            && Path.GetFileName(parent).Equals("collections", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(grandparent).Equals("postman", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(grandparent) is { } root)
        {
            return root;
        }

        return parent ?? full;
    }

    /// <summary>The first of name, name-1, name-2 … not in <paramref name="taken"/> (case-insensitive).</summary>
    public static string Unique(string safe, ISet<string> taken)
    {
        var candidate = safe;
        for (var i = 1; taken.Contains(candidate); i++)
        {
            candidate = $"{safe}-{i}";
        }

        return candidate;
    }
}
