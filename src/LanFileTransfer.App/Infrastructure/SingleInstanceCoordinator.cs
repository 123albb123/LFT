using System.Security.Cryptography;
using System.Text;

namespace LanFileTransfer.App.Infrastructure;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _cancellation = new();

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent, bool isPrimary)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceCoordinator Create(string baseDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(baseDirectory).ToUpperInvariant())))[..24];
        var name = $"Local\\LanFileTransfer.{hash}";
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
        return new SingleInstanceCoordinator(mutex, activation, createdNew);
    }

    public void SignalPrimary()
    {
        try { _activationEvent.Set(); } catch { }
    }

    public void ListenForActivation(Action activate)
    {
        _ = Task.Run(() =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    if (!_activationEvent.WaitOne(TimeSpan.FromSeconds(1))) continue;
                    activate();
                }
                catch (ObjectDisposedException) { return; }
            }
        });
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _activationEvent.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
        _cancellation.Dispose();
    }
}
