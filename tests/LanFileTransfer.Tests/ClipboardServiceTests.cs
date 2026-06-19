using System.Runtime.InteropServices;
using LanFileTransfer.App.Services;

namespace LanFileTransfer.Tests;

public sealed class ClipboardServiceTests
{
    [Fact]
    public async Task RetriesWhenClipboardIsTemporarilyBusy()
    {
        var attempts = 0;
        var copied = await ClipboardService.TrySetTextAsync(
            "http://192.168.1.88:28080/test.txt",
            _ =>
            {
                attempts++;
                if (attempts < 3) throw new ExternalException("Clipboard busy");
            },
            TimeSpan.Zero);

        Assert.True(copied);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ReturnsFalseInsteadOfCrashingAfterRepeatedFailures()
    {
        var copied = await ClipboardService.TrySetTextAsync(
            "value",
            _ => throw new ExternalException("Clipboard busy"),
            TimeSpan.Zero);

        Assert.False(copied);
    }
}
