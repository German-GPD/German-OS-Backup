using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GermanOSBackup;

internal sealed class BackupStats
{
    public long BytesCopied, FilesCopied, FilesSeen, Errors, BytesPerSecond;
    public bool IsScanning, HasCompletedInitialScan;
    public DateTime LastScanFinished = DateTime.MinValue;
}

internal sealed class BackupEngine : IDisposable
{
    private readonly AppConfig config; private readonly string logPath;
    private readonly SemaphoreSlim scanLock = new(1, 1);
    private readonly SemaphoreSlim ecoSlots = new(1, 1);
    private readonly SemaphoreSlim balancedSlots = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 3));
    private readonly SemaphoreSlim maxSlots = new(Math.Clamp(Environment.ProcessorCount, 2, 6));
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly CancellationTokenSource stop = new();
    private readonly object queueLock = new();
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BackupStats stats = new();
    private readonly Stopwatch speedClock = Stopwatch.StartNew();
    private long bytesInWindow; private int disposed; private int started;
    private readonly object scanCancellationLock = new();
    private CancellationTokenSource? activeScan;

    public BackupEngine(AppConfig config, string logPath) { this.config = config; this.logPath = logPath; }
    public BackupStats Stats => stats;
    public string PerformanceMode => NormalizeMode(config.PerformanceMode);
    public static string NormalizeMode(string? mode) => mode is "eco" or "max" ? mode : "balanced";

    public void SetPerformanceMode(string mode)
    {
        config.PerformanceMode = NormalizeMode(mode);
        Program.SaveConfig(config);
        Log("Режим производительности изменён: " + config.PerformanceMode + ".");
    }
    public static IEnumerable<(string Name, string Path)> Sources()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var savedGames = Path.Combine(home, "Saved Games");
        var candidates = new[]
        {
            ("Рабочий стол", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Документы", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Изображения", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Видео", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ("Загрузки", Path.Combine(home, "Downloads")),
            ("Сохранённые игры", savedGames),
            ("AppData-Roaming", appData),
            ("AppData-LocalLow", Path.Combine(home, "AppData", "LocalLow")),
            ("AppData-Local", localAppData),
            ("ProgramData", commonData)
        };
        foreach (var item in candidates)
            if (!string.IsNullOrWhiteSpace(item.Item2) && Directory.Exists(item.Item2))
                yield return (item.Item1, Path.GetFullPath(item.Item2));

        // Xbox libraries may live on D:, E:, F:, etc., not only on the Windows drive.
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? xboxPath = null;
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    var candidate = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                    if (Directory.Exists(candidate)) xboxPath = Path.GetFullPath(candidate);
                }
            }
            catch { }
            if (xboxPath is not null)
                yield return ($"XboxGames-{drive.Name.TrimEnd(Path.DirectorySeparatorChar, ':')}", xboxPath);
        }
    }

    public static IEnumerable<string> DefaultSources()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(home, "AppData", "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(home, "Saved Games")
        };
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            RefreshWatchers();
            _ = Task.Run(ProcessQueueAsync);
        }
        return Task.CompletedTask;
    }

    public void RefreshWatchers()
    {
        lock (watchers)
        {
            foreach (var watcher in watchers) watcher.Dispose();
            watchers.Clear();

            foreach (var source in config.AutoUpdateSources ?? new List<string>())
            {
                if (!config.SelectedSources.Any(s => string.Equals(Path.GetFullPath(s), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!Directory.Exists(source)) { Log($"Автообновление пропущено: папка недоступна {source}"); continue; }
                try
                {
                    var watcher = new FileSystemWatcher(source)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 32 * 1024,
                        EnableRaisingEvents = false
                    };
                    watcher.Created += (_, e) => Queue(e.FullPath);
                    watcher.Changed += (_, e) => Queue(e.FullPath);
                    watcher.Renamed += (_, e) => Queue(e.FullPath);
                    watcher.Error += (_, e) =>
                    {
                        Log($"События файловой системы потеряны для {source}: {e.GetException().Message}. Запущена полная сверка.");
                        _ = Task.Run(RequestScanAsync);
                    };
                    watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                    Log("Динамическое автообновление включено: " + source);
                }
                catch (Exception ex) { Log($"Не удалось включить автообновление для {source}: {ex.Message}"); }
            }
        }
    }

    public void StopCurrentScan()
    {
        lock (scanCancellationLock) activeScan?.Cancel();
    }

    public async Task<(long Checked, long Missing, long Different)> VerifyBackupAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        var ct = linked.Token;
        await scanLock.WaitAsync(ct).ConfigureAwait(false);
        lock (scanCancellationLock) activeScan = linked;
        long checkedFiles = 0, missing = 0, different = 0;
        try
        {
            stats.IsScanning = true;
            foreach (var (_, source, targetRoot) in ConfiguredSources())
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(source)) continue;
                var stack = new Stack<(string Source, string Target)>();
                stack.Push((source, targetRoot));
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (from, to) = stack.Pop();
                    string[] dirs, files;
                    try { dirs = Directory.GetDirectories(from); files = Directory.GetFiles(from); }
                    catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Глубокая проверка: папка пропущена {from}: {ex.Message}"); continue; }
                    foreach (var dir in dirs)
                    {
                        try
                        {
                            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                                stack.Push((dir, Path.Combine(to, Path.GetFileName(dir))));
                        }
                        catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Глубокая проверка папки {dir}: {ex.Message}"); }
                    }
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            {
                                Log($"Глубокая проверка: пропущена символическая ссылка {file}");
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            different++;
                            Interlocked.Increment(ref stats.Errors);
                            Log($"Глубокая проверка: не удалось проверить атрибуты {file}: {ex.Message}");
                            continue;
                        }
                        var target = Path.Combine(to, Path.GetFileName(file));
                        try
                        {
                            if (!File.Exists(target)) { missing++; Log($"Глубокая проверка: отсутствует копия {target}"); continue; }
                            await using var original = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await using var backup = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            var originalHash = await SHA256.HashDataAsync(original, ct).ConfigureAwait(false);
                            var backupHash = await SHA256.HashDataAsync(backup, ct).ConfigureAwait(false);
                            if (!CryptographicOperations.FixedTimeEquals(originalHash, backupHash))
                            {
                                different++;
                                Log($"Глубокая проверка: содержимое отличается {target}");
                            }
                            checkedFiles++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { different++; Interlocked.Increment(ref stats.Errors); Log($"Глубокая проверка файла {file}: {ex.Message}"); }
                    }
                }
            }
            Log($"Глубокая проверка завершена. Проверено: {checkedFiles:N0}; отсутствует: {missing:N0}; отличаются/ошибки: {different:N0}.");
            return (checkedFiles, missing, different);
        }
        finally
        {
            stats.IsScanning = false;
            stats.LastScanFinished = DateTime.Now;
            lock (scanCancellationLock) { if (ReferenceEquals(activeScan, linked)) activeScan = null; }
            scanLock.Release();
        }
    }

    public async Task RequestScanAsync()
    {
        // Directory enumeration can block on slow, sleeping or unhealthy drives.
        // Keep every filesystem walk off the WinForms UI thread.
        if (Volatile.Read(ref disposed) != 0) return;
        try { await scanLock.WaitAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (Volatile.Read(ref disposed) != 0) { scanLock.Release(); return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        lock (scanCancellationLock) activeScan = operation;

        await Task.Run(async () =>
        {
            stats.IsScanning = true;
            var startedAt = DateTime.UtcNow;
            try
            {
                foreach (var (name, source, targetRoot) in ConfiguredSources())
                {
                    operation.Token.ThrowIfCancellationRequested();
                    if (Directory.Exists(source)) await CopyTreeAsync(name, source, targetRoot, operation.Token).ConfigureAwait(false);
                }

                if (!stop.IsCancellationRequested) stats.HasCompletedInitialScan = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref stats.Errors);
                Log($"Ошибка сканирования: {ex}");
            }
            finally
            {
                stats.IsScanning = false;
                stats.LastScanFinished = DateTime.Now;
                lock (scanCancellationLock) { if (ReferenceEquals(activeScan, operation)) activeScan = null; }
                scanLock.Release();
                Log($"Сканирование завершено за {(DateTime.UtcNow - startedAt).TotalMinutes:0.0} мин. Всего скопировано файлов: {Interlocked.Read(ref stats.FilesCopied):N0}.");
            }
        }).ConfigureAwait(false);
    }

    private async Task CopyTreeAsync(string name, string source, string targetRoot, CancellationToken ct)
    {
        var rootTarget = targetRoot; Directory.CreateDirectory(rootTarget);
        var stack = new Stack<(string Source, string Target)>(); stack.Push((source, rootTarget));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); var (from, to) = stack.Pop(); string[] dirs, files;
            try { dirs = Directory.GetDirectories(from); files = Directory.GetFiles(from); }
            catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Папка пропущена {from}: {ex.Message}"); continue; }
            foreach (var dir in dirs)
            {
                try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) stack.Push((dir, Path.Combine(to, Path.GetFileName(dir)))); }
                catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Не удалось прочитать {dir}: {ex.Message}"); }
            }
            var tasks = new List<Task>(Math.Min(files.Length, 128));
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        Log($"Пропущена символическая ссылка: {file}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref stats.Errors);
                    Log($"Не удалось проверить атрибуты файла {file}: {ex.Message}");
                    continue;
                }
                Interlocked.Increment(ref stats.FilesSeen);
                tasks.Add(CopyFileSafelyAsync(file, Path.Combine(to, Path.GetFileName(file)), ct));
                if (tasks.Count >= 64) { await Task.WhenAll(tasks); tasks.Clear(); }
            }
            if (tasks.Count > 0) await Task.WhenAll(tasks);
        }
    }

    private async Task CopyFileSafelyAsync(string source, string target, CancellationToken ct)
    {
        try { await CopyFileAsync(source, target, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Ошибка копирования {source}: {ex.Message}"); }
    }

    private async Task ProcessQueueAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(900, stop.Token); string[] batch;
                lock (queueLock) { batch = pending.Take(96).ToArray(); foreach (var item in batch) pending.Remove(item); }
                foreach (var path in batch)
                {
                    if (stop.IsCancellationRequested) break; var target = MapPath(path); if (target is null) continue;
                    try
                    {
                        if (Directory.Exists(path)) Directory.CreateDirectory(target);
                        else if (File.Exists(path))
                        {
                            // Wait for games and applications to finish writing a save before copying it.
                            var before = new FileInfo(path);
                            var length = before.Length;
                            var modified = before.LastWriteTimeUtc;
                            await Task.Delay(450, stop.Token);
                            if (!File.Exists(path)) continue; // Keep the old backup if the source was deleted.
                            var after = new FileInfo(path);
                            if (after.Length != length || after.LastWriteTimeUtc != modified)
                            {
                                Queue(path);
                                continue;
                            }
                            await CopyFileAsync(path, target, stop.Token);
                        }
                        // Deletions are intentionally ignored: backup copies are retained.
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Ошибка автообновления {path}: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Queue(string path) { if (Volatile.Read(ref disposed) != 0) return; lock (queueLock) { if (pending.Count < 20000) pending.Add(path); } }

    private string? MapPath(string path)
    {
        string full; try { full = Path.GetFullPath(path); } catch { return null; }
        foreach (var (name, source, targetRoot) in ConfiguredSources())
        {
            var normalized = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, normalized, StringComparison.OrdinalIgnoreCase)) return targetRoot;
            var prefix = normalized + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(source, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
            return Path.Combine(targetRoot, relative);
        }
        return null;
    }

    private IEnumerable<(string Name, string Path, string TargetRoot)> ConfiguredSources()
    {
        // Only use defaults for a genuinely fresh configuration. If saved paths
        // are missing, do not silently back up unrelated folders instead.
        var configured = config.SelectedSources;
        if (configured is not { Count: > 0 })
        {
            foreach (var path in DefaultSources())
            {
                var standard = Sources().FirstOrDefault(x =>
                    string.Equals(path, x.Path, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(x.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                var name = string.IsNullOrEmpty(standard.Name) ? "Custom" : standard.Name;
                yield return (name, path, Path.Combine(config.Destination, name));
            }
            yield break;
        }

        var selected = configured.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0) yield break;

        var roots = selected.Where(path => !selected.Any(other =>
            !string.Equals(path, other, StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

        foreach (var path in roots)
        {
            var standard = Sources().FirstOrDefault(x =>
                string.Equals(path, x.Path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(x.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(standard.Path))
            {
                var relative = Path.GetRelativePath(standard.Path, path);
                var target = relative == "." ? Path.Combine(config.Destination, standard.Name) : Path.Combine(config.Destination, standard.Name, relative);
                yield return (standard.Name, path, target);
            }
            else
            {
                var drive = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, ':') ?? "Root";
                var relative = Path.GetRelativePath(Path.GetPathRoot(path) ?? path, path);
                yield return ("Custom", path, Path.Combine(config.Destination, "Custom", drive, relative));
            }
        }
    }

    private async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        var mode = PerformanceMode;
        var slots = mode == "eco" ? ecoSlots : mode == "max" ? maxSlots : balancedSlots;
        await slots.WaitAsync(ct);
        string? temp = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var src = new FileInfo(source); if (!src.Exists) return;
                    if (File.Exists(target)) { var dst = new FileInfo(target); if (dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc) return; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (mode != "eco" && src.Length <= 64L * 1024 * 1024 && await Task.Run(() => RustCopyEngine.TryCopy(source, target), ct).ConfigureAwait(false))
                    {
                        File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                        Interlocked.Increment(ref stats.FilesCopied);
                        Interlocked.Add(ref stats.BytesCopied, src.Length);
                        Interlocked.Add(ref bytesInWindow, src.Length);
                        UpdateSpeed();
                        return;
                    }
                    temp = target + ".gob-tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
                    long written = 0;
                    var bufferSize = mode == "eco" ? 128 * 1024 : mode == "balanced" ? 512 * 1024 : 1024 * 1024;
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = new byte[bufferSize]; int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            written += read;
                            Interlocked.Add(ref bytesInWindow, read);
                            UpdateSpeed();
                            if (mode == "eco") await Task.Delay(8, ct);
                        }
                        await output.FlushAsync(ct);
                    }
                    File.Move(temp, target, true); temp = null; File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                    Interlocked.Increment(ref stats.FilesCopied); Interlocked.Add(ref stats.BytesCopied, written); return;
                }
                catch (IOException) when (attempt < 2) { if (temp is not null) { try { File.Delete(temp); } catch { } temp = null; } await Task.Delay(300 * (attempt + 1), ct); }
            }
        }
        finally { if (temp is not null) { try { File.Delete(temp); } catch { } } slots.Release(); }
    }

    public async Task CreateZipAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(config.Destination);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("Папка резервной копии ещё не существует. Сначала запусти копирование.");
        zipPath = Path.GetFullPath(zipPath);
        if (zipPath.StartsWith(sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ZIP-архив нельзя сохранять внутрь папки, которую архивируем. Выбери другое место.");
        var level = PerformanceMode switch { "eco" => CompressionLevel.SmallestSize, "max" => CompressionLevel.Fastest, _ => CompressionLevel.Optimal };
        var tempZip = zipPath + ".gob-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create);
                var stack = new Stack<string>(); stack.Push(sourceRoot);
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = stack.Pop();
                    foreach (var dir in Directory.EnumerateDirectories(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) stack.Push(dir);
                    }
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(file);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                            file.Contains(".gob-tmp-", StringComparison.OrdinalIgnoreCase) ||
                            file.Contains(".gob-rust-", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".gob-restore-tmp", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var relative = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                        archive.CreateEntryFromFile(file, Path.GetFileName(sourceRoot) + "/" + relative, level);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempZip, zipPath, true);
            Log("ZIP-архив создан: " + zipPath);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private void UpdateSpeed()
    {
        lock (speedClock)
        {
            if (speedClock.ElapsedMilliseconds < 1000) return;
            var bytes = Interlocked.Exchange(ref bytesInWindow, 0); stats.BytesPerSecond = (long)(bytes / Math.Max(0.1, speedClock.Elapsed.TotalSeconds)); speedClock.Restart();
        }
    }
    private void Log(string message) { try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); File.AppendAllText(logPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}"); } catch { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        lock (watchers) { foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); }
        lock (queueLock) pending.Clear();
        // Do not dispose semaphores here: an in-flight copy may still be unwinding after cancellation.
    }
}