using System.Diagnostics;
using System.Text.Json;

namespace GermanOSBackup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GermanOSBackup");
        var configPath = Path.Combine(dataDir, "config.json");
        var logPath = Path.Combine(dataDir, "backup.log");
        try
        {
            Directory.CreateDirectory(dataDir);
            AppConfig config;
            if (!File.Exists(configPath))
            {
                using var setup = new SetupForm();
                if (setup.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(setup.Destination)) return;
                config = new AppConfig { Destination = setup.Destination! };
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
            }

            if (string.IsNullOrWhiteSpace(config.Destination) || !Path.IsPathFullyQualified(config.Destination))
                throw new InvalidDataException("Путь для резервной копии в config.json некорректен.");

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;
            Application.Run(new BackupApplicationContext(config, logPath));
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(dataDir); File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}"); } catch { }
            MessageBox.Show($"German OS Backup не смог запуститься. Подробности в журнале:\n{logPath}", "German OS Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

internal sealed class AppConfig { public string Destination { get; set; } = ""; }
