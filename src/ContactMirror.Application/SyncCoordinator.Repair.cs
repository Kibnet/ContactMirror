using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application;

public sealed partial class SyncCoordinator
{
    private const string RepairField = "$repairGoogleSnapshot";
    private static string StateHash(EntityState state) => JsonSemantics.Hash(JsonSemantics.Serialize(JsonSerializer.SerializeToNode(state, JsonSemantics.Options)!));
    private static bool RepairEligible(Prepared p, Guid id, out bool complete, out string reason)
    {
        complete = false; reason = "Нет доверенной связи с Google. Исправление недоступно.";
        if (p.View.IsRecovery || p.UnsafeRemoteIdentity || p.View.Issues.Any(i => i.Id == id)) { reason = "Сначала устраните ошибки файлов или восстановления состояния."; return false; }
        if (!p.View.Contacts.TryGetValue(id, out var local) || !p.View.States.TryGetValue(id, out var state) || state.Kind != EntityKind.Contact || state.Id != id
            || state.Baseline["google"] is not JsonObject snapshot || snapshot["person"] is not JsonObject baselinePerson || !p.People.TryGetValue(id, out var remote)) return false;
        try
        {
            if (snapshot["resourceName"]?.ToString() != state.ResourceName || PersonCodec.Resource(baselinePerson) != state.ResourceName || PersonCodec.SourceId(baselinePerson) != state.SourceId
                || PersonCodec.Resource(remote) != state.ResourceName || PersonCodec.SourceId(remote) != state.SourceId) return false;
        }
        catch (SyncException) { return false; }
        var pending = p.View.Pending.Where(o => o.EntityId == id).ToArray();
        if (pending.Length == 0) return ReadOnlyModified(local, state);
        if (pending[0].Field != RepairField || pending[0].SnapshotRepair is not { } intent
            || pending.Any(o => o.Field != RepairField || !JsonSemantics.Equal(JsonSerializer.SerializeToNode(o.SnapshotRepair, JsonSemantics.Options), JsonSerializer.SerializeToNode(intent, JsonSemantics.Options))))
        { reason = "Сначала согласуйте незавершённые операции этого контакта."; return false; }
        if (intent.StateHash != StateHash(state) || !JsonSemantics.Equal(intent.Google, snapshot) || intent.LocalPath != local.Path || intent.PhotoHash != local.PhotoHash
            || (local.Hash != intent.InputHash && local.Hash != intent.OutputHash))
        { reason = "Файл, фото или связь изменились после незавершённого исправления. Новые правки сохранены; требуется ручная проверка резервной копии."; return false; }
        complete = local.Hash == intent.OutputHash;
        reason = ""; return true;
    }

    private static SyncEntry EnhanceEntry(Prepared p, SyncEntry e)
    {
        var local = (e.Entity == EntityKind.Contact ? p.View.Contacts : p.View.Groups).GetValueOrDefault(e.EntityId);
        var state = p.View.States.GetValueOrDefault(e.EntityId);
        var before = e.Before; var google = e.Google;
        if (e.Field == "$snapshot") before = state?.Baseline["google"] ?? e.Local;
        var repair = false; var complete = false; var explanation = e.Explanation;
        PhotoComparison? photo = null;
        if (e.Entity == EntityKind.Contact && local is not null)
        {
            if (e.Field is "$entity" or RepairField)
            {
                before = state?.Baseline;
                if (p.People.TryGetValue(e.EntityId, out var remote)) google = DocumentCodec.ToJson(ContactFromRemote(p, e.EntityId, remote, DocumentCodec.Contact(local.Document)));
                if (e.Kind == ChangeKind.Blocked)
                {
                    repair = RepairEligible(p, e.EntityId, out complete, out var reason);
                    if (!repair) explanation += "\n" + reason;
                }
            }
            var remoteBytes = p.Photos.GetValueOrDefault(e.EntityId);
            var remoteHash = remoteBytes is null ? null : JsonSemantics.Hash(remoteBytes);
            var lChanged = local.PhotoHash != state?.PhotoLocalHash; var rChanged = remoteHash != state?.PhotoRemoteHash;
            if (e.Field == "photo" || e.Kind == ChangeKind.Blocked && (lChanged || rChanged))
            {
                var status = lChanged && rChanged && local.PhotoHash != remoteHash ? "Разные правки фото — выберите версию"
                    : lChanged ? local.PhotoHash is null ? "Фото удалено в файле" : state?.PhotoLocalHash is null ? "Фото добавлено в файле" : "Фото заменено в файле (сравнивается содержимое)"
                    : rChanged ? remoteHash is null ? "Фото удалено в Google" : "Фото изменено в Google" : "Фото согласовано";
                var queued = e.Field == "photo" && p.RestoredPhotos.TryGetValue(e.EntityId, out _);
                var planned = queued ? p.RestoredPhotos[e.EntityId] : null;
                if (queued) status = "К отправке: фотография из журнала. Текущие изображения показаны отдельно; выберите источник изменения.";
                photo = new(state?.PhotoLocalHash, state?.PhotoRemoteHash, local.PhotoHash, remoteHash, local.Document["photo"]?.ToString(), local.PhotoBytes?.ToArray(), remoteBytes?.ToArray(), status, queued, planned is null ? null : JsonSemantics.Hash(planned), planned?.ToArray());
            }
        }
        return new() { Key = e.Key, EntityId = e.EntityId, Entity = e.Entity, Name = e.Name, Field = e.Field, Kind = e.Kind,
            Before = JsonSemantics.Clone(before), Local = JsonSemantics.Clone(e.Local), Google = JsonSemantics.Clone(google), Explanation = explanation,
            LocalPath = local?.Path, CanRepairGoogleSnapshot = repair, CompletesLocalRepair = complete, PhotoComparison = photo };
    }

