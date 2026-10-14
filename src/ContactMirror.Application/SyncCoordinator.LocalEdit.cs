using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Application;

public sealed partial class SyncCoordinator
{
    private const string LocalEditField = "$localEdit";
    private sealed record LocalEditEntry(string Key, Guid EntityId, EntityKind Entity, string Field, ChangeKind Kind, string Name);
    private sealed record RegisteredEdit(Prepared Plan, LocalEditEntry Entry, SyncLocalEdit Handle, JsonObject Original, JsonObject? Remote);
    private readonly Dictionary<Guid, RegisteredEdit> localEdits = [];

    public async Task<SyncLocalEdit> OpenLocalEditAsync(SyncPreview preview, string entryKey, CancellationToken token = default)
    {
        await _mutex.WaitAsync(token);
        try
        {
            if (!_plans.TryGetValue(preview.PlanId, out var p) || p.Preview.Root != preview.Root || p.Preview.Account.Key != preview.Account.Key)
                throw new SyncException("stalePlan", "Сравнение устарело. Проверьте изменения снова.");
            var entry = p.LocalEditEntries.GetValueOrDefault(entryKey) ?? throw new SyncException("missingEntry", "Изменение не найдено.");
            if (entry.Kind is ChangeKind.Blocked or ChangeKind.Reconcile or ChangeKind.Snapshot || entry.Field.StartsWith('$') && entry.Field != "$entity"
                || p.View.IsRecovery || p.View.Pending.Count > 0 || p.UnsafeRemoteIdentity || p.View.Issues.Count > 0)
                throw new SyncException("editUnavailable", "Сначала завершите восстановление и исправьте ошибки папки. Служебные сведения Google не редактируются.");
            var local = (entry.Entity == EntityKind.Contact ? p.View.Contacts : p.View.Groups).GetValueOrDefault(entry.EntityId);
            var state = p.View.States.GetValueOrDefault(entry.EntityId);
            if (ReadOnlyModified(local, state) || local?.Document["google"] is not null && state is null)
                throw new SyncException("protectedField", "Сначала восстановите доверенную связь Google.");
            JsonObject? remote = null;
            if (entry.Entity == EntityKind.Contact && p.People.TryGetValue(entry.EntityId, out var person))
                remote = DocumentCodec.ToJson(ContactFromRemote(p, entry.EntityId, person, local is null ? new() { Id = entry.EntityId } : DocumentCodec.Contact(local.Document)));
            else if (entry.Entity == EntityKind.Group && p.Groups.TryGetValue(entry.EntityId, out var group))
                remote = DocumentCodec.ToJson(GroupFromRemote(entry.EntityId, group, local is null ? null : DocumentCodec.Group(local.Document)));
            var original = (JsonObject)(local?.Document ?? remote ?? throw new SyncException("missingLocal", "Запись отсутствует с обеих сторон.")).DeepClone();
            if (local is null && state is not null) original["google"] = state.Baseline["google"]?.DeepClone();
            var labels = p.View.Groups.ToDictionary(g => g.Key, g => DocumentCodec.Group(g.Value.Document).Name);
            var needed = entry.Entity == EntityKind.Contact ? DocumentCodec.Contact(remote ?? original).Labels : [];
            var missing = needed.Where(id => !labels.ContainsKey(id)).ToDictionary(id => id, id => p.Groups.GetValueOrDefault(id)?["name"]?.ToString() ?? "Неизвестный ярлык");
            var rawRemote = (entry.Entity == EntityKind.Contact ? p.People : p.Groups).GetValueOrDefault(entry.EntityId);
            var handle = new SyncLocalEdit { EntityId = entry.EntityId, Kind = entry.Entity, Field = entry.Field, Root = p.Preview.Root, Name = entry.Name,
                LocalExists = local is not null, ResourceName = rawRemote?["resourceName"]?.ToString(), SourceId = entry.Entity == EntityKind.Contact && rawRemote is not null ? PersonCodec.SourceId(rawRemote) : null,
                Document = (JsonObject)original.DeepClone(), GoogleDocument = (JsonObject?)remote?.DeepClone(), PhotoBytes = (local?.PhotoBytes ?? (local is null ? p.Photos.GetValueOrDefault(entry.EntityId) : null))?.ToArray(),
                GooglePhotoBytes = p.Photos.GetValueOrDefault(entry.EntityId)?.ToArray(), Labels = labels, MissingLabels = missing,
                LabelResources = p.Groups.ToDictionary(g => g.Key, g => g.Value["resourceName"]!.ToString()), PreparedAt = p.Preview.PreparedAt };
            localEdits.Clear(); localEdits.Add(handle.Token, new(p, entry, handle, original, remote));
            return handle;
        }
        finally { _mutex.Release(); }
    }

