using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GermanOSBackup;

internal sealed class DashboardForm : Form
{
    private readonly AppConfig config;
    private readonly BackupEngine engine;
    private readonly bool startHidden;
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };
    private readonly NotifyIcon tray;
    private bool allowClose;
    private bool started;
    private bool webReady;
    private ulong previousIdle, previousKernel, previousUser;
    private bool hasCpuSample;
    private static readonly System.Net.Http.HttpClient UpdateClient = new();

    public DashboardForm(AppConfig config, bool startHidden)
    {
        this.config = config;
        this.startHidden = startHidden;
        engine = new BackupEngine(config, Program.LogPath);
        Text = "German OS Backup C# Edition";
        ClientSize = new Size(1180, 820);
        MinimumSize = new Size(900, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 16, 15);
        Font = new Font("Segoe UI", 10F);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть German OS Backup C# Edition", null, (_, _) => ShowWindow());
        menu.Items.Add("Выбрать папки для копирования…", null, async (_, _) => await ShowSourcesAsync());
        menu.Items.Add("Проверить сейчас", null, async (_, _) => await engine.RequestScanAsync());
        menu.Items.Add("Остановить текущую операцию", null, (_, _) => engine.StopCurrentScan());
        menu.Items.Add("Глубокая проверка (SHA-256)", null, async (_, _) => await VerifyAsync());
        menu.Items.Add("Восстановить файлы…", null, (_, _) => ShowRestore());
        menu.Items.Add("Открыть папку копий", null, (_, _) => OpenDestination());
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenFile(Program.LogPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => { allowClose = true; Close(); });
        tray = new NotifyIcon
        {
            Text = "German OS Backup C# Edition",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowWindow();

        Controls.Add(web);
        Shown += async (_, _) =>
        {
            ApplyMicaBackdrop();
            try
            {
                // Keep WebView2's profile in a user-writable location instead of the
                // install directory; this avoids common first-run/permission failures.
                var webViewData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GermanOSBackup", "WebView2");
                Directory.CreateDirectory(webViewData);

                await web.EnsureCoreWebView2Async(
                    await CoreWebView2Environment.CreateAsync(null, webViewData));

                web.DefaultBackgroundColor = Color.Transparent;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.WebMessageReceived += async (_, args) =>
                {
                    try
                    {
                        using var json = JsonDocument.Parse(args.WebMessageAsJson);
                        var action = json.RootElement.TryGetProperty("action", out var value) ? value.GetString() : "";
                        await HandleActionAsync(action ?? "", json.RootElement);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "German OS Backup C# Edition", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                web.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess)
                    {
                        File.AppendAllText(Program.LogPath, $"[{DateTime.Now:O}] WebView2 navigation failed: {args.WebErrorStatus}{Environment.NewLine}");
                        MessageBox.Show(this, "Web-интерфейс не загрузился: " + args.WebErrorStatus +
                            "\\nПодробности записаны в журнал: " + Program.LogPath,
                            "German OS Backup C# Edition", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    webReady = true;
                    RefreshStats();
                };
                web.NavigateToString(Html);
            }
            catch (Exception ex)
            {
                File.AppendAllText(Program.LogPath, $"[{DateTime.Now:O}] WebView2 initialization failed: {ex}{Environment.NewLine}");
                MessageBox.Show(this,
                    "Не удалось запустить WebView2.\\n\\n" +
                    ex.Message + "\\n\\n" +
                    "Проверь журнал: " + Program.LogPath +
                    "\\n\\nЕсли Runtime уже установлен, это может быть проблема профиля WebView2 или его прав доступа.",
                    "German OS Backup C# Edition", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (startHidden) Hide();
            if (!started)
            {
                started = true;
                await engine.StartAsync();
            }
        };
        uiTimer.Tick += (_, _) => RefreshStats();
        uiTimer.Start();
        FormClosing += (_, e) =>
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(1800, "German OS Backup C# Edition", "Работа продолжается в фоне. Открой приложение через значок в трее.", ToolTipIcon.Info);
            }
        };
    }

    private async Task HandleActionAsync(string action, JsonElement root)
    {
        switch (action)
        {
            case "checkUpdates":
                await CheckUpdatesAsync();
                break;
            case "sources":
                await ShowSourcesAsync();
                break;
            case "toggleAuto":
                if (root.TryGetProperty("path", out var autoPathValue))
                {
                    var autoPath = autoPathValue.GetString();
                    if (!string.IsNullOrWhiteSpace(autoPath) &&
                        config.SelectedSources.Any(s => string.Equals(Path.GetFullPath(s), Path.GetFullPath(autoPath), StringComparison.OrdinalIgnoreCase)))
                    {
                        config.AutoUpdateSources ??= new List<string>();
                        var existing = config.AutoUpdateSources.FindIndex(s => string.Equals(Path.GetFullPath(s), Path.GetFullPath(autoPath), StringComparison.OrdinalIgnoreCase));
                        if (existing >= 0) config.AutoUpdateSources.RemoveAt(existing);
                        else config.AutoUpdateSources.Add(Path.GetFullPath(autoPath));
                        Program.SaveConfig(config);
                        engine.RefreshWatchers();
                        details(existing >= 0 ? "Автообновление отключено для выбранной папки. Резервная копия сохранена." : "Автообновление включено: изменения будут копироваться автоматически. Удаления оригиналов не удаляют копии.");
                        RefreshStats();
                    }
                }
                break;
            case "restore":
                ShowRestore();
                break;
            case "verify":
                await VerifyAsync();
                break;
            case "performanceMode":
                var mode = root.TryGetProperty("mode", out var modeValue) ? modeValue.GetString() : "balanced";
                engine.SetPerformanceMode(mode ?? "balanced");
                try
                {
                    Process.GetCurrentProcess().PriorityClass = engine.PerformanceMode switch
                    {
                        "eco" => ProcessPriorityClass.Idle,
                        "max" => ProcessPriorityClass.Normal,
                        _ => ProcessPriorityClass.BelowNormal
                    };
                }
                catch { }
                details(engine.PerformanceMode switch
                {
                    "eco" => "Режим экономии: одна операция копирования, низкий приоритет и ограниченная скорость записи.",
                    "max" => "Максимальная скорость включена. Нагрузка на диск и процессор может вырасти.",
                    _ => "Баланс включён: умеренная параллельность и сниженный приоритет процесса."
                });
                RefreshStats();
                break;
            case "scan":
                details("Резервное копирование запущено вручную. Автоматического повтора не будет.");
                await engine.RequestScanAsync();
                break;
            case "zip":
                using (var dialog = new SaveFileDialog { Title = "Сохранить резервную копию в ZIP", Filter = "ZIP-архив (*.zip)|*.zip", FileName = "GermanOSBackup-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", AddExtension = true, DefaultExt = "zip" })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        try
                        {
                            details("Создаём ZIP-архив. Это может занять время…");
                            await engine.CreateZipAsync(dialog.FileName);
                            details("ZIP-архив успешно создан.");
                            MessageBox.Show(this, "Архив создан:\n" + dialog.FileName, "ZIP-архив", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось создать ZIP", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                }
                break;
            case "stop":
                engine.StopCurrentScan();
                details("Останавливаем текущую операцию. Уже сохранённые файлы останутся на месте.");
                break;
            case "openDestination":
                OpenDestination();
                break;
            case "chooseDestination":
                using (var dialog = new FolderBrowserDialog { Description = "Выбери отдельную папку для резервных копий", UseDescriptionForTitle = true, ShowNewFolderButton = true })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        Program.ValidateDestination(dialog.SelectedPath);
                        if (MessageBox.Show(this,
                            "Новые файлы будут копироваться в выбранную папку. Уже существующие резервные копии автоматически не перемещаются. Продолжить?",
                            "Изменение места хранения", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) break;
                        config.Destination = Path.GetFullPath(dialog.SelectedPath);
                        Program.SaveConfig(config);
                        engine.RefreshWatchers();
                        details("Место хранения изменено. Новые и обновляемые копии будут записываться по новому пути.");
                        RefreshStats();
                    }
                }
                break;
            case "log":
                OpenFile(Program.LogPath);
                break;
            case "help":
                MessageBox.Show(this,
                    "КАК ПОЛЬЗОВАТЬСЯ GERMAN OS BACKUP\n\n1. «Источники» — выбери папки, которые нужно сохранить.\n2. «Проверить сейчас» — запусти сканирование вручную.\n3. Число ошибок показывает файлы, которые не удалось обработать; открой журнал, чтобы увидеть причину.\n4. «Восстановить» копирует отсутствующие файлы в выбранную папку и не перезаписывает существующие.\n5. Для важных данных держи ещё одну копию на отдельном физическом диске.",
                    "Подсказки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case "exit":
                allowClose = true;
                Close();
                break;
        }
    }


    private async Task CheckUpdatesAsync()
    {
        try
        {
            details("Проверяем последнюю версию на GitHub…");
            if (UpdateClient.DefaultRequestHeaders.UserAgent.Count == 0) UpdateClient.DefaultRequestHeaders.UserAgent.ParseAdd("GermanOSBackup-Updater/1.0");
            using var response = await UpdateClient.GetAsync("https://api.github.com/repos/German-GPD/German-OS-Backup/releases/latest");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latestVersion))
            {
                MessageBox.Show(this, "GitHub не вернул корректный номер версии.", "Проверка обновлений", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var rawCurrentVersion = typeof(Program).Assembly.GetName().Version ?? new Version(1, 3, 1);
            var currentVersion = new Version(rawCurrentVersion.Major, Math.Max(0, rawCurrentVersion.Minor), Math.Max(0, rawCurrentVersion.Build));
            if (latestVersion <= currentVersion)
            {
                details("Установлена последняя версия " + currentVersion.ToString(3) + ".");
                MessageBox.Show(this, "У тебя уже актуальная версия German OS Backup C# Edition " + currentVersion.ToString(3) + ".", "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string? downloadUrl = null;
            string? checksumUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var name) || !asset.TryGetProperty("browser_download_url", out var url)) continue;
                    if (name.GetString() == "GermanOSBackup-CS-Setup-x64.exe") downloadUrl = url.GetString();
                    else if (name.GetString() == "SHA256SUMS.txt") checksumUrl = url.GetString();
                }
            }
            static bool IsOfficialReleaseAsset(string? value) =>
                Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                uri.Host == "github.com" &&
                uri.AbsolutePath.StartsWith("/German-GPD/German-OS-Backup/releases/download/", StringComparison.Ordinal);
            if (!IsOfficialReleaseAsset(downloadUrl) || !IsOfficialReleaseAsset(checksumUrl))
            {
                MessageBox.Show(this, "В официальном релизе не найден установщик или файл контрольных сумм.", "Обновление недоступно", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this,
                $"Доступна German OS Backup C# Edition {latestVersion}. Сейчас установлена версия {currentVersion.ToString(3)}.\n\nСкачать официальный установщик и запустить обновление? Настройки и папки резервных копий хранятся отдельно.",
                "Доступно обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            var installerPath = Path.Combine(Path.GetTempPath(), "GermanOSBackup-CS-Setup-x64.exe");
            details("Скачиваем установщик версии " + latestVersion + "…");
            var bytes = await UpdateClient.GetByteArrayAsync(downloadUrl!);
            var manifest = await UpdateClient.GetStringAsync(checksumUrl!);
            string? expectedHash = null;
            foreach (var line in manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == "GermanOSBackup-CS-Setup-x64.exe")
                {
                    expectedHash = parts[0];
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), SHA256.HashData(bytes)))
            {
                throw new InvalidDataException("SHA-256 установщика не совпадает с контрольной суммой официального релиза. Установка отменена.");
            }
            await File.WriteAllBytesAsync(installerPath, bytes);
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            details("Установщик обновления запущен. Следуй инструкциям на экране.");
            MessageBox.Show(this, "Установщик скачан и запущен. Заверши установку, чтобы обновить программу.", "Обновление запущено", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            details("Не удалось проверить обновления.");
            MessageBox.Show(this, "Не удалось проверить или скачать обновление. Проверь подключение к интернету.\n\n" + ex.Message, "Ошибка обновления", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            try { File.AppendAllText(Program.LogPath, $"[{DateTime.Now:O}] Update check failed: {ex}{Environment.NewLine}"); } catch { }
        }
    }

    private double GetCpuUsagePercent()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
            var idleNow = idle.ToUInt64(); var kernelNow = kernel.ToUInt64(); var userNow = user.ToUInt64();
            if (!hasCpuSample)
            {
                previousIdle = idleNow; previousKernel = kernelNow; previousUser = userNow; hasCpuSample = true;
                return 0;
            }
            var idleDelta = idleNow - previousIdle; var kernelDelta = kernelNow - previousKernel; var userDelta = userNow - previousUser;
            previousIdle = idleNow; previousKernel = kernelNow; previousUser = userNow;
            var total = kernelDelta + userDelta;
            return total == 0 ? 0 : Math.Clamp((double)(total - Math.Min(total, idleDelta)) * 100.0 / total, 0, 100);
        }
        catch { return -1; }
    }

    private (double UsedGb, double TotalGb, double Percent) GetRamUsage()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(status) || status.ullTotalPhys == 0) return (0, 0, 0);
            var used = status.ullTotalPhys - status.ullAvailPhys;
            return (Math.Round(used / 1073741824d, 1), Math.Round(status.ullTotalPhys / 1073741824d, 1), Math.Clamp((int)status.dwMemoryLoad, 0, 100));
        }
        catch { return (0, 0, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public readonly ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    private async Task StartBackupAsync()
    {
        try
        {
            await engine.StartAsync();
            try { Process.GetCurrentProcess().PriorityClass = engine.PerformanceMode switch { "eco" => ProcessPriorityClass.Idle, "max" => ProcessPriorityClass.Normal, _ => ProcessPriorityClass.BelowNormal }; } catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось начать копирование", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ShowSourcesAsync()
    {
        using var form = new SourcesForm(config);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            engine.RefreshWatchers();
            details("Источники сохранены. Нажми «Создать / обновить копию», когда будешь готов.");
            RefreshStats();
        }
    }

    private void ShowRestore()
    {
        using var form = new RestoreForm(config.Destination);
        form.ShowDialog(this);
    }

    private async Task VerifyAsync()
    {
        try
        {
            details("Глубокая проверка читает оригиналы и копии и может занять время.");
            var result = await engine.VerifyBackupAsync();
            MessageBox.Show(this,
                $"Проверено файлов: {result.Checked:N0}\nНет копии: {result.Missing:N0}\nОтличается или ошибка: {result.Different:N0}",
                "Результат проверки SHA-256", MessageBoxButtons.OK,
                result.Missing + result.Different == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshStats();
        }
        catch (OperationCanceledException) { details("Проверка остановлена."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка проверки", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void RefreshStats()
    {
        if (!webReady || web.IsDisposed) return;
        var s = engine.Stats;
        var errors = Interlocked.Read(ref s.Errors);
        var scanning = Volatile.Read(ref s.IsScanning);
        var status = scanning ? "copying" : errors > 0 ? "attention" : s.HasCompletedInitialScan ? "completed" : "waiting";
        var headline = scanning ? (s.HasCompletedInitialScan ? "Обновляем резервную копию" : "Создаём первую копию")
            : errors > 0 ? "Нужна проверка ошибок" : s.HasCompletedInitialScan ? "Последнее копирование завершено" : "Ожидаем ручного запуска";
        // Query the OS once per UI refresh and reuse the same coherent RAM sample.
        var ram = GetRamUsage();
        var cpu = GetCpuUsagePercent();
        var payload = JsonSerializer.Serialize(new
        {
            status,
            headline,
            lastScan = s.LastScanFinished == DateTime.MinValue ? "Ещё не завершено" : s.LastScanFinished.ToString("dd.MM.yyyy, HH:mm:ss"),
            bytes = Interlocked.Read(ref s.BytesCopied),
            files = Interlocked.Read(ref s.FilesCopied),
            seen = Interlocked.Read(ref s.FilesSeen),
            errors,
            speed = Interlocked.Read(ref s.BytesPerSecond),
            scanning,
            destination = config.Destination,
            sourceCount = config.SelectedSources?.Count ?? 0,
            sourcePaths = config.SelectedSources ?? new List<string>(),
            autoUpdateSources = config.AutoUpdateSources ?? new List<string>(),
            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.4.0",
            cpuPercent = cpu,
            ramUsedGb = ram.UsedGb,
            ramTotalGb = ram.TotalGb,
            ramPercent = ram.Percent,
            performanceMode = engine.PerformanceMode,
            accentColor = SystemAccentHex
        });
        _ = web.CoreWebView2.ExecuteScriptAsync($"window.updateStats({payload});");
    }

    private void details(string message)
    {
        if (!webReady || web.IsDisposed) return;
        var json = JsonSerializer.Serialize(message);
        _ = web.CoreWebView2.ExecuteScriptAsync($"window.setNotice({json});");
    }

    private void OpenDestination()
    {
        try
        {
            Directory.CreateDirectory(config.Destination);
            Process.Start(new ProcessStartInfo(config.Destination) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть папку", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "German OS Backup C# Edition log");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        uiTimer.Stop(); uiTimer.Dispose();
        tray.Visible = false; tray.Dispose(); engine.Dispose();
        base.OnFormClosed(e);
    }

    private static readonly string SystemAccentHex = GetSystemAccentHex();

    private static string GetSystemAccentHex()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "AccentColor", null);
            if (value is int color)
                return "#" + (color & 0xFF).ToString("X2") +
                    ((color >> 8) & 0xFF).ToString("X2") +
                    ((color >> 16) & 0xFF).ToString("X2");
        }
        catch { }
        return "#9bf0b0";
    }

    private void ApplyMicaBackdrop()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        try
        {
            var mica = 2;
            _ = DwmSetWindowAttribute(Handle, 38, ref mica, sizeof(int));
            var squareCorners = 1;
            _ = DwmSetWindowAttribute(Handle, 33, ref squareCorners, sizeof(int));
            var darkTitleBar = 1;
            _ = DwmSetWindowAttribute(Handle, 20, ref darkTitleBar, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int valueSize);

    private static readonly string Html = LoadDashboardHtml();

    private static string LoadDashboardHtml()
    {
        using var stream = typeof(DashboardForm).Assembly.GetManifestResourceStream("GermanOSBackup.dashboard.html")
            ?? throw new InvalidOperationException("Embedded dashboard.html resource was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

}
