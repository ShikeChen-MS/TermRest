using System.Text;

namespace TermRest.Core.IO;

/// <summary>Formatting facts about a text file that must survive a load/save round trip.</summary>
public sealed record TextFileInfo(bool HasBom, bool HasTrailingNewline, bool UsesCrLf)
{
    public static readonly TextFileInfo PostmanDefault = new(false, false, false);
}

public static class TextFile
{
    /// <summary>
    /// The largest file TermRest reads: far above real collections (tens of MB with saved examples), it keeps a huge file
    /// from exhausting memory.
    /// </summary>
    internal const long MaxSize = 256L * 1024 * 1024;

    private const UnixFileMode ReadPermissions = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode WritePermissions = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads a UTF-8 text file; throws <see cref="IOException"/> for any other encoding, which saving would garble, and
    /// for anything that is not a regular file of at most <see cref="MaxSize"/> bytes.
    /// </summary>
    public static (string Text, TextFileInfo Info) Read(string path)
    {
        using var stream = OpenRead(path);
        return Decode(path, ReadAll(stream, path));
    }

    /// <summary>Like <see cref="Read(string)"/>, and tells what the file held, to find out later whether another program changed it.</summary>
    internal static (string Text, TextFileInfo Info) Read(string path, out FileStamp stamp)
    {
        using var stream = OpenRead(path);
        var bytes = ReadAll(stream, path);
        stamp = new FileStamp(bytes);
        return Decode(path, bytes);
    }

