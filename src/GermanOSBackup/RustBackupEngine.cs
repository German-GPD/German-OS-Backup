using System.Runtime.InteropServices;

namespace GermanOSBackup;

/// Thin, optional bridge to the Rust file-copy core. The managed copy path
/// remains available for development builds where the native library is absent.
internal static class RustBackupEngine
{
    private const string LibraryName = "german_os_backup_engine.dll";

    [DllImport(LibraryName, EntryPoint = "gob_copy_file", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CopyFileNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string target,
        out ulong bytesCopied);

    internal static bool TryCopyFile(string source, string target, out long bytesCopied)
    {
        try
        {
            var result = CopyFileNative(source, target, out var copied);
            if (result != 0)
                throw new IOException($"Rust backup engine failed to copy a file (code {result}): {source}");
            bytesCopied = checked((long)copied);
            return true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }

        bytesCopied = 0;
        return false;
    }
}
