using System.IO;

namespace LanFileTransfer.App.Services;

internal sealed class ProgressWriteStream(Stream inner, Action<long> progress) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public long BytesWritten => Interlocked.Read(ref _written);

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Report(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        Report(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Report(buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Report(buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        // 响应流由 ASP.NET Core 管理，这个包装器不拥有它。
    }

    private void Report(int count)
    {
        var value = Interlocked.Add(ref _written, count);
        progress(value);
    }
}
