using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application;

public sealed class SyncCoordinator(IGoogleContactsGateway gateway, IWorkspaceStore store, Action<Exception>? diagnostics = null) : ISyncCoordinator
{
    private readonly Dictionary<Guid, Prepared> _plans = [];
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private sealed class Prepared
    {
        public required WorkspaceView View { get; init; }
        public required SyncPreview Preview { get; set; }
        public Dictionary<Guid, JsonObject> People { get; } = [];
        public Dictionary<Guid, JsonObject> Groups { get; } = [];
        public Dictionary<string, Guid> GroupIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, byte[]?> Photos { get; } = [];
        public Dictionary<Guid, JsonObject> Restored { get; } = [];
        public Dictionary<Guid, LocalEntity?> WorkingLocal { get; } = [];
        public Dictionary<Guid, EntityState> WorkingStates { get; } = [];
        public HashSet<string> ConfirmedWrites { get; } = [];
        public Dictionary<Guid, byte[]?> RestoredPhotos { get; } = [];
        public Dictionary<Guid, byte[]?> RemoteRestorePhotos { get; } = [];
        public bool UnsafeRemoteIdentity { get; set; }
        public Dictionary<Guid, JsonArray> ReadOnlyImages { get; } = [];
    }

    public async Task<SyncPreview> PrepareAsync(string root, AccountIdentity account, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            progress?.Report(new("Проверяем папку и состояние…"));
            await using var session = await store.OpenAsync(root, account, cancellationToken);
            var remote = await gateway.ReadAllAsync(progress, cancellationToken);
            var p = await BuildAsync(session, account, remote, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _plans.Clear(); _plans.Add(p.Preview.PlanId, p);
            return p.Preview;
        }
        finally { _mutex.Release(); }
    }

