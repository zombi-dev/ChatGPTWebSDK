using System.Text;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

internal sealed class CodexEventStream(IAsyncEnumerator<ServerSentEvent> events, ServerSentEvent first, CancellationTokenSource lifetime, IDisposable response) : Stream
{
    private byte[] _buffer = Encode(first); private int _offset; private bool _complete; private bool _disposed;
    private static byte[] Encode(ServerSentEvent item) => Encoding.UTF8.GetBytes((item.Event is null ? "" : "event: " + item.Event + "\n") +
        string.Concat(item.Data.Split('\n').Select(line => "data: " + line + "\n")) + "\n");
    public override bool CanRead => !_disposed; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ct.ThrowIfCancellationRequested(); if (destination.Length == 0) return 0;
        if (_offset == _buffer.Length)
        {
            using var registration = ct.Register(lifetime.Cancel);
            if (_complete || !await events.MoveNextAsync().ConfigureAwait(false)) { _complete = true; return 0; }
            _buffer = Encode(events.Current); _offset = 0;
        }
        var count = Math.Min(destination.Length, _buffer.Length - _offset); _buffer.AsMemory(_offset, count).CopyTo(destination); _offset += count; return count;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; lifetime.Cancel();
            try { events.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult(); }
            finally { lifetime.Dispose(); response.Dispose(); }
        }
        _complete = true; base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
