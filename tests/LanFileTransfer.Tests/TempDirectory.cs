namespace LanFileTransfer.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LanFileTransfer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
