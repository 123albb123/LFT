using System.Net.Sockets;
using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;
using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class StartupStabilityTests
{
    [Fact]
    public void DamagedConfigurationIsBackedUpAndRestoredToDefaults()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsurePortableDirectories();
        File.WriteAllText(paths.SettingsFile, "{ not-json");

        var store = new PortableConfigStore(paths);
        var settings = store.Load();

        Assert.Equal(28080, settings.Port);
        Assert.NotNull(store.RecoveryInfo);
        Assert.True(store.RecoveryInfo!.BackupSucceeded);
        Assert.True(File.Exists(store.RecoveryInfo.BackupFile!));
        Assert.Contains("28080", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void DamagedConfigurationStillLoadsDefaultsWhenBackupAndSaveFail()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsurePortableDirectories();
        File.WriteAllText(paths.SettingsFile, "{ not-json");
        using var lockFile = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var store = new PortableConfigStore(paths);
        var settings = store.Load();

        Assert.Equal(28080, settings.Port);
        Assert.NotNull(store.RecoveryInfo);
        Assert.NotNull(store.RecoveryInfo!.BackupException);
        Assert.NotNull(store.RecoveryInfo.SaveException);
    }

    [Fact]
    public void FileLoggingFailureKeepsMemoryLogAvailable()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        Directory.CreateDirectory(paths.BaseDirectory);
        File.WriteAllText(paths.LogsDirectory, "not a directory");
        var log = new AppLogService(paths);

        log.Info("业务操作仍可继续");

        Assert.Contains(log.Recent, line => line.Contains("业务操作仍可继续"));
        Assert.Single(log.Recent, line => line.Contains("日志目录不可写"));
    }

    [Fact]
    public void SecondInstanceUsesSameDirectoryAndDoesNotBecomePrimary()
    {
        using var temp = new TempDirectory();
        using var primary = SingleInstanceCoordinator.Create(temp.Path);
        using var secondary = SingleInstanceCoordinator.Create(temp.Path);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public void PortInUseIsClassifiedClearly()
    {
        var error = ServerStartException.Wrap(
            new SocketException((int)SocketError.AddressAlreadyInUse), 28080);

        Assert.Equal(ServerStartFailureKind.PortInUse, error.Kind);
        Assert.Contains("28080", error.Message);
    }

    [Fact]
    public async Task ServerCanStartAgainAfterOccupiedPortIsReleased()
    {
        using var temp = new TempDirectory();
        var port = GetFreePort();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths);
        config.Load();
        config.Replace(new AppSettings { Port = port }, persist: false);
        var log = new AppLogService(paths);
        var events = new EventHub();
        var transfers = new TransferRegistry(events);
        using var catalog = new FileCatalog(paths, config, events, log);
        var firstServer = new HttpFileServer(config, catalog, new NetworkAddressService(), events, transfers, new WebAssetProvider(), log);
        var retryServer = new HttpFileServer(config, catalog, new NetworkAddressService(), events, transfers, new WebAssetProvider(), log);
        await firstServer.StartAsync();
        try
        {
            var failure = await Assert.ThrowsAsync<ServerStartException>(() => retryServer.StartAsync());
            Assert.Equal(ServerStartFailureKind.PortInUse, failure.Kind);

            await firstServer.StopAsync();
            await retryServer.StartAsync();
            Assert.True(retryServer.IsRunning);
        }
        finally
        {
            await firstServer.StopAsync();
            await retryServer.StopAsync();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
