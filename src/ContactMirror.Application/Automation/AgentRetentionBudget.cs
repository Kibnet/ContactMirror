using ContactMirror.Core;

namespace ContactMirror.Application.Automation;

/// <summary>A single retained-payload budget covers prepared inputs, chunk values and local restore copies.</summary>
public sealed class AgentRetentionBudget
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> reservations = new(StringComparer.Ordinal);
    public long RetainedBytes { get { lock (gate) return reservations.Values.Sum(); } }
    public void Reserve(string id, long bytes)
    {
        lock (gate)
        {
            if (bytes < 0 || reservations.Values.Sum() - reservations.GetValueOrDefault(id) + bytes > 512L * 1024 * 1024)
                throw new SyncException("resource_limit", "Лимит подготовленных данных 512 МБ исчерпан. Завершите или обновите прежние операции.");
            reservations[id] = bytes;
        }
    }
    public void Release(string id) { lock (gate) reservations.Remove(id); }
}
