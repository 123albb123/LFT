using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class FileNamePolicyTests
{
    [Theory]
    [InlineData("说明.txt")]
    [InlineData("app.conf")]
    [InlineData("带 空格 01.zip")]
    public void AcceptsSafeUnicodeLeafNames(string name)
    {
        Assert.True(FileNamePolicy.IsSafeLeafName(name, out _));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("CON.txt")]
    [InlineData("name. ")]
    [InlineData(".upload-a.tmp")]
    public void RejectsUnsafeOrReservedNames(string name)
    {
        Assert.False(FileNamePolicy.IsSafeLeafName(name, out _));
    }

    [Fact]
    public void RejectsOverlongNamesAndRecognizesSystemRoutes()
    {
        var longName = new string('文', FileNamePolicy.MaxFileNameLength) + ".txt";
        Assert.False(FileNamePolicy.IsSafeLeafName(longName, out _));
        Assert.True(FileNamePolicy.IsSystemRouteName("web"));
        Assert.True(FileNamePolicy.IsSystemRouteName("favicon.ico"));
    }

    [Fact]
    public void ResolvesOnlyDirectChildOfRoot()
    {
        using var temp = new TempDirectory();
        var path = FileNamePolicy.ResolveContainedPath(temp.Path, "测试.txt");
        Assert.Equal(System.IO.Path.Combine(temp.Path, "测试.txt"), path, ignoreCase: true);
        Assert.Throws<InvalidDataException>(() => FileNamePolicy.ResolveContainedPath(temp.Path, "..\\outside.txt"));
    }
}