    private RegisteredEdit Registered(SyncLocalEdit edit)
    {
        if (!localEdits.TryGetValue(edit.Token, out var registration) || !ReferenceEquals(registration.Handle, edit)
            || !_plans.TryGetValue(registration.Plan.Preview.PlanId, out var active) || !ReferenceEquals(active, registration.Plan))
            throw new SyncException("stalePlan", "Правка устарела. Черновик сохранён; откройте изменение после новой проверки.");
        return registration;
    }

    private static async Task<LocalEntity?> CheckLocalEditAsync(RegisteredEdit edit, IWorkspaceSession session, CancellationToken token)
    {
        var p = edit.Plan; var id = edit.Entry.EntityId;
        if (p.BindingHash is null || p.BindingHash != session.BindingHash || session.View.IsRecovery || session.View.Pending.Count > 0 || session.View.Issues.Count > 0
            || !JsonSemantics.Equal(JsonSerializer.SerializeToNode(p.View.States, JsonSemantics.Options), JsonSerializer.SerializeToNode(session.View.States, JsonSemantics.Options))
            || !p.View.Groups.OrderBy(x => x.Key).Select(x => (x.Key, x.Value.Hash)).SequenceEqual(session.View.Groups.OrderBy(x => x.Key).Select(x => (x.Key, x.Value.Hash))))
            throw new SyncException("localDrift", "Связь, ярлыки или состояние папки изменились. Обновите сравнение; черновик остаётся открытым.");
        var old = (edit.Entry.Entity == EntityKind.Contact ? p.View.Contacts : p.View.Groups).GetValueOrDefault(id);
        var current = await session.ReadLocalAsync(edit.Entry.Entity, id, token);
        if (current?.Hash != old?.Hash || current?.PhotoHash != old?.PhotoHash || current?.Path != old?.Path)
            throw new SyncException("localDrift", "Файл или фотография изменились после сравнения. Новая версия не перезаписана.");
        return current;
    }

