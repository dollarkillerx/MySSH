namespace MySSH.Mcp;

/// <summary>
/// Read-only pass-through stream that invokes a callback once when the underlying stream reports EOF (or a connection
/// reset). StreamServerTransport does not cancel in-flight requests on EOF (spike G7): a local client closing the
/// connection is gone for good, so the listener treats EOF as "cancel the session".
/// </summary>
internal sealed class EofSignalingStream(Stream inner, Action onEof) : Stream
{
    private int _signalled;

    private int Check(int n)
    {
        if (n == 0 && Interlocked.Exchange(ref _signalled, 1) == 0) onEof();
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return Check(inner.Read(buffer, offset, count)); }
        catch (IOException) { Check(0); throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return Check(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)); }
        catch (IOException) { Check(0); throw; } // connection reset == EOF for our purposes
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
