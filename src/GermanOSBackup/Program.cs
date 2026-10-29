using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

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
            var isFirstRun = config is null || string.IsNullOrWhiteSpace(config.Destination);
            if (isFirstRun)
            {
                using var setup = new FirstRunForm();
                if (setup.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(setup.Destination)) return;
                config = new AppConfig
                {
                    Destination = Path.GetFullPath(setup.Destination),
                    FirstRunComplete = true,
                    StartWithWindows = setup.StartWithWindows,
                    SelectedSources = BackupEngine.DefaultSources().ToList()
                };
                SaveConfig(config);

                using (var sources = new SourcesForm(config))
                {
                    if (sources.ShowDialog() != DialogResult.OK)
                    {
                        File.Delete(ConfigPath);
                        MessageBox.Show("Настройка не завершена. При следующем запуске мастер откроется снова.", "German OS Backup C# Edition", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
            }

            ValidateDestination(config!.Destination);
            if (config.SelectedSources is not { Count: > 0 })
            {
                config.SelectedSources = BackupEngine.DefaultSources().ToList();
                SaveConfig(config);
            }

            if (config.StartWithWindows.HasValue) ConfigureStartup(config.StartWithWindows.Value);

            var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            try { Process.GetCurrentProcess().PriorityClass = background ? ProcessPriorityClass.BelowNormal : (args.Contains("--first-run", StringComparer.OrdinalIgnoreCase) ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal); } catch { }
            Application.Run(new DashboardForm(config, background));
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:O}] FATAL {ex}{Environment.NewLine}"); } catch { }
            MessageBox.Show($"German OS Backup C# Edition не смог запуститься. Подробности: {LogPath}", "German OS Backup C# Edition", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    internal static void ConfigureStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
            key.SetValue("GermanOSBackup", $"\"{Application.ExecutablePath}\" --background");
        else
            key.DeleteValue("GermanOSBackup", throwOnMissingValue: false);
    }

    internal static void ValidateDestination(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Путь резервной копии должен быть абсолютным.");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var windowsDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.Equals(Path.GetPathRoot(full), windowsDrive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Для копии выбери другой диск, не системный.");
        if (string.IsNullOrWhiteSpace(full) || full.Length < 3) throw new InvalidDataException("Выбери отдельную папку, а не корень диска.");
        var selectedSources = configSourcesForValidation();
        foreach (var source in selectedSources)
        {
            var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(sourceFull, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                sourceFull.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Папка резервной копии не должна совпадать с выбранной папкой, находиться внутри неё или быть её родительской папкой.");
        }

        IEnumerable<string> configSourcesForValidation()
        {
            var config = LoadConfig();
            if (config?.SelectedSources is { Count: > 0 }) return config.SelectedSources;
            return BackupEngine.Sources().Select(x => x.Path);
        }
    }
}

internal sealed class AppConfig
{
    public string Destination { get; set; } = "";
    public string PerformanceMode { get; set; } = "balanced";
    public bool FirstRunComplete { get; set; }
    public bool? StartWithWindows { get; set; }
    public List<string> SelectedSources { get; set; } = new();
    public List<string> AutoUpdateSources { get; set; } = new();
}