    private static void ValidateLocalCandidate(RegisteredEdit edit, JsonObject output, ContactPhotoEdit photo, WorkspaceView view)
    {
        var e = edit.Entry; var original = edit.Original;
        var allowed = e.Field == "$entity" ? e.Entity == EntityKind.Contact ? new[] { "data", "labels", "starred" } : new[] { "name", "clientData" }
            : e.Entity == EntityKind.Contact && CapabilityRegistry.WritableFields.Contains(e.Field) ? new[] { "data" } : e.Field == "photo" ? Array.Empty<string>() : new[] { e.Field };
        foreach (var key in original.Select(x => x.Key).Union(output.Select(x => x.Key)).Where(key => !allowed.Contains(key)))
            if (original.ContainsKey(key) != output.ContainsKey(key) || !JsonNode.DeepEquals(original[key], output[key]))
                throw new SyncException("protectedField", "Правка затронула сведения вне выбранного раздела: " + key);
        if (photo.Changed && e.Field is not ("photo" or "$entity")) throw new SyncException("protectedField", "Фото не входит в выбранное изменение.");
        if (e.Entity == EntityKind.Contact)
        {
            var parsed = DocumentCodec.Contact(output);
            if (parsed.Labels.Any(id => !view.Groups.ContainsKey(id))) throw new SyncException("groupDependency", "Сначала сохраните недостающие ярлыки в папку.");
            if (CapabilityRegistry.WritableFields.Contains(e.Field))
            {
                var beforeData = original["data"] as JsonObject ?? new();
                foreach (var field in beforeData.Select(x => x.Key).Union(parsed.Data.Select(x => x.Key)).Where(x => x != e.Field))
                    if (beforeData.ContainsKey(field) != parsed.Data.ContainsKey(field) || !JsonNode.DeepEquals(beforeData[field], parsed.Data[field]))
                        throw new SyncException("protectedField", "Изменён другой раздел контакта.");
            }
            foreach (var pair in parsed.Data.Where(pair => !JsonNode.DeepEquals(original["data"]?[pair.Key], pair.Value) || e.Field == "$entity" && !view.Contacts.ContainsKey(e.EntityId)))
                if (CapabilityRegistry.ValidateEditable(pair.Key, pair.Value) is { Count: > 0 } errors) throw new SyncException("invalidField", string.Join("\n", errors));
        }
        else _ = DocumentCodec.Group(output);
    }

    public async Task<SyncLocalEditResult> SaveLocalEditAsync(SyncLocalEdit edit, JsonObject document, ContactPhotoEdit photo, CancellationToken token = default)
    {
        var output = (JsonObject)document.DeepClone(); photo = new(photo.Changed, photo.Bytes?.ToArray());
        await _mutex.WaitAsync(token);
        try
        {
            var registration = Registered(edit); var p = registration.Plan; var e = registration.Entry;
            await using var session = await store.OpenAsync(p.Preview.Root, p.Preview.Account, token);
            var current = await CheckLocalEditAsync(registration, session, token);
            ValidateLocalCandidate(registration, output, photo, session.View);
            if (e.Entity == EntityKind.Contact && !p.View.Contacts.ContainsKey(e.EntityId)) photo = new(true, photo.Changed ? photo.Bytes : p.Photos.GetValueOrDefault(e.EntityId)?.ToArray());
            var remote = (e.Entity == EntityKind.Contact ? p.People : p.Groups).GetValueOrDefault(e.EntityId);
            EntityState? initial = null;
            if (!p.View.States.ContainsKey(e.EntityId) && remote is not null)
            {
                initial = StateFor(p, e.EntityId, e.Entity, remote, registration.Remote!);
                initial.Baseline = (JsonObject)registration.Remote!.DeepClone();
                initial.PhotoLocalHash = initial.PhotoRemoteHash = p.Photos.GetValueOrDefault(e.EntityId) is { } bytes ? JsonSemantics.Hash(bytes) : null;
            }
            var run = await session.BeginRunAsync([new(e.Entity, e.EntityId, current?.Document, current?.PhotoBytes, remote, p.Photos.GetValueOrDefault(e.EntityId), p.View.States.GetValueOrDefault(e.EntityId), current?.RawBytes, current?.Path)], token);
            var payload = new JsonObject { ["document"] = output.DeepClone(), ["bindingHash"] = p.BindingHash, ["state"] = JsonSerializer.SerializeToNode(initial, JsonSemantics.Options), ["beforeHash"] = current?.Hash,
                ["stateBeforeHash"] = p.View.States.TryGetValue(e.EntityId, out var originalState) ? StateHash(originalState) : null,
                ["photoChanged"] = photo.Changed, ["photoHash"] = photo.Changed ? photo.Bytes is null ? null : JsonSemantics.Hash(photo.Bytes) : current?.PhotoHash };
            var operation = new JournalOperation(e.Key, e.EntityId, e.Entity, LocalEditField, "started", payload, remote?["resourceName"]?.ToString(), IntentPhoto: photo.Bytes);
            await session.RecordAsync(run, operation, CancellationToken.None);
            _plans.Clear(); localEdits.Clear();
            try
            {
                var saved = await session.WriteAsync(e.Entity, e.EntityId, output, current?.Hash, photo.Bytes, current?.PhotoHash, photo.Changed, token);
                if (initial is not null) await session.SaveStateAsync(initial, CancellationToken.None);
                await session.RecordAsync(run, operation with { Status = "committed" }, CancellationToken.None);
                await session.CompleteRunAsync(run, new(run, 1, 0, 0, [new(e.Key, "confirmed", "Сохранено только в папку.")]), CancellationToken.None);
                return new((JsonObject)saved.Document.DeepClone(), saved.PhotoBytes?.ToArray());
            }
            catch (Exception error)
            {
                await session.RecordAsync(run, operation with { Status = "unknown", Message = error.Message }, CancellationToken.None);
                throw new ContactEditorWriteException(ContactWriteOutcome.Uncertain, null, "Результат локальной записи требует проверки. Черновик и резервная копия сохранены; выполните новую проверку изменений.", error);
            }
        }
        finally { _mutex.Release(); }
    }