    private async Task<Prepared> BuildAsync(IWorkspaceSession session, AccountIdentity account, RemoteSnapshot remote, IProgress<SyncProgress>? progress, CancellationToken token)
    {
        var view = session.View;
        var entries = new List<SyncEntry>();
        var notices = view.Issues.Select(i => $"{Path.GetFileName(i.Path)}: {i.Message}").ToList();
        if (view.IsRecovery) notices.Add("Восстановление состояния: проверьте связи с Google. Удаления отключены до согласования папки.");
        if (view.Pending.Count > 0) notices.Add("Есть незавершённые операции. Их результат будет проверен до повторной записи.");
        var p = new Prepared { View = view, Preview = new() { Root = session.Root, Account = account, Entries = [] } };
        foreach (var pendingPhoto in view.Pending.Where(o => o.Field == "photo" && o.IntentPhoto is not null)) p.RestoredPhotos[pendingPhoto.EntityId] = pendingPhoto.IntentPhoto;
        var states = view.States;
        foreach (var (id, state) in states) p.WorkingStates[id] = state;
        // Preserve first-match identity semantics without rescanning the whole workspace per person.
        var groupStates = new Dictionary<string, EntityState>(StringComparer.Ordinal);
        var contactStates = new Dictionary<string, EntityState>(StringComparer.Ordinal);
        foreach (var state in states.Values)
        {
            token.ThrowIfCancellationRequested();
            if (state.Kind == EntityKind.Group) groupStates.TryAdd(state.ResourceName, state);
            else if (state.SourceId is { } sourceId) contactStates.TryAdd(sourceId, state);
        }
        var recoveredGroups = new Dictionary<string, LocalEntity>(StringComparer.Ordinal);
        foreach (var local in view.Groups.Values)
            if (local.Document["google"]?["resourceName"]?.GetValue<string>() is { } resource) recoveredGroups.TryAdd(resource, local);
        var recoveredContacts = new Dictionary<string, LocalEntity>(StringComparer.Ordinal);
        foreach (var local in view.Contacts.Values)
        {
            token.ThrowIfCancellationRequested();
            if (local.Document["google"]?["person"] is JsonObject raw && TrySource(raw) is { } sourceId) recoveredContacts.TryAdd(sourceId, local);
        }
        var pendingGroups = new Dictionary<string, JournalOperation>(StringComparer.Ordinal);
        var pendingContacts = new Dictionary<string, JournalOperation>(StringComparer.Ordinal);
        foreach (var pending in view.Pending)
            if (pending.ResourceName is { } resource) (pending.Kind == EntityKind.Group ? pendingGroups : pendingContacts).TryAdd(resource, pending);
        var mapped = new HashSet<Guid>();
        foreach (var group in remote.Groups.Where(g => g["groupType"]?.GetValue<string>() == "USER_CONTACT_GROUP"))
        {
            token.ThrowIfCancellationRequested();
            var resource = group["resourceName"]?.GetValue<string>() ?? throw new SyncException("invalidGroup", "Google вернул ярлык без идентификатора.");
            var existing = groupStates.GetValueOrDefault(resource);
            var recovered = recoveredGroups.GetValueOrDefault(resource);
            var pending = pendingGroups.GetValueOrDefault(resource);
            var id = existing?.Id ?? recovered?.Id ?? pending?.EntityId ?? Guid.NewGuid();
            if (!mapped.Add(id)) throw new SyncException("identityCollision", "Несколько ярлыков Google указывают на одну локальную запись.");
            p.Groups[id] = (JsonObject)group.DeepClone(); p.GroupIds[resource] = id;
        }
        foreach (var pair in states.Where(x => x.Value.Kind == EntityKind.Group)) p.GroupIds.TryAdd(pair.Value.ResourceName, pair.Key);
        mapped.Clear();
        foreach (var person in remote.People)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var source = PersonCodec.SourceId(person);
                var resource = PersonCodec.Resource(person);
                var existing = contactStates.GetValueOrDefault(source);
                var recovered = recoveredContacts.GetValueOrDefault(source);
                var pending = pendingContacts.GetValueOrDefault(resource);
                var id = existing?.Id ?? recovered?.Id ?? pending?.EntityId ?? Guid.NewGuid();
                if (!mapped.Add(id)) throw new SyncException("identityCollision", "Google объединил несколько записей. Проверьте связь контактов вручную.");
                p.People[id] = (JsonObject)person.DeepClone();
            }
            catch (SyncException ex)
            {
                p.UnsafeRemoteIdentity = true;
                var id = Guid.NewGuid();
                entries.Add(new() { Key = $"Contact:{id}:blocked", EntityId = id, Entity = EntityKind.Contact, Name = "Контакт с неоднозначным источником", Field = "$entity", Kind = ChangeKind.Blocked, Google = person.DeepClone(), Explanation = ex.Message });
            }
        }
        var photoCount = 0;
        foreach (var (id, person) in p.People)
        {
            token.ThrowIfCancellationRequested();
            var url = PersonCodec.PhotoUrl(person);
            p.Photos[id] = url is null ? null : await gateway.DownloadPhotoAsync(url, token);
            var readOnly = new JsonArray();
            foreach (var field in new[] { "photos", "coverPhotos" })
                foreach (var value in (person[field] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    if (value["default"]?.GetValue<bool>() == true || field == "photos" && PersonCodec.IsContactValue(value, person) || value["url"] is not JsonValue imageUrl) continue;
                    var sourceUrl = imageUrl.GetValue<string>();
                    var image = await gateway.DownloadPhotoAsync(sourceUrl, token);
                    var cached = await session.CacheImageAsync(image, sourceUrl, token);
                    readOnly.Add(new JsonObject { ["field"] = field, ["url"] = sourceUrl, ["path"] = cached, ["sha256"] = JsonSemantics.Hash(image) });
                }
            if (readOnly.Count > 0) p.ReadOnlyImages[id] = readOnly;
            progress?.Report(new("Читаем фотографии…", ++photoCount, p.People.Count));
        }
        foreach (var id in view.Groups.Keys.Concat(states.Values.Where(s => s.Kind == EntityKind.Group).Select(s => s.Id)).Concat(p.Groups.Keys).Concat(view.Pending.Where(o => o.Kind == EntityKind.Group).Select(o => o.EntityId)).Distinct())
        {
            token.ThrowIfCancellationRequested();
            BuildGroup(p, id, entries);
        }
        foreach (var id in view.Contacts.Keys.Concat(states.Values.Where(s => s.Kind == EntityKind.Contact).Select(s => s.Id)).Concat(p.People.Keys).Concat(view.Pending.Where(o => o.Kind == EntityKind.Contact).Select(o => o.EntityId)).Distinct())
        {
            token.ThrowIfCancellationRequested();
            BuildContact(p, id, entries);
        }
        foreach (var pending in view.Pending.DistinctBy(o => (o.EntityId, o.Field)))
        {
            var local = pending.Kind == EntityKind.Contact ? view.Contacts.GetValueOrDefault(pending.EntityId) : view.Groups.GetValueOrDefault(pending.EntityId);
            var currentRemote = pending.Kind == EntityKind.Contact ? p.People.GetValueOrDefault(pending.EntityId) : p.Groups.GetValueOrDefault(pending.EntityId);
            if (local is null || currentRemote is null || entries.Any(e => e.EntityId == pending.EntityId && (e.Field == pending.Field || e.Kind == ChangeKind.Blocked))) continue;
            entries.Add(EntityEntry(pending.EntityId, pending.Kind, "Сверить незавершённую операцию", ChangeKind.Reconcile, local.Document, currentRemote, "Текущее состояние будет повторно проверено, после чего завершится только этот пункт журнала.", "$journal:" + pending.Field));
        }
        var candidates = entries.Where(e => e.Field.StartsWith("$relink:", StringComparison.Ordinal)).Select(e => e.Field[8..]).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Kind == ChangeKind.CreateLocal && entry.Google?["resourceName"] is JsonValue resource && candidates.Contains(resource.GetValue<string>()))
                entries[index] = EntityEntry(entry.EntityId, entry.Entity, entry.Name, ChangeKind.Blocked, entry.Local, entry.Google, "Возможный результат незавершённого создания. Сначала выберите связь или явный повтор для исходного локального файла.");
        }
        foreach (var issue in view.Issues)
        {
            var id = issue.Id ?? Guid.NewGuid();
            entries.Add(new() { Key = $"issue:{Guid.NewGuid()}", EntityId = id, Entity = EntityKind.Contact, Name = Path.GetFileName(issue.Path), Field = "$file", Kind = ChangeKind.Blocked, Explanation = issue.Message });
        }
        p.Preview = new() { Root = session.Root, Account = account, Entries = entries, Notices = notices, ContactCount = p.People.Count, GroupCount = p.Groups.Count, IsRecovery = view.IsRecovery || view.Pending.Count > 0 };
        token.ThrowIfCancellationRequested();
        return p;
    }
    private static string? TrySource(JsonObject person) { try { return PersonCodec.SourceId(person); } catch (SyncException) { return null; } }
    private static JsonObject? PersonFrom(LocalEntity? local) => local?.Document["google"]?["person"] as JsonObject;
    private static JsonNode EmptyArray(JsonNode? value) => value ?? new JsonArray();
    private static JsonNode EmptyFalse(JsonNode? value) => value ?? JsonValue.Create(false)!;
    private static bool ReadOnlyModified(LocalEntity? local, EntityState? state) => local is not null && state is not null && !JsonSemantics.Equal(local.Document["google"], state.Baseline["google"]);
    private static bool BlockedEntity(Prepared p, Guid id)
    {
        if (p.View.Issues.Any(i => i.Id == id)) return true;
        var local = p.View.Contacts.GetValueOrDefault(id) ?? p.View.Groups.GetValueOrDefault(id);
        if (!ReadOnlyModified(local, p.View.States.GetValueOrDefault(id))) return false;
        var remote = p.People.TryGetValue(id, out var person) ? SnapshotFor(p, id, person) : p.Groups.GetValueOrDefault(id);
        return !p.View.Pending.Any(o => o.EntityId == id && o.ResourceName == remote?["resourceName"]?.GetValue<string>()) || !JsonSemantics.Equal(local?.Document["google"], remote, true);
    }

    private static SyncEntry EntityEntry(Guid id, EntityKind entity, string name, ChangeKind kind, JsonNode? local, JsonNode? remote, string? message = null, string field = "$entity") => new()
    { Key = $"{entity}:{id}:{field}", EntityId = id, Entity = entity, Name = name, Field = field, Kind = kind, Local = JsonSemantics.Clone(local), Google = JsonSemantics.Clone(remote), Explanation = message };

    private void BuildGroup(Prepared p, Guid id, List<SyncEntry> entries)
    {
        var local = p.View.Groups.GetValueOrDefault(id); var state = p.View.States.GetValueOrDefault(id); var remote = p.Groups.GetValueOrDefault(id);
        var name = local?.Document["name"]?.GetValue<string>() ?? remote?["name"]?.GetValue<string>() ?? state?.Baseline["name"]?.GetValue<string>() ?? "Ярлык";
        if (BlockedEntity(p, id)) { entries.Add(EntityEntry(id, EntityKind.Group, name, ChangeKind.Blocked, local?.Document, remote, "Файл изменяет служебные данные Google или не прошёл проверку.")); return; }
        if (BuildRecoveryCreate(p, id, EntityKind.Group, name, local, entries)) return;
        if (local is null || remote is null)
        {
            BuildMissing(p, id, EntityKind.Group, name, local, state, remote, entries); return;
        }
        foreach (var field in new[] { "name", "clientData" })
        {
            var baseline = state?.Baseline[field] ?? local.Document["google"]?[field];
            var entry = ThreeWayPlanner.Field(id, EntityKind.Group, name, field, field == "clientData" ? EmptyArray(baseline) : baseline, field == "clientData" ? EmptyArray(local.Document[field]) : local.Document[field], field == "clientData" ? EmptyArray(remote[field]) : remote[field]);
            if (entry is not null) entries.Add(entry);
        }
        if (!JsonSemantics.Equal(local.Document["google"], remote, true)) entries.Add(EntityEntry(id, EntityKind.Group, name, ChangeKind.Snapshot, local.Document["google"], remote, field: "$snapshot"));
        if (state is null && !entries.Any(e => e.EntityId == id)) entries.Add(EntityEntry(id, EntityKind.Group, name, ChangeKind.Reconcile, local.Document, remote, "Подтвердить связь ярлыка с Google.", "$binding"));
    }

    private void BuildContact(Prepared p, Guid id, List<SyncEntry> entries)
    {
        var local = p.View.Contacts.GetValueOrDefault(id); var state = p.View.States.GetValueOrDefault(id); var remote = p.People.GetValueOrDefault(id);
        var localDoc = local is null ? null : DocumentCodec.Contact(local.Document);
        var name = localDoc is not null ? PersonCodec.DisplayName(localDoc) : remote is not null ? PersonCodec.DisplayName(new() { Id = id, Data = PersonCodec.Data(remote), Google = PersonCodec.Snapshot(remote) }) : "Контакт";
        if (BlockedEntity(p, id)) { entries.Add(EntityEntry(id, EntityKind.Contact, name, ChangeKind.Blocked, local?.Document, remote, "Файл изменяет данные раздела google. Верните эту часть из резервной копии, сохранив правки data.")); return; }
        if (BuildRecoveryCreate(p, id, EntityKind.Contact, name, local, entries)) return;
        if (local is null || remote is null) { BuildMissing(p, id, EntityKind.Contact, name, local, state, remote, entries); return; }
        var remoteDoc = ContactFromRemote(p, id, remote, localDoc!);
        var baseline = state?.Baseline ?? RecoveryBaseline(p, id, local!);
        foreach (var field in CapabilityRegistry.WritableFields)
        {
            // Missing categories and explicit empty arrays both mean an empty category.
            if ((localDoc!.Data[field] is null or JsonArray { Count: 0 }) &&
                (remoteDoc.Data[field] is null or JsonArray { Count: 0 }) &&
                (baseline?["data"]?[field] is null or JsonArray { Count: 0 })) continue;
            var l = EmptyArray(localDoc!.Data[field]); var r = EmptyArray(remoteDoc.Data[field]); var b = EmptyArray(baseline?["data"]?[field]);
            var changed = ThreeWayPlanner.Compare(b, l, r);
            if (changed is null) continue;
            string? block = null;
            if (changed is ChangeKind.Upload or ChangeKind.Conflict)
            {
                var unknown = CapabilityRegistry.UnknownReplacementFields(field, PersonCodec.ContactValues(remote, field));
                if (unknown.Count > 0) block = string.Join("\n", unknown);
            }
            var entry = ThreeWayPlanner.Field(id, EntityKind.Contact, name, field, b, l, r, block);
            if (entry is not null) entries.Add(entry);
        }
        var labels = new JsonArray(remoteDoc.Labels.Select(label => (JsonNode?)JsonValue.Create(label.ToString())).ToArray());
        var unknownLabels = localDoc!.Labels.Where(g => !p.Groups.ContainsKey(g) && !p.View.Groups.ContainsKey(g) && !p.View.States.ContainsKey(g)).ToArray();
        var labelEntry = ThreeWayPlanner.Field(id, EntityKind.Contact, name, "labels", EmptyArray(baseline?["labels"]), EmptyArray(local.Document["labels"]), EmptyArray(labels), unknownLabels.Length > 0 ? "Указан ярлык, который не существует в папке или Google." : null);
        if (labelEntry is not null) entries.Add(labelEntry);
        var starEntry = ThreeWayPlanner.Field(id, EntityKind.Contact, name, "starred", EmptyFalse(baseline?["starred"]), JsonValue.Create(localDoc.Starred), JsonValue.Create(remoteDoc.Starred));
        if (starEntry is not null) entries.Add(starEntry);
        var remoteHash = p.Photos.GetValueOrDefault(id) is { } bytes ? JsonSemantics.Hash(bytes) : null;
        // A queued image cannot replace a newer edit to the real local file after identity reconciliation.
        var queuedPhoto = p.View.Pending.LastOrDefault(o => o.EntityId == id && o.Field == "photo" && o.IntentPhoto is not null);
        if (queuedPhoto is not null && (queuedPhoto.HasIntentInput ? local.PhotoHash != queuedPhoto.IntentInputPhotoHash : local.PhotoHash is not null)) p.RestoredPhotos.Remove(id);
        var plannedPhotoHash = p.RestoredPhotos.TryGetValue(id, out var intentPhoto) ? intentPhoto is null ? null : JsonSemantics.Hash(intentPhoto) : local.PhotoHash;
        var localChanged = plannedPhotoHash != state?.PhotoLocalHash;
        var remoteChanged = remoteHash != state?.PhotoRemoteHash;
        if (state is null) { localChanged = plannedPhotoHash is not null; remoteChanged = remoteHash is not null; }
        ChangeKind? photoKind = !localChanged && !remoteChanged ? null : plannedPhotoHash == remoteHash ? ChangeKind.Reconcile : !localChanged ? ChangeKind.Download : !remoteChanged ? ChangeKind.Upload : ChangeKind.Conflict;
        if (queuedPhoto is { HasIntentInput: false } && p.RestoredPhotos.ContainsKey(id) && plannedPhotoHash != remoteHash) photoKind = ChangeKind.Conflict;
        if (photoKind is not null) entries.Add(EntityEntry(id, EntityKind.Contact, name, photoKind.Value, JsonValue.Create(queuedPhoto is not null && p.RestoredPhotos.ContainsKey(id) ? "Фото из журнала · SHA-256: " + plannedPhotoHash : localDoc.Photo), JsonValue.Create(PersonCodec.PhotoUrl(remote)), queuedPhoto is not null && p.RestoredPhotos.ContainsKey(id) ? "Из папки: фотография, сохранённая в журнале незавершённого создания. Из Google: текущее изображение сервера." : "Сравнивается текущее содержимое файла фотографии, включая новую правку после восстановления связи.", "photo"));
        if (!JsonSemantics.Equal(local.Document["google"], remoteDoc.Google, true)) entries.Add(EntityEntry(id, EntityKind.Contact, name, ChangeKind.Snapshot, local.Document["google"], remoteDoc.Google, field: "$snapshot"));
        if (state is null && !entries.Any(e => e.EntityId == id)) entries.Add(EntityEntry(id, EntityKind.Contact, name, ChangeKind.Reconcile, local.Document, remote, "Подтвердить связь контакта с Google.", "$binding"));
    }

    private bool BuildRecoveryCreate(Prepared p, Guid id, EntityKind kind, string name, LocalEntity? local, List<SyncEntry> entries)
    {
        var pending = p.View.Pending.FirstOrDefault(o => o.EntityId == id && o.Status is "started" or "unknown" && (o.CreateIntent || o.Field is "$entity" or "$retry" or "$restoreCreate"));
        var mappedRemote = kind == EntityKind.Contact ? p.People.GetValueOrDefault(id) : p.Groups.GetValueOrDefault(id);
        if (mappedRemote is not null && p.View.Pending.Any(o => o.EntityId == id && o.Status == "confirmed" && o.Field.StartsWith("$relink:", StringComparison.Ordinal) && o.ResourceName == mappedRemote["resourceName"]?.GetValue<string>())) return false;
        var desired = local?.Document ?? pending?.Payload;
        if (pending is null || pending.ResourceName is not null || desired is null) return false;
        if (local is null) { p.Restored[id] = (JsonObject)desired.DeepClone(); p.RestoredPhotos[id] = pending.IntentPhoto; }
        var candidates = kind == EntityKind.Contact ? p.People.Where(x => !p.View.States.ContainsKey(x.Key) && LooksSimilar(desired, x.Value)).ToArray() : p.Groups.Where(x => !p.View.States.ContainsKey(x.Key) && x.Value["name"]?.ToString() == desired["name"]?.ToString()).ToArray();
        foreach (var (_, candidate) in candidates)
        {
            var resource = candidate["resourceName"]!.GetValue<string>();
            entries.Add(EntityEntry(id, kind, name, ChangeKind.Conflict, desired, candidate, "Результат создания неизвестен. Эта запись — возможный результат; выберите Google только если это нужный контакт. Связь не устанавливается автоматически.", "$relink:" + resource));
        }
        entries.Add(EntityEntry(id, kind, name, ChangeKind.Conflict, desired, null, "Предыдущий запрос мог выполниться. Повторное создание может дать дубликат. Выбор «Из папки» явно создаст ещё одну запись.", "$retry"));
        return true;
    }
    private static bool LooksSimilar(JsonObject local, JsonObject remote)
    {
        var data = local["data"] as JsonObject ?? new(); var r = PersonCodec.Data(remote);
        return new[] { "names", "emailAddresses", "phoneNumbers" }.Any(f => data[f] is JsonArray { Count: > 0 } && JsonSemantics.Equal(CapabilityRegistry.WritableProjection(f, data[f]), r[f]));
    }
    private void BuildMissing(Prepared p, Guid id, EntityKind kind, string name, LocalEntity? local, EntityState? state, JsonObject? remote, List<SyncEntry> entries)
    {
        if (local is null && remote is null)
        {
            if (state is not null && p.View.Pending.Any(o => o.EntityId == id)) entries.Add(EntityEntry(id, kind, name, ChangeKind.Reconcile, null, null, "Обе стороны отсутствуют. Проверить отсутствие записи Google и завершить незавершённое удаление.", "$completeAbsent"));
            return;
        }
        var unsafeDelete = p.View.IsRecovery || p.View.Pending.Count > 0 || p.View.Issues.Count > 0 || p.UnsafeRemoteIdentity || kind == EntityKind.Contact && p.View.Contacts.Count == 0 && p.View.States.Values.Any(s => s.Kind == EntityKind.Contact);
        if (local is null && state is null) { entries.Add(EntityEntry(id, kind, name, ChangeKind.CreateLocal, null, remote)); return; }
        if (remote is null && state is null && local!.Document["google"] is null)
        {
            var newDoc = kind == EntityKind.Contact ? DocumentCodec.Contact(local.Document) : null;
            var issue = newDoc is not null && !newDoc.Data.Any(x => x.Value is JsonArray { Count: > 0 }) ? "Новый контакт пока пуст. Заполните хотя бы одну категорию." : null;
            entries.Add(EntityEntry(id, kind, name, issue is null ? ChangeKind.CreateRemote : ChangeKind.Blocked, local.Document, null, issue)); return;
        }
        if (state is null)
        { entries.Add(EntityEntry(id, kind, name, ChangeKind.Conflict, local?.Document, remote, "Предыдущая связь не найдена. Выберите существующую сторону; удаления по отсутствию состояния отключены.")); return; }
        var changed = kind == EntityKind.Contact
            ? !JsonSemantics.Equal(local?.Document["data"] ?? (remote is null ? null : PersonCodec.Data(remote)), state.Baseline["data"])
            : !JsonSemantics.Equal(local?.Document["name"] ?? remote?["name"], state.Baseline["name"]) || !JsonSemantics.Equal(EmptyArray(local?.Document["clientData"] ?? remote?["clientData"]), EmptyArray(state.Baseline["clientData"]));
        if (kind == EntityKind.Contact)
        {
            var doc = remote is null ? null : ContactFromRemote(p, id, remote, new() { Id = id });
            changed |= !JsonSemantics.Equal(EmptyArray(local?.Document["labels"] ?? (doc is null ? null : DocumentCodec.ToJson(doc)["labels"])), EmptyArray(state.Baseline["labels"]));
            changed |= !JsonSemantics.Equal(EmptyFalse(local?.Document["starred"] ?? (doc is null ? null : JsonValue.Create(doc.Starred))), EmptyFalse(state.Baseline["starred"]));
            changed |= local is not null ? local.PhotoHash != state.PhotoLocalHash : (p.Photos.GetValueOrDefault(id) is { } photo ? JsonSemantics.Hash(photo) : null) != state.PhotoRemoteHash;
        }
        var kindOfChange = unsafeDelete ? ChangeKind.Blocked : changed ? ChangeKind.Conflict : local is null ? ChangeKind.DeleteRemote : ChangeKind.DeleteLocal;
        entries.Add(EntityEntry(id, kind, name, kindOfChange, local?.Document, remote, unsafeDelete ? "Удаление отключено: восстановление состояния или ошибки чтения файлов. Сначала устраните ошибки." : changed ? "Контакт удалён с одной стороны, а с другой изменён. Выберите сторону, которую нужно сохранить." : "Удаление требует явного выбора. Восстановление в Google может создать новый идентификатор."));
    }
    private static JsonObject? RecoveryBaseline(Prepared p, Guid id, LocalEntity local)
    {
        var person = PersonFrom(local);
        return person is null ? null : DocumentCodec.ToJson(ContactFromRemote(p, id, person, new() { Id = id }));
    }
    private static ContactDocument ContactFromRemote(Prepared p, Guid id, JsonObject remote, ContactDocument preserve)
    {
        var memberships = PersonCodec.Memberships(remote);
        return new() { Id = id, Data = PersonCodec.Data(remote), Extensions = (JsonObject)preserve.Extensions.DeepClone(), Photo = preserve.Photo, Labels = memberships.Where(p.GroupIds.ContainsKey).Select(g => p.GroupIds[g]).Distinct().Order().ToList(), Starred = memberships.Contains("contactGroups/starred"), Google = SnapshotFor(p, id, remote) };
    }
    private static JsonObject SnapshotFor(Prepared p, Guid id, JsonObject person)
    {
        var snapshot = PersonCodec.Snapshot(person);
        if (p.ReadOnlyImages.TryGetValue(id, out var images)) snapshot["readOnlyImages"] = images.DeepClone();
        return snapshot;
    }
    private static GroupDocument GroupFromRemote(Guid id, JsonObject remote, GroupDocument? preserve = null) => new() { Id = id, Name = remote["name"]!.GetValue<string>(), ClientData = (JsonArray?)remote["clientData"]?.DeepClone() ?? [], Extensions = (JsonObject?)preserve?.Extensions.DeepClone() ?? new(), Google = (JsonObject)remote.DeepClone() };

    private static JsonArray MembershipPayload(Prepared p, IReadOnlyList<Guid> labels, bool starred, IReadOnlyList<string> systemGroups)
    {
        var resources = labels.Select(id => p.Groups.TryGetValue(id, out var group) ? group["resourceName"]!.GetValue<string>() : throw new SyncException("groupDependency", "Один из ярлыков ещё не создан.")).Concat(systemGroups).ToHashSet(StringComparer.Ordinal);
        if (starred) resources.Add("contactGroups/starred");
        return new JsonArray(resources.Order(StringComparer.Ordinal).Select(resource => (JsonNode)new JsonObject { ["contactGroupMembership"] = new JsonObject { ["contactGroupResourceName"] = resource } }).ToArray());
    }

    public async Task<SyncRunResult> ApplyAsync(SyncPreview preview, IReadOnlyList<PlanChoice> choices, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!_plans.Remove(preview.PlanId, out var p) || p.Preview.Root != preview.Root || p.Preview.Account.Key != preview.Account.Key) throw new SyncException("stalePlan", "Этот план устарел. Проверьте изменения ещё раз.");
            var choiceMap = choices.ToDictionary(c => c.Key, StringComparer.Ordinal);
            var selected = p.Preview.Entries.Where(e => choiceMap.ContainsKey(e.Key) && choiceMap[e.Key].Resolution != Resolution.Skip).ToArray();
            if (selected.Length != choiceMap.Count || selected.Any(e => !e.IsSelectable)) throw new SyncException("invalidSelection", "Выбор содержит неподдерживаемые или устаревшие изменения.");
            foreach (var entity in selected.GroupBy(e => e.EntityId))
                if (entity.Count(e => e.Field.StartsWith("$relink:") || e.Field == "$retry") > 1) throw new SyncException("invalidSelection", "Для одной незавершённой записи выберите только один вариант восстановления.");
            if (selected.Any(e => e.Kind == ChangeKind.Conflict && choiceMap[e.Key].Resolution == Resolution.Automatic)) throw new SyncException("unresolvedConflict", "Для конфликтных изменений нужно выбрать сторону.");
            await using var session = await store.OpenAsync(preview.Root, preview.Account, cancellationToken);
            await ValidateInputsAsync(p, session, selected, choiceMap, progress, cancellationToken);
            var items = selected.Select(e => (e.EntityId, e.Entity)).Distinct().Select(pair =>
            {
                var local = pair.Entity == EntityKind.Contact ? p.View.Contacts.GetValueOrDefault(pair.EntityId) : p.View.Groups.GetValueOrDefault(pair.EntityId);
                var remote = pair.Entity == EntityKind.Contact ? p.People.GetValueOrDefault(pair.EntityId) : p.Groups.GetValueOrDefault(pair.EntityId);
                return new BackupItem(pair.Entity, pair.EntityId, local?.Document, local?.PhotoBytes, remote, p.Photos.GetValueOrDefault(pair.EntityId), p.View.States.GetValueOrDefault(pair.EntityId), local?.RawBytes, local?.Path);
            }).ToList();
            foreach (var deletion in selected.Where(e => e.Entity == EntityKind.Group && e.DeletesRemote(choiceMap[e.Key].Resolution)))
            {
                var resource = p.Groups[deletion.EntityId]["resourceName"]!.GetValue<string>();
                foreach (var affected in p.People.Where(x => PersonCodec.Memberships(x.Value).Contains(resource)))
                    if (!items.Any(i => i.Id == affected.Key))
                    {
                        var local = p.View.Contacts.GetValueOrDefault(affected.Key);
                        items.Add(new(EntityKind.Contact, affected.Key, local?.Document, local?.PhotoBytes, affected.Value, p.Photos.GetValueOrDefault(affected.Key), p.View.States.GetValueOrDefault(affected.Key), local?.RawBytes, local?.Path));
                    }
            }
            progress?.Report(new("Сохраняем резервную копию…"));
            var runId = await session.BeginRunAsync(items, cancellationToken);
            var results = new List<OperationResult>();
            var ordered = selected.OrderBy(e => e.Entity == EntityKind.Group && e.DeletesRemote(choiceMap[e.Key].Resolution) ? 4 : e.Entity == EntityKind.Group ? 0 : e.Kind == ChangeKind.CreateLocal ? 1 : 2).ThenBy(e => e.EntityId).ThenBy(e => e.Field.StartsWith("$journal:", StringComparison.Ordinal) ? 2 : e.Field == "$snapshot" ? 1 : 0).ToArray();
            foreach (var entry in ordered)
            {
                if (cancellationToken.IsCancellationRequested) break;
                progress?.Report(new($"{entry.Name}: {CapabilityRegistry.Title(entry.Field)}", results.Count, ordered.Length));
                var createIntent = CreatesRemote(p, entry, choiceMap[entry.Key].Resolution);
                var intentPhoto = entry.Field == "$restoreCreate" && choiceMap[entry.Key].Resolution == Resolution.UseGoogle ? p.RemoteRestorePhotos.GetValueOrDefault(entry.EntityId) : p.RestoredPhotos.TryGetValue(entry.EntityId, out var recoveryPhoto) ? recoveryPhoto : p.WorkingLocal.GetValueOrDefault(entry.EntityId)?.PhotoBytes;
                var operation = new JournalOperation(entry.Key, entry.EntityId, entry.Entity, entry.Field, "started", PayloadFor(p, entry, choiceMap[entry.Key].Resolution), RemoteResource(p, entry), CreateIntent: createIntent, IntentPhoto: createIntent ? intentPhoto : null);
                await session.RecordAsync(runId, operation, CancellationToken.None);
                try
                {
                    await ApplyOneAsync(p, session, runId, entry, choiceMap[entry.Key].Resolution, cancellationToken);
                    await session.RecordAsync(runId, operation with { Status = "committed", ResourceName = RemoteResource(p, entry) }, CancellationToken.None);
                    var resolvedField = entry.Field.StartsWith("$journal:", StringComparison.Ordinal) ? entry.Field[9..] : entry.Field;
                    if (resolvedField.StartsWith("$relink:", StringComparison.Ordinal) || resolvedField == "$retry")
                    {
                        foreach (var pending in p.View.Pending.Where(o => o.EntityId == entry.EntityId && (o.CreateIntent || o.Field is "$entity" or "$retry" or "$restoreCreate"))) await session.ResolvePendingAsync(entry.EntityId, pending.Field, CancellationToken.None);
                        await session.ResolvePendingAsync(entry.EntityId, resolvedField, CancellationToken.None);
                    }
                    else await session.ResolvePendingAsync(entry.EntityId, resolvedField == "$completeAbsent" ? null : resolvedField, CancellationToken.None);
                    results.Add(new(entry.Key, "confirmed", $"{entry.Name} · {CapabilityRegistry.Title(entry.Field)}: выполнено"));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    diagnostics?.Invoke(ex);
                    var ambiguous = ex is SyncException { IsAmbiguous: true } || ex is OperationCanceledException && RemoteWriting(entry, choiceMap[entry.Key].Resolution);
                    var status = p.ConfirmedWrites.Contains(entry.Key) ? "confirmed" : ambiguous ? "unknown" : "failed";
                    // A confirmed remote write may precede a failed local commit. Keep its durable identity/result.
                    var resource = RemoteResource(p, entry);
                    await session.RecordAsync(runId, operation with { Status = status, ResourceName = resource, Message = SafeMessage(ex) }, CancellationToken.None);
                    results.Add(new(entry.Key, status == "confirmed" ? "failed" : status, $"{entry.Name} · {CapabilityRegistry.Title(entry.Field)}: {SafeMessage(ex)}"));
                    if (status == "confirmed") break;
                    if (ambiguous || ex is OperationCanceledException || ex is SyncException { Code: "localDrift" or "remoteDrift" or "authRequired" or "google-rate-limit" }) break;
                }
            }
            var skipped = ordered.Length - results.Count;
            var run = new SyncRunResult(runId, results.Count(r => r.Status == "confirmed"), results.Count(r => r.Status == "failed") + skipped, results.Count(r => r.Status == "unknown"), results);
            await session.CompleteRunAsync(runId, run, CancellationToken.None);
            if (run.IsComplete && (!p.Preview.IsRecovery || p.Preview.Entries.All(e => selected.Any(s => s.Key == e.Key)))) await session.AcknowledgeRecoveryAsync(CancellationToken.None);
            return run;
        }
        finally { _mutex.Release(); }
    }

    private static string SafeMessage(Exception ex) => ex is SyncException ? ex.Message : ex is OperationCanceledException ? "Операция остановлена; проверьте результат перед повтором." : "Не удалось завершить операцию. Резервная копия сохранена; проверьте изменения ещё раз.";
    private static bool RemoteWriting(SyncEntry e, Resolution r) => e.Kind is ChangeKind.Upload or ChangeKind.CreateRemote or ChangeKind.DeleteRemote || e.Field.StartsWith("$restore:", StringComparison.Ordinal) || e.Field == "$restoreCreate" || e.Kind == ChangeKind.Conflict && r == Resolution.UseLocal;
    private static bool CreatesRemote(Prepared p, SyncEntry e, Resolution r) => e.Kind == ChangeKind.CreateRemote || e.Field is "$restoreCreate" or "$retry" || e.Kind == ChangeKind.Conflict && e.Field == "$entity" && e.Local is not null && RemoteResource(p, e) is null && r == Resolution.UseLocal;
    private static JsonObject? PayloadFor(Prepared p, SyncEntry entry, Resolution resolution) => entry.Field == "$restoreCreate" ? (resolution == Resolution.UseGoogle ? entry.Google : entry.Local) as JsonObject : p.Restored.GetValueOrDefault(entry.EntityId) ?? entry.Local as JsonObject ?? (p.View.Contacts.GetValueOrDefault(entry.EntityId) ?? p.View.Groups.GetValueOrDefault(entry.EntityId))?.Document;
    private static string? RemoteResource(Prepared p, SyncEntry e) => (e.Entity == EntityKind.Contact ? p.People.GetValueOrDefault(e.EntityId) : p.Groups.GetValueOrDefault(e.EntityId))?["resourceName"]?.GetValue<string>();

    private async Task ValidateInputsAsync(Prepared p, IWorkspaceSession session, IReadOnlyList<SyncEntry> selected, IReadOnlyDictionary<string, PlanChoice> choices, IProgress<SyncProgress>? progress, CancellationToken token)
    {
        // Google-to-folder writes use the snapshot the user has already reviewed.
        // Fresh reads protect remote writes, recovery and deletions only.
        static bool NeedsRemoteCheck(SyncEntry e, Resolution resolution) => RemoteWriting(e, resolution)
            || e.DeletesLocal(resolution) || e.Field == "$completeAbsent"
            || e.Field.StartsWith("$relink:", StringComparison.Ordinal) || e.Field.StartsWith("$journal:", StringComparison.Ordinal);
        RemoteSnapshot? fresh = null;
        Dictionary<string, JsonObject>? people = null, groups = null;
        if (selected.Where(e => NeedsRemoteCheck(e, choices[e.Key].Resolution)).Select(e => e.EntityId).Distinct().Take(20).Count() >= 20)
        {
            progress?.Report(new("Повторно проверяем каталог Google перед записью…"));
            fresh = await gateway.ReadAllAsync(progress, token);
            people = fresh.People.ToDictionary(PersonCodec.Resource, StringComparer.Ordinal);
            groups = fresh.Groups.ToDictionary(g => g["resourceName"]!.GetValue<string>(), StringComparer.Ordinal);
        }
        var relinks = selected.Where(e => e.Field.StartsWith("$relink:", StringComparison.Ordinal)).ToArray();
        if (relinks.GroupBy(e => e.Field[8..]).Any(g => g.Count() > 1)) throw new SyncException("identityCollision", "Одна запись Google выбрана для нескольких локальных файлов.");
        foreach (var relink in relinks)
        {
            var resource = relink.Field[8..];
            var actual = relink.Entity == EntityKind.Contact ? await gateway.GetPersonAsync(resource, token) : await gateway.GetGroupAsync(resource, token);
            if (!JsonSemantics.Equal(actual, relink.Google, true)) throw new SyncException("remoteDrift", "Кандидат восстановления изменился. Проверьте изменения снова.");
        }
        foreach (var group in selected.GroupBy(e => (e.EntityId, e.Entity)))
        {
            var (id, kind) = group.Key;
            var old = kind == EntityKind.Contact ? p.View.Contacts.GetValueOrDefault(id) : p.View.Groups.GetValueOrDefault(id);
            var current = await session.ReadLocalAsync(kind, id, token);
            if (old?.Hash != current?.Hash || old?.PhotoHash != current?.PhotoHash) throw new SyncException("localDrift", "Файлы изменились после сравнения. Проверьте изменения заново.");
            p.WorkingLocal[id] = current;
            if (!group.Any(e => NeedsRemoteCheck(e, choices[e.Key].Resolution))) continue;
            var remote = kind == EntityKind.Contact ? p.People.GetValueOrDefault(id) : p.Groups.GetValueOrDefault(id);
            if (remote is not null)
            {
                var resource = remote["resourceName"]!.GetValue<string>();
                var actual = fresh is not null
                    ? (kind == EntityKind.Contact ? people! : groups!).GetValueOrDefault(resource)
                    : kind == EntityKind.Contact ? await gateway.GetPersonAsync(resource, token) : await gateway.GetGroupAsync(resource, token);
                if (!JsonSemantics.Equal(actual, remote, true) || kind == EntityKind.Contact && actual is not null && PersonCodec.Source(actual)["etag"]?.ToString() != PersonCodec.Source(remote)["etag"]?.ToString()) throw new SyncException("remoteDrift", "Данные Google изменились после сравнения. Новый план защитит обе версии.");
                if (kind == EntityKind.Contact && actual is not null)
                {
                    var url = PersonCodec.PhotoUrl(actual); var bytes = url is null ? null : await gateway.DownloadPhotoAsync(url, token);
                    if ((bytes is null ? null : JsonSemantics.Hash(bytes)) != (p.Photos.GetValueOrDefault(id) is { } previous ? JsonSemantics.Hash(previous) : null)) throw new SyncException("remoteDrift", "Фотография Google изменилась. Проверьте изменения снова.");
                }
            }
            else if (p.View.States.TryGetValue(id, out var state) && group.Any(e => e.DeletesLocal(choices[e.Key].Resolution) || e.Field == "$completeAbsent"))
            {
                var actual = kind == EntityKind.Contact ? await gateway.GetPersonAsync(state.ResourceName, token) : await gateway.GetGroupAsync(state.ResourceName, token);
                if (actual is not null) throw new SyncException("remoteDrift", "Запись Google снова доступна. Проверьте изменения до удаления файла.");
            }
        }
        foreach (var deletion in selected.Where(e => e.Entity == EntityKind.Group && e.DeletesRemote(choices[e.Key].Resolution)))
        {
            fresh ??= await gateway.ReadAllAsync(cancellationToken: token);
            var resource = p.Groups[deletion.EntityId]["resourceName"]!.GetValue<string>();
            var expected = p.People.Values.Where(x => PersonCodec.Memberships(x).Contains(resource)).Select(PersonCodec.SourceId).Order().ToArray();
            var actual = fresh.People.Where(x => PersonCodec.Memberships(x).Contains(resource)).Select(PersonCodec.SourceId).Order().ToArray();
            if (!expected.SequenceEqual(actual)) throw new SyncException("remoteDrift", "Состав удаляемого ярлыка изменился. Проверьте список участников снова.");
            foreach (var affected in p.People.Where(x => PersonCodec.Memberships(x.Value).Contains(resource)))
            {
                var current = await session.ReadLocalAsync(EntityKind.Contact, affected.Key, token);
                var old = p.View.Contacts.GetValueOrDefault(affected.Key);
                if (current?.Hash != old?.Hash || current?.PhotoHash != old?.PhotoHash) throw new SyncException("localDrift", "Файл участника ярлыка изменился. Проверьте изменения снова.");
                p.WorkingLocal[affected.Key] = current;
            }
        }
    }

    private async Task ApplyOneAsync(Prepared p, IWorkspaceSession session, Guid runId, SyncEntry e, Resolution resolution, CancellationToken token)
    {
        if (e.Field.StartsWith("$journal:", StringComparison.Ordinal))
        {
            await GetCurrentAsync(p, session, e, token);
            var resource = RemoteResource(p, e);
            if (!p.WorkingStates.TryGetValue(e.EntityId, out var state) || state.ResourceName != resource) throw new SyncException("recoveryPending", "Связь ещё не сохранена. Сначала согласуйте поля и снимок этой записи.");
            await EnsureRemoteAsync(p, e, token); return;
        }
        if (e.Field.StartsWith("$restore:", StringComparison.Ordinal))
        {
            var field = e.Field[9..];
            var desired = resolution == Resolution.UseGoogle ? e.Google : e.Local;
            var resolved = EntityEntry(e.EntityId, e.Entity, e.Name, ChangeKind.Upload, desired, null, field: field);
            resolved = new SyncEntry { Key = e.Key, EntityId = resolved.EntityId, Entity = resolved.Entity, Name = resolved.Name, Field = field, Kind = ChangeKind.Upload, Local = JsonSemantics.Clone(desired) };
            if (field == "photo" && resolution == Resolution.UseGoogle) p.RestoredPhotos[e.EntityId] = p.RemoteRestorePhotos.GetValueOrDefault(e.EntityId);
            if (e.Entity == EntityKind.Contact) await ApplyContactFieldAsync(p, session, runId, resolved, Resolution.Automatic, token);
            else await ApplyGroupFieldAsync(p, session, runId, resolved, Resolution.Automatic, token);
            return;
        }
        if (e.Field == "$restoreCreate")
        {
            p.Restored[e.EntityId] = (JsonObject)(resolution == Resolution.UseGoogle ? e.Google! : e.Local!).DeepClone();
            if (resolution == Resolution.UseGoogle) p.RestoredPhotos[e.EntityId] = p.RemoteRestorePhotos.GetValueOrDefault(e.EntityId);
            var resolved = new SyncEntry { Key = e.Key, EntityId = e.EntityId, Entity = e.Entity, Name = e.Name, Field = "$entity", Kind = ChangeKind.CreateRemote, Local = p.Restored[e.EntityId] };
            await ApplyEntityAsync(p, session, runId, resolved, Resolution.Automatic, token); return;
        }
        if (e.Field == "$completeAbsent")
        {
            await session.RemoveStateAsync(e.EntityId, token); p.WorkingStates.Remove(e.EntityId); return;
        }
        if (e.Field.StartsWith("$relink:", StringComparison.Ordinal))
        {
            if (resolution != Resolution.UseGoogle) throw new SyncException("recoveryChoice", "Для связи с найденной записью выберите «Из Google». Повторное создание — отдельный вариант.");
            var remote = e.Google!.AsObject();
            if (e.Entity == EntityKind.Contact) { p.People[e.EntityId] = (JsonObject)remote.DeepClone(); var url = PersonCodec.PhotoUrl(remote); p.Photos[e.EntityId] = url is null ? null : await gateway.DownloadPhotoAsync(url, token); }
            else { p.Groups[e.EntityId] = (JsonObject)remote.DeepClone(); p.GroupIds[remote["resourceName"]!.GetValue<string>()] = e.EntityId; }
            p.ConfirmedWrites.Add(e.Key);
            await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", p.Restored.GetValueOrDefault(e.EntityId), remote["resourceName"]!.GetValue<string>()), CancellationToken.None);
            var intentPhoto = p.View.Pending.FirstOrDefault(o => o.EntityId == e.EntityId && (o.CreateIntent || o.Field is "$entity" or "$retry" or "$restoreCreate") && o.IntentPhoto is not null)?.IntentPhoto;
            if (e.Entity == EntityKind.Contact && intentPhoto is not null)
                await session.RecordAsync(runId, new(e.Key + ":photo-intent", e.EntityId, e.Entity, "photo", "started", p.Restored.GetValueOrDefault(e.EntityId), remote["resourceName"]!.GetValue<string>(), IntentPhoto: intentPhoto, IntentInputPhotoHash: p.Photos.GetValueOrDefault(e.EntityId) is { } inputPhoto ? JsonSemantics.Hash(inputPhoto) : null, HasIntentInput: true), CancellationToken.None);
            await DownloadEntityAsync(p, session, e, token); return;
        }
        if (e.Field == "$retry" && resolution != Resolution.UseLocal) throw new SyncException("recoveryChoice", "Явный повтор создания доступен только через «Из папки». Для найденной записи выберите отдельный вариант связи «Из Google».");
        if (e.Field is "$entity" or "$retry" or "$restoreDeleteLocal" or "$restoreLocal" or "$restoreUnbind" or "$restoreFile") { await ApplyEntityAsync(p, session, runId, e, resolution, token); return; }
        if (e.Entity == EntityKind.Group) { await ApplyGroupFieldAsync(p, session, runId, e, resolution, token); return; }
        await ApplyContactFieldAsync(p, session, runId, e, resolution, token);
    }

    private async Task ApplyEntityAsync(Prepared p, IWorkspaceSession session, Guid runId, SyncEntry e, Resolution resolution, CancellationToken token)
    {
        var local = await GetCurrentAsync(p, session, e, token);
        var remote = e.Entity == EntityKind.Contact ? p.People.GetValueOrDefault(e.EntityId) : p.Groups.GetValueOrDefault(e.EntityId);
        if (e.Field == "$restoreFile")
        {
            var target = (JsonObject)p.Restored[e.EntityId].DeepClone();
            p.WorkingLocal[e.EntityId] = await session.WriteAsync(e.Entity, e.EntityId, target, local?.Hash, p.RestoredPhotos.GetValueOrDefault(e.EntityId), local?.PhotoHash, e.Entity == EntityKind.Contact, token);
            return;
        }
        var deleteRemote = e.DeletesRemote(resolution);
        var deleteLocal = e.DeletesLocal(resolution);
        if (deleteRemote)
        {
            if (p.View.IsRecovery || p.View.Issues.Count > 0) throw new SyncException("unsafeDeletion", "Удаление недоступно, пока папка требует восстановления.");
            if (remote is null) return;
            await EnsureRemoteAsync(p, e, token);
            if (e.Entity == EntityKind.Contact) await gateway.DeleteContactAsync(PersonCodec.Resource(remote), token);
            else await gateway.DeleteGroupAsync(remote["resourceName"]!.GetValue<string>(), token);
            p.ConfirmedWrites.Add(e.Key);
            await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", ResourceName: remote["resourceName"]!.GetValue<string>()), CancellationToken.None);
            if (e.Entity == EntityKind.Group) await CleanupDeletedGroupAsync(p, session, e.EntityId, remote["resourceName"]!.GetValue<string>(), token);
            await session.RemoveStateAsync(e.EntityId, CancellationToken.None);
            (e.Entity == EntityKind.Contact ? p.People : p.Groups).Remove(e.EntityId);
            p.WorkingStates.Remove(e.EntityId);
            if (e.Field == "$restoreUnbind" && local is not null)
            {
                var target = (JsonObject)p.Restored[e.EntityId].DeepClone(); target["google"] = null;
                p.WorkingLocal[e.EntityId] = await session.WriteAsync(e.Entity, e.EntityId, target, local.Hash, p.RestoredPhotos.GetValueOrDefault(e.EntityId), local.PhotoHash, e.Entity == EntityKind.Contact, token);
            }
            return;
        }
        if (deleteLocal)
        {
            if (local is not null) await session.TrashAsync(e.Entity, e.EntityId, local.Hash, runId, token);
            await session.RemoveStateAsync(e.EntityId, token); p.WorkingStates.Remove(e.EntityId);
            p.WorkingLocal[e.EntityId] = null;
            return;
        }
        if (remote is not null && (e.Kind == ChangeKind.CreateLocal || e.Kind == ChangeKind.Conflict && resolution == Resolution.UseGoogle))
        { await DownloadEntityAsync(p, session, e, token); return; }
        if (local is null && !p.Restored.ContainsKey(e.EntityId)) throw new SyncException("missingLocal", "Локальная версия не найдена. Проверьте изменения заново.");
        if (e.Entity == EntityKind.Group)
        {
            var doc = DocumentCodec.Group(p.Restored.GetValueOrDefault(e.EntityId) ?? local!.Document);
            var created = await gateway.CreateGroupAsync(doc.Name, doc.ClientData, token);
            p.Groups[e.EntityId] = created; p.GroupIds[created["resourceName"]!.GetValue<string>()] = e.EntityId;
            p.ConfirmedWrites.Add(e.Key);
            await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", DocumentCodec.ToJson(doc), created["resourceName"]!.GetValue<string>(), CreateIntent: true), CancellationToken.None);
            await DownloadEntityAsync(p, session, e, token); return;
        }
        var contact = DocumentCodec.Contact(p.Restored.GetValueOrDefault(e.EntityId) ?? local!.Document);
        foreach (var field in contact.Data) if (CapabilityRegistry.ValidateEditable(field.Key, field.Value).Count > 0) throw new SyncException("invalidField", "Контакт содержит неподдерживаемую категорию.");
        if (contact.Labels.Any(id => !p.Groups.ContainsKey(id))) throw new SyncException("groupDependency", "Сначала создайте все ярлыки этого контакта.");
        var payload = (JsonObject)contact.Data.DeepClone();
        payload["memberships"] = MembershipPayload(p, contact.Labels, contact.Starred, ["contactGroups/myContacts"]);
        var person = await gateway.CreateContactAsync(payload, token);
        p.People[e.EntityId] = person;
        p.ConfirmedWrites.Add(e.Key);
        var createPhoto = p.RestoredPhotos.TryGetValue(e.EntityId, out var restoredPhoto) ? restoredPhoto : local?.PhotoBytes;
        await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", DocumentCodec.ToJson(contact), PersonCodec.Resource(person), CreateIntent: true, IntentPhoto: createPhoto), CancellationToken.None);
        var state = StateFor(p, e.EntityId, EntityKind.Contact, person, local?.Document ?? DocumentCodec.ToJson(contact));
        // Save the remote identity before optional photo work; failure must never lead to a repeated create.
        await SaveStateAsync(p, session, state);
        if (createPhoto is not null)
        {
            var photoResult = await gateway.UpdatePhotoAsync(PersonCodec.Resource(person), createPhoto, token);
            person = await gateway.GetPersonAsync(PersonCodec.Resource(person), token) ?? throw new SyncException("remoteDrift", "Контакт исчез после записи фотографии.");
            ConfirmPhotoReadback(photoResult, person);
            p.People[e.EntityId] = person;
        }
        var url = PersonCodec.PhotoUrl(person); p.Photos[e.EntityId] = url is null ? null : await gateway.DownloadPhotoAsync(url, token);
        await CommitContactAsync(p, session, e, ContactFromRemote(p, e.EntityId, person, contact), state, [], syncAll: true, keepOriginalPhoto: local is not null && !p.RestoredPhotos.ContainsKey(e.EntityId), token, preferRestoredPhoto: true);
    }

    private async Task DownloadEntityAsync(Prepared p, IWorkspaceSession session, SyncEntry e, CancellationToken token)
    {
        var local = await GetCurrentAsync(p, session, e, token);
        if (e.Entity == EntityKind.Group)
        {
            var remote = p.Groups[e.EntityId];
            var doc = GroupFromRemote(e.EntityId, remote, local is null ? null : DocumentCodec.Group(local.Document));
            p.WorkingLocal[e.EntityId] = await session.WriteAsync(e.Entity, e.EntityId, DocumentCodec.ToJson(doc), local?.Hash, cancellationToken: token);
            var state = StateFor(p, e.EntityId, e.Entity, remote, DocumentCodec.ToJson(doc)); state.Baseline = DocumentCodec.ToJson(doc);
            await SaveStateAsync(p, session, state); return;
        }
        var person = p.People[e.EntityId];
        var preserve = local is null ? new ContactDocument { Id = e.EntityId } : DocumentCodec.Contact(local.Document);
        var contact = ContactFromRemote(p, e.EntityId, person, preserve);
        var s = StateFor(p, e.EntityId, e.Entity, person, DocumentCodec.ToJson(contact));
        await CommitContactAsync(p, session, e, contact, s, [], syncAll: true, keepOriginalPhoto: false, token);
    }

    private async Task<LocalEntity?> GetCurrentAsync(Prepared p, IWorkspaceSession session, SyncEntry e, CancellationToken token)
    {
        var current = await session.ReadLocalAsync(e.Entity, e.EntityId, token);
        var expected = p.WorkingLocal.GetValueOrDefault(e.EntityId) ?? (e.Entity == EntityKind.Contact ? p.View.Contacts.GetValueOrDefault(e.EntityId) : p.View.Groups.GetValueOrDefault(e.EntityId));
        if (current?.Hash != expected?.Hash || current?.PhotoHash != expected?.PhotoHash) throw new SyncException("localDrift", "Google мог уже принять предыдущие изменения; новая версия файла сохранена. Проверьте изменения снова.");
        return current;
    }
    private async Task EnsureRemoteAsync(Prepared p, SyncEntry e, CancellationToken token)
    {
        var expected = e.Entity == EntityKind.Contact ? p.People[e.EntityId] : p.Groups[e.EntityId];
        var resource = expected["resourceName"]!.GetValue<string>();
        var current = e.Entity == EntityKind.Contact ? await gateway.GetPersonAsync(resource, token) : await gateway.GetGroupAsync(resource, token);
        if (!JsonSemantics.Equal(expected, current, true) || e.Entity == EntityKind.Contact && current is not null && PersonCodec.Source(expected)["etag"]?.ToString() != PersonCodec.Source(current)["etag"]?.ToString()) throw new SyncException("remoteDrift", "Google изменился во время синхронизации. Проверьте новый план.");
    }

    private async Task ApplyGroupFieldAsync(Prepared p, IWorkspaceSession session, Guid runId, SyncEntry e, Resolution resolution, CancellationToken token)
    {
        var local = await GetCurrentAsync(p, session, e, token) ?? throw new SyncException("missingLocal", "Файл ярлыка исчез.");
        var remote = p.Groups[e.EntityId]; var doc = DocumentCodec.Group(local.Document);
        var state = StateFor(p, e.EntityId, e.Entity, remote, local.Document);
        if (e.Field is "$binding" or "$snapshot")
        {
            doc.Google = (JsonObject)remote.DeepClone();
        }
        else
        {
            var upload = e.Kind == ChangeKind.Upload || e.Kind == ChangeKind.Conflict && resolution == Resolution.UseLocal;
            var desired = upload ? e.Local : e.Google;
            if (upload)
            {
                await EnsureRemoteAsync(p, e, token);
                var name = e.Field == "name" ? desired!.GetValue<string>() : remote["name"]!.GetValue<string>();
                var data = e.Field == "clientData" ? desired!.AsArray() : (JsonArray?)remote["clientData"] ?? [];
                remote = await gateway.UpdateGroupAsync(remote["resourceName"]!.GetValue<string>(), name, data, token);
                p.Groups[e.EntityId] = remote;
                p.ConfirmedWrites.Add(e.Key);
                await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", local.Document, remote["resourceName"]!.GetValue<string>()), CancellationToken.None);
                desired = e.Field == "name" ? remote["name"] : remote["clientData"] ?? new JsonArray();
            }
            if (e.Field == "name") doc.Name = desired!.GetValue<string>(); else doc.ClientData = (JsonArray)desired!.DeepClone();
            state.Baseline[e.Field] = JsonSemantics.Clone(desired);
            doc.Google = (JsonObject)remote.DeepClone();
        }
        state.ResourceName = remote["resourceName"]!.GetValue<string>(); state.Baseline["google"] = remote.DeepClone();
        if (e.Field == "$binding") state.Baseline = DocumentCodec.ToJson(doc);
        p.WorkingLocal[e.EntityId] = await session.WriteAsync(e.Entity, e.EntityId, DocumentCodec.ToJson(doc), local.Hash, cancellationToken: token);
        await SaveStateAsync(p, session, state);
    }

    private async Task ApplyContactFieldAsync(Prepared p, IWorkspaceSession session, Guid runId, SyncEntry e, Resolution resolution, CancellationToken token)
    {
        var local = await GetCurrentAsync(p, session, e, token) ?? throw new SyncException("missingLocal", "Файл контакта исчез.");
        var doc = DocumentCodec.Contact(local.Document); var person = p.People[e.EntityId];
        var state = StateFor(p, e.EntityId, EntityKind.Contact, person, local.Document);
        var upload = e.Kind == ChangeKind.Upload || e.Kind == ChangeKind.Conflict && resolution == Resolution.UseLocal;
        var desired = upload ? e.Local : e.Google;
        if (CapabilityRegistry.WritableFields.Contains(e.Field))
        {
            if (upload)
            {
                var unknown = CapabilityRegistry.UnknownReplacementFields(e.Field, PersonCodec.ContactValues(person, e.Field));
                if (unknown.Count > 0) throw new SyncException("unknownField", string.Join("\n", unknown));
                await EnsureRemoteAsync(p, e, token);
                var payload = new JsonObject { [e.Field] = JsonSemantics.Clone(desired) ?? new JsonArray(), ["metadata"] = new JsonObject { ["sources"] = new JsonArray(PersonCodec.Source(person).DeepClone()) } };
                person = await gateway.UpdateContactAsync(PersonCodec.Resource(person), payload, [e.Field], token);
                p.People[e.EntityId] = person;
                p.ConfirmedWrites.Add(e.Key);
                await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", payload, PersonCodec.Resource(person)), CancellationToken.None);
                var readback = await gateway.GetPersonAsync(PersonCodec.Resource(person), token) ?? throw new SyncException("remoteDrift", "Контакт исчез после изменения.");
                if (!JsonSemantics.Equal(PersonCodec.Data(person)[e.Field], PersonCodec.Data(readback)[e.Field])) throw new SyncException("remoteDrift", "Поле Google изменилось сразу после записи. Новая версия будет показана в следующем сравнении.");
                person = readback; p.People[e.EntityId] = person;
                desired = EmptyArray(PersonCodec.Data(person)[e.Field]);
            }
            doc.Data[e.Field] = JsonSemantics.Clone(desired) ?? new JsonArray();
            state.Baseline["data"] ??= new JsonObject(); state.Baseline["data"]![e.Field] = JsonSemantics.Clone(desired) ?? new JsonArray();
        }
        else if (e.Field is "labels" or "starred")
        {
            if (upload)
            {
                await EnsureRemoteAsync(p, e, token);
                var desiredLabels = e.Field == "labels" ? desired!.AsArray().Select(x => Guid.Parse(x!.GetValue<string>())).ToList() : ContactFromRemote(p, e.EntityId, person, doc).Labels;
                var starred = e.Field == "starred" ? desired!.GetValue<bool>() : PersonCodec.Memberships(person).Contains("contactGroups/starred");
                var wanted = desiredLabels.Select(id => p.Groups.TryGetValue(id, out var g) ? g["resourceName"]!.GetValue<string>() : throw new SyncException("groupDependency", "Один из выбранных ярлыков ещё не создан.")).ToHashSet(StringComparer.Ordinal);
                if (starred) wanted.Add("contactGroups/starred");
                var actual = PersonCodec.Memberships(person).Where(r => p.GroupIds.ContainsKey(r) || r == "contactGroups/starred").ToHashSet(StringComparer.Ordinal);
                foreach (var add in wanted.Except(actual)) await gateway.ModifyMembershipAsync(add, PersonCodec.Resource(person), true, token);
                foreach (var remove in actual.Except(wanted)) await gateway.ModifyMembershipAsync(remove, PersonCodec.Resource(person), false, token);
                p.ConfirmedWrites.Add(e.Key);
                await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", local.Document, PersonCodec.Resource(person)), CancellationToken.None);
                person = await gateway.GetPersonAsync(PersonCodec.Resource(person), token) ?? throw new SyncException("remoteDrift", "Контакт исчез во время обновления ярлыков.");
                p.People[e.EntityId] = person;
                var final = ContactFromRemote(p, e.EntityId, person, doc);
                if (e.Field == "labels" && !final.Labels.Order().SequenceEqual(desiredLabels.Order()) || e.Field == "starred" && final.Starred != starred) throw new SyncException("membershipNotConfirmed", "Google пока не подтвердил выбранные ярлыки. Проверьте изменения заново.");
                desired = e.Field == "labels" ? DocumentCodec.ToJson(final)["labels"] : JsonValue.Create(final.Starred);
            }
            if (e.Field == "labels") doc.Labels = desired!.AsArray().Select(x => Guid.Parse(x!.GetValue<string>())).ToList(); else doc.Starred = desired!.GetValue<bool>();
            state.Baseline[e.Field] = JsonSemantics.Clone(desired);
        }
        else if (e.Field == "photo")
        {
            if (upload)
            {
                await EnsureRemoteAsync(p, e, token);
                var desiredPhoto = p.RestoredPhotos.TryGetValue(e.EntityId, out var restoredPhoto) ? restoredPhoto : local.PhotoBytes;
                var photoResult = await gateway.UpdatePhotoAsync(PersonCodec.Resource(person), desiredPhoto, token);
                p.People[e.EntityId] = photoResult;
                p.ConfirmedWrites.Add(e.Key);
                await session.RecordAsync(runId, new(e.Key, e.EntityId, e.Entity, e.Field, "confirmed", local.Document, PersonCodec.Resource(person)), CancellationToken.None);
                person = await gateway.GetPersonAsync(PersonCodec.Resource(person), token) ?? throw new SyncException("remoteDrift", "Контакт исчез после записи фотографии."); p.People[e.EntityId] = person;
                ConfirmPhotoReadback(photoResult, person);
                var url = PersonCodec.PhotoUrl(person); p.Photos[e.EntityId] = url is null ? null : await gateway.DownloadPhotoAsync(url, token);
            }
            doc.Google = SnapshotFor(p, e.EntityId, person);
            await CommitContactAsync(p, session, e, doc, state, ["photo"], false, upload && !p.RestoredPhotos.ContainsKey(e.EntityId), token, preferRestoredPhoto: upload);
            return;
        }
        else if (e.Field == "extensions") { doc.Extensions = (JsonObject)desired!.DeepClone(); }
        else if (e.Field == "$binding") state.Baseline = DocumentCodec.ToJson(doc);
        doc.Google = SnapshotFor(p, e.EntityId, person); state.Baseline["google"] = doc.Google.DeepClone(); state.ResourceName = PersonCodec.Resource(person); state.SourceId = PersonCodec.SourceId(person);
        p.WorkingLocal[e.EntityId] = await session.WriteAsync(e.Entity, e.EntityId, DocumentCodec.ToJson(doc), local.Hash, expectedPhotoHash: local.PhotoHash, cancellationToken: token);
        if (e.Field == "$binding") { state.PhotoLocalHash = local.PhotoHash; state.PhotoRemoteHash = p.Photos.GetValueOrDefault(e.EntityId) is { } photo ? JsonSemantics.Hash(photo) : null; }
        await SaveStateAsync(p, session, state);
    }

    private async Task CommitContactAsync(Prepared p, IWorkspaceSession session, SyncEntry e, ContactDocument doc, EntityState state, string[] fields, bool syncAll, bool keepOriginalPhoto, CancellationToken token, bool preferRestoredPhoto = false)
    {
        var local = await GetCurrentAsync(p, session, e, token);
        var photo = p.Photos.GetValueOrDefault(e.EntityId);
        doc.Google = SnapshotFor(p, e.EntityId, p.People[e.EntityId]);
        var photoToWrite = preferRestoredPhoto && p.RestoredPhotos.TryGetValue(e.EntityId, out var restoredPhoto) ? restoredPhoto : photo;
        var written = await session.WriteAsync(EntityKind.Contact, e.EntityId, DocumentCodec.ToJson(doc), local?.Hash, keepOriginalPhoto ? local?.PhotoBytes : photoToWrite, local?.PhotoHash, writePhoto: !keepOriginalPhoto, cancellationToken: token);
        p.WorkingLocal[e.EntityId] = written;
        if (syncAll) state.Baseline = (JsonObject)written.Document.DeepClone();
        else foreach (var field in fields) state.Baseline[field] = written.Document[field]?.DeepClone();
        state.Baseline["google"] = doc.Google.DeepClone(); state.ResourceName = PersonCodec.Resource(p.People[e.EntityId]); state.SourceId = PersonCodec.SourceId(p.People[e.EntityId]);
        state.PhotoLocalHash = written.PhotoHash; state.PhotoRemoteHash = photo is null ? null : JsonSemantics.Hash(photo);
        await SaveStateAsync(p, session, state);
    }
    private static EntityState StateFor(Prepared p, Guid id, EntityKind kind, JsonObject remote, JsonObject fallback)
    {
        var existing = p.WorkingStates.GetValueOrDefault(id);
        var baseline = (JsonObject)fallback.DeepClone();
        var confirmedCreate = p.View.Pending.FirstOrDefault(o => o.EntityId == id && o.Field is "$entity" or "$retry" && o.Status == "confirmed" && o.Payload is not null);
        if (existing is null && confirmedCreate?.Payload is JsonObject payload && payload["data"] is JsonObject)
            baseline = (JsonObject)payload.DeepClone();
        else if (existing is null && fallback["google"] is JsonObject previous)
        {
            if (kind == EntityKind.Contact && previous["person"] is JsonObject previousPerson)
                baseline = DocumentCodec.ToJson(ContactFromRemote(p, id, previousPerson, DocumentCodec.Contact(fallback)));
            else if (kind == EntityKind.Group)
            {
                baseline["name"] = JsonSemantics.Clone(previous["name"]);
                baseline["clientData"] = JsonSemantics.Clone(previous["clientData"]) ?? new JsonArray();
            }
        }
        return existing is not null ? JsonSerializer.Deserialize<EntityState>(JsonSerializer.Serialize(existing, JsonSemantics.Options), JsonSemantics.Options)!
            : new() { Id = id, Kind = kind, ResourceName = remote["resourceName"]!.GetValue<string>(), SourceId = kind == EntityKind.Contact ? PersonCodec.SourceId(remote) : null, Baseline = baseline };
    }
    private static async Task SaveStateAsync(Prepared p, IWorkspaceSession session, EntityState state)
    {
        await session.SaveStateAsync(state, CancellationToken.None);
        p.WorkingStates[state.Id] = state;
    }
    private static void ConfirmPhotoReadback(JsonObject mutation, JsonObject readback)
    {
        if (PersonCodec.Resource(mutation) != PersonCodec.Resource(readback) || PersonCodec.SourceId(mutation) != PersonCodec.SourceId(readback)
            || PersonCodec.Source(mutation)["etag"]?.ToString() != PersonCodec.Source(readback)["etag"]?.ToString()
            || !JsonSemantics.Equal(PersonCodec.ContactValues(mutation, "photos"), PersonCodec.ContactValues(readback, "photos"), true))
            throw new SyncException("remoteDrift", "Фотография Google изменилась сразу после записи. Обе стороны будут показаны в новом сравнении; разные изображения не считаются согласованными.");
    }

    private async Task CleanupDeletedGroupAsync(Prepared p, IWorkspaceSession session, Guid groupId, string resource, CancellationToken token)
    {
        foreach (var (id, remote) in p.People.Where(x => PersonCodec.Memberships(x.Value).Contains(resource)).ToArray())
        {
            var entry = EntityEntry(id, EntityKind.Contact, "Контакт", ChangeKind.Download, null, null, field: "labels");
            var local = await GetCurrentAsync(p, session, entry, token);
            var person = await gateway.GetPersonAsync(PersonCodec.Resource(remote), token);
            if (person is null) throw new SyncException("remoteDrift", "Участник ярлыка исчез. Откройте новый план восстановления.");
            p.People[id] = person;
            if (local is null) continue;
            var doc = DocumentCodec.Contact(local.Document); doc.Labels.Remove(groupId); doc.Google = SnapshotFor(p, id, person);
            p.WorkingLocal[id] = await session.WriteAsync(EntityKind.Contact, id, DocumentCodec.ToJson(doc), local.Hash, expectedPhotoHash: local.PhotoHash, cancellationToken: token);
            var state = StateFor(p, id, EntityKind.Contact, person, local.Document); var baselineLabels = (state.Baseline["labels"] as JsonArray ?? []).Where(x => x?.GetValue<string>() != groupId.ToString()).Select(JsonSemantics.Clone).ToArray(); state.Baseline["labels"] = new JsonArray(baselineLabels); state.Baseline["google"] = doc.Google.DeepClone(); await SaveStateAsync(p, session, state);
        }
        p.GroupIds.Remove(resource);
    }

    public Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string root, CancellationToken cancellationToken = default) => store.GetHistoryAsync(root, cancellationToken);
    public Task<string> CreateContactFileAsync(string root, CancellationToken cancellationToken = default) => store.CreateContactFileAsync(root, cancellationToken);
    public async Task CleanupBackupAsync(string root, AccountIdentity account, Guid runId, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try { await using var session = await store.OpenAsync(root, account, cancellationToken); await session.DeleteBackupAsync(runId, cancellationToken); _plans.Clear(); }
        finally { _mutex.Release(); }
    }

    public async Task<SyncPreview> PrepareRestoreAsync(string root, AccountIdentity account, Guid runId, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var preview = await PrepareAsync(root, account, progress, cancellationToken);
        var backups = await store.GetBackupAsync(root, runId, cancellationToken);
        var p = _plans[preview.PlanId]; var entries = new List<SyncEntry>();
        foreach (var item in backups)
        {
            if (p.View.Pending.Any(o => o.EntityId == item.Id && o.Status is "started" or "unknown" && (o.CreateIntent || o.Field is "$entity" or "$retry" or "$restoreCreate")))
            {
                entries.AddRange(preview.Entries.Where(e => e.EntityId == item.Id));
                continue;
            }
            var current = item.Kind == EntityKind.Contact ? p.View.Contacts.GetValueOrDefault(item.Id) : p.View.Groups.GetValueOrDefault(item.Id);
            var remote = item.Kind == EntityKind.Contact ? p.People.GetValueOrDefault(item.Id) : p.Groups.GetValueOrDefault(item.Id);
            // Absence is meaningful per side: a download did not create the pre-existing Google record.
            if (item.Remote is null && remote is not null)
            {
                if (item.LocalDocument is not null)
                {
                    p.Restored[item.Id] = (JsonObject)item.LocalDocument.DeepClone(); p.RestoredPhotos[item.Id] = item.LocalPhoto;
                    entries.Add(EntityEntry(item.Id, item.Kind, "Отменить создание записи Google", ChangeKind.DeleteRemote, item.LocalDocument, remote, "Запись Google отсутствовала до запуска. Удаление требует явного выбора; локальный файл сохранится.", "$restoreUnbind"));
                }
                else entries.Add(EntityEntry(item.Id, item.Kind, "Отменить создание записи Google", ChangeKind.DeleteRemote, null, remote));
                continue;
            }
            if (item.LocalDocument is null && current is not null && item.Remote is not null)
            {
                entries.Add(EntityEntry(item.Id, item.Kind, "Отменить скачивание файла", ChangeKind.DeleteLocal, current.Document, remote, "Google существовал до запуска и сохраняется. Новый локальный файл переместится в корзину.", "$restoreDeleteLocal"));
                continue;
            }
            var desired = item.LocalDocument ?? (item.Remote is null ? null : item.Kind == EntityKind.Contact
                ? DocumentCodec.ToJson(ContactFromRemote(p, item.Id, item.Remote, new() { Id = item.Id }))
                : DocumentCodec.ToJson(GroupFromRemote(item.Id, item.Remote)));
            if (desired is null) continue;
            p.Restored[item.Id] = (JsonObject)desired.DeepClone();
            p.RestoredPhotos[item.Id] = item.LocalDocument is null ? item.RemotePhoto : item.LocalPhoto;
            p.RemoteRestorePhotos[item.Id] = item.RemotePhoto;
            var remoteBefore = item.Remote is null ? null : item.Kind == EntityKind.Contact
                ? DocumentCodec.ToJson(ContactFromRemote(p, item.Id, item.Remote, new() { Id = item.Id }))
                : DocumentCodec.ToJson(GroupFromRemote(item.Id, item.Remote));
            if (remote is null)
            {
                var different = item.LocalDocument is not null && remoteBefore is not null && (!JsonSemantics.Equal(desired["data"], remoteBefore["data"]) || !JsonSemantics.Equal(desired["name"], remoteBefore["name"]) || !JsonSemantics.Equal(desired["clientData"], remoteBefore["clientData"]) || !JsonSemantics.Equal(desired["labels"], remoteBefore["labels"]) || !JsonSemantics.Equal(desired["starred"], remoteBefore["starred"]) || (item.LocalPhoto is null ? null : JsonSemantics.Hash(item.LocalPhoto)) != (item.RemotePhoto is null ? null : JsonSemantics.Hash(item.RemotePhoto)));
                entries.Add(EntityEntry(item.Id, item.Kind, "Восстановить удалённую запись", different ? ChangeKind.Conflict : item.Remote is null ? ChangeKind.Download : ChangeKind.CreateRemote, desired, different ? remoteBefore : null,
                    different ? "Версии ДО запуска отличались. Выберите источник копии; выбранная версия восстановится в Google и папке с новым идентификатором Google." : item.Remote is null ? "Восстановить локальный файл из копии." : "Google назначит восстановленной записи новый идентификатор.", different ? "$restoreCreate" : item.Remote is null ? "$restoreFile" : "$entity"));
                continue;
            }
            if (current is null) entries.Add(EntityEntry(item.Id, item.Kind, "Восстановить локальный файл", ChangeKind.CreateLocal, null, remote, "Сначала будет восстановлен файл, затем выбранные поля из копии.", "$restoreLocal"));
            var name = item.Kind == EntityKind.Contact ? PersonCodec.DisplayName(DocumentCodec.Contact(desired)) : desired["name"]!.GetValue<string>();
            if (item.Kind == EntityKind.Group)
            {
                foreach (var field in new[] { "name", "clientData" })
                {
                    if (remoteBefore is not null && !JsonSemantics.Equal(desired[field] ?? new JsonArray(), remoteBefore[field] ?? new JsonArray()))
                        entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Conflict, desired[field] ?? new JsonArray(), remoteBefore[field] ?? new JsonArray(), RestoreSourceExplanation, "$restore:" + field));
                    else if (!JsonSemantics.Equal(desired[field] ?? new JsonArray(), remote[field] ?? new JsonArray()) || !JsonSemantics.Equal(desired[field], current?.Document[field]))
                        entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Upload, desired[field], remote[field], "Значение из резервной копии → Google и папка.", field));
                }
            }
            else
            {
                foreach (var field in CapabilityRegistry.WritableFields)
                {
                    var target = EmptyArray(desired["data"]?[field]); var actual = EmptyArray(PersonCodec.Data(remote)[field]);
                    var unknown = CapabilityRegistry.UnknownReplacementFields(field, PersonCodec.ContactValues(remote, field));
                    if (remoteBefore is not null && !JsonSemantics.Equal(target, EmptyArray(remoteBefore["data"]?[field])))
                    {
                        entries.Add(EntityEntry(item.Id, item.Kind, name, unknown.Count == 0 ? ChangeKind.Conflict : ChangeKind.Blocked, target, EmptyArray(remoteBefore["data"]?[field]), unknown.Count == 0 ? RestoreSourceExplanation : string.Join("\n", unknown), "$restore:" + field));
                        continue;
                    }
                    if (!JsonSemantics.Equal(target, actual) || !JsonSemantics.Equal(target, EmptyArray(current?.Document["data"]?[field])))
                    {
                        entries.Add(EntityEntry(item.Id, item.Kind, name, unknown.Count == 0 ? ChangeKind.Upload : ChangeKind.Blocked, target, actual, unknown.Count == 0 ? "Значение из резервной копии → Google и папка." : string.Join("\n", unknown), field));
                    }
                }
                var remoteDoc = DocumentCodec.ToJson(ContactFromRemote(p, item.Id, remote, new() { Id = item.Id }));
                foreach (var field in new[] { "labels", "starred" })
                    if (remoteBefore is not null && !JsonSemantics.Equal(desired[field], remoteBefore[field])) entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Conflict, desired[field], remoteBefore[field], RestoreSourceExplanation, "$restore:" + field));
                    else if (!JsonSemantics.Equal(desired[field], remoteDoc[field]) || !JsonSemantics.Equal(desired[field], current?.Document[field])) entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Upload, desired[field], remoteDoc[field], "Значение из резервной копии → Google и папка.", field));
                if (!JsonSemantics.Equal(desired["extensions"], current?.Document["extensions"])) entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Download, current?.Document["extensions"], desired["extensions"] ?? new JsonObject(), "Восстановить локальные дополнительные данные.", "extensions"));
                var photo = p.RestoredPhotos[item.Id];
                var targetHash = photo is null ? null : JsonSemantics.Hash(photo);
                var remoteHash = p.Photos.GetValueOrDefault(item.Id) is { } actualPhoto ? JsonSemantics.Hash(actualPhoto) : null;
                var remoteBeforeHash = item.RemotePhoto is null ? null : JsonSemantics.Hash(item.RemotePhoto);
                if (remoteBefore is not null && targetHash != remoteBeforeHash) entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Conflict, desired["photo"], JsonValue.Create(PersonCodec.PhotoUrl(item.Remote!)), RestoreSourceExplanation, "$restore:photo"));
                else if (targetHash != remoteHash || targetHash != current?.PhotoHash) entries.Add(EntityEntry(item.Id, item.Kind, name, ChangeKind.Upload, desired["photo"], JsonValue.Create(PersonCodec.PhotoUrl(remote)), "Восстановить содержимое фотографии из резервной копии.", "photo"));
            }
        }
        var restored = new SyncPreview { PlanId = preview.PlanId, Root = preview.Root, Account = preview.Account, Entries = entries, ContactCount = preview.ContactCount, GroupCount = preview.GroupCount, IsRecovery = preview.IsRecovery, Notices = [.. preview.Notices, "План восстановления подготовлен по текущему состоянию. Файлы и Google пока не изменены."] };
        p.Preview = restored;
        return restored;
    }
    private const string RestoreSourceExplanation = "Версии ДО выбранного запуска отличались. «Из папки» и «Из Google» выбирают источник из РЕЗЕРВНОЙ КОПИИ. Выбранное значение будет записано и в Google, и в папку; текущие версии попадут в новую копию.";
}
