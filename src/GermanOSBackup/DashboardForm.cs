using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.3.1",
            cpuPercent = GetCpuUsagePercent(),
            ramUsedGb = GetRamUsage().UsedGb,
            ramTotalGb = GetRamUsage().TotalGb,
            ramPercent = GetRamUsage().Percent,
            performanceMode = engine.PerformanceMode
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

    private const string Html = """
<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<style>
:root{color-scheme:dark;--bg:#0d100f;--surface:#141917;--surface2:#1b221e;--line:#29332d;--text:#f0f5f1;--muted:#9aa79e;--green:#9bf0b0;--red:#ff8f91;--amber:#f4ca78;--radius:12px}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",sans-serif}button{font:inherit;cursor:pointer;border:1px solid var(--line);background:#202923;color:var(--text);padding:10px 14px;border-radius:8px;transition:.15s}button:hover{border-color:#638b6c;background:#26332a}button.primary{background:var(--green);color:#102017;border-color:var(--green);font-weight:700}button.primary:hover{background:#b8ffca}.shell{display:grid;grid-template-columns:218px 1fr;min-height:100vh}.side{padding:24px 14px;border-right:1px solid var(--line);background:#101412;display:flex;flex-direction:column;gap:26px}.brand{display:flex;gap:10px;align-items:center;padding:0 8px;font-weight:700;font-size:15px}.mark{display:grid;place-items:center;width:32px;height:32px;border:1px solid #41684b;color:var(--green);border-radius:9px;font-weight:800}.nav{display:grid;gap:5px}.nav button{text-align:left;background:transparent;border-color:transparent;color:var(--muted)}.nav button.active,.nav button:hover{background:#1c2720;color:var(--text);border-color:#2b4131}.side-note{margin-top:auto;padding:12px;color:var(--muted);font-size:12px;background:#151b17;border:1px solid var(--line);border-radius:9px}.main{padding:28px clamp(20px,3vw,42px);max-width:1450px;width:100%;margin:0 auto}.top{display:flex;align-items:center;justify-content:space-between;gap:18px;margin-bottom:28px}.eyebrow{font-size:11px;letter-spacing:1.5px;color:var(--green);font-weight:700}.top h1{font-size:27px;line-height:1.2;margin:5px 0 0;letter-spacing:-.6px}.top-actions{display:flex;gap:8px;flex-wrap:wrap}.notice{display:none;margin-bottom:16px;padding:12px 14px;background:#1a2820;border:1px solid #36553e;border-radius:9px;color:#d9f8df}.hero{padding:20px 22px;background:linear-gradient(115deg,#1b2b21,#17201b 65%,#151b18);border:1px solid #314b38;border-radius:var(--radius);display:flex;align-items:center;justify-content:space-between;gap:24px;margin-bottom:16px}.hero .label{color:var(--green);font-weight:700;font-size:12px}.hero h2{margin:5px 0;font-size:23px;letter-spacing:-.4px}.hero p{margin:0;color:#bac9be;max-width:690px}.status-pill{white-space:nowrap;display:flex;align-items:center;gap:8px;padding:9px 12px;border:1px solid #466a4d;border-radius:999px;background:#18251c;font-size:12px;font-weight:700;color:var(--green)}.dot{width:7px;height:7px;border-radius:50%;background:currentColor}.status-pill.attention{color:var(--amber);border-color:#69562e;background:#292316}.status-pill.copying{color:#a9c7ff;border-color:#354f77;background:#172131}.grid{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:12px;margin-bottom:16px}.card{background:var(--surface);border:1px solid var(--line);border-radius:var(--radius);padding:17px;min-width:0}.card-label{display:flex;justify-content:space-between;gap:8px;color:var(--muted);font-size:12px}.value{font-size:27px;font-weight:700;letter-spacing:-.7px;margin:8px 0 2px}.hint{font-size:11px;color:var(--muted)}.error-card .value{color:var(--amber)}.section-grid{display:grid;grid-template-columns:minmax(0,1.45fr) minmax(270px,1fr);gap:12px;margin-bottom:16px}.section-title{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:14px}.section-title h3{font-size:15px;margin:0}.section-title span{font-size:11px;color:var(--muted)}.chart-wrap{height:175px;position:relative}.chart-wrap canvas{width:100%;height:100%}.chart-legend{display:flex;justify-content:space-between;color:var(--muted);font-size:11px;margin-top:5px}.empty-chart{position:absolute;inset:0;display:grid;place-items:center;color:var(--muted);font-size:12px;pointer-events:none}.path{display:flex;align-items:center;gap:10px;padding:12px;background:#101512;border:1px solid var(--line);border-radius:9px;margin-bottom:12px;min-width:0}.drive-icon{width:36px;height:36px;border-radius:9px;background:#203126;color:var(--green);display:grid;place-items:center;font-weight:800;flex-shrink:0}.path-text{min-width:0;flex:1}.path-text b{display:block;font-size:12px;margin-bottom:2px}.path-text span{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:var(--muted);font-size:12px}.source-row{display:flex;justify-content:space-between;gap:10px;color:var(--muted);font-size:12px;margin:10px 0 14px}.actions{display:grid;grid-template-columns:1fr 1fr;gap:8px}.actions button{font-size:12px;padding:9px 8px}.health{display:grid;gap:13px}.health-row{display:flex;align-items:center;gap:10px}.health-icon{width:30px;height:30px;border-radius:8px;display:grid;place-items:center;background:#202923;color:var(--green);flex-shrink:0}.health-row div:nth-child(2){flex:1}.health-row b{font-size:12px;display:block}.health-row small{color:var(--muted);font-size:11px}.health-row strong{font-size:12px}.progress-track{height:6px;background:#29322c;border-radius:8px;overflow:hidden;margin-top:14px}.progress-bar{height:100%;width:0;background:var(--green);transition:width .3s}.progress-bar.running{width:35%;background:linear-gradient(90deg,#557b60,#b9ffca,#557b60);background-size:200% 100%;animation:indeterminate 1.3s linear infinite}@keyframes indeterminate{from{background-position:100% 0}to{background-position:-100% 0}}.footer{display:flex;justify-content:space-between;gap:12px;color:#758279;font-size:11px;padding:10px 2px 0}.tip{display:inline-grid;place-items:center;width:17px;height:17px;border:1px solid #47544a;border-radius:50%;font-size:10px;color:var(--muted);cursor:help;position:relative}.tip:hover:after{content:attr(data-tip);position:absolute;z-index:5;left:20px;top:-8px;width:220px;padding:9px;background:#252f28;border:1px solid #46574a;border-radius:7px;color:var(--text);font-size:11px;font-weight:400;letter-spacing:0;box-shadow:0 8px 25px #0008}.muted{color:var(--muted)}.source-paths{display:grid;gap:5px;max-height:100px;overflow:auto;margin:8px 0 14px}.source-paths div{font-size:11px;color:var(--muted);overflow-wrap:anywhere;padding:6px 8px;background:#101512;border-left:2px solid #435c47}.source-path-item{display:flex;align-items:center;gap:8px;justify-content:space-between}.source-path-item span{min-width:0;flex:1}.source-path-item button.watch-toggle{padding:4px 7px;font-size:10px;white-space:nowrap}.source-path-item button.watch-toggle.enabled{border-color:#4d7c59;color:#b9ffca;background:#1b2b20}.resource-bars{display:grid;gap:8px;margin-top:8px}.resource-line{display:flex;justify-content:space-between;gap:8px;font-size:10px;color:var(--muted)}.resource-track{height:4px;background:#29322c;margin-top:4px}.resource-fill{height:100%;width:0;background:var(--green)}@media(max-width:1000px){.shell{grid-template-columns:175px 1fr}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}.section-grid{grid-template-columns:1fr}}@media(max-width:700px){.shell{display:block}.side{display:none}.main{padding:20px 14px}.top{align-items:flex-start;flex-direction:column}.hero{align-items:flex-start;flex-direction:column}.grid{gap:8px}.value{font-size:23px}.footer{flex-direction:column}}

/* UI refinement: sharper geometry, clearer hierarchy, denser dashboard */
:root{--radius:5px;--surface:#131715;--surface2:#191f1b;--line:#28312b;--green:#a0f2ae}
button{border-radius:4px;padding:9px 12px}button:focus-visible{outline:2px solid var(--green);outline-offset:2px}
.shell{grid-template-columns:204px minmax(0,1fr)}.side{position:sticky;top:0;height:100vh;padding:22px 12px;gap:22px}.mark{border-radius:4px}.nav{gap:3px}.nav button{position:relative;border-radius:4px;padding:11px 10px}.nav button.active{background:#1b2820;border-color:#293d2f;color:#f1f6f2}.nav button.active:before{content:"";position:absolute;left:-1px;top:7px;bottom:7px;width:2px;background:var(--green)}
.side-note,.card,.hero,.notice,.path,.status-pill,.drive-icon,.health-icon{border-radius:5px}.main{padding:25px clamp(18px,2.5vw,36px)}.top{margin-bottom:20px}.top h1{font-size:25px}.hero{background:#151d18;border-color:#304337;padding:19px 20px}.hero h2{font-size:22px}.grid{gap:10px;margin-bottom:12px}.card{padding:15px}.value{font-size:25px}.section-grid{gap:10px;margin-bottom:12px}.chart-wrap{height:155px}.section-title{margin-bottom:12px}.actions button{min-height:38px}.progress-track{border-radius:2px}.progress-bar{border-radius:2px}.tip{border-radius:3px}.footer{padding-top:14px}
#modeEco,#modeBalanced,#modeMax{min-height:42px}#modeEco:before{content:"◌  ";color:#b7c4ba}#modeBalanced:before{content:"◒  ";color:#b7c4ba}#modeMax:before{content:"↗  ";color:#b7c4ba}#modeEco.primary:before,#modeBalanced.primary:before,#modeMax.primary:before{color:#102017}
button:disabled{opacity:.42;cursor:not-allowed;filter:saturate(.4)}.notice{box-shadow:inset 3px 0 var(--green)}
@media(max-width:1000px){.shell{grid-template-columns:170px minmax(0,1fr)}.side{padding:18px 9px}.main{padding:20px 16px}}
@media(max-width:700px){.side{position:static;height:auto;display:flex;flex-direction:row;align-items:center;overflow-x:auto;padding:10px;border-right:0;border-bottom:1px solid var(--line)}.brand{white-space:nowrap}.nav{display:flex;gap:4px}.nav button{white-space:nowrap}.side-note{display:none}.main{padding:16px 12px}.top h1{font-size:22px}.hero h2{font-size:20px}.status-pill{white-space:normal}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}.section-grid{grid-template-columns:minmax(0,1fr)}.actions{grid-template-columns:repeat(2,minmax(0,1fr))}}
</style></head><body><div class="shell">
<aside class="side"><div class="brand"><div class="mark">G_</div><span>German OS Backup C# Edition</span></div><nav class="nav"><button class="active" onclick="tab('overview')">▦　 Обзор</button><button onclick="act('sources')">▣　 Источники</button><button onclick="act('restore')">↶　 Восстановление</button><button onclick="act('verify')">✓　 Проверка целостности</button><button onclick="act('log')">≡　 Журнал событий</button></nav><div class="side-note"><b>Подсказка</b><p>Копия должна находиться на отдельном физическом диске, если нужно защититься от поломки накопителя.</p><button onclick="act('help')" style="width:100%">Все подсказки</button></div></aside>
<main class="main"><header class="top"><div><div class="eyebrow">ЛОКАЛЬНАЯ ЗАЩИТА ФАЙЛОВ</div><h1>Обзор резервного копирования</h1></div><div class="top-actions"><button onclick="act('help')">? Помощь</button><button onclick="act('checkUpdates')">↻ Проверить обновления</button><button onclick="act('zip')">Создать ZIP</button><button onclick="act('scan')">Создать / обновить копию</button><button class="primary" onclick="act('sources')">Выбрать папки</button></div></header>
<div class="notice" id="notice"></div>
<section class="hero"><div><div class="label">СОСТОЯНИЕ РЕЗЕРВНОЙ КОПИИ</div><h2 id="headline">Подключаемся…</h2><p id="detail">Сканирование запускается вручную. Здесь видно, выполняется ли копирование и чем завершилась последняя проверка.</p></div><div class="status-pill" id="pill"><span class="dot"></span><span id="pillText">ЗАПУСК</span></div></section>
<section class="card" style="margin-bottom:16px"><div class="section-title"><h3>Режим нагрузки</h3><span id="modeDescription">Баланс между скоростью и отзывчивостью ПК</span></div><div class="actions" style="grid-template-columns:repeat(3,minmax(0,1fr))"><button id="modeEco" onclick="setMode('eco')">Экономия ресурсов</button><button id="modeBalanced" onclick="setMode('balanced')">Баланс</button><button id="modeMax" onclick="setMode('max')">Максимальная скорость</button></div><p class="hint" style="margin:10px 0 0">Режим влияет на параллельность копирования и приоритет приложения. Изменение применяется сразу и сохраняется после перезапуска.</p></section>
<section class="grid"><article class="card"><div class="card-label">Скопировано <span class="tip" data-tip="Общий объём данных, записанных резервным копированием с момента запуска приложения.">i</span></div><div class="value" id="bytes">0 Б</div><div class="hint">Объём записанных файлов</div></article><article class="card"><div class="card-label">Файлы сохранены <span class="tip" data-tip="Количество файлов, которые программа успешно скопировала.">i</span></div><div class="value" id="files">0</div><div class="hint">За время работы приложения</div></article><article class="card error-card"><div class="card-label">Ошибки <span class="tip" data-tip="Файлы или папки, которые не удалось обработать. Открой журнал, чтобы узнать причину.">i</span></div><div class="value" id="errors">0</div><div class="hint" id="errorHint">Нет зарегистрированных ошибок</div></article><article class="card"><div class="card-label">Текущая скорость <span class="tip" data-tip="При отсутствии изменений скорость равна нулю — это нормально.">i</span></div><div class="value" id="speed">0 Б/с</div><div class="hint" id="scanHint">Ожидание</div></article><article class="card"><div class="card-label">Нагрузка ПК</div><div class="value" id="cpuUsage">—</div><div class="hint">Процессор</div><div class="resource-bars"><div><div class="resource-line"><span>Оперативная память</span><b id="ramUsage">—</b></div><div class="resource-track"><div class="resource-fill" id="ramProgress"></div></div></div><div><div class="resource-line"><span>CPU</span><b id="cpuPercentLabel">—</b></div><div class="resource-track"><div class="resource-fill" id="cpuProgress"></div></div></div></div></article></section>
<section class="section-grid"><article class="card"><div class="section-title"><h3>Активность копирования</h3><span id="chartLabel">Последние обновления</span></div><div class="chart-wrap"><canvas id="chart"></canvas><div class="empty-chart" id="chartEmpty">График скорости появится во время копирования</div></div><div class="chart-legend"><span>Ранние замеры</span><span>Сейчас</span></div></article>
<article class="card"><div class="section-title"><h3>Что защищено</h3><span class="tip" data-tip="Список источников можно изменить в любой момент.">i</span></div><div class="path"><div class="drive-icon">D:</div><div class="path-text"><b>Папка резервной копии</b><span id="destination">Загрузка пути…</span></div></div><div class="source-row"><span>Выбрано источников</span><b id="sourceCount">—</b></div><div class="source-paths" id="sourcePaths"><div>Загрузка списка папок…</div></div><div class="actions"><button onclick="act('chooseDestination')">Изменить место</button><button onclick="act('openDestination')">Открыть папку</button><button onclick="act('sources')">Выбрать папки</button><button onclick="act('restore')">Восстановить</button></div></article></section>
<section class="section-grid"><article class="card"><div class="section-title"><h3>Последняя операция</h3><span class="tip" data-tip="Проверка SHA-256 запускается вручную и сравнивает оригиналы с резервной копией.">i</span></div><div class="health"><div class="health-row"><div class="health-icon">✓</div><div><b>Время завершения</b><small>Последняя операция</small></div><strong id="lastScan">Ещё не завершено</strong></div><div class="health-row"><div class="health-icon">↻</div><div><b>Автоматическое копирование</b><small>Запуск только по команде пользователя</small></div><strong id="watchStatus">Выключено</strong></div><div class="health-row"><div class="health-icon">!</div><div><b>Ошибки и пропуски</b><small>Открой журнал для подробностей</small></div><strong id="healthErrors">0</strong></div></div><div class="progress-track"><div class="progress-bar" id="progress"></div></div><div class="hint" id="progressText" style="margin-top:8px">Ожидает ручного запуска</div></article>
<article class="card"><div class="section-title"><h3>Быстрые действия</h3><span>Управление</span></div><div class="actions"><button onclick="act('scan')">Запустить копирование</button><button onclick="act('stop')">Остановить</button><button onclick="act('verify')">Проверить SHA-256</button><button onclick="act('log')">Открыть журнал</button><button onclick="act('restore')">Восстановить файлы</button><button onclick="act('exit')">Полностью выйти</button></div><p class="hint" style="margin-top:12px">Закрытие окна прячет приложение в трей. Чтобы остановить работу, выбери «Полностью выйти».</p></article></section>
<footer class="footer"><span>German OS Backup C# Edition · <span id="appVersion">v1.3.1</span> · Локально, без телеметрии</span><span>Обновления проверяются только по запросу</span></footer></main></div>
<script>
const samples=[];function act(action,extra={}){window.chrome.webview.postMessage({action,...extra})}function tab(name){document.querySelectorAll('.nav button').forEach(x=>x.classList.remove('active'));event.currentTarget.classList.add('active');if(name==='overview')return}
function setMode(mode){window.chrome.webview.postMessage({action:"performanceMode",mode})}
function fmtBytes(n){if(!n||n<0)return'0 Б';const u=['Б','КБ','МБ','ГБ','ТБ'];let i=0,v=n;while(v>=1024&&i<u.length-1){v/=1024;i++}return v.toLocaleString('ru-RU',{maximumFractionDigits:1})+' '+u[i]}
function fmtSpeed(n){return fmtBytes(n)+'/с'}function fmtNum(n){return Number(n||0).toLocaleString('ru-RU')}
function updateStats(s){document.querySelectorAll('button[onclick="act(\'stop\')"]').forEach(b=>b.disabled=!s.scanning);document.querySelectorAll('button[onclick="act(\'scan\')"]').forEach(b=>b.disabled=!!s.scanning);const mode=s.performanceMode||'balanced';document.querySelectorAll('[id^="mode"]').forEach(x=>x.classList.remove('primary'));const selected=document.getElementById(mode==='eco'?'modeEco':mode==='max'?'modeMax':'modeBalanced');if(selected)selected.classList.add('primary');document.getElementById('modeDescription').textContent=mode==='eco'?'Минимальная нагрузка, копирование медленнее':mode==='max'?'Приоритет скорости, возможна высокая нагрузка':'Умеренная нагрузка и хорошая отзывчивость ПК';document.getElementById('headline').textContent=s.headline;document.getElementById('bytes').textContent=fmtBytes(s.bytes);document.getElementById('files').textContent=fmtNum(s.files);document.getElementById('errors').textContent=fmtNum(s.errors);document.getElementById('speed').textContent=fmtSpeed(s.speed);document.getElementById('lastScan').textContent=s.lastScan;document.getElementById('destination').textContent=s.destination;document.querySelector('.drive-icon').textContent=(s.destination.match(/^[A-Za-z]:/)||['DISK'])[0];document.getElementById('sourceCount').textContent=fmtNum(s.sourceCount);const sourcePaths=document.getElementById('sourcePaths');sourcePaths.replaceChildren();(s.sourcePaths||[]).forEach(path=>{const row=document.createElement('div');row.className='source-path-item';const label=document.createElement('span');label.textContent=path;const enabled=(s.autoUpdateSources||[]).some(p=>p.toLowerCase()===path.toLowerCase());const button=document.createElement('button');button.className='watch-toggle'+(enabled?' enabled':'');button.textContent=enabled?'Авто: вкл.':'Авто: выкл.';button.title='Переключить динамическое копирование для этой папки';button.addEventListener('click',()=>act('toggleAuto',{path}));row.append(label,button);sourcePaths.appendChild(row)});if(!(s.sourcePaths||[]).length)sourcePaths.textContent='Источники не выбраны';document.getElementById('appVersion').textContent='v'+(s.version||'1.3.1');const cpu=Number(s.cpuPercent);document.getElementById('cpuUsage').textContent=cpu<0?'Н/Д':Math.round(cpu)+'%';document.getElementById('cpuPercentLabel').textContent=cpu<0?'Н/Д':Math.round(cpu)+'%';document.getElementById('cpuProgress').style.width=(cpu<0?0:Math.min(100,cpu))+'%';document.getElementById('ramUsage').textContent=fmtBytes((s.ramUsedGb||0)*1073741824)+' / '+fmtBytes((s.ramTotalGb||0)*1073741824);document.getElementById('ramProgress').style.width=Math.min(100,Number(s.ramPercent)||0)+'%';document.getElementById('healthErrors').textContent=fmtNum(s.errors);document.getElementById('errorHint').textContent=s.errors?'Открой журнал и проверь причины':'Нет зарегистрированных ошибок';document.getElementById('scanHint').textContent=s.scanning?'Идёт сканирование':'Сейчас не копирует данные';const watched=(s.autoUpdateSources||[]).length;document.getElementById('watchStatus').textContent=watched?watched+' папок':'Выключено';document.getElementById('progressText').textContent=s.scanning?'Обработано файлов: '+fmtNum(s.seen):'Ручное копирование или динамические обновления выбранных папок';document.getElementById('progress').classList.toggle('running',!!s.scanning);document.getElementById('progress').style.width=s.scanning?'35%':'0%';const pill=document.getElementById('pill');pill.className='status-pill '+(s.status==='attention'?'attention':s.status==='copying'?'copying':'');document.getElementById('pillText').textContent=s.status==='attention'?'ТРЕБУЕТСЯ ВНИМАНИЕ':s.status==='copying'?'КОПИРОВАНИЕ':s.status==='completed'?'ПОСЛЕДНЯЯ КОПИЯ ГОТОВА':'ОЖИДАНИЕ';document.getElementById('progressText').title=s.scanning?'Счётчик показывает уже обнаруженные файлы; точный процент заранее неизвестен.':'Нажми «Создать / обновить копию», чтобы начать копирование вручную.';document.getElementById('detail').textContent=s.errors?'Обнаружены ошибки или пропущенные элементы. Открой журнал событий, чтобы узнать, что именно не удалось скопировать.':'Последняя операция: '+s.lastScan+'. Динамическое обновление включается отдельно у каждой папки.';samples.push(Math.max(0,Number(s.speed)||0));if(samples.length>32)samples.shift();drawChart(s.scanning)}
function drawChart(scanning){const canvas=document.getElementById('chart'),ctx=canvas.getContext('2d'),r=canvas.getBoundingClientRect(),dpr=window.devicePixelRatio||1;canvas.width=Math.max(1,r.width*dpr);canvas.height=Math.max(1,r.height*dpr);ctx.scale(dpr,dpr);const w=r.width,h=r.height,p=12,vals=samples.length?samples:[0],max=Math.max(1,...vals);ctx.strokeStyle='#2b3730';ctx.lineWidth=1;for(let i=1;i<4;i++){let y=p+(h-p*2)*i/4;ctx.beginPath();ctx.moveTo(p,y);ctx.lineTo(w-p,y);ctx.stroke()}ctx.beginPath();vals.forEach((v,i)=>{const x=p+(w-p*2)*(i/Math.max(1,vals.length-1)),y=h-p-(h-p*2)*v/max;if(i===0)ctx.moveTo(x,y);else ctx.lineTo(x,y)});ctx.lineWidth=2;ctx.strokeStyle='#9bf0b0';ctx.stroke();const last=vals[vals.length-1],x=p+(w-p*2)*(vals.length-1)/Math.max(1,vals.length-1),y=h-p-(h-p*2)*last/max;ctx.lineTo(x,h-p);ctx.lineTo(p,h-p);ctx.closePath();ctx.fillStyle='rgba(155,240,176,.08)';ctx.fill();document.getElementById('chartEmpty').style.display=scanning?'none':'grid';document.getElementById('chartLabel').textContent=scanning?'Скорость в реальном времени':'Последние замеры скорости'}
function setNotice(text){const n=document.getElementById('notice');n.textContent=text;n.style.display='block';setTimeout(()=>n.style.display='none',6000)}window.addEventListener('resize',()=>drawChart(false));
</script></body></html>
""";
}
