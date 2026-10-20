using ContactMirror.Application.Automation;
using ContactMirror.Core;

namespace ContactMirror.Application;

public sealed partial class SyncCoordinator
{
    private readonly Dictionary<Guid, string> planOwners = [];
    private readonly Dictionary<Guid, (string Reason, DateTimeOffset Until)> expiredPlans = [];
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
    public AgentRetentionBudget RetentionBudget { get; } = new();
    public event Action<Guid, string>? PlanInvalidated;
    public event Action<Guid>? RunStarted;
    public async Task<SyncPreview> ReadPreparedPlanAsync(Guid id)
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_plans.TryGetValue(id, out var p)) throw new SyncException(expiredPlans.GetValueOrDefault(id).Reason ?? "plan_expired", "План больше не действителен.");
            return RequirePlan(p.Preview).Preview;
        }
        finally { _mutex.Release(); }
    }

    private void RegisterPlan(Prepared prepared)
    {
        foreach (var pair in _plans.ToArray())
            if (Now - pair.Value.Preview.PreparedAt > PlanLifetime ||
                PlanOrigin.Current == "gui" && planOwners.GetValueOrDefault(pair.Key) == "gui")
                ExpirePlan(pair.Key, "plan_expired");
        while (PlanOrigin.Current != "gui" && _plans.Count(p => planOwners.GetValueOrDefault(p.Key) != "gui") >= 8)
            ExpirePlan(_plans.Where(p => planOwners.GetValueOrDefault(p.Key) != "gui").OrderBy(p => p.Value.Preview.PreparedAt).First().Key, "plan_expired");
        RetentionBudget.Reserve("plan:" + prepared.Preview.PlanId, PreparedBytes(prepared));
        _plans.Add(prepared.Preview.PlanId, prepared); planOwners.Add(prepared.Preview.PlanId, PlanOrigin.Current);
        foreach (var pair in expiredPlans.Where(p => p.Value.Until < Now).ToArray()) expiredPlans.Remove(pair.Key);
        while (expiredPlans.Count > 128) expiredPlans.Remove(expiredPlans.Keys.First());
    }
    private static long PreparedBytes(Prepared p)
    {
        using var counter = new JsonByteCounter();
        long JsonBytes(System.Text.Json.Nodes.JsonNode? n) => counter.Count(n);
        return p.View.Contacts.Values.Concat(p.View.Groups.Values).Sum(e => (long)(e.RawBytes?.Length ?? 0) + JsonBytes(e.Document) + (e.PhotoBytes?.Length ?? 0)) +
            p.People.Values.Sum(JsonBytes) + p.Groups.Values.Sum(JsonBytes) + p.Restored.Values.Sum(JsonBytes) +
            p.Photos.Values.Concat(p.RestoredPhotos.Values).Concat(p.RemoteRestorePhotos.Values).Sum(b => (long)(b?.Length ?? 0)) +
            p.Preview.Entries.Sum(e => 1024L + JsonBytes(e.Before) + JsonBytes(e.Local) + JsonBytes(e.Google) +
                (e.PhotoComparison?.LocalBytes?.LongLength ?? 0) + (e.PhotoComparison?.GoogleBytes?.LongLength ?? 0) + (e.PhotoComparison?.PlannedBytes?.LongLength ?? 0));
    }
    private Prepared RequirePlan(SyncPreview preview)
    {
        if (_plans.TryGetValue(preview.PlanId, out var p) && Now - p.Preview.PreparedAt > PlanLifetime)
            ExpirePlan(preview.PlanId, "plan_expired");
        if (!_plans.TryGetValue(preview.PlanId, out p))
            throw new SyncException(expiredPlans.GetValueOrDefault(preview.PlanId).Reason ?? "plan_expired", "План больше не действителен. Подготовьте новый план.");
        if (p.Preview.Root != preview.Root || p.Preview.Account.Key != preview.Account.Key || planOwners.GetValueOrDefault(preview.PlanId) != PlanOrigin.Current)
            throw new SyncException("plan_not_owned", "План принадлежит другому исполнителю.");
        return p;
    }
    private void ExpirePlan(Guid id, string reason)
    {
        foreach(var edit in localEdits.Where(pair => pair.Value.Plan.Preview.PlanId == id).Select(pair => pair.Key).ToArray()) localEdits.Remove(edit);
        _plans.Remove(id); planOwners.Remove(id); expiredPlans[id] = (reason, Now + PlanLifetime); PlanInvalidated?.Invoke(id, reason);
        RetentionBudget.Release("plan:" + id);
    }
    public async Task InvalidatePlansAsync(string? root = null, string? accountKey = null, string reason = "plan_stale")
    {
        await _mutex.WaitAsync();
        try { InvalidatePlans(root, accountKey, reason); }
        finally { _mutex.Release(); }
    }
    private void InvalidatePlans(string? root, string? accountKey, string reason)
    {
        foreach (var pair in _plans.ToArray())
            if ((root is null || string.Equals(pair.Value.Preview.Root, root, StringComparison.OrdinalIgnoreCase)) &&
                (accountKey is null || pair.Value.Preview.Account.Key == accountKey)) ExpirePlan(pair.Key, reason);
    }
}
