using System.IO;
using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;

namespace LanFileTransfer.App.Services;

public sealed class FileCatalog : IDisposable
{
    private readonly AppPaths _paths;
    private readonly PortableConfigStore _config;
    private readonly EventHub _events;
    private readonly AppLogService _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _watcherGate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _reconcileTimer;
    private string _directory = string.Empty;
    private CatalogFileSnapshot[] _lastSnapshot = [];
    private readonly Dictionary<string, DateTimeOffset> _internalChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _externalLogTimes = new(StringComparer.OrdinalIgnoreCase);

    public FileCatalog(AppPaths paths, PortableConfigStore config, EventHub events, AppLogService log)
    {
        _paths = paths;
        _config = config;
        _events = events;
        _log = log;
        ChangeDirectory(config.Current.UploadDirectory);
    }

    public event Action? FilesChanged;

    public string DirectoryPath => _directory;

    public IReadOnlyList<SharedFileItem> GetFiles()
    {
        try
        {
            var files = new List<SharedFileItem>();
            foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (Path.GetFileName(path).StartsWith(".upload-", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    var info = new FileInfo(path);
                    files.Add(new SharedFileItem(info.Name, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _log.Warning($"跳过无法读取的文件：{Path.GetFileName(path)}。{exception.Message}");
                }
            }

            return files
                .OrderByDescending(item => item.LastModifiedUtc)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            _log.Warning($"共享目录暂时不可访问：{_directory}");
            throw new DirectoryNotFoundException($"共享目录暂时不可访问：{_directory}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Error("读取共享目录失败", exception);
            throw new IOException("共享目录暂时不可访问。", exception);
        }
    }

    public string ResolveExisting(string name) => FileNamePolicy.ResolveContainedPath(_directory, name, mustExist: true);

    public string ResolveDestination(string name) => FileNamePolicy.ResolveContainedPath(_directory, name);

    public void ChangeDirectory(string configuredPath)
    {
        var fullPath = _paths.ResolveUploadDirectory(configuredPath);
        Directory.CreateDirectory(fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("共享目录不能是符号链接或联接点。");
        }

        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
            _reconcileTimer?.Dispose();
            _directory = fullPath;
            _internalChanges.Clear();
            _externalLogTimes.Clear();
            _lastSnapshot = CaptureSnapshot();

            _watcher = new FileSystemWatcher(_directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnWatchEvent;
            _watcher.Changed += OnWatchEvent;
            _watcher.Deleted += OnWatchEvent;
            _watcher.Renamed += OnWatchEvent;
            _watcher.Error += (_, args) => _log.Warning($"文件监视器异常，将依靠定时刷新：{args.GetException().Message}");
            _debounceTimer = new Timer(_ => NotifyChanged(), null, Timeout.Infinite, Timeout.Infinite);
            _reconcileTimer = new Timer(_ => ReconcileDirectory(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        PublishChanged();
        CleanupTemporaryFiles();
    }

    public async Task<SharedFileItem> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default,
        IProgress<FileCopyProgress>? progress = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("源文件不存在。", sourcePath);
        }

        var sourceAttributes = File.GetAttributes(sourcePath);
        if ((sourceAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("不允许添加符号链接文件。");
        }

        var fileName = Path.GetFileName(sourcePath);
        if (!FileNamePolicy.IsSafeLeafName(fileName, out var error))
        {
            throw new InvalidDataException(error);
        }

        var temporary = CreateTemporaryPath();
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var total = source.Length;
                long copied = 0;
                progress?.Report(new FileCopyProgress(copied, total));
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress?.Report(new FileCopyProgress(copied, total));
                }
                await destination.FlushAsync(cancellationToken);
            }

            return await CommitTemporaryAsync(temporary, fileName, _config.Current.DuplicateBehavior, cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public string CreateTemporaryPath() => Path.Combine(_directory, $".upload-{Guid.NewGuid():N}.tmp");

    public async Task<SharedFileItem> CommitTemporaryAsync(
        string temporaryPath,
        string requestedName,
        DuplicateBehavior behavior,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!FileNamePolicy.IsSafeLeafName(requestedName, out var error)) throw new InvalidDataException(error);
            if (FileNamePolicy.IsSystemRouteName(requestedName))
            {
                if (behavior == DuplicateBehavior.AutoRename)
                {
                    requestedName = "共享-" + requestedName;
                    if (!FileNamePolicy.IsSafeLeafName(requestedName, out error)) throw new InvalidDataException(error);
                }
                else
                {
                    throw new InvalidDataException("文件名与系统路径冲突，请更换名称。");
                }
            }

            var destination = ResolveDestination(requestedName);
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("不允许覆盖符号链接或联接点文件。");
                }
                destination = behavior switch
                {
                    DuplicateBehavior.Reject => throw new DuplicateFileException(requestedName),
                    DuplicateBehavior.AutoRename => FindAvailableName(requestedName),
                    _ => destination
                };
            }

            MarkInternalChange(destination);
            File.Move(temporaryPath, destination, overwrite: behavior == DuplicateBehavior.Overwrite);
            var info = new FileInfo(destination);
            var item = new SharedFileItem(info.Name, info.Length, info.LastWriteTimeUtc);
            NotifyChanged();
            return item;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var path = ResolveExisting(name);
            MarkInternalChange(path);
            File.Delete(path);
            NotifyChanged();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void CleanupTemporaryFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, ".upload-*.tmp", SearchOption.TopDirectoryOnly))
            {
                TryDelete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"清理临时文件失败：{exception.Message}");
        }
    }

    public void Dispose()
    {
        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
            _reconcileTimer?.Dispose();
        }
        _writeGate.Dispose();
    }

    private string FindAvailableName(string requestedName)
    {
        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var extension = Path.GetExtension(requestedName);
        for (var index = 1; index < 100_000; index++)
        {
            var suffix = $" ({index}){extension}";
            var maximumStemLength = FileNamePolicy.MaxFileNameLength - suffix.Length;
            if (maximumStemLength <= 0) throw new IOException("文件名过长，无法自动重命名。");
            var shortenedStem = stem.Length > maximumStemLength ? stem[..maximumStemLength] : stem;
            var candidateName = shortenedStem + suffix;
            if (!FileNamePolicy.IsSafeLeafName(candidateName, out var error)) throw new InvalidDataException(error);
            var candidate = ResolveDestination(candidateName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new IOException("无法生成可用的文件名。");
    }

    private void OnWatchEvent(object sender, FileSystemEventArgs args)
    {
        var fileName = Path.GetFileName(args.FullPath);
        if (fileName.StartsWith(".upload-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_watcherGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _internalChanges.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            {
                _internalChanges.Remove(expired);
            }

            var internalChange = _internalChanges.TryGetValue(args.FullPath, out var suppressUntil) && suppressUntil > now;
            if (!internalChange && (!_externalLogTimes.TryGetValue(args.FullPath, out var lastLogged) || now - lastLogged >= TimeSpan.FromSeconds(1)))
            {
                _externalLogTimes[args.FullPath] = now;
                _log.Info($"文件变化 · {fileName} · 本机");
            }
            _debounceTimer?.Change(250, Timeout.Infinite);
        }
    }

    private void NotifyChanged()
    {
        try
        {
            var snapshot = CaptureSnapshot();
            lock (_watcherGate)
            {
                _lastSnapshot = snapshot;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"目录变化校准失败：{exception.Message}");
        }

        PublishChanged();
    }

    internal bool ReconcileDirectory()
    {
        CatalogFileSnapshot[] snapshot;
        try
        {
            snapshot = CaptureSnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"定时校准共享目录失败：{exception.Message}");
            return false;
        }

        var changed = false;
        lock (_watcherGate)
        {
            if (!_lastSnapshot.SequenceEqual(snapshot))
            {
                _lastSnapshot = snapshot;
                changed = true;
            }
        }

        if (changed)
        {
            PublishChanged();
        }
        return changed;
    }

    private CatalogFileSnapshot[] CaptureSnapshot()
    {
        return Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith(".upload-", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(info => new CatalogFileSnapshot(info.Name, info.Length, info.LastWriteTimeUtc.Ticks))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void MarkInternalChange(string path)
    {
        lock (_watcherGate)
        {
            _internalChanges[path] = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }

    private void PublishChanged()
    {
        FilesChanged?.Invoke();
        _events.Publish("files-changed", new { at = DateTimeOffset.UtcNow });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 退出或上传失败时尽力清理，后续启动会再次清理。
        }
    }

    private readonly record struct CatalogFileSnapshot(string Name, long Length, long LastWriteTicks);
}

public readonly record struct FileCopyProgress(long BytesCopied, long TotalBytes);

public sealed class DuplicateFileException(string fileName)
    : IOException($"文件“{fileName}”已存在。")
{
}
