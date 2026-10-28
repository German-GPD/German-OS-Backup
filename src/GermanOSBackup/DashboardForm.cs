using System.Diagnostics;

namespace GermanOSBackup;

internal sealed class DashboardForm : Form
{
    private readonly AppConfig config;
    private readonly BackupEngine engine;
    private readonly bool startHidden;
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 15 * 60 * 1000 };
    private readonly Label status = Palette.Label("Подготовка…", 10, Palette.Green, true);
    private readonly Label headline = Palette.Label("Готовим резервную копию", 21, Palette.Text, true);
    private readonly Label details = Palette.Label("Подключаем источники и рассчитываем объём.", 10, Palette.Muted);
    private readonly Label copied = Palette.Label("0 Б", 23, Palette.Text, true);
    private readonly Label files = Palette.Label("0", 23, Palette.Text, true);
    private readonly Label errors = Palette.Label("0", 23, Palette.Text, true);
    private readonly Label speed = Palette.Label("—", 16, Palette.Text, true);
    private readonly Label progressText = Palette.Label("Подготовка к сканированию", 10, Palette.Muted);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 22, ForeColor = Palette.Green, BackColor = Palette.Panel2 };
    private readonly Label destinationLabel = Palette.Label("", 10, Palette.Text);
    private readonly Label lastScanLabel = Palette.Label("Первое сканирование ещё не завершено.", 9, Palette.Muted);
    private readonly NotifyIcon tray;
    private Button? stopButton;
    private bool allowClose;
    private bool started;
    private bool operationBusy;

    public DashboardForm(AppConfig config, bool startHidden)
    {
        this.config = config;
        this.startHidden = startHidden;
        engine = new BackupEngine(config, Program.LogPath);

        Text = "German OS Backup";
        ClientSize = new Size(1020, 790);
        MinimumSize = new Size(820, 690);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        Font = Palette.Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть главное окно", null, (_, _) => ShowWindow());
        menu.Items.Add("Открыть папку резервной копии", null, (_, _) => OpenDestination());
        menu.Items.Add("Запустить проверку сейчас", null, async (_, _) => await RunScanAsync());
        menu.Items.Add("Остановить текущую операцию", null, (_, _) => StopOperation());
        menu.Items.Add("Глубокая проверка SHA-256", null, async (_, _) => await RunVerifyAsync());
        menu.Items.Add("Восстановление файлов…", null, (_, _) => OpenRestore());
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenFile(Program.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => { allowClose = true; Close(); });
        tray = new NotifyIcon
        {
            Text = "German OS Backup",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowWindow();

        BuildUi();
        Shown += async (_, _) =>
        {
            if (startHidden) Hide();
            if (!started) { started = true; await StartBackupAsync(); }
        };
        uiTimer.Tick += (_, _) => RefreshStats();
        uiTimer.Start();
        scanTimer.Tick += async (_, _) => await RunScanAsync();
        scanTimer.Start();
        FormClosing += (_, e) =>
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(1800, "German OS Backup", "Программа продолжает работу в фоне.", ToolTipIcon.Info);
            }
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Palette.Background,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(24, 14, 24, 18),
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background, Margin = Padding.Empty };
        var mark = Palette.Label("GOB", 14, Palette.Background, true);
        mark.TextAlign = ContentAlignment.MiddleCenter;
        mark.BackColor = Palette.Green;
        mark.SetBounds(0, 6, 48, 38);
        header.Controls.Add(mark);
        var brand = Palette.Label("German OS Backup", 14, Palette.Text, true);
        brand.SetBounds(60, 4, 300, 24);
        header.Controls.Add(brand);
        var caption = Palette.Label("Локальное резервное копирование Windows", 9, Palette.Muted);
        caption.SetBounds(60, 28, 390, 20);
        header.Controls.Add(caption);
        var version = Palette.Label("WINDOWS  ·  LOCAL BACKUP", 9, Palette.Muted, true);
        version.TextAlign = ContentAlignment.MiddleRight;
        version.SetBounds(0, 10, 400, 24);
        version.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(version);
        root.Controls.Add(header, 0, 0);

        var intro = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Palette.Background, Margin = new Padding(0, 8, 0, 0) };
        intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        intro.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = Palette.Label("Обзор защиты", 25, Palette.Text, true);
        title.Dock = DockStyle.Fill;
        var subtitle = Palette.Label("Состояние копирования, производительность и быстрые действия — всё в главном окне.", 10, Palette.Muted);
        subtitle.Dock = DockStyle.Fill;
        intro.Controls.Add(title, 0, 0);
        intro.Controls.Add(subtitle, 0, 1);
        root.Controls.Add(intro, 0, 1);

        var statusCard = CreateCard();
        statusCard.Padding = new Padding(22, 17, 22, 16);
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Palette.Panel, Margin = Padding.Empty };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusLayout.Controls.Add(status, 0, 0);
        statusLayout.Controls.Add(headline, 0, 1);
        statusLayout.Controls.Add(details, 0, 2);
        statusLayout.SetColumnSpan(details, 2);
        stopButton = Palette.Button("Остановить", false);
        stopButton.Dock = DockStyle.Fill;
        stopButton.Margin = new Padding(8, 0, 0, 8);
        stopButton.Click += (_, _) => StopOperation();
        statusLayout.Controls.Add(stopButton, 1, 0);
        statusLayout.SetRowSpan(stopButton, 2);
        statusCard.Controls.Add(statusLayout);
        root.Controls.Add(statusCard, 0, 2);

        var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Palette.Background, Margin = new Padding(0, 12, 0, 0) };
        for (var i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        AddStat(stats, 0, "ОБЪЁМ СКОПИРОВАННОГО", copied, "данные, записанные за всё время");
        AddStat(stats, 1, "ФАЙЛЫ", files, "успешно скопировано");
        AddStat(stats, 2, "ОШИБКИ", errors, "подробности доступны в журнале");
        AddStat(stats, 3, "СКОРОСТЬ", speed, "текущая скорость копирования");
        root.Controls.Add(stats, 0, 3);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, BackColor = Palette.Background, Margin = new Padding(0, 12, 0, 0) };
        for (var i = 0; i < 5; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actionTitle = Palette.Label("БЫСТРЫЕ ДЕЙСТВИЯ", 9, Palette.Muted, true);
        actionTitle.Dock = DockStyle.Fill;
        actions.Controls.Add(actionTitle, 0, 0);
        actions.SetColumnSpan(actionTitle, 5);
        AddAction(actions, 0, "Проверить сейчас", async () => await RunScanAsync());
        AddAction(actions, 1, "Проверка SHA-256", async () => await RunVerifyAsync());
        AddAction(actions, 2, "Восстановление", () => OpenRestore());
        AddAction(actions, 3, "Источники", () => OpenSources());
        AddAction(actions, 4, "Журнал событий", () => OpenFile(Program.LogPath));
        root.Controls.Add(actions, 0, 4);

        var bottom = CreateCard();
        bottom.Padding = new Padding(18, 12, 18, 12);
        var bottomLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Palette.Panel, Margin = Padding.Empty };
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bottomLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        bottomLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        bottomLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var destTitle = Palette.Label("МЕСТО РЕЗЕРВНОЙ КОПИИ", 9, Palette.Green, true);
        destTitle.Dock = DockStyle.Fill;
        bottomLayout.Controls.Add(destTitle, 0, 0);
        destinationLabel.Dock = DockStyle.Fill;
        destinationLabel.AutoEllipsis = true;
        bottomLayout.Controls.Add(destinationLabel, 0, 1);
        lastScanLabel.Dock = DockStyle.Fill;
        bottomLayout.Controls.Add(lastScanLabel, 0, 2);
        var open = Palette.Button("Открыть папку", false);
        open.Dock = DockStyle.Fill;
        open.Margin = new Padding(8, 4, 0, 4);
        open.Click += (_, _) => OpenDestination();
        bottomLayout.Controls.Add(open, 1, 0);
        bottomLayout.SetRowSpan(open, 3);
        var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Palette.Panel, Margin = new Padding(0, 8, 0, 0) };
        progressRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        progressRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progressText.Dock = DockStyle.Fill;
        progressRow.Controls.Add(progressText, 0, 0);
        progressRow.Controls.Add(progress, 0, 1);
        bottomLayout.Controls.Add(progressRow, 0, 3);
        bottomLayout.SetColumnSpan(progressRow, 2);
        // Reserve a fourth row for the live progress indicator.
        bottomLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        bottom.Controls.Add(bottomLayout);
        root.Controls.Add(bottom, 0, 5);

        destinationLabel.Text = config.Destination;
    }

    private static Panel CreateCard() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Palette.Panel,
        Padding = new Padding(0),
        Margin = Padding.Empty
    };

    private static void AddStat(TableLayoutPanel table, int column, string caption, Label value, string footer)
    {
        var card = CreateCard();
        card.Margin = new Padding(column == 0 ? 0 : 7, 0, column == 3 ? 0 : 7, 0);
        card.Padding = new Padding(14, 12, 10, 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Palette.Panel, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var cap = Palette.Label(caption, 8.5f, Palette.Muted, true);
        cap.Dock = DockStyle.Fill;
        cap.AutoEllipsis = true;
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(0);
        var foot = Palette.Label(footer, 8.5f, Palette.Muted);
        foot.Dock = DockStyle.Fill;
        foot.AutoEllipsis = true;
        layout.Controls.Add(cap, 0, 0);
        layout.Controls.Add(value, 0, 1);
        layout.Controls.Add(foot, 0, 2);
        card.Controls.Add(layout);
        table.Controls.Add(card, column, 0);
    }

    private static void AddAction(TableLayoutPanel table, int column, string text, Func<Task> action)
    {
        var button = Palette.Button(text, column == 0);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(column == 0 ? 0 : 5, 0, column == 4 ? 0 : 5, 0);
        button.Click += async (_, _) => await action();
        table.Controls.Add(button, column, 1);
    }

    private static void AddAction(TableLayoutPanel table, int column, string text, Action action)
    {
        var button = Palette.Button(text, false);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(column == 0 ? 0 : 5, 0, column == 4 ? 0 : 5, 0);
        button.Click += (_, _) => action();
        table.Controls.Add(button, column, 1);
    }

    private async Task StartBackupAsync()
    {
        try
        {
            SetBusy(true);
            await engine.StartAsync();
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        catch (Exception ex)
        {
            status.Text = "ТРЕБУЕТСЯ ВНИМАНИЕ";
            headline.Text = "Не удалось начать копирование";
            details.Text = ex.Message;
            tray.ShowBalloonTip(3000, "German OS Backup", "Не удалось начать резервное копирование. Откройте журнал событий.", ToolTipIcon.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task RunScanAsync()
    {
        if (operationBusy) return;
        try
        {
            SetBusy(true);
            details.Text = "Проверяем выбранные папки и обновляем резервную копию…";
            await engine.RequestScanAsync();
        }
        catch (Exception ex) { ShowError("Проверка резервной копии", ex); }
        finally { SetBusy(false); RefreshStats(); }
    }

    private async Task RunVerifyAsync()
    {
        if (operationBusy) return;
        try
        {
            SetBusy(true);
            details.Text = "Глубокая проверка читает оригиналы и копии, сравнивая SHA-256. Это может занять время.";
            var result = await engine.VerifyBackupAsync();
            MessageBox.Show(this,
                $"Проверено файлов: {result.Checked:N0}{Environment.NewLine}Нет копии: {result.Missing:N0}{Environment.NewLine}Отличается или ошибка: {result.Different:N0}",
                "Результат проверки SHA-256", MessageBoxButtons.OK,
                result.Missing + result.Different == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { details.Text = "Проверка остановлена."; }
        catch (Exception ex) { ShowError("Глубокая проверка", ex); }
        finally { SetBusy(false); RefreshStats(); }
    }

    private void OpenSources()
    {
        using var form = new SourcesForm(config);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            engine.RefreshWatchers();
            destinationLabel.Text = config.Destination;
            details.Text = "Источники обновлены. Запускаем проверку выбранных папок…";
            _ = RunScanAsync();
        }
    }

    private void OpenRestore()
    {
        using var form = new RestoreForm(config.Destination);
        form.ShowDialog(this);
        RefreshStats();
    }

    private void StopOperation()
    {
        engine.StopCurrentScan();
        details.Text = "Отправлена команда остановки. Уже сохранённые файлы останутся на месте.";
    }

    private void SetBusy(bool busy)
    {
        operationBusy = busy;
        if (stopButton is not null) stopButton.Enabled = busy || engine.Stats.IsScanning;
    }

    private void RefreshStats()
    {
        var s = engine.Stats;
        copied.Text = FormatBytes(Interlocked.Read(ref s.BytesCopied));
        files.Text = Interlocked.Read(ref s.FilesCopied).ToString("N0");
        errors.Text = Interlocked.Read(ref s.Errors).ToString("N0");
        speed.Text = FormatBytes(Interlocked.Read(ref s.BytesPerSecond)) + "/с";
        bool scanning = Volatile.Read(ref s.IsScanning);
        status.Text = scanning ? "●  КОПИРОВАНИЕ ВЫПОЛНЯЕТСЯ" : "●  " + (s.HasCompletedInitialScan ? "ЗАЩИТА АКТИВНА" : "ОЖИДАНИЕ");
        headline.Text = scanning ? (s.HasCompletedInitialScan ? "Сохраняем изменения" : "Создаём первую полную копию") : (s.HasCompletedInitialScan ? "Резервная копия актуальна" : "Ожидаем начало копирования");
        if (!operationBusy)
            details.Text = scanning ? "Файлы обрабатываются в фоне. Можно остановить текущую операцию." : "Изменения отслеживаются автоматически, контрольная проверка выполняется каждые 15 минут.";
        progress.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        progressText.Text = scanning ? $"Обход файловой системы · проверено файлов: {Interlocked.Read(ref s.FilesSeen):N0}" : "Фоновое отслеживание изменений включено";
        progress.Value = scanning ? 100 : 0;
        lastScanLabel.Text = "Последняя проверка: " + (s.LastScanFinished == DateTime.MinValue ? "ещё не завершена" : s.LastScanFinished.ToString("dd.MM.yyyy  HH:mm"));
        if (stopButton is not null) stopButton.Enabled = scanning || operationBusy;
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double n = Math.Max(0, value); var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OpenDestination() { try { Directory.CreateDirectory(config.Destination); Process.Start(new ProcessStartInfo(config.Destination) { UseShellExecute = true }); } catch (Exception ex) { ShowError("Папка резервной копии", ex); } }
    private static void OpenFile(string path)
    {
        try { if (!File.Exists(path)) File.WriteAllText(path, "German OS Backup log"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть файл", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void ShowError(string title, Exception ex) => MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        uiTimer.Stop(); uiTimer.Dispose(); scanTimer.Stop(); scanTimer.Dispose();
        tray.Visible = false; tray.Dispose(); engine.Dispose();
        base.OnFormClosed(e);
    }
}
