namespace LanFileTransfer.App.Infrastructure;

public sealed class StartupPreflight
{
    public static void Verify(AppPaths paths)
    {
        VerifyWritableDirectory(paths.BaseDirectory, "程序目录");
        VerifyWritableDirectory(paths.DefaultDataDirectory, "Data 目录");
        VerifyWritableDirectory(paths.ConfigDirectory, "Config 目录");
        VerifyWritableDirectory(paths.LogsDirectory, "Logs 目录");

        if (File.Exists(paths.SettingsFile))
        {
            using var stream = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }

        var dataAttributes = File.GetAttributes(paths.DefaultDataDirectory);
        if ((dataAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("默认共享目录不能是符号链接或联接点。");
        }
    }

    private static void VerifyWritableDirectory(string directory, string displayName)
    {
        Directory.CreateDirectory(directory);
        var attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new UnauthorizedAccessException($"{displayName}被标记为只读。");
        }

        var probe = Path.Combine(directory, $".lan-file-transfer-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }
}
