using System.Collections.Concurrent;

namespace LanFileTransfer.App.Services;

public sealed class TransferRegistry(EventHub events)
{
    private readonly ConcurrentDictionary<string, TransferState> _active = new(StringComparer.Ordinal);

    public int ActiveCount => _active.Count;

    public bool Begin(string id, string direction, string fileName, long total)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80)
        {
            return false;
        }

        var state = new TransferState(direction, fileName, Math.Max(total, 0));
        if (!_active.TryAdd(id, state))
        {
            return false;
        }

        Publish(id, state, 0, "running");
        return true;
    }

    public void Report(string id, long bytes)
    {
        if (!_active.TryGetValue(id, out var state))
        {
            return;
        }

        state.LastActivityAt = DateTimeOffset.UtcNow;

        var now = Environment.TickCount64;
        if (bytes < state.Total && now - Interlocked.Read(ref state.LastPublishedAt) < 150)
        {
            return;
        }

        Interlocked.Exchange(ref state.LastPublishedAt, now);
        Publish(id, state, bytes, "running");
    }

    public void Complete(string id, long bytes)
    {
        if (_active.TryRemove(id, out var state))
        {
            Publish(id, state, bytes, "completed");
        }
    }

    public void Fail(string id, string error)
    {
        if (_active.TryRemove(id, out var state))
        {
            events.Publish("transfer", new
            {
                id,
                direction = state.Direction,
                fileName = state.FileName,
                total = state.Total,
                bytes = 0,
                percent = 0,
                status = "failed",
                error
            });
        }
    }

    public void FailAll(string error)
    {
        foreach (var id in _active.Keys)
        {
            Fail(id, error);
        }
    }

    public void CleanupExpired(TimeSpan maximumAge)
    {
        var threshold = DateTimeOffset.UtcNow - maximumAge;
        foreach (var pair in _active)
        {
            if (pair.Value.LastActivityAt < threshold) Fail(pair.Key, "传输长时间没有进度，已结束。");
        }
    }

    private void Publish(string id, TransferState state, long bytes, string status)
    {
        var percent = state.Total == 0 ? 100 : Math.Clamp((int)Math.Round(bytes * 100d / state.Total), 0, 100);
        events.Publish("transfer", new
        {
            id,
            direction = state.Direction,
            fileName = state.FileName,
            total = state.Total,
            bytes,
            percent,
            status
        });
    }

    private sealed class TransferState(string direction, string fileName, long total)
    {
        public string Direction { get; } = direction;
        public string FileName { get; } = fileName;
        public long Total { get; } = total;
        public long LastPublishedAt;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
