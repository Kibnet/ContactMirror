using System.Text.Json;
namespace ContactMirror.Mcp;

/// <summary>Bounds each stdio line before JSON parsing and never echoes invalid contact input.</summary>
internal sealed class BoundedInputStream(Stream inner, LineOutputStream output) : Stream
{
    private readonly byte[] readBuffer=new byte[8192];
    private int readOffset,readCount,frameOffset;
    private byte[]? frame;
    private bool ended;
    public Action? Ended { get; set; }
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken token=default)
    {
        if(destination.IsEmpty) return 0;
        while(frame is null || frameOffset==frame.Length)
        {
            if(ended) return 0;
            using var line=new MemoryStream();
            while(true)
            {
                if(readOffset==readCount)
                {
                    readCount=await inner.ReadAsync(readBuffer,token); readOffset=0;
                    if(readCount==0) { ended=true; Ended?.Invoke(); return 0; }
                }
                var newline=readBuffer.AsSpan(readOffset,readCount-readOffset).IndexOf((byte)'\n'); var count=newline<0 ? readCount-readOffset : newline+1;
                if(line.Length+count>32L*1024*1024) { ended=true; Ended?.Invoke(); throw new IOException("MCP input frame limit exceeded."); }
                line.Write(readBuffer.AsSpan(readOffset,count)); readOffset+=count;
                if(newline>=0) break;
            }
            var bytes=line.ToArray();
            try { using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=64 }); frame=bytes; frameOffset=0; }
            catch(JsonException) { await output.ParseErrorAsync(token); frame=null; }
        }
        var copied=Math.Min(destination.Length,frame.Length-frameOffset); frame.AsMemory(frameOffset,copied).CopyTo(destination); frameOffset+=copied; return copied;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
