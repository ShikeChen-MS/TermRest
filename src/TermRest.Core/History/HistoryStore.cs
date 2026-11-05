using System.Security.Cryptography;
using System.Text;
using TermRest.Core.IO;
using TermRest.Core.Json;

namespace TermRest.Core.History;

/// <summary>
/// Request history for one workspace, stored per user outside the workspace folder (so it is never committed with
/// the collections): <c>history.jsonl</c> (one entry per line, append-only, compacted to <see cref="MaxEntries"/>) plus
/// <c>bodies/&lt;id&gt;.bin</c> with the raw response bodies. Files are private to the user on Unix (0600/0700).
/// </summary>
public sealed class HistoryStore
{
    public const string FileName = "history.jsonl";
    public const string StateDirectoryVariable = "TERMREST_STATE_DIR";

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Lock _gate = new();
    private int? _lineCount;

    public HistoryStore(string folder, int maxEntries = 500)
    {
        Folder = Path.GetFullPath(folder);
        MaxEntries = maxEntries;
    }

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, FileName);

    public string BodiesFolder => Path.Combine(Folder, "bodies");

    /// <summary>Entries kept; older ones are removed as new ones are added.</summary>
    public int MaxEntries
    {
        get;
        set => field = Math.Max(1, value);
    }

    /// <summary>The workspace this history belongs to (written to <c>workspace.txt</c> for orientation).</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>
    /// Per-user state folder: <c>$TERMREST_STATE_DIR</c>, else <c>%LOCALAPPDATA%\TermRest</c> (Windows),
    /// <c>~/Library/Application Support/TermRest</c> (macOS) or <c>$XDG_STATE_HOME/termrest</c> (<c>~/.local/state/termrest</c>).
    /// </summary>
    public static string DefaultStateDirectory()
    {
        if (Environment.GetEnvironmentVariable(StateDirectoryVariable) is { Length: > 0 } custom)
        {
            return Path.GetFullPath(custom);
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local.Length > 0 ? local : Path.GetTempPath(), "TermRest");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0)
        {
            return Path.Combine(Path.GetTempPath(), "termrest");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "TermRest");
        }

        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return Path.Combine(string.IsNullOrEmpty(state) ? Path.Combine(home, ".local", "state") : state, "termrest");
    }

    /// <summary>Folder name for a workspace: its folder name plus a hash of the full path, e.g. <c>my-api-3f2a9c1b0d4e</c>.</summary>
    public static string WorkspaceKey(string workspaceRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var normalized = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        var name = new string(Path.GetFileName(full).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).Trim('.');
        return $"{(name.Length == 0 ? "root" : name[..Math.Min(name.Length, 40)])}-{hash}";
    }

    public static HistoryStore ForWorkspace(string workspaceRoot, int maxEntries = 500, string? stateDirectory = null) =>
        new(Path.Combine(stateDirectory ?? DefaultStateDirectory(), "history", WorkspaceKey(workspaceRoot)), maxEntries)
        {
            WorkspaceRoot = Path.GetFullPath(workspaceRoot),
        };

    /// <summary>Appends an entry (and its response body), then drops the oldest entries beyond <see cref="MaxEntries"/>.</summary>
    public void Add(HistoryEntry entry, byte[]? body = null)
    {
        lock (_gate)
        {
            EnsureFolder();
            if (body is not null && entry.BodySaved)
            {
                WritePrivateFile(BodyPath(entry.Id), body);
            }

            var line = Encoding.UTF8.GetBytes(PostmanJson.SerializeCompact(entry.ToJson()) + "\n");
            Retry(() =>
            {
                using var stream = new FileStream(FilePath, CreateOptions(FileMode.Append, FileAccess.Write));
                stream.Write(line);
            });

            _lineCount = _lineCount is { } count ? count + 1 : ReadLines().Count;
            if (_lineCount > MaxEntries + Math.Max(10, MaxEntries / 5))
            {
                Compact();
            }
        }
    }

    /// <summary>All entries, newest first. Damaged lines are skipped.</summary>
    public List<HistoryEntry> Load()
    {
        lock (_gate)
        {
            var lines = ReadLines();
            _lineCount = lines.Count;
            var entries = new List<HistoryEntry>(lines.Count);
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (HistoryEntry.TryParse(lines[i], out var entry))
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
    }

    /// <summary>The stored response body of an entry, or null when none was kept.</summary>
    public byte[]? ReadBody(HistoryEntry entry)
    {
        var path = BodyPath(entry.Id);
        return entry.BodySaved && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var lines = ReadLines();
            var kept = lines.Where(line => HistoryEntry.ReadId(line) != id).ToList();
            if (kept.Count == lines.Count)
            {
                return false;
            }

            Rewrite(kept);
            DeleteFile(BodyPath(id));
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            DeleteFile(FilePath);
            if (Directory.Exists(BodiesFolder))
            {
                Directory.Delete(BodiesFolder, recursive: true);
            }

            _lineCount = 0;
        }
    }

    // ----- files ---------------------------------------------------------------------------------

    private string BodyPath(string id) => Path.Combine(BodiesFolder, id + ".bin");

    private void Compact()
    {
        var lines = ReadLines();
        var kept = new List<string>();
        var keptIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = lines.Count - 1; i >= 0 && kept.Count < MaxEntries; i--)
        {
            if (HistoryEntry.ReadId(lines[i]) is { } id)
            {
                kept.Add(lines[i]);
                keptIds.Add(id);
            }
        }

        kept.Reverse();
        Rewrite(kept);
        if (Directory.Exists(BodiesFolder))
        {
            foreach (var file in Directory.EnumerateFiles(BodiesFolder, "*.bin"))
            {
                if (!keptIds.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    DeleteFile(file);
                }
            }
        }
    }

    private void Rewrite(List<string> lines)
    {
        Retry(() => TextFile.WriteAtomic(FilePath, string.Join('\n', lines), new TextFileInfo(false, lines.Count > 0, false)));
        _lineCount = lines.Count;
    }

    private List<string> ReadLines()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        List<string> lines = [];
        Retry(() =>
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var read = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    read.Add(line);
                }
            }

            lines = read;
        });
        return lines;
    }

    private void EnsureFolder()
    {
        if (Directory.Exists(Folder))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Folder);
        }
        else
        {
            Directory.CreateDirectory(Folder, PrivateDirectory);
        }

        if (WorkspaceRoot is not null)
        {
            File.WriteAllText(Path.Combine(Folder, "workspace.txt"), WorkspaceRoot + "\n");
        }
    }

    private void WritePrivateFile(string path, byte[] bytes)
    {
        if (!Directory.Exists(BodiesFolder))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(BodiesFolder);
            }
            else
            {
                Directory.CreateDirectory(BodiesFolder, PrivateDirectory);
            }
        }

        using var stream = new FileStream(path, CreateOptions(FileMode.Create, FileAccess.Write));
        stream.Write(bytes);
    }

    private static FileStreamOptions CreateOptions(FileMode mode, FileAccess access)
    {
        // Exclusive while writing: a TUI and a CLI recording into the same history never interleave lines.
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFile;
        }

        return options;
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a body file that cannot be removed now is removed by a later compaction.
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25); // another TermRest process is writing the same history
            }
        }
    }
}
