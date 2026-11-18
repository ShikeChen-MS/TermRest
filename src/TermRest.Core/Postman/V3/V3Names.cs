using System.Runtime.ExceptionServices;

namespace TermRest.Core.Postman.V3;

/// <summary>File and folder naming rules of the Postman collection format v3 layout.</summary>
internal static class V3Names
{
    public const string RequestSuffix = ".request.yaml";
    public const string ExampleSuffix = ".example.yaml";
    public const string ResourcesDirectory = ".resources";
    public const string DefinitionFile = "definition.yaml";
    public const string EnvironmentSuffix = ".environment.yaml";

    /// <summary>Postman keeps file names short; longer names are stored in the file's <c>name</c> key.</summary>
    private const int MaxLength = 64;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly string[] CollectionFolders = ["collections"];

    private static readonly string[] LayoutFolders = ["collections", "environments", "globals"];

    /// <summary>
    /// A file-system-safe name: characters that are invalid on any OS become "-" (like Postman does), and so do leading
    /// dots, which would hide the file or folder (folders starting with "." are not loaded, and ".resources" is taken).
    /// </summary>
    public static string Safe(string name)
    {
        var chars = name.Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c).ToArray();
        var safe = new string(chars).Trim();
        if (safe.Length > MaxLength)
        {
            // Never between the two halves of a surrogate pair (an emoji, say): git cannot add a file whose name ends in
            // half a character, and on Unix .NET writes it as U+FFFD.
            safe = safe[..(char.IsHighSurrogate(safe[MaxLength - 1]) ? MaxLength - 1 : MaxLength)];
        }

        safe = safe.TrimEnd('.', ' ');
        if (safe.Length == 0 || safe.All(c => c == '.'))
        {
            safe = "untitled";
        }

        var dots = safe.Length - safe.TrimStart('.').Length;
        safe = new string('-', dots) + safe[dots..];

        // Windows reserves a device name also with an extension (NUL.txt is NUL), spaces before the dot ignored, and so does
        // Git for Windows: the "-" goes right after the name (con.example.org becomes con-.example.org).
        var stem = safe.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem) ? string.Concat(stem, "-", safe.AsSpan(stem.Length)) : safe;
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

    /// <summary>
    /// A folder is a v3 collection when it has a collection definition or request files of its own; or, in a workspace's
    /// <c>postman/collections</c> folder (where Postman keeps collections, and writes no definition for one without settings
    /// of its own), when its folders have them.
    /// </summary>
    public static bool IsCollectionDirectory(string directory) =>
        Directory.Exists(directory)
        && (HasContent(directory) || (IsInCollectionsFolder(directory) && FoldersHaveContent(directory, MaxFolderDepth)));

    private const int MaxFolderDepth = 16;

    private static readonly EnumerationOptions FolderSearch = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private static bool HasContent(string directory) =>
        File.Exists(DefinitionPath(directory)) || Directory.EnumerateFiles(directory, "*.request.y*ml").Any(IsRequestFile);

    /// <summary>
    /// Whether folders of <paramref name="directory"/> (not hidden ones, nor links) have definitions or requests. A folder that
    /// cannot be listed (another user's) may hold them: when no other folder has any, its error is thrown, so that loading
    /// says why rather than the collection being left out without a word.
    /// </summary>
    private static bool FoldersHaveContent(string directory, int depth)
    {
        if (depth <= 0)
        {
            return false;
        }

        Exception? unreadable = null;
        foreach (var sub in Directory.EnumerateDirectories(directory, "*", FolderSearch))
        {
            if (Path.GetFileName(sub).StartsWith('.'))
            {
                continue;
            }

            try
            {
                if (HasContent(sub) || FoldersHaveContent(sub, depth - 1))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable ??= ex; // another folder may have them
            }
        }

        if (unreadable is not null)
        {
            ExceptionDispatchInfo.Throw(unreadable);
        }

        return false;
    }

    /// <summary>Whether <paramref name="directory"/> is in a <c>postman/collections</c> folder, where Postman keeps collections.</summary>
    public static bool IsInCollectionsFolder(string directory)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        return parent is not null && string.Equals(Path.GetFileName(parent), CollectionFolders[0], StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), "postman", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The workspace folder for a collection directory: the folder that holds <c>postman/collections</c>.</summary>
    public static string WorkspaceRootFor(string collectionDirectory) => WorkspaceRootAbove(collectionDirectory, CollectionFolders);

    /// <summary>
    /// The workspace folder for a file given on its own: the folder that holds <c>postman</c> for a file in
    /// <c>postman/collections</c>, <c>postman/environments</c> or <c>postman/globals</c>, where the workspace keeps such
    /// files; else the folder the file is in.
    /// </summary>
    public static string WorkspaceRootForFile(string file) => WorkspaceRootAbove(file, LayoutFolders);

    private static string WorkspaceRootAbove(string path, string[] folders)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full);
        var grandparent = parent is null ? null : Path.GetDirectoryName(parent);
        if (parent is not null && grandparent is not null
            && folders.Contains(Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase)
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
