using System.Reflection;

namespace LanFileTransfer.App.Services;

public sealed class WebAssetProvider
{
    private readonly Assembly _assembly = typeof(WebAssetProvider).Assembly;

    public string ReadText(string fileName)
    {
        var suffix = $".Web.{fileName.Replace('/', '.')}";
        var resourceName = _assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = _assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"找不到 Web 资源：{fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