    public async Task SaveLocalLabelDependenciesAsync(SyncLocalEdit edit, CancellationToken token = default)
    {
        // Each step has its own backup and outcome. The caller must prepare a new contact target afterwards.
        await _mutex.WaitAsync(token);
        RegisteredEdit registration;
        try { registration = Registered(edit); } finally { _mutex.Release(); }
        var root = registration.Plan.Preview.Root; var account = registration.Plan.Preview.Account;
        var missing = DocumentCodec.Contact(registration.Remote ?? registration.Original).Labels.Where(id => !registration.Plan.View.Groups.ContainsKey(id))
            .Select(id => registration.Plan.Groups.GetValueOrDefault(id)?["resourceName"]?.ToString() ?? throw new SyncException("groupDependency", "Неизвестный источник ярлыка.")).ToArray();
        var savedCount = 0;
        try { foreach (var resource in missing)
        {
            var preview = await PrepareAsync(root, account, cancellationToken: token);
            var entry = preview.Entries.FirstOrDefault(e => e.Entity == EntityKind.Group && e.Google?["resourceName"]?.ToString() == resource && e.Field == "$entity");
            if (entry is null) throw new SyncException("groupDependency", "Ярлык изменился или требует ручного восстановления. Обновите сравнение.");
            var group = await OpenLocalEditAsync(preview, entry.Key, token);
            if (!group.CanReset) throw new SyncException("groupDependency", "Доверенная версия ярлыка Google отсутствует.");
            await SaveLocalEditAsync(group, group.Document, new(), token);
            savedCount++;
        }
        }
        catch (Exception error) when (savedCount > 0)
        { throw new SyncException("groupDependencyPartial", $"Сохранено ярлыков: {savedCount}. Контакт ещё не записан. Обновите сравнение и проверьте версии. {error.Message}", inner: error); }
    }

