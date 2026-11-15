namespace JamesTools.Mcp;

public sealed class PayloadLimitException : Exception;

/// <summary>A counting sink that prevents JSON serialization from allocating an unbounded output buffer.</summary>
public sealed class BoundedBuffer(int maximumBytes) : Stream
{
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_length + buffer.Length > maximumBytes) throw new PayloadLimitException();
        _length += buffer.Length;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Validates each entire UTF-8 JSON-line frame before exposing bytes to the SDK parser.</summary>
public sealed class BoundedInputStream(Stream source, Action? endOfInput = null) : Stream
{
    public const int MaximumRequestBytes = 64 * 1024;
    private readonly byte[] _line = new byte[MaximumRequestBytes];
    private readonly byte[] _readBuffer = new byte[4096];
    private int _readOffset, _readLength, _lineOffset, _lineLength;
    private long _frameWindow = Environment.TickCount64;
    private int _frameCount;
    private bool _ended;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _ended) return 0;
        if (_lineOffset == _lineLength)
        {
            _lineOffset = _lineLength = 0;
            // Idle stdin is allowed. Once a frame begins, its remainder has a 15-second deadline.
            using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                if (_readOffset == _readLength)
                {
                    _readLength = await source.ReadAsync(_readBuffer, frameTimeout.Token);
                    _readOffset = 0;
                    if (_readLength == 0)
                    {
                        _ended = true;
                        endOfInput?.Invoke();
                        if (_lineLength != 0) throw new IOException("Incomplete MCP frame.");
                        return 0;
                    }
                }
                if (_lineLength == 0) frameTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var value = _readBuffer[_readOffset++];
                if (_lineLength == _line.Length) throw new IOException("MCP request exceeds 64 KiB.");
                _line[_lineLength++] = value;
                if (value == (byte)'\n') break;
            }
            // Apply backpressure before SDK dispatch, including notifications and invalid requests.
            // At most 32 new frames/second; no unbounded application-level queue.
            var elapsed = Environment.TickCount64 - _frameWindow;
            if (elapsed >= 1000) { _frameWindow = Environment.TickCount64; _frameCount = 0; }
            if (++_frameCount > 32)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 1000 - elapsed)), cancellationToken);
                _frameWindow = Environment.TickCount64;
                _frameCount = 1;
            }
        }
        var count = Math.Min(buffer.Length, _lineLength - _lineOffset);
        _line.AsMemory(_lineOffset, count).CopyTo(buffer);
        _lineOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Stages a complete response so no partial oversized frame reaches stdout.</summary>
public sealed class BoundedOutputStream(Stream destination) : Stream
{
    private readonly MemoryStream _frame = new();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var newline = buffer.Span.IndexOf((byte)'\n');
            var length = newline < 0 ? buffer.Length : newline + 1;
            if (_frame.Length + length > DiagnosticAccess.MaximumPayloadBytes) throw new PayloadLimitException();
            _frame.Write(buffer.Span[..length]);
            buffer = buffer[length..];
            if (newline < 0) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await destination.WriteAsync(_frame.GetBuffer().AsMemory(0, (int)_frame.Length), deadline.Token);
            await destination.FlushAsync(deadline.Token);
            _frame.SetLength(0);
        }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected override void Dispose(bool disposing) { if (disposing) _frame.Dispose(); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
