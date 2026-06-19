using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;

namespace LanFileTransfer.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void CreatesPortableDefaultsAndReloadsThem()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new PortableConfigStore(paths);
        var settings = store.Load();

        Assert.Equal(28080, settings.Port);
        Assert.True(settings.LanOnly);
        Assert.True(settings.AllowWebUpload);
        Assert.False(settings.AllowWebDelete);
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(Directory.Exists(paths.DefaultDataDirectory));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(65536)]
    public void RejectsInvalidPorts(int port)
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => PortableConfigStore.Validate(new AppSettings { Port = port }, paths));
    }
}
