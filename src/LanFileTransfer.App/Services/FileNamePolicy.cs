using System.Text.RegularExpressions;

namespace LanFileTransfer.App.Services;

public static partial class FileNamePolicy
{
    public const int MaxFileNameLength = 180;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<string> SystemRouteNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "api", "assets", "qr", "favicon.ico", "index.html", "app.css", "app.js", "settings", "files", "events"
    };

    public static bool IsSafeLeafName(string? name, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "文件名不能为空。";
            return false;
        }

        if (name is "." or ".." || name.StartsWith(".upload-", StringComparison.OrdinalIgnoreCase))
        {
            error = "文件名不可用。";
            return false;
        }

        if (name.Length > MaxFileNameLength)
        {
            error = $"文件名不能超过 {MaxFileNameLength} 个字符。";
            return false;
        }

        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\'))
        {
            error = "只允许共享目录根目录中的单个文件名。";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "文件名包含 Windows 不支持的字符。";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd(' ', '.');
        if (ReservedNames.Contains(stem) || ControlCharacterRegex().IsMatch(name))
        {
            error = "文件名是 Windows 保留名称或包含控制字符。";
            return false;
        }

        return true;
    }

    public static bool IsSystemRouteName(string name) => SystemRouteNames.Contains(name);

    public static string ResolveContainedPath(string rootDirectory, string name, bool mustExist = false)
    {
        if (!IsSafeLeafName(name, out var error))
        {
            throw new InvalidDataException(error);
        }

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, name));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("目标路径位于共享目录之外。");
        }

        if (mustExist)
        {
            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException("文件不存在。", name);
            }

            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("不允许访问符号链接或联接点文件。");
            }
        }

        return candidate;
    }

    [GeneratedRegex(@"[\x00-\x1F]")]
    private static partial Regex ControlCharacterRegex();
}
