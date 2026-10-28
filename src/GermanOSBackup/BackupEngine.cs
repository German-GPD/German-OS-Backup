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
        foreach (var (_, source) in Sources())
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
        if (Volatile.Read(ref disposed) != 0 || !await scanLock.WaitAsync(0)) return;
        stats.IsScanning = true; var startedAt = DateTime.UtcNow;
        try
        {
            foreach (var (name, source) in Sources())
            {
                if (stop.IsCancellationRequested) break;
                if (Directory.Exists(source)) await CopyTreeAsync(name, source, stop.Token);
            }
            if (!stop.IsCancellationRequested) stats.HasCompletedInitialScan = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Interlocked.Increment(ref stats.Errors); Log($"Ошибка сканирования: {ex}"); }
        finally
        {
            stats.IsScanning = false; stats.LastScanFinished = DateTime.Now; scanLock.Release();
            Log($"Сканирование завершено за {(DateTime.UtcNow - startedAt).TotalMinutes:0.0} мин. Всего скопировано файлов: {Interlocked.Read(ref stats.FilesCopied):N0}.");
        }
    }

    private async Task CopyTreeAsync(string name, string source, CancellationToken ct)
    {
        var rootTarget = Path.Combine(config.Destination, name); Directory.CreateDirectory(rootTarget);
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
        foreach (var (name, source) in Sources())
        {
            var prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(source, full);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
            return Path.Combine(config.Destination, name, relative);
        }
        return null;
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