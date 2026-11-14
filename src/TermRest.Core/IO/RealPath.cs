namespace TermRest.Core.IO;

/// <summary>Paths as the file system follows them: with their symbolic links (on Windows, junctions too) resolved.</summary>
public static class RealPath
{
    /// <summary>
    /// The path of an existing file or folder with every link in it resolved, as realpath(3) gives it; null when it is not
    /// there, or its links cannot be read or go round in a loop.
    /// </summary>
    public static string? Of(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var (outcome, resolved) = Resolve(root, Names(full[root.Length..]), _ => true);
            return outcome == Outcome.Resolved && Path.Exists(resolved) ? resolved : null; // no links left: nothing else is followed
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is in <paramref name="folder"/> also once its links are resolved, as the OS follows
    /// them when the file is opened. Nothing outside the folder is looked at: not a path outside it as written, nor where a
    /// link that leads out of it goes (on Windows, looking at \\server\share makes Windows sign in to that server with the
    /// user's credentials). A path that is not there counts as in the folder: opening it fails.
    /// </summary>
    /// <param name="realFolder">The folder with its own links resolved (<see cref="Of"/>), if known.</param>
    public static bool IsInside(string path, string folder, string? realFolder = null)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!IsWithin(full, root))
            {
                return false;
            }

            var real = realFolder ?? Of(root) ?? root;

            // The folders on the way to it, as written or real, are the user's: a link or a .. may lead through them back in.
            bool InReach(string p) => IsWithin(p, real) || IsWithin(real, p) || IsWithin(root, p);
            var (outcome, resolved) = Resolve(real, Names(Path.GetRelativePath(root, full)), InReach);
            return outcome == Outcome.Resolved && IsWithin(resolved!, real);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether opening <paramref name="path"/> reaches a network share outside <paramref name="folder"/>: it is on one
    /// (<see cref="IsNetworkPath"/>), or a link on the way leads to one, which is not looked at. On Windows, looking at a file
    /// there makes Windows sign in to that server with the user's credentials. Never on Linux and macOS.
    /// </summary>
    public static bool ReachesNetwork(string path, string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            bool Local(string p) => !IsNetworkPath(p) || IsWithin(p, root);
            if (!Local(full))
            {
                return true;
            }

            var start = Path.GetPathRoot(full)!;
            return Resolve(start, Names(full[start.Length..]), Local).Outcome == Outcome.Stopped;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // not a path that can be opened
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is on a network share: \\server\share, //server/share, \\?\UNC\server\share, or another
    /// device path than a drive's or a volume's (\\?\C:\, \\?\Volume{…}\). Windows only: elsewhere such paths are local.
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        static bool Separator(char c) => c is '\\' or '/';
        var rest = path.AsSpan();
        if (rest.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            rest = rest[4..];
        }
        else if (rest.Length >= 2 && Separator(rest[0]) && Separator(rest[1]))
        {
            if (rest.Length < 4 || rest[2] is not ('?' or '.') || !Separator(rest[3]))
            {
                return true; // \\server\share
            }

            rest = rest[4..];
        }
        else
        {
            return false;
        }

        // A device path: a drive's or a volume's is local; UNC, GLOBALROOT and the others may not be.
        return !((rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':') || rest.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or in it, as they are written (nothing is read).</summary>
    public static bool IsWithin(string path, string folder)
    {
        try
        {
            var relative = Path.GetRelativePath(folder, path);
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
                                       && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private enum Outcome
    {
        Resolved,

        /// <summary>A path was refused before it was looked at.</summary>
        Stopped,

        /// <summary>A link could not be read, or links go round in a loop.</summary>
        Unknown,
    }

    /// <summary>
    /// <paramref name="names"/> (the first on top) resolved one by one from <paramref name="resolved"/>, a path without
    /// links, as the OS resolves a path: a link goes where it leads (an absolute target from its root, a relative one from
    /// the link's folder), and a <c>..</c> to the parent of where the names before it led, not of the path as written.
    /// Each path is shown to <paramref name="mayLookAt"/> before it is looked at; a refused one stops the walk.
    /// </summary>
    private static (Outcome Outcome, string? Path) Resolve(string resolved, Stack<string> names, Func<string, bool> mayLookAt)
    {
        var links = 0;
        while (names.TryPop(out var name))
        {
            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved; // the root's parent is the root
                if (!mayLookAt(resolved))
                {
                    return (Outcome.Stopped, null);
                }

                continue;
            }

            var next = Path.Join(resolved, name);
            if (!mayLookAt(next))
            {
                return (Outcome.Stopped, null);
            }

            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget; // the link's own data: where it leads is not looked at
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (Outcome.Unknown, null);
            }

            if (target is null)
            {
                resolved = next; // not a link, or not there
            }
            else if (++links > 40 || (Path.IsPathRooted(target) && !Path.IsPathFullyQualified(target)))
            {
                return (Outcome.Unknown, null); // a loop; or \dir or C:dir, which depend on the current drive or folder
            }
            else if (Path.IsPathRooted(target))
            {
                resolved = Path.GetPathRoot(target)!;
                Push(names, target[resolved.Length..]);
            }
            else
            {
                Push(names, target);
            }
        }

        return (Outcome.Resolved, resolved);
    }

    private static Stack<string> Names(string path)
    {
        var names = new Stack<string>();
        Push(names, path);
        return names;
    }

    /// <summary>The names of <paramref name="path"/>'s parts onto <paramref name="names"/>, the first on top.</summary>
    private static void Push(Stack<string> names, string path)
    {
        var parts = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            names.Push(parts[i]);
        }
    }
}
