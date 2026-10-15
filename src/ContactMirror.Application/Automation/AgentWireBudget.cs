using System.Text.Encodings.Web;
using System.Text.Json;
using ContactMirror.Core;
namespace ContactMirror.Application.Automation;

/// <summary>Conservative size under the SDK's default JSON escaping, leaving room for JSON-RPC metadata.</summary>
public static class AgentWireBudget
{
    public const int MaximumPayload = 3 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSemantics.Options) { Encoder = JavaScriptEncoder.Default, WriteIndented = false };
    public static long Measure<T>(T value)
    {
        using var writer = new Utf8JsonWriter(Stream.Null,new() { Encoder = JavaScriptEncoder.Default });
        JsonSerializer.Serialize(writer,value,Options); writer.Flush(); return writer.BytesCommitted;
    }
}
