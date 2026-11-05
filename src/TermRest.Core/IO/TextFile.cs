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
    /// BOM, line-ending and trailing-newline style. Writes to a temp file first so a crash never truncates data.
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

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(info.HasBom));
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
