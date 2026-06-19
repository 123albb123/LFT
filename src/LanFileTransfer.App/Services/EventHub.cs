using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace LanFileTransfer.App.Services;

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return new Subscription(id, channel.Reader, this);
    }

    public void Publish(string type, object? data = null)
    {
        var json = JsonSerializer.Serialize(new { type, data });
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(json);
        }
    }

    private void Remove(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public sealed class Subscription(Guid id, ChannelReader<string> reader, EventHub owner) : IDisposable
    {
        public ChannelReader<string> Reader { get; } = reader;
        public void Dispose() => owner.Remove(id);
    }
}
