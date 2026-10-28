using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace GermanOSBackup;

internal static class Program
{
    private const string AppFolderName = "GermanOSBackup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "GermanOSBackup";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\GermanOSBackup.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance) return;

        ApplicationConfiguration.Initialize();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(localAppData, AppFolderName);
        var configPath = Path.Combine(dataDir, "config.json");
        var logPath = Path.Combine(dataDir, "backup.log");

        try
        {
            Directory.CreateDirectory(dataDir);

            // Keep the running app in a stable per-user location, then launch that copy.
            var installedExe = Path.Combine(localAppData, "Programs", AppFolderName, "GermanOSBackup.exe");
            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe)
                && File.Exists(currentExe)
                && !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(installedExe)!);
                File.Copy(currentExe, installedExe, overwrite: true);
                mutex.ReleaseMutex();
                Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
                return;
            }

            AppConfig config;
            if (!File.Exists(configPath))
            {
                using var setup = new SetupForm();
                if (setup.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(setup.Destination)) return;

                config = new AppConfig { Destination = Path.GetFullPath(setup.Destination) };
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                RegisterStartup(installedExe);
            }
            else
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
                var installedPath = Path.Combine(localAppData, "Programs", AppFolderName, "GermanOSBackup.exe");
                if (File.Exists(installedPath)) RegisterStartup(installedPath);
            }

            if (string.IsNullOrWhiteSpace(config.Destination) || !Path.IsPathFullyQualified(config.Destination))
                throw new InvalidDataException("Путь для резервной копии в config.json некорректен.");

            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.Equals(Path.GetPathRoot(config.Destination), systemDrive, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Папка резервной копии должна находиться не на системном диске.");

            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
            Application.Run(new BackupApplicationContext(config, logPath));
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(dataDir); File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}"); } catch { }
            MessageBox.Show($"German OS Backup не смог запуститься. Подробности в журнале:\n{logPath}", "German OS Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void RegisterStartup(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(RunValueName, $"\"{exePath}\"");
}
        catch { }
    }
}

internal sealed class AppConfig
{
    public string Destination { get; set; } = "";
}
