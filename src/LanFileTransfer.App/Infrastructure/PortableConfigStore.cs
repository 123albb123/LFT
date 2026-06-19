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

    public AppSettings Load()
    {
        _paths.EnsurePortableDirectories();
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
        catch
        {
            var backup = $"{_paths.SettingsFile}.invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(_paths.SettingsFile, backup, overwrite: true);
            settings = new AppSettings();
        }

        Replace(settings, persist: true);
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
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, _paths.SettingsFile, overwrite: true);
    }
}
