using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Core;
[assembly: InternalsVisibleTo("ContactMirror.McpTests")]
namespace ContactMirror.Application.Automation;

/// <summary>Counts the canonical serializer output without retaining another copy of each document.</summary>
internal sealed class JsonByteCounter : IDisposable
{
    private readonly Utf8JsonWriter writer = new(Stream.Null, new() { Indented = JsonSemantics.Options.WriteIndented, Encoder = JsonSemantics.Options.Encoder });
    public long Count(JsonNode? node)
    {
        if(node is null) return 0;
        writer.Reset(Stream.Null); JsonSerializer.Serialize(writer,node,JsonSemantics.Options); writer.Flush(); return writer.BytesCommitted + 1; // Canonical files end with LF.
    }
    public void Dispose()=>writer.Dispose();
}
