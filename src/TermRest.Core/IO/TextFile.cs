using System.Text;

namespace TermRest.Core.IO;

/// <summary>Formatting facts about a text file that must survive a load/save round trip.</summary>
public sealed record TextFileInfo(bool HasBom, bool HasTrailingNewline, bool UsesCrLf)
{
    public static readonly TextFileInfo PostmanDefault = new(false, false, false);
}

public static class TextFile
{
    public static (string Text, TextFileInfo Info) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        var usesCrLf = text.Contains("\r\n", StringComparison.Ordinal);
        var trailingNewline = text.EndsWith('\n');
        return (text, new TextFileInfo(hasBom, trailingNewline, usesCrLf));
    }

    /// <summary>
    /// Writes <paramref name="text"/> (LF line endings, no trailing newline) applying the original file's
    /// BOM, line-ending and trailing-newline style. Writes to a temp file first so a crash never truncates data,
    /// and keeps the replaced file's identity: symbolic links are written through, Unix permissions are copied
    /// (environment and certificate files may hold secrets) and Windows ACLs survive via <see cref="File.Replace(string, string, string?, bool)"/>.
    /// </summary>
    public static void WriteAtomic(string path, string text, TextFileInfo? info = null)
    {
        info ??= TextFileInfo.PostmanDefault;
        if (info.UsesCrLf)
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        if (info.HasTrailingNewline)
        {
            text += info.UsesCrLf ? "\r\n" : "\n";
        }

        var fullPath = ResolveLinks(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var exists = File.Exists(fullPath);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            UnixFileMode? mode = null;
            if (exists && !OperatingSystem.IsWindows())
            {
                // Create the temp file with the original permissions so a 0600 secrets file is never world-readable.
                mode = File.GetUnixFileMode(fullPath);
                options.UnixCreateMode = mode;
            }

            using (var writer = new StreamWriter(temp, new UTF8Encoding(info.HasBom), options))
            {
                writer.Write(text);
            }

            if (!exists)
            {
                File.Move(temp, fullPath);
            }
            else
            {
                if (mode is { } unixMode && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temp, unixMode); // the create mode is subject to the umask
                }

                try
                {
                    File.Replace(temp, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException) when (OperatingSystem.IsWindows() && File.Exists(temp) && File.Exists(fullPath))
                {
                    // Some file systems (certain network shares and sync drivers) lack ReplaceFile; overwrite instead.
                    File.Move(temp, fullPath, overwrite: true);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The final target of a symbolic link (so a linked file stays linked and gets the edit), else the path itself.</summary>
    private static string ResolveLinks(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path; // does not exist yet
        }
    }
}
