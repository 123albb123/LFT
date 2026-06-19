using LanFileTransfer.App.Infrastructure;
using LanFileTransfer.App.Models;
using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class FileCatalogTests
{
    [Fact]
    public async Task AppliesAllDuplicatePolicies()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths);
        config.Load();
        var events = new EventHub();
        var log = new AppLogService(paths);
        using var catalog = new FileCatalog(paths, config, events, log);
        var source = System.IO.Path.Combine(temp.Path, "source", "demo.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "first");

        await catalog.ImportAsync(source);
        await File.WriteAllTextAsync(source, "second");
        await catalog.ImportAsync(source);
        Assert.Equal("second", await File.ReadAllTextAsync(System.IO.Path.Combine(catalog.DirectoryPath, "demo.txt")));

        config.Replace(config.Current with { DuplicateBehavior = DuplicateBehavior.AutoRename }, persist: false);
        await catalog.ImportAsync(source);
        Assert.Contains(catalog.GetFiles(), file => file.Name == "demo (1).txt");

        config.Replace(config.Current with { DuplicateBehavior = DuplicateBehavior.Reject }, persist: false);
        await Assert.ThrowsAsync<DuplicateFileException>(() => catalog.ImportAsync(source));
    }

    [Fact]
    public void DoesNotListTemporaryUploadFiles()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        using var catalog = new FileCatalog(paths, config, new EventHub(), new AppLogService(paths));
        File.WriteAllText(catalog.CreateTemporaryPath(), "partial");
        Assert.Empty(catalog.GetFiles());
    }

    [Fact]
    public void ListsNewestModifiedFileFirst()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        using var catalog = new FileCatalog(paths, config, new EventHub(), new AppLogService(paths));
        var older = System.IO.Path.Combine(catalog.DirectoryPath, "older.txt");
        var newer = System.IO.Path.Combine(catalog.DirectoryPath, "newer.txt");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        Assert.Equal(new[] { "newer.txt", "older.txt" }, catalog.GetFiles().Select(file => file.Name));
    }
}
