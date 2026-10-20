namespace ContactMirror.Mcp;

/// <summary>Writes complete JSON lines atomically, including parse errors between SDK messages.</summary>
internal sealed class LineOutputStream(Stream inner) : Stream
{
    private readonly SemaphoreSlim bufferGate = new(1,1), sinkGate = new(1,1);
    private readonly MemoryStream pending = new();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token)=>WriteAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
    {
        await bufferGate.WaitAsync(token);
        try
        {
            while(!buffer.IsEmpty)
            {
                var newline=buffer.Span.IndexOf((byte)'\n'); var count=newline<0 ? buffer.Length : newline+1;
                if(pending.Length+count>4*1024*1024) throw new IOException("MCP output frame limit exceeded.");
                pending.Write(buffer.Span[..count]); buffer=buffer[count..];
                if(newline>=0) { var frame=pending.ToArray(); pending.SetLength(0); await WriteFrameAsync(frame,token); }
            }
        }
        finally { bufferGate.Release(); }
    }
    private async Task WriteFrameAsync(ReadOnlyMemory<byte> frame,CancellationToken token)
    {
        await sinkGate.WaitAsync(token); try { await inner.WriteAsync(frame,token); await inner.FlushAsync(token); } finally { sinkGate.Release(); }
    }
    public Task ParseErrorAsync(CancellationToken token)=>WriteFrameAsync("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Invalid JSON input.\"}}\n"u8.ToArray(),token);
    public override void Flush()=>inner.Flush();
    public override Task FlushAsync(CancellationToken token)=>inner.FlushAsync(token);
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if(disposing) { pending.Dispose(); bufferGate.Dispose(); sinkGate.Dispose(); } base.Dispose(disposing); }
}
