// Clearspace | NEW (locked folders): protects an opened folder's session key with Windows DPAPI.
// The protected bytes can only be unprotected by the same Windows user on this PC, which lets
// Clearspace lock a folder again when you leave it (or after a crash) without asking for the password.
// The key is deleted from the database as soon as the folder is locked again.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Clearspace.Services;

internal static class SessionKeyProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private static readonly byte[] Entropy = "Clearspace locked folder session v1"u8.ToArray();

    internal static byte[] Protect(byte[] data) => Transform(data, protect: true);
    internal static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new CryptographicException("Windows could not " + (protect ? "protect" : "read") + " the saved folder key.", new Win32Exception());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // Clear before freeing: for Unprotect this buffer holds the session key.
                Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
            input.Free();
            entropy.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
