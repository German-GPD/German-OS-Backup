using System.Diagnostics;
using System.Text.Json;

namespace GermanOSBackup;

internal static class Program
{
    internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GermanOSBackup");
    internal static readonly string ConfigPath = Path.Combine(DataDirectory, "config.json");
    internal static readonly string LogPath = Path.Combine(DataDirectory, "backup.log");

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\GermanOSBackup.SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(DataDirectory);
        try
        {
            var config = LoadConfig();
            if (config is null || string.IsNullOrWhiteSpace(config.Destination))
            {
                using var setup = new FirstRunForm();
                if (setup.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(setup.Destination)) return;
                config = new AppConfig { Destination = Path.GetFullPath(setup.Destination), FirstRunComplete = true };
                SaveConfig(config);
            }
            ValidateDestination(config.Destination);
            var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            try { Process.GetCurrentProcess().PriorityClass = background ? ProcessPriorityClass.BelowNormal : (args.Contains("--first-run", StringComparer.OrdinalIgnoreCase) ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal); } catch { }
            Application.Run(new DashboardForm(config, background));
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] FATAL {ex}{Environment.NewLine}");
            MessageBox.Show($"German OS Backup не смог запуститься. Подробности: {LogPath}", "German OS Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static AppConfig? LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return null;
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)); }
        catch { return null; }
    }

    internal static void SaveConfig(AppConfig config)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ConfigPath, true);
    }

    internal static void ValidateDestination(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Путь резервной копии должен быть абсолютным.");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var windowsDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.Equals(Path.GetPathRoot(full), windowsDrive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Для копии выбери другой диск, не системный.");
        if (string.IsNullOrWhiteSpace(full) || full.Length < 3) throw new InvalidDataException("Выбери отдельную папку, а не корень диска.");
        foreach (var (_, source) in BackupEngine.Sources())
            if (full.Equals(source, StringComparison.OrdinalIgnoreCase) || full.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Папка резервной копии не должна находиться внутри исходной папки.");
    }
}

internal sealed class AppConfig
{
    public string Destination { get; set; } = "";
    public bool FirstRunComplete { get; set; }
}