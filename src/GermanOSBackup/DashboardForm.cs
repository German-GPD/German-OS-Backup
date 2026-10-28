using System.Diagnostics;

namespace GermanOSBackup;

internal sealed class DashboardForm : Form
{
    private readonly AppConfig config;
    private readonly BackupEngine engine;
    private readonly bool startHidden;
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 15 * 60 * 1000 };
    private readonly Label status = Palette.Label("Подготовка…", 12, Palette.Green, true);
    private readonly Label headline = Palette.Label("Готовим резервную копию", 21, Palette.Text, true);
    private readonly Label details = Palette.Label("Подключаем источники и рассчитываем объём.", 10, Palette.Muted);
    private readonly Label copied = Palette.Label("0 Б", 22, Palette.Text, true);
    private readonly Label files = Palette.Label("0", 22, Palette.Text, true);
    private readonly Label errors = Palette.Label("0", 22, Palette.Text, true);
    private readonly Label speed = Palette.Label("—", 13, Palette.Text, true);
    private readonly Label progressText = Palette.Label("Подготовка к сканированию", 10, Palette.Muted);
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, ForeColor = Palette.Green, BackColor = Palette.Panel2 };
    private readonly NotifyIcon tray;
    private bool allowClose;
    private bool started;

    public DashboardForm(AppConfig config, bool startHidden)
    {
        this.config = config; this.startHidden = startHidden; engine = new BackupEngine(config, Program.LogPath);
        Text = "German OS Backup"; ClientSize = new Size(900, 690); MinimumSize = new Size(760, 620); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Background; ForeColor = Palette.Text; Font = Palette.Font;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть German OS Backup", null, (_, _) => ShowWindow());
        menu.Items.Add("Открыть папку копий", null, (_, _) => OpenDestination());
        menu.Items.Add("Проверить сейчас", null, async (_, _) => await engine.RequestScanAsync());
        menu.Items.Add("Остановить текущую проверку", null, (_, _) => engine.StopCurrentScan());
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenFile(Program.LogPath));
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Выход", null, (_, _) => { allowClose = true; Close(); });
        tray = new NotifyIcon { Text = "German OS Backup", Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        BuildUi();
        Shown += async (_, _) =>
        {
            if (startHidden) Hide();
            if (!started) { started = true; await StartBackupAsync(); }
        };
        uiTimer.Tick += (_, _) => RefreshStats(); uiTimer.Start();
        scanTimer.Tick += async (_, _) => await engine.RequestScanAsync(); scanTimer.Start();
        FormClosing += (_, e) => { if (!allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); tray.ShowBalloonTip(1800, "German OS Backup", "Программа продолжает работу в фоне.", ToolTipIcon.Info); } };
    }

    private void BuildUi()
    {
        var header = new Panel { BackColor = Palette.Background }; header.SetBounds(0, 0, 900, 76); header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(header);
        var logo = Palette.Label("G_", 17, Palette.Green, true); logo.SetBounds(28, 23, 38, 30); header.Controls.Add(logo);
        var brand = Palette.Label("German OS Backup", 13, Palette.Text, true); brand.SetBounds(72, 24, 230, 28); header.Controls.Add(brand);
        var badge = Palette.Label("LOCAL BACKUP  /  WINDOWS", 9, Palette.Muted, true); badge.TextAlign = ContentAlignment.MiddleRight; badge.SetBounds(630, 25, 235, 24); badge.Anchor = AnchorStyles.Top | AnchorStyles.Right; header.Controls.Add(badge);
        var line = new Panel { BackColor = Palette.Line, Height = 1 }; line.SetBounds(25, 75, 840, 1); line.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(line);
        var hello = Palette.Label("Резервная копия", 25, Palette.Text, true); hello.SetBounds(30, 98, 620, 40); Controls.Add(hello);
        var sub = Palette.Label("Состояние защиты файлов и активность копирования.", 10, Palette.Muted); sub.SetBounds(32, 141, 650, 23); Controls.Add(sub);
        var statusPanel = new Panel { BackColor = Palette.Panel, BorderStyle = BorderStyle.FixedSingle }; statusPanel.SetBounds(30, 183, 840, 142); statusPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(statusPanel);
        status.SetBounds(22, 16, 600, 24); statusPanel.Controls.Add(status); headline.SetBounds(22, 44, 790, 36); statusPanel.Controls.Add(headline); details.SetBounds(22, 87, 790, 30); statusPanel.Controls.Add(details);
        var stopButton = Palette.Button("Остановить", false); stopButton.SetBounds(650, 12, 160, 34); stopButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; statusPanel.Controls.Add(stopButton);
        stopButton.Click += (_, _) => { engine.StopCurrentScan(); details.Text = "Останавливаем текущую операцию… Уже сохранённые файлы останутся на месте."; };
        var stats = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, BackColor = Palette.Background }; stats.SetBounds(30, 346, 840, 112); stats.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); Controls.Add(stats);
        AddStat(stats, 0, "СКОПИРОВАНО", copied, "объём файлов"); AddStat(stats, 1, "ФАЙЛЫ", files, "сохранено файлов"); AddStat(stats, 2, "ОШИБКИ", errors, "подробности в журнале"); AddStat(stats, 3, "СКОРОСТЬ", speed, "текущая скорость");
        var progressPanel = new Panel { BackColor = Palette.Panel, BorderStyle = BorderStyle.FixedSingle }; progressPanel.SetBounds(30, 478, 840, 91); progressPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(progressPanel);
        progressText.SetBounds(17, 12, 790, 20); progressText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; progressPanel.Controls.Add(progressText);
        progress.SetBounds(17, 42, 802, 10); progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; progressPanel.Controls.Add(progress);
        var destTitle = Palette.Label("МЕСТО РЕЗЕРВНОЙ КОПИИ", 9, Palette.Green, true); destTitle.SetBounds(32, 590, 300, 20); Controls.Add(destTitle);
        var destination = Palette.Label(config.Destination, 10, Palette.Muted); destination.SetBounds(32, 614, 590, 25); destination.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(destination);
        var settings = Palette.Button("Источники…", false); settings.SetBounds(515, 601, 165, 40); settings.Anchor = AnchorStyles.Top | AnchorStyles.Right; Controls.Add(settings);
        settings.Click += async (_, _) =>
        {
            using var form = new SourcesForm(config);
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                engine.RefreshWatchers();
                details.Text = "Источники обновлены. Проверяем выбранные папки…";
                await engine.RequestScanAsync();
            }
        };
        var open = Palette.Button("Открыть папку", false); open.SetBounds(700, 601, 170, 40); open.Anchor = AnchorStyles.Top | AnchorStyles.Right; Controls.Add(open); open.Click += (_, _) => OpenDestination();
        var note = Palette.Label("Окно можно закрыть — резервное копирование останется в трее.", 9, Palette.Muted); note.SetBounds(32, 650, 800, 20); Controls.Add(note);
    }

    private static void AddStat(TableLayoutPanel table, int column, string caption, Label value, string footer)
    {
        var panel = new Panel { BackColor = Palette.Panel, Margin = new Padding(column == 0 ? 0 : 5, 0, 5, 0), Dock = DockStyle.Fill }; table.Controls.Add(panel, column, 0);
        var cap = Palette.Label(caption, 8, Palette.Muted, true); cap.SetBounds(13, 12, 175, 18); panel.Controls.Add(cap);
        value.SetBounds(13, 32, 180, 31); panel.Controls.Add(value);
        var foot = Palette.Label(footer, 8, Palette.Muted); foot.SetBounds(13, 72, 185, 18); panel.Controls.Add(foot);
    }

    private async Task StartBackupAsync()
    {
        try
        {
            await engine.StartAsync();
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        catch (Exception ex) { status.Text = "● ТРЕБУЕТСЯ ВНИМАНИЕ"; headline.Text = "Не удалось начать копирование"; details.Text = ex.Message; }
    }

    private void RefreshStats()
    {
        var s = engine.Stats; copied.Text = FormatBytes(Interlocked.Read(ref s.BytesCopied)); files.Text = Interlocked.Read(ref s.FilesCopied).ToString("N0"); errors.Text = Interlocked.Read(ref s.Errors).ToString("N0"); speed.Text = FormatBytes(Interlocked.Read(ref s.BytesPerSecond)) + "/с";
        bool scanning = Volatile.Read(ref s.IsScanning);
        status.Text = scanning ? "● КОПИРОВАНИЕ ВЫПОЛНЯЕТСЯ" : "● " + (s.HasCompletedInitialScan ? "ЗАЩИТА АКТИВНА" : "ОЖИДАНИЕ");
        headline.Text = scanning ? (s.HasCompletedInitialScan ? "Обновляем изменения" : "Создаём первую полную копию") : (s.HasCompletedInitialScan ? "Резервная копия актуальна" : "Ожидаем начало копирования");
        details.Text = $"Последнее сканирование: {(s.LastScanFinished == DateTime.MinValue ? "ещё не завершено" : s.LastScanFinished.ToString("dd.MM.yyyy HH:mm:ss"))}. Ошибки и пропущенные файлы записываются в журнал.";
        progress.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        progressText.Text = scanning ? $"Обход файловой системы · проверено файлов: {Interlocked.Read(ref s.FilesSeen):N0}" : "Фоновое отслеживание изменений включено";
        progress.Value = scanning ? 100 : 0;
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"]; double n = Math.Max(0, value); var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OpenDestination() { try { Process.Start(new ProcessStartInfo(config.Destination) { UseShellExecute = true }); } catch { } }
    private static void OpenFile(string path) { try { if (!File.Exists(path)) File.WriteAllText(path, "German OS Backup log"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
    protected override void OnFormClosed(FormClosedEventArgs e) { uiTimer.Stop(); uiTimer.Dispose(); scanTimer.Stop(); scanTimer.Dispose(); tray.Visible = false; tray.Dispose(); engine.Dispose(); base.OnFormClosed(e); }
}