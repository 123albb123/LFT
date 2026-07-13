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

    [Fact]
    public async Task CleansTemporaryFilesAndSerializesAutoRename()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        config.Replace(config.Current with { DuplicateBehavior = DuplicateBehavior.AutoRename }, persist: false);
        using var catalog = new FileCatalog(paths, config, new EventHub(), new AppLogService(paths));
        File.WriteAllText(catalog.CreateTemporaryPath(), "leftover");
        catalog.CleanupTemporaryFiles();
        Assert.Empty(Directory.EnumerateFiles(catalog.DirectoryPath, ".upload-*.tmp"));

        var first = catalog.CreateTemporaryPath();
        var second = catalog.CreateTemporaryPath();
        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");
        var items = await Task.WhenAll(
            catalog.CommitTemporaryAsync(first, "same.txt", DuplicateBehavior.AutoRename),
            catalog.CommitTemporaryAsync(second, "same.txt", DuplicateBehavior.AutoRename));

        Assert.Equal(2, items.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RejectsSystemRouteNameUnlessAutoRenameIsSelected()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        using var catalog = new FileCatalog(paths, config, new EventHub(), new AppLogService(paths));
        var temporary = catalog.CreateTemporaryPath();
        await File.WriteAllTextAsync(temporary, "web");

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.CommitTemporaryAsync(temporary, "web", DuplicateBehavior.Reject));
        Assert.True(File.Exists(temporary));
        var item = await catalog.CommitTemporaryAsync(temporary, "web", DuplicateBehavior.AutoRename);
        Assert.Equal("共享-web", item.Name);
    }

    [Fact]
    public async Task ImportReportsCopiedBytesAndTotalSize()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        using var catalog = new FileCatalog(paths, config, new EventHub(), new AppLogService(paths));
        var source = Path.Combine(temp.Path, "progress.bin");
        await File.WriteAllBytesAsync(source, new byte[1024 * 1024 + 17]);
        var updates = new List<FileCopyProgress>();

        await catalog.ImportAsync(source, progress: new InlineProgress<FileCopyProgress>(updates.Add));

        Assert.NotEmpty(updates);
        Assert.Equal(new FileInfo(source).Length, updates[^1].BytesCopied);
        Assert.Equal(new FileInfo(source).Length, updates[^1].TotalBytes);
    }

    [Fact]
    public void ReconcilePublishesOnlyWhenDirectorySnapshotChanges()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var config = new PortableConfigStore(paths); config.Load();
        using var catalog = new FileCatalog(paths, config, new EventHub(), new AppLogService(paths));

        Assert.False(catalog.ReconcileDirectory());
        File.WriteAllText(Path.Combine(catalog.DirectoryPath, "external.txt"), "changed");
        Assert.True(catalog.ReconcileDirectory());
        Assert.False(catalog.ReconcileDirectory());
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
