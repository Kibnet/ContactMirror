using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public static class JsonSemantics
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    public static bool Equal(JsonNode? a, JsonNode? b, bool ignoreVolatile = false) => Canonical(a, ignoreVolatile) == Canonical(b, ignoreVolatile);
    public static string Canonical(JsonNode? value, bool ignoreVolatile = false) => value switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.Where(x => !ignoreVolatile || x.Key is not ("etag" or "updateTime")).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value, ignoreVolatile))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(x => Canonical(x, ignoreVolatile)).OrderBy(x => x, StringComparer.Ordinal)) + "]",
        _ => value.ToJsonString()
    };

    public static JsonObject ParseObject(byte[] bytes, string fileName)
    {
        try
        {
            if (bytes.Length > 16 * 1024 * 1024) throw new SyncException("fileTooLarge", $"Файл слишком большой (лимит 16 МБ): {fileName}");
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                    throw new SyncException("duplicateKey", $"Повторяющееся свойство JSON: {fileName}");
            }
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                ?? throw new SyncException("invalidJson", $"Ожидается объект JSON: {fileName}");
        }
        catch (JsonException ex) { throw new SyncException("invalidJson", $"Не удалось прочитать JSON: {fileName}, строка {(ex.LineNumber ?? 0) + 1}, позиция {(ex.BytePositionInLine ?? 0) + 1}.", inner: ex); }
    }

    public static byte[] Serialize<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options) + "\n");
    public static JsonNode? Clone(JsonNode? value) => value?.DeepClone();
}
