using System.Diagnostics;

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
    private readonly SemaphoreSlim copySlots = new(Math.Clamp(Environment.ProcessorCount, 2, 6));
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly CancellationTokenSource stop = new();
    private readonly object queueLock = new();
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BackupStats stats = new();
    private readonly Stopwatch speedClock = Stopwatch.StartNew();
    private long bytesInWindow; private int disposed; private int started;

    public BackupEngine(AppConfig config, string logPath) { this.config = config; this.logPath = logPath; }
    public BackupStats Stats => stats;
    public static IEnumerable<(string Name, string Path)> Sources()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        yield return ("Users", Path.Combine(drive, "Users")); yield return ("ProgramData", Path.Combine(drive, "ProgramData")); yield return ("XboxGames", Path.Combine(drive, "XboxGames"));
    }

    public async Task StartAsync()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        Directory.CreateDirectory(config.Destination);
        foreach (var (_, source, _) in ConfiguredSources())
        {
            if (!Directory.Exists(source)) continue;
            try
            {
                var watcher = new FileSystemWatcher(source) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                watcher.Created += (_, e) => Queue(e.FullPath); watcher.Changed += (_, e) => Queue(e.FullPath); watcher.Renamed += (_, e) => Queue(e.FullPath);
                watcher.Error += (_, _) => _ = RequestScanAsync(); watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception ex) { Log($"Наблюдение недоступно для {source}: {ex.Message}"); }
        }
        _ = Task.Run(ProcessQueueAsync);
        await RequestScanAsync();
    }

    public async Task RequestScanAsync()
    {
        // Directory enumeration can block on slow, sleeping or unhealthy drives.
        // Keep every filesystem walk off the WinForms UI thread.
        if (Volatile.Read(ref disposed) != 0 || !await scanLock.WaitAsync(0).ConfigureAwait(false)) return;

        await Task.Run(async () =>
        {
            stats.IsScanning = true;
            var startedAt = DateTime.UtcNow;
            try
            {
                foreach (var (name, source, targetRoot) in ConfiguredSources())
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (Directory.Exists(source)) await CopyTreeAsync(name, source, targetRoot, stop.Token).ConfigureAwait(false);
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
                ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref stats.FilesSeen);
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
                    try { if (Directory.Exists(path)) Directory.CreateDirectory(target); else if (File.Exists(path)) await CopyFileAsync(path, target, stop.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Ошибка изменения {path}: {ex.Message}"); }
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
        var selected = config.SelectedSources?.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected is not { Count: > 0 })
        {
            foreach (var (name, path) in Sources())
                if (Directory.Exists(path)) yield return (name, path, Path.Combine(config.Destination, name));
            yield break;
        }

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
        await copySlots.WaitAsync(ct);
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
                    temp = target + ".gob-tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
                    long written = 0;
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = new byte[1024 * 1024]; int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), ct); written += read; }
                        await output.FlushAsync(ct);
                    }
                    File.Move(temp, target, true); temp = null; File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                    Interlocked.Increment(ref stats.FilesCopied); Interlocked.Add(ref stats.BytesCopied, written); Interlocked.Add(ref bytesInWindow, written); UpdateSpeed(); return;
                }
                catch (IOException) when (attempt < 2) { if (temp is not null) { try { File.Delete(temp); } catch { } temp = null; } await Task.Delay(300 * (attempt + 1), ct); }
            }
        }
        finally { if (temp is not null) { try { File.Delete(temp); } catch { } } copySlots.Release(); }
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
        stop.Cancel(); foreach (var watcher in watchers) watcher.Dispose();
        // Do not dispose semaphores here: an in-flight copy may still be unwinding after cancellation.
    }
}