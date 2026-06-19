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
            return Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith(".upload-", StringComparison.OrdinalIgnoreCase))
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .Select(path => new FileInfo(path))
                .Select(info => new SharedFileItem(info.Name, info.Length, info.LastWriteTimeUtc))
                .OrderByDescending(item => item.LastModifiedUtc)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(_directory);
            return [];
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
            _reconcileTimer = new Timer(_ => NotifyChanged(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        NotifyChanged();
    }

    public async Task<SharedFileItem> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
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
                await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
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
            var destination = ResolveDestination(requestedName);
            if (File.Exists(destination))
            {
                destination = behavior switch
                {
                    DuplicateBehavior.Reject => throw new DuplicateFileException(requestedName),
                    DuplicateBehavior.AutoRename => FindAvailableName(requestedName),
                    _ => destination
                };
            }

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
        foreach (var path in Directory.EnumerateFiles(_directory, ".upload-*.tmp", SearchOption.TopDirectoryOnly))
        {
            TryDelete(path);
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
            var candidate = ResolveDestination($"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new IOException("无法生成可用的文件名。");
    }

    private void OnWatchEvent(object sender, FileSystemEventArgs args)
    {
        if (Path.GetFileName(args.FullPath).StartsWith(".upload-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        lock (_watcherGate)
        {
            _debounceTimer?.Change(250, Timeout.Infinite);
        }
    }

    private void NotifyChanged()
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
}

public sealed class DuplicateFileException(string fileName)
    : IOException($"文件“{fileName}”已存在。")
{
}