    private static async Task<bool> RecoverLocalEditsAsync(IWorkspaceSession session, CancellationToken token)
    {
        if (session.HasRecoveryFlag) return false;
        var changed = false;
        foreach (var operation in session.View.Pending.Where(p => p.Field == LocalEditField).DistinctBy(p => p.EntityId))
        {
            if (operation.Payload is not { } payload || payload["document"] is not JsonObject intended) continue;
            if (payload["bindingHash"]?.ToString() != session.BindingHash) continue;
            var current = await session.ReadLocalAsync(operation.Kind, operation.EntityId, token);
            if (payload["delete"]?.GetValue<bool>() == true)
            {
                var priorState = session.View.States.GetValueOrDefault(operation.EntityId);
                if ((priorState is null ? null : StateHash(priorState)) != payload["stateBeforeHash"]?.ToString()) continue;
                if (current is not null && current.Hash != payload["beforeHash"]?.ToString()) continue;
                await session.ResolvePendingAsync(operation.EntityId, LocalEditField, CancellationToken.None);
                await session.AcknowledgeRecoveryAsync(CancellationToken.None); changed = true; continue;
            }
            var initial = payload["state"]?.Deserialize<EntityState>(JsonSemantics.Options);
            var state = session.View.States.GetValueOrDefault(operation.EntityId);
            if (initial is null && (state is null ? null : StateHash(state)) != payload["stateBeforeHash"]?.ToString()) continue;
            var expected = (JsonObject)intended.DeepClone();
            if (operation.Kind == EntityKind.Contact) expected["photo"] = current?.Document["photo"]?.DeepClone();
            var matches = current is not null && JsonNode.DeepEquals(expected, current.Document) && current.PhotoHash == payload["photoHash"]?.ToString();
            if (matches && initial is not null)
            {
                if (initial.Id != operation.EntityId || initial.Kind != operation.Kind || initial.ResourceName != operation.ResourceName
                    || !JsonNode.DeepEquals(initial.Baseline["google"], intended["google"]) || state is not null && StateHash(state) != StateHash(initial)) continue;
                if (state is null) await session.SaveStateAsync(initial, CancellationToken.None);
            }
            var unchanged = current?.Hash == payload["beforeHash"]?.ToString() && (initial is null || current is null && state is null);
            if (!matches && !unchanged) continue;
            await session.ResolvePendingAsync(operation.EntityId, LocalEditField, CancellationToken.None);
            await session.AcknowledgeRecoveryAsync(CancellationToken.None); changed = true;
        }
        return changed;
    }

    public async Task DeleteLocalEditAsync(SyncLocalEdit edit, CancellationToken token = default)
    {
        await _mutex.WaitAsync(token);
        try
        {
            var registration = Registered(edit); var p = registration.Plan; var e = registration.Entry;
            if (registration.Remote is not null) throw new SyncException("deleteUnavailable", "Google-версия существует; используйте сброс значения.");
            await using var session = await store.OpenAsync(p.Preview.Root, p.Preview.Account, token);
            var current = await CheckLocalEditAsync(registration, session, token) ?? throw new SyncException("missingLocal", "Файл отсутствует.");
            if (e.Entity == EntityKind.Group && session.View.Contacts.Values.Any(c => DocumentCodec.Contact(c.Document).Labels.Contains(e.EntityId)))
                throw new SyncException("groupDependency", "Ярлык ещё используется контактами. Сначала снимите его в карточках.");
            var run = await session.BeginRunAsync([new(e.Entity, e.EntityId, current.Document, current.PhotoBytes, null, null, p.View.States.GetValueOrDefault(e.EntityId), current.RawBytes, current.Path)], token);
            var operation = new JournalOperation(e.Key, e.EntityId, e.Entity, LocalEditField, "started", new JsonObject { ["document"] = current.Document.DeepClone(), ["bindingHash"] = p.BindingHash, ["beforeHash"] = current.Hash, ["stateBeforeHash"] = p.View.States.TryGetValue(e.EntityId, out var oldState) ? StateHash(oldState) : null, ["delete"] = true });
            await session.RecordAsync(run, operation, CancellationToken.None);
            _plans.Clear(); localEdits.Clear();
            try
            {
                await session.TrashAsync(e.Entity, e.EntityId, current.Hash, run, token);
                await session.RecordAsync(run, operation with { Status = "committed" }, CancellationToken.None);
                await session.CompleteRunAsync(run, new(run, 1, 0, 0, [new(e.Key, "confirmed", "Файл перемещён в локальную корзину. Google не изменён.")]), CancellationToken.None);
            }
            catch (Exception error)
            {
                await session.RecordAsync(run, operation with { Status = "unknown", Message = error.Message }, CancellationToken.None);
                throw new ContactEditorWriteException(ContactWriteOutcome.Uncertain, null, "Результат перемещения в корзину требует проверки. Резервная копия сохранена.", error);
            }
        }
        finally { _mutex.Release(); }
    }
}
