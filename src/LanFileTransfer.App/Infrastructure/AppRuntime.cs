namespace LanFileTransfer.App.Infrastructure;

public sealed class AppRuntime(AppPaths paths, PortableConfigStore config, AppLogService log)
{
    public AppPaths Paths { get; } = paths;
    public PortableConfigStore Config { get; } = config;
    public AppLogService Log { get; } = log;

    public static AppRuntime Start(string? baseDirectory = null)
    {
        var paths = new AppPaths(baseDirectory);
        StartupPreflight.Verify(paths);
        var config = new PortableConfigStore(paths);
        config.Load();
        var log = new AppLogService(paths);
        return new AppRuntime(paths, config, log);
    }
}
