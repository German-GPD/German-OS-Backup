using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GermanOSBackup;

/// <summary>
/// Optional native Rust file-copy engine. The managed copy path remains a safe
/// fallback when the native library is not present (for development runs).
/// </summary>
internal static class RustCopyEngine
{
    [DllImport("german_os_backup_engine.dll", EntryPoint = "gob_copy_file_utf16",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int CopyFileNative(string source, string target);

    internal static bool TryCopy(string source, string target)
    {
        try
        {
            var result = CopyFileNative(source, target);
            if (result != 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException("Rust-движок не смог безопасно скопировать файл.",
                    new Win32Exception(error == 0 ? result : error));
            }
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }
}
