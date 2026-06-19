using System.Collections.Concurrent;

namespace LanFileTransfer.App.Infrastructure;

public sealed class AppLogService
{
    private readonly AppPaths _paths;
    private readonly object _fileGate = new();
    private readonly ConcurrentQueue<string> _recent = new();

    public AppLogService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.LogsDirectory);
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
            Directory.CreateDirectory(_paths.LogsDirectory);
            File.AppendAllText(Path.Combine(_paths.LogsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            CleanupOldLogs();
        }

        EntryAdded?.Invoke(line);
    }

    private void CleanupOldLogs()
    {
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
                // 日志清理不能影响主流程。
            }
        }
    }
}
