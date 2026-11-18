using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace TermRest.Core.IO;

/// <summary>
/// Windows' Data Protection API (DPAPI): encrypts data for the signed-in user, so that only that user's account can decrypt
/// it. Used through P/Invoke, as Native AOT has no System.Security.Cryptography.ProtectedData.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class Dpapi
{
    /// <summary>CRYPTPROTECT_UI_FORBIDDEN: fail rather than show a prompt.</summary>
    private const int UiForbidden = 0x1;

    public static byte[] Protect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy) => Transform(data, entropy, protect: true);

    /// <exception cref="CryptographicException">The data was protected for another user or computer, or is damaged.</exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy) => Transform(data, entropy, protect: false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* salt = entropy)
        {
            var inBlob = new Blob { Size = data.Length, Data = (IntPtr)input };
            var saltBlob = new Blob { Size = entropy.Length, Data = (IntPtr)salt };
            var ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref saltBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref saltBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.Data, outBlob.Size).ToArray();
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr memory);
}
