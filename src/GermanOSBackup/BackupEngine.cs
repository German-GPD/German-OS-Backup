namespace GermanOSBackup;

internal sealed class BackupEngine : IDisposable
{
    private readonly AppConfig config;
    private readonly string logPath;
    private readonly SemaphoreSlim scanLock = new(1, 1);
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly CancellationTokenSource stop = new();
    private readonly object queueLock = new();
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    public BackupEngine(AppConfig config, string logPath) { this.config = config; this.logPath = logPath; }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(config.Destination);
        foreach (var (name, source) in Sources())
        {
            if (!Directory.Exists(source)) continue;
            try
            {
                var watcher = new FileSystemWatcher(source) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 16 * 1024 };
                watcher.Created += (_, e) => Queue(e.FullPath);
                watcher.Changed += (_, e) => Queue(e.FullPath);
                watcher.Renamed += (_, e) => Queue(e.FullPath);
                watcher.Error += (_, _) => _ = RequestScanAsync();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) { Log($"Наблюдение недоступно для {source}: {ex.Message}"); }
        }
        _ = Task.Run(ProcessQueueAsync);
        await RequestScanAsync();
    }

    public async Task RequestScanAsync()
    {
        if (Volatile.Read(ref disposed) != 0 || !await scanLock.WaitAsync(0)) return;
        try
        {
            foreach (var (name, source) in Sources())
            {
                if (stop.IsCancellationRequested) break;
                if (Directory.Exists(source)) await CopyTreeAsync(name, source, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Ошибка сканирования: {ex}"); }
        finally { scanLock.Release(); }
    }

    private async Task CopyTreeAsync(string name, string source, CancellationToken ct)
    {
        var rootTarget = Path.Combine(config.Destination, name);
        Directory.CreateDirectory(rootTarget);
        var stack = new Stack<(string Source, string Target)>();
        stack.Push((source, rootTarget));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (from, to) = stack.Pop();
            string[] dirs, files;
            try { dirs = Directory.GetDirectories(from); files = Directory.GetFiles(from); }
            catch (Exception ex) { Log($"Папка пропущена {from}: {ex.Message}"); continue; }
            foreach (var dir in dirs)
            {
                try { if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) stack.Push((dir, Path.Combine(to, Path.GetFileName(dir)))); }
                catch (Exception ex) { Log($"Не удалось прочитать {dir}: {ex.Message}"); }
            }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try { await CopyFileAsync(file, Path.Combine(to, Path.GetFileName(file)), ct); }
                catch (Exception ex) { Log($"Ошибка копирования {file}: {ex.Message}"); }
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1200, stop.Token);
                string[] batch;
                lock (queueLock) { batch = pending.Take(64).ToArray(); foreach (var item in batch) pending.Remove(item); }
                foreach (var path in batch)
                {
                    if (stop.IsCancellationRequested) break;
                    var target = MapPath(path);
                    if (target is null) continue;
                    try
                    {
                        if (Directory.Exists(path)) Directory.CreateDirectory(target);
                        else if (File.Exists(path)) await CopyFileAsync(path, target, stop.Token);
                    }
                    catch (Exception ex) { Log($"Ошибка обработки изменения {path}: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Queue(string path)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (queueLock) { if (pending.Count < 5000) pending.Add(path); }
    }

    private string? MapPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return null; }
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

    private static IEnumerable<(string Name, string Path)> Sources()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        yield return ("Users", Path.Combine(drive, "Users"));
        yield return ("ProgramData", Path.Combine(drive, "ProgramData"));
        yield return ("XboxGames", Path.Combine(drive, "XboxGames"));
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src = new FileInfo(source);
                if (!src.Exists) return;
                if (File.Exists(target))
                {
                    var dst = new FileInfo(target);
                    if (dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc) return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".gob-tmp";
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 32768, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output, ct);
                File.Move(temp, target, true);
                File.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                return;
            }
            catch (IOException) when (attempt < 2) { await Task.Delay(400 * (attempt + 1), ct); }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    private void Log(string message)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); File.AppendAllText(logPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}"); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        foreach (var watcher in watchers) watcher.Dispose();
        stop.Dispose();
        scanLock.Dispose();
    }
}
