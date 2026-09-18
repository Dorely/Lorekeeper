namespace Lorekeeper.ProjectArchive;

internal sealed class ProjectArchiveWriteLimitStream(Stream inner, long maximumBytes) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Reserve(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(buffer.Length);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Reserve(count);
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reserve(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    private void Reserve(int count)
    {
        _written = checked(_written + count);
        if (_written > maximumBytes)
            throw new ProjectArchiveException("Archive output exceeds its configured byte limit.");
    }
}
