using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ContactMirror.Core;
using ContactMirror.Application.Automation;

namespace ContactMirror.Infrastructure.Automation;

internal sealed class AgentValues(TimeProvider? timeProvider = null)
{
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    public AgentRetentionBudget Budget { get; set; } = new();
    internal sealed record Scope(string? Root, string? AccountKey, string Version, string Source, string? BindingHash);
    private sealed record Value(byte[] Bytes, Scope Scope, string Mime, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Value> values = new();
    private string? root;
    private string? accountKey;
    private string? bindingHash;
    public void SetContext(string? workspace, string? account, string? binding = null) { root = workspace; accountKey = account; bindingHash = binding; }
    public void Clear() { foreach (var id in values.Keys.ToArray()) Remove(id); }
    private void Remove(string id) { if (values.TryRemove(id, out _)) Budget.Release("value:" + id); }
    public void RemoveSource(string prefix) { foreach (var pair in values.Where(p => p.Value.Scope.Source.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) Remove(pair.Key); }
    public JsonObject Store(byte[] bytes, string version, string source, string mime = "application/json")
    {
        foreach (var pair in values.Where(p => p.Value.Expires < Now)) Remove(pair.Key);
        var hash = JsonSemantics.Hash(bytes);
        var id = JsonSemantics.Hash(string.Join("|", root, accountKey, bindingHash, version, source, mime, hash));
        if (!values.ContainsKey(id))
        {
            Budget.Reserve("value:" + id, bytes.LongLength);
            values[id] = new(bytes.ToArray(), new(root, accountKey, version, source, bindingHash), mime, Now.AddMinutes(15));
        }
        return new JsonObject { ["externalValue"] = true, ["valueId"] = id, ["version"] = version, ["source"] = source, ["mimeType"] = mime, ["totalBytes"] = bytes.Length, ["sha256"] = JsonSemantics.Hash(bytes) };
    }
    public JsonNode? Bound(JsonNode? data, string version, string source)
    {
        if (data is null) return null;
        var bytes = JsonSemantics.Serialize(data);
        return bytes.Length <= 64 * 1024 ? data.DeepClone() : Store(bytes, version, source);
    }
    public async Task<JsonObject> ReadAsync(string id, string version, int offset, int count, Func<Scope, Task> authorize)
    {
        if (!values.TryGetValue(id, out var value) || value.Expires < Now || value.Scope.Version != version)
            throw new SyncException("value_stale", "Значение устарело. Прочитайте контакт или план заново.");
        await authorize(value.Scope);
        return Chunk(value.Bytes, offset, count, value.Scope.Version, value.Mime);
    }
    public static JsonObject Chunk(byte[] bytes, int offset, int count, string version, string mime)
    {
        if (offset < 0 || offset > bytes.Length || count < 1 || count > 1024 * 1024) throw new SyncException("invalid_range", "Размер порции должен быть от 1 байта до 1 МБ.");
        var take = Math.Min(count, bytes.Length - offset);
        return new JsonObject { ["base64"] = Convert.ToBase64String(bytes, offset, take), ["offset"] = offset, ["count"] = take, ["totalBytes"] = bytes.Length,
            ["sha256"] = JsonSemantics.Hash(bytes), ["version"] = version, ["mimeType"] = mime };
    }
}
