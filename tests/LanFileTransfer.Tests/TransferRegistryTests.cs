using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class TransferRegistryTests
{
    [Fact]
    public void FailAllAlwaysClearsActiveTransfers()
    {
        var registry = new TransferRegistry(new EventHub());
        Assert.True(registry.Begin("upload-1", "upload", "a.txt", 10));
        Assert.True(registry.Begin("download-1", "download", "b.txt", 10));

        registry.FailAll("服务停止");

        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public void ExpiredTransfersAreRemoved()
    {
        var registry = new TransferRegistry(new EventHub());
        Assert.True(registry.Begin("stale", "upload", "a.txt", 10));

        registry.CleanupExpired(TimeSpan.Zero);

        Assert.Equal(0, registry.ActiveCount);
    }
}
