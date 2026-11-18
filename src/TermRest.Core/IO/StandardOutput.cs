using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TermRest.Core.IO;

/// <summary>Standard output as written to by a command that streams what it receives.</summary>
public static partial class StandardOutput
{
    /// <summary>
    /// Standard output, when it is a pipe, as a stream whose writes fail with an <see cref="IOException"/> once the program
    /// reading it has ended (<c>| head -n 5</c>): .NET's console streams take that for a write that succeeded, so a command
    /// would go on receiving for nothing. Null when it is not a pipe (a file, a console …).
    /// </summary>
    public static Stream? OpenPipe()
    {
        try
        {
            var handle = OperatingSystem.IsWindows() ? new SafeFileHandle(GetStdHandle(-11), ownsHandle: false) : new SafeFileHandle(1, ownsHandle: false);
            if (handle.IsInvalid)
            {
                return null;
            }

            var stream = new FileStream(handle, FileAccess.Write, bufferSize: 0);
            if (!stream.CanSeek)
            {
                return stream;
            }

            stream.Dispose(); // a file: written as before, where the console stream writes
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            // not a handle a stream can write to: the console stream does
        }

        return null;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetStdHandle(int handle);
}
