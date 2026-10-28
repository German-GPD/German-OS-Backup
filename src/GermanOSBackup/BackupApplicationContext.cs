using Microsoft.Win32;
using System.Diagnostics;

namespace GermanOSBackup;

internal sealed class BackupApplicationContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer;
    private readonly BackupEngine engine;
    private readonly AppConfig config;

    public BackupApplicationContext(AppConfig config, string logPath)
    {
        this.config = config;
        engine = new BackupEngine(config, logPath);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть папку копий", null, (_, _) => OpenDestination());
        menu.Items.Add("Проверить сейчас", null, async (_, _) => await engine.RequestScanAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Включить запуск с Windows", null, (_, _) => Startup(true));
        menu.Items.Add("Выключить запуск с Windows", null, (_, _) => Startup(false));
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenFile(logPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Text = "German OS Backup",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => OpenDestination();

        timer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        timer.Tick += async (_, _) => await engine.RequestScanAsync();
        timer.Start();

        _ = StartEngineSafelyAsync();
    }

    private async Task StartEngineSafelyAsync()
    {
        try { await engine.StartAsync(); }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GermanOSBackup", "backup.log"), $"[{DateTime.Now:O}] Ошибка запуска: {ex}{Environment.NewLine}"); } catch { }
            tray.ShowBalloonTip(5000, "German OS Backup", "Не удалось запустить копирование. Проверь журнал.", ToolTipIcon.Warning);
        }
    }

    private void OpenDestination()
    {
        try
        {
            Directory.CreateDirectory(config.Destination);
            Process.Start(new ProcessStartInfo(config.Destination) { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, "German OS Backup log\n");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private static void Startup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                    key.SetValue("GermanOSBackup", $"\"{exe}\"");
}
            else
            {
                key.DeleteValue("GermanOSBackup", false);
            }
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        timer.Dispose();
        tray.Visible = false;
        tray.Dispose();
        engine.Dispose();
        base.ExitThreadCore();
    }
}
