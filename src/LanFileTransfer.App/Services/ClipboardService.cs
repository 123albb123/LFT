using System.Runtime.InteropServices;
using System.Windows;

namespace LanFileTransfer.App.Services;

public static class ClipboardService
{
    private const int MaximumAttempts = 6;

    public static Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        return TrySetTextAsync(
            text,
            value => Clipboard.SetDataObject(value, copy: true),
            TimeSpan.FromMilliseconds(80),
            cancellationToken);
    }

    internal static async Task<bool> TrySetTextAsync(
        string text,
        Action<string> writer,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(writer);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                writer(text);
                return true;
            }
            catch (ExternalException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        return false;
    }
}
