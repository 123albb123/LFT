using System.Text.Json;
using System.Text.Json.Serialization;
using LanFileTransfer.App.Models;

namespace LanFileTransfer.App.Infrastructure;

public sealed class PortableConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private AppSettings _current = new();

    public ConfigurationRecoveryInfo? RecoveryInfo { get; private set; }

    public PortableConfigStore(AppPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void ValidateCurrent() => Validate(Current, _paths);

    public AppSettings Load()
    {
        _paths.EnsurePortableDirectories();
        RecoveryInfo = null;
        AppSettings settings;
        if (!File.Exists(_paths.SettingsFile))
        {
            settings = new AppSettings();
            Replace(settings, persist: true);
            return settings;
        }

        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile), JsonOptions)
                       ?? new AppSettings();
            Validate(settings, _paths);
        }
        catch (Exception exception)
        {
            var backup = Path.Combine(
                _paths.ConfigDirectory,
                $"settings.invalid-{DateTime.Now:yyyyMMddHHmmss}.json");
            Exception? backupException = null;
            try
            {
                File.Copy(_paths.SettingsFile, backup, overwrite: false);
            }
            catch (Exception copyException)
            {
                backupException = copyException;
                backup = null!;
            }

            RecoveryInfo = new ConfigurationRecoveryInfo(exception, backup, backupException);
            settings = new AppSettings();
        }

        try
        {
            Replace(settings, persist: true);
        }
        catch (Exception saveException) when (RecoveryInfo is not null)
        {
            RecoveryInfo = RecoveryInfo with { SaveException = saveException };
            lock (_gate)
            {
                _current = settings;
            }
        }
        return settings;
    }

    public void Replace(AppSettings settings, bool persist)
    {
        Validate(settings, _paths);
        lock (_gate)
        {
            _current = settings;
            if (persist)
            {
                SaveCore(settings);
            }
        }
    }

    public static void Validate(AppSettings settings, AppPaths paths)
    {
        if (settings.Port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.Port), "端口必须在 1024 到 65535 之间。");
        }

        if (settings.MaxUploadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.MaxUploadBytes), "最大上传大小必须大于 0。");
        }

        var directory = paths.ResolveUploadDirectory(settings.UploadDirectory);
        Directory.CreateDirectory(directory);
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("共享目录不能是符号链接或联接点。");
        }
    }

    private void SaveCore(AppSettings settings)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var temporary = $"{_paths.SettingsFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(_paths.SettingsFile))
            {
                File.Replace(temporary, _paths.SettingsFile, null);
            }
            else
            {
                File.Move(temporary, _paths.SettingsFile);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // 临时文件清理失败不应覆盖原始保存错误。
            }
        }
    }
}

public sealed record ConfigurationRecoveryInfo(Exception OriginalException, string? BackupFile, Exception? BackupException)
{
    public bool BackupSucceeded => BackupFile is not null && BackupException is null;
    public Exception? SaveException { get; init; }
}
