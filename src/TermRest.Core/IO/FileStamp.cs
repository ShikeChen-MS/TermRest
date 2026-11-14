using System.Security.Cryptography;

namespace TermRest.Core.IO;

/// <summary>
/// What a file held when TermRest last read or wrote it: its length and a hash of its content. Tells whether another
/// program (a <c>git pull</c>, Postman, an editor) changed it since; one that only touched it did not. The content decides
/// also when the file's time is the same: a copy that keeps its time (<c>cp -p</c>, an archive) may hold other content.
/// </summary>
internal sealed class FileStamp
{
    private readonly long _length;
    private readonly byte[] _hash;

    public FileStamp(ReadOnlySpan<byte> content)
    {
        _length = content.Length;
        _hash = SHA256.HashData(content);
    }

    /// <summary>True when <paramref name="path"/> holds other content now, or cannot be read.</summary>
    public bool HasChanged(string path)
    {
        try
        {
            using var stream = TextFile.OpenRead(path);
            if (!stream.CanSeek || stream.Length != _length)
            {
                return true;
            }

            return !SHA256.HashData(TextFile.ReadAll(stream, path)).AsSpan().SequenceEqual(_hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
