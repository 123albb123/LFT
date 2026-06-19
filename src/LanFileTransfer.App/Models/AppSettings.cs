namespace LanFileTransfer.App.Models;

public enum DuplicateBehavior
{
    Overwrite,
    AutoRename,
    Reject
}

public sealed record AppSettings
{
    public int Port { get; init; } = 28080;
    public string UploadDirectory { get; init; } = "Data";
    public bool LanOnly { get; init; } = true;
    public bool AllowWebUpload { get; init; } = true;
    public bool AllowWebDelete { get; init; }
    public DuplicateBehavior DuplicateBehavior { get; init; } = DuplicateBehavior.Overwrite;
    public long MaxUploadBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}