    public async Task<SyncRunResult> RepairGoogleSnapshotAsync(SyncPreview preview, string entryKey, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!_plans.Remove(preview.PlanId, out var p) || p.Preview.Root != preview.Root || p.Preview.Account.Key != preview.Account.Key)
                throw new SyncException("stalePlan", "Этот план устарел. Проверьте изменения ещё раз.");
            var entry = p.Preview.Entries.SingleOrDefault(e => e.Key == entryKey);
            if (entry is not { Entity: EntityKind.Contact, Kind: ChangeKind.Blocked } || !RepairEligible(p, entry.EntityId, out var complete, out _))
                throw new SyncException("repairUnavailable", "Исправление недоступно для этой записи. Проверьте файлы, связь и незавершённые операции.");
            var id = entry.EntityId; var old = p.View.Contacts[id]; var state = p.View.States[id];
            await using var session = await store.OpenAsync(p.Preview.Root, p.Preview.Account, cancellationToken);
            var current = await session.ReadLocalAsync(EntityKind.Contact, id, cancellationToken);
            if (session.View.IsRecovery || session.View.Issues.Any(i => i.Id == id) || current is null || current.Hash != old.Hash || current.PhotoHash != old.PhotoHash || current.Path != old.Path
                || !session.View.States.TryGetValue(id, out var actualState) || StateHash(state) != StateHash(actualState)
                || !JsonSemantics.Equal(JsonSerializer.SerializeToNode(p.View.Pending.Where(o => o.EntityId == id), JsonSemantics.Options), JsonSerializer.SerializeToNode(session.View.Pending.Where(o => o.EntityId == id), JsonSemantics.Options)))
                throw new SyncException("localDrift", "Файл, фото или связь изменились после сравнения. Новые правки сохранены; проверьте изменения заново.");
            var pending = p.View.Pending.FirstOrDefault(o => o.EntityId == id && o.Field == RepairField);
            var replacement = (JsonObject)current.Document.DeepClone(); replacement["google"] = state.Baseline["google"]!.DeepClone();
            var intent = pending?.SnapshotRepair ?? new(current.Hash, current.PhotoHash, JsonSemantics.Hash(JsonSemantics.Serialize(replacement)), StateHash(state), current.Path, (JsonObject)state.Baseline["google"]!.DeepClone());
            progress?.Report(new("Сохраняем резервную копию перед локальным исправлением…"));
            var runId = await session.BeginRunAsync([new(EntityKind.Contact, id, current.Document, current.PhotoBytes, p.People[id], p.Photos.GetValueOrDefault(id), state, current.RawBytes, current.Path)], cancellationToken);
            var operation = new JournalOperation(entryKey, id, EntityKind.Contact, RepairField, "started", ResourceName: state.ResourceName, SnapshotRepair: intent);
            await session.RecordAsync(runId, operation, CancellationToken.None);
            SyncRunResult result;
            try
            {
                if (!complete) await session.WriteAsync(EntityKind.Contact, id, replacement, current.Hash, expectedPhotoHash: current.PhotoHash, cancellationToken: cancellationToken);
                // No state/baseline update: editable changes must remain visible in the next manual comparison.
                result = new(runId, 1, 0, 0, [new(entryKey, "confirmed", "Служебная копия исправлена. Правки полей и фото сохранены.")]);
                await session.CommitLocalRepairAsync(runId, operation with { Status = "committed" }, result, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostics?.Invoke(ex);
                // A crash can occur after the atomic replacement. Preserve the intent for explicit recovery.
                await session.RecordAsync(runId, operation with { Status = "unknown", Message = SafeMessage(ex) }, CancellationToken.None);
                result = new(runId, 0, 0, 1, [new(entryKey, "unknown", SafeMessage(ex))]);
                await session.CompleteRunAsync(runId, result, CancellationToken.None);
            }
            return result;
        }
        finally { _mutex.Release(); }
    }
}
