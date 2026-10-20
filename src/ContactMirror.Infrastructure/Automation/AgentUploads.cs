using System.Collections.Concurrent;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Automation;

internal sealed class AgentUploads(TimeProvider? timeProvider = null) : IDisposable
{
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    private sealed class Upload(string bridge, string key, int length, string hash, DateTimeOffset expires)
    {
        public string Bridge { get; } = bridge;
        public string Key { get; } = key;
        public int Length { get; } = length;
        public string Hash { get; } = hash;
        public DateTimeOffset Expires { get; } = expires;
        public MemoryStream Data { get; } = new();
        public bool Sealed { get; set; }
    }
    private readonly ConcurrentDictionary<string, Upload> uploads = new();
    public string Begin(string bridge, string key, int length, string hash)
    {
        Expire();
        var old = uploads.FirstOrDefault(u => u.Value.Bridge == bridge && u.Value.Key == key);
        if (old.Value is not null)
        {
            if (old.Value.Length != length || old.Value.Hash != hash) throw new SyncException("idempotency_conflict", "uploadKey уже использован для другого фото.");
            return old.Key;
        }
        if (length is < 16 or > 20 * 1024 * 1024 || hash.Length != 64) throw new SyncException("invalidPhoto", "Фото должно быть JPEG/PNG до 20 МБ с SHA256.");
        if (uploads.Count(u => u.Value.Bridge == bridge) >= 2 || uploads.Values.Sum(u => (long)u.Length) + length > 80L * 1024 * 1024) throw new SyncException("resource_limit", "Завершите предыдущие загрузки фото.");
        var id = Guid.NewGuid().ToString("N"); uploads[id] = new(bridge, key, length, hash, Now.AddMinutes(10)); return id;
    }
    private Upload Get(string bridge, string id)
    {
        Expire(); if (!uploads.TryGetValue(id, out var u) || u.Bridge != bridge) throw new SyncException("upload_expired", "Загрузка недоступна. Загрузите фото заново."); return u;
    }
    public void Chunk(string bridge, string id, int offset, byte[] bytes)
    {
        var u = Get(bridge, id);
        if (bytes.Length is < 1 or > 1024 * 1024 || offset < 0 || (long)offset + bytes.Length > u.Length) throw new SyncException("invalid_range", "Неверная порция фотографии.");
        if (offset < u.Data.Length && offset + bytes.Length <= u.Data.Length)
        {
            if (!u.Data.GetBuffer().AsSpan(offset, bytes.Length).SequenceEqual(bytes)) throw new SyncException("idempotency_conflict", "Порция уже содержит другие байты."); return;
        }
        if (u.Sealed || offset != u.Data.Length) throw new SyncException("invalid_range", "Порции загружаются последовательно.");
        u.Data.Write(bytes);
    }
    public byte[] Complete(string bridge, string id)
    {
        var u = Get(bridge, id); var data = u.Data.ToArray();
        if (data.Length != u.Length || !string.Equals(JsonSemantics.Hash(data), u.Hash, StringComparison.OrdinalIgnoreCase)) throw new SyncException("photo_hash", "Размер или SHA256 фотографии не совпал.");
        FileWorkspaceStore.ValidateImage(data); u.Sealed = true; return data;
    }
    public byte[] Take(string bridge, string id)
    {
        var u = Get(bridge, id); if (!u.Sealed) throw new SyncException("upload_not_sealed", "Сначала завершите загрузку фото."); return u.Data.ToArray();
    }
    public void Abort(string bridge, string id) { if (uploads.TryGetValue(id, out var u) && u.Bridge == bridge && uploads.TryRemove(id, out _)) u.Data.Dispose(); }
    public void Disconnected(string bridge) { foreach (var pair in uploads.Where(u => u.Value.Bridge == bridge).ToArray()) Abort(bridge, pair.Key); }
    private void Expire() { foreach (var pair in uploads.Where(u => u.Value.Expires < Now).ToArray()) Abort(pair.Value.Bridge, pair.Key); }
    public void Dispose() { foreach (var pair in uploads.ToArray()) Abort(pair.Value.Bridge, pair.Key); }
}
