using System.Security.Cryptography;

namespace TermRest.Core.IO;

/// <summary>
/// What a file held when TermRest last read or wrote it: its length, last write time and a hash of its content. Tells
/// whether another program (a <c>git pull</c>, Postman, an editor) changed it since; one that only touched it did not.
/// </summary>
internal sealed class FileStamp
{
    private readonly long _length;
    private readonly byte[] _hash;
    private DateTime _lastWriteTimeUtc;

    public FileStamp(DateTime lastWriteTimeUtc, ReadOnlySpan<byte> content)
    {
        _length = content.Length;
        _hash = SHA256.HashData(content);
        _lastWriteTimeUtc = lastWriteTimeUtc;
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

            if (File.GetLastWriteTimeUtc(stream.SafeFileHandle) == _lastWriteTimeUtc)
            {
                return false;
            }

            var content = TextFile.ReadAll(stream, path, out var lastWriteTimeUtc);
            if (!SHA256.HashData(content).AsSpan().SequenceEqual(_hash))
            {
                return true;
            }

            _lastWriteTimeUtc = lastWriteTimeUtc; // only touched: no need to read it again next time
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