    internal static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);

    /// <summary>
    /// The content of a regular file. A device or a pipe (which a symbolic link in a cloned repository can point at, such
    /// as /dev/zero or /dev/stdin) and a file over <see cref="MaxSize"/> are refused: reading them would never end, block,
    /// or exhaust memory.
    /// </summary>
    internal static byte[] ReadAll(FileStream stream, string path)
    {
        if (!stream.CanSeek)
        {
            throw new IOException($"'{path}' is not a regular file.");
        }

        var length = stream.Length;
        if (length > MaxSize)
        {
            throw new IOException($"'{path}' is too large ({length / (1024 * 1024)} MB; TermRest reads files of up to {MaxSize / (1024 * 1024)} MB).");
        }

        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() >= 0)
        {
            // Devices such as /dev/zero report no length.
            throw new IOException($"'{path}' is not a regular file, or it grew while it was read.");
        }

        return bytes;
    }

    private static (string Text, TextFileInfo Info) Decode(string path, byte[] bytes)
    {
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            var line = 1 + bytes.AsSpan(offset, Math.Clamp(ex.Index, 0, bytes.Length - offset)).Count((byte)'\n');
            throw new IOException($"'{path}' is not UTF-8 text (line {line}); convert it to UTF-8.", ex);
        }

        // CRLF only when most line breaks are: saving writes one kind, so a line whose break differs (one a Windows editor
        // wrote in an LF file, say) is the only line that changes.
        var crlf = text.AsSpan().Count("\r\n");
        var usesCrLf = crlf > text.AsSpan().Count('\n') - crlf;
        var trailingNewline = text.EndsWith('\n');
        return (text, new TextFileInfo(hasBom, trailingNewline, usesCrLf));
    }

    /// <summary>
    /// Writes <paramref name="text"/> (LF line endings, no trailing newline) applying the original file's
    /// BOM, line-ending and trailing-newline style. Writes to a temp file first so a crash never truncates data,
    /// and keeps the replaced file's identity: symbolic links are written through, Unix permissions are copied
    /// (environment and certificate files may hold secrets) and Windows ACLs survive via <see cref="File.Replace(string, string, string?, bool)"/>.
    /// </summary>
    /// <param name="newFileMode">
    /// On Unix, the permissions of the file when it is new, from its first byte (see <see cref="CopyMode"/>); null: the
    /// default (0666 minus the umask). A file that exists keeps its own.
    /// </param>
    public static void WriteAtomic(string path, string text, TextFileInfo? info = null, UnixFileMode? newFileMode = null) =>
        Write(path, text, info, newFileMode, stamp: false);

    /// <summary>
    /// Like <see cref="WriteAtomic(string, string, TextFileInfo?, UnixFileMode?)"/>, and tells what the file holds now, to
    /// find out later whether another program changed it.
    /// </summary>
    internal static void WriteAtomic(string path, string text, TextFileInfo? info, out FileStamp stamp, UnixFileMode? newFileMode = null) =>
        stamp = Write(path, text, info, newFileMode, stamp: true)!;

    /// <summary>
    /// <paramref name="text"/> (LF line endings, no trailing newline) as <see cref="WriteAtomic(string, string, TextFileInfo?, UnixFileMode?)"/>
    /// writes it, but for the byte order mark: with the line endings and trailing newline of <paramref name="info"/>.
    /// </summary>
    public static string AsWritten(string text, TextFileInfo? info)
    {
        info ??= TextFileInfo.PostmanDefault;
        if (info.UsesCrLf)
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return info.HasTrailingNewline ? text + (info.UsesCrLf ? "\r\n" : "\n") : text;
    }

    /// <summary>
    /// On Unix, the permissions for a file made from <paramref name="source"/> (an imported or converted copy): its read and
    /// write permissions, so that a private (chmod 600) file stays private. Null on Windows, where a new file inherits the
    /// access rules of its folder as copies do, and when <paramref name="source"/> does not exist.
    /// </summary>
    public static UnixFileMode? CopyMode(string source)
    {
        if (OperatingSystem.IsWindows() || !Path.Exists(source))
        {
            return null;
        }

        return File.GetUnixFileMode(source) & (ReadPermissions | WritePermissions);
    }

    /// <summary>
    /// The permissions for a folder of files with <paramref name="fileMode"/>: whoever may read them may enter it, and its
    /// owner may always add files.
    /// </summary>
    internal static UnixFileMode FolderMode(UnixFileMode fileMode) =>
        fileMode | (UnixFileMode)((int)(fileMode & ReadPermissions) >> 2) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static FileStamp? Write(string path, string text, TextFileInfo? info, UnixFileMode? newFileMode, bool stamp)
    {
        info ??= TextFileInfo.PostmanDefault;
        text = AsWritten(text, info);

        var encoding = new UTF8Encoding(info.HasBom);
        var preamble = encoding.GetPreamble();
        var bytes = new byte[preamble.Length + encoding.GetByteCount(text)];
        preamble.CopyTo(bytes, 0);
        encoding.GetBytes(text, bytes.AsSpan(preamble.Length));

        var fullPath = ResolveLinks(Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var exists = File.Exists(fullPath);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var written = false;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, BufferSize = 0 };
            UnixFileMode? mode = null;
            if (!OperatingSystem.IsWindows())
            {
                // Create the temp file with its final permissions (the original file's, or those asked for a new one) so a
                // 0600 secrets file is never world-readable.
                mode = exists ? File.GetUnixFileMode(fullPath) : newFileMode;
                options.UnixCreateMode = mode;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                if (exists)
                {
                    stream.Flush(flushToDisk: true); // a power loss right after the replace must not leave an empty file
                }
            }

            written = true;
            if (mode is { } unixMode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, unixMode); // the create mode is subject to the umask
            }

            if (!exists)
            {
                File.Move(temp, fullPath);
            }
            else
            {
                try
                {
                    File.Replace(temp, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException) when (File.Exists(temp) && (OperatingSystem.IsWindows() || !File.Exists(fullPath)))
                {
                    // Some file systems (certain network shares and sync drivers) lack ReplaceFile; overwrite instead. This
                    // also completes a ReplaceFile that failed after removing the original: the content is in the temp file.
                    File.Move(temp, fullPath, overwrite: true);
                }
            }

            return stamp ? new FileStamp(bytes) : null;
        }
        finally
        {
            // Never delete the only copy of the content: the temp file stays when a failed replace removed the original.
            if (File.Exists(temp) && !(written && exists && !File.Exists(fullPath)))
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
