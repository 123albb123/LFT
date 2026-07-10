using System.Collections.Concurrent;

namespace LanFileTransfer.App.Infrastructure;

public sealed class AppLogService
{
    private readonly AppPaths _paths;
    private readonly object _fileGate = new();
    private readonly ConcurrentQueue<string> _recent = new();
    private DateOnly? _lastCleanupDate;
    private int _fileWarningRaised;

    public AppLogService(AppPaths paths)
    {
        _paths = paths;
        TryCleanupOldLogs();
    }

    public event Action<string>? EntryAdded;

    public IReadOnlyList<string> Recent => _recent.ToArray();

    public void Info(string message) => Write(string.Empty, message);
    public void Warning(string message) => Write("警告", message);
    public void Error(string message, Exception? exception = null) =>
        Write("错误", exception is null ? message : $"{message}：{exception.Message}");

    private void Write(string level, string message)
    {
        var label = string.IsNullOrEmpty(level) ? string.Empty : $"{level} · ";
        var line = $"[{DateTime.Now:HH:mm:ss}] {label}{message}";
        _recent.Enqueue(line);
        while (_recent.Count > 500 && _recent.TryDequeue(out _))
        {
        }

        lock (_fileGate)
        {
            try
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                File.AppendAllText(Path.Combine(_paths.LogsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
                TryCleanupOldLogs();
            }
            catch
            {
                NotifyFileLoggingUnavailable();
            }
        }

        RaiseEntryAdded(line);
    }

    private void TryCleanupOldLogs()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_lastCleanupDate == today)
            {
                return;
            }

            Directory.CreateDirectory(_paths.LogsDirectory);
            foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个旧日志无法清理不应影响写入。
                }
            }
            _lastCleanupDate = today;
        }
        catch
        {
            NotifyFileLoggingUnavailable();
        }
    }

    private void NotifyFileLoggingUnavailable()
    {
        if (Interlocked.Exchange(ref _fileWarningRaised, 1) != 0)
        {
            return;
        }

        var warning = $"[{DateTime.Now:HH:mm:ss}] 警告 · 日志目录不可写，日志仅保留在当前窗口内。";
        _recent.Enqueue(warning);
        while (_recent.Count > 500 && _recent.TryDequeue(out _))
        {
        }
        RaiseEntryAdded(warning);
    }

    private void RaiseEntryAdded(string line)
    {
        foreach (var callback in EntryAdded?.GetInvocationList().Cast<Action<string>>() ?? [])
        {
            try { callback(line); }
            catch { }
        }
    }
}
