namespace LanFileTransfer.App.Services;

internal static class SafeContentTypes
{
    private static readonly Dictionary<string, string> PreviewTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".conf"] = "text/plain; charset=utf-8",
        [".config"] = "text/plain; charset=utf-8",
        [".ini"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".csv"] = "text/plain; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "text/plain; charset=utf-8",
        [".yaml"] = "text/plain; charset=utf-8",
        [".yml"] = "text/plain; charset=utf-8",
        [".html"] = "text/plain; charset=utf-8",
        [".htm"] = "text/plain; charset=utf-8",
        [".svg"] = "text/plain; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm"
    };

    public static (string ContentType, bool CanPreview) Get(string fileName)
    {
        return PreviewTypes.TryGetValue(Path.GetExtension(fileName), out var contentType)
            ? (contentType, true)
            : ("application/octet-stream", false);
    }
}
