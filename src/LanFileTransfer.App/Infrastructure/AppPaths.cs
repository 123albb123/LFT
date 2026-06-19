namespace LanFileTransfer.App.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        ConfigDirectory = Path.Combine(BaseDirectory, "Config");
        LogsDirectory = Path.Combine(BaseDirectory, "Logs");
        DefaultDataDirectory = Path.Combine(BaseDirectory, "Data");
        SettingsFile = Path.Combine(ConfigDirectory, "settings.json");
    }

    public string BaseDirectory { get; }
    public string ConfigDirectory { get; }
    public string LogsDirectory { get; }
    public string DefaultDataDirectory { get; }
    public string SettingsFile { get; }

    public void EnsurePortableDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DefaultDataDirectory);
    }

    public string ResolveUploadDirectory(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return DefaultDataDirectory;
        }

        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(BaseDirectory, configuredPath));
    }
}
