namespace LanFileTransfer.App.Models;

public sealed record SharedFileItem(
    string Name,
    long Size,
    DateTime LastModifiedUtc)
{
    public int Index { get; init; }

    public string DisplaySize => Size switch
    {
        >= 1024L * 1024 * 1024 => $"{Size / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{Size / (1024d * 1024):0.##} MB",
        >= 1024 => $"{Size / 1024d:0.##} KB",
        _ => $"{Size} B"
    };

    public string DisplayModified => LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
