using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Automation;

public sealed partial class AgentRuntime
{
    private async Task<AgentResult> DispatchWorkflowAsync(AgentCall call, string? root, IProgress<SyncProgress>? progress, CancellationToken token)
    {
        var a = call.Arguments;
        if (call.Tool.StartsWith("groups_", StringComparison.Ordinal)) return await GroupAsync(call, root!, token);
        switch (call.Tool)
        {
            case "sync_prepare":
                CheckAccount(a); var prepared = await sync.PrepareAsync(root!, Connected(), progress, token); plans[prepared.PlanId.ToString()] = new(prepared.Root, prepared.Account.Key, prepared.PreparedAt, await BindingHashAsync(root!, token));
                return Ok(new { planId = prepared.PlanId, planDigest = PlanDigest(prepared), entries = prepared.Entries.Count, prepared.ContactCount, prepared.GroupCount, prepared.IsRecovery });
            case "sync_get_plan":
            {
                var plan = await GetPlanAsync(a); var choices = Choices(a, false); ValidateSelection(plan, choices);
                var digest = PlanDigest(plan); var rows = plan.Entries.Select(e => (Func<JsonNode>)(() => new JsonObject
                {
                    ["entryKey"] = e.Key, ["id"] = e.EntityId.ToString(), ["entity"] = e.Entity.ToString(), ["name"] = values.Bound(JsonValue.Create(e.Name), digest, "plan:" + plan.PlanId), ["field"] = e.Field,
                    ["kind"] = e.Kind.ToString(), ["selectable"] = e.IsSelectable, ["defaultSelected"] = e.DefaultSelected, ["canRepairGoogleSnapshot"] = e.CanRepairGoogleSnapshot,
                    ["before"] = values.Bound(e.Before, digest, "plan:" + plan.PlanId + ":before"), ["local"] = values.Bound(e.Local, digest, "plan:" + plan.PlanId + ":local"), ["google"] = values.Bound(e.Google, digest, "plan:" + plan.PlanId + ":google"),
                    ["explanation"] = values.Bound(JsonValue.Create(e.Explanation), digest, "plan:" + plan.PlanId), ["photo"] = PhotoComparisonDto(e.PhotoComparison, digest, plan.PlanId.ToString()),
                    ["resolutions"] = new JsonArray((e.Kind == ChangeKind.Conflict ? new[] { "UseLocal", "UseGoogle", "Skip" } : new[] { "Automatic", "Skip" }).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
                })).ToArray();
                var result = Page(a, rows, digest); result["planId"] = plan.PlanId.ToString(); result["planDigest"] = digest;
                result["selectionDigest"] = SelectionDigest(plan, choices); result["notices"] = values.Bound(JsonSerializer.SerializeToNode(plan.Notices), digest, "plan:" + plan.PlanId);
                result["selectedDeletes"] = choices.Count(c => plan.Entries.First(e => e.Key == c.Key).IsDestructiveFor(c.Resolution)); return Ok(result);
            }
            case "sync_apply":
            {
                var plan = await GetPlanAsync(a); var choices = Choices(a, true); ValidateSelection(plan, choices);
                if (Required(a, "selectionDigest") != SelectionDigest(plan, choices)) throw new SyncException("plan_stale", "Проверенная selection изменилась; прочитайте план с choices заново.");
                var selected = choices.Where(c => c.Resolution != Resolution.Skip).Select(c => (Entry: plan.Entries.First(e => e.Key == c.Key), c.Resolution)).ToArray();
                if (selected.Length == 0) throw new SyncException("empty_selection", "Выберите изменения явно.");
                var deleted = selected.Count(x => x.Entry.IsDestructiveFor(x.Resolution));
                if (deleted > 0 && (!Flag(a, "confirmDeletions") || plan.RequiresDeletionConfirmation(deleted) && Optional(a, "deletePhrase") != "УДАЛИТЬ")) throw new SyncException("confirmation_required", "Подтвердите выбранные удаления; для значительной части контактов укажите deletePhrase=УДАЛИТЬ.");
                if (selected.Any(x => WritesRemote(x.Entry, x.Resolution)) && !configuration().GoogleWrite) throw new SyncException("permission_denied", "Выбранные изменения требуют разрешения записи в Google.");
                WriteIntent(new() { ["action"] = "sync", ["entryKeys"] = new JsonArray(selected.Select(x => (JsonNode?)JsonValue.Create(x.Entry.Key)).ToArray()) });
                using var remote = AgentRemoteWrite.Enter(() => { Permission(call); if (!configuration().GoogleWrite) throw new SyncException("permission_denied", "Запись в Google отключена."); });
                var result = await sync.ApplyAsync(plan, choices, progress, token); plans.TryRemove(plan.PlanId.ToString(), out _); await CommittedAsync(plan.Root);
                return SyncResult(result);
            }
            case "sync_repair_snapshot":
            {
                var plan = await GetPlanAsync(a); if (Required(a, "planDigest") != PlanDigest(plan)) throw new SyncException("plan_stale", "План изменился.");
                WriteIntent(new() { ["action"] = "repair", ["entryKeys"] = new JsonArray(Required(a, "entryKey")) });
                var result = await sync.RepairGoogleSnapshotAsync(plan, Required(a, "entryKey"), progress, token); plans.TryRemove(plan.PlanId.ToString(), out _); await CommittedAsync(plan.Root); return SyncResult(result);
            }
            default: return await HistoryAsync(call, root!, progress, token);
        }
    }
    private static bool WritesRemote(SyncEntry e, Resolution r) => e.Kind is ChangeKind.Upload or ChangeKind.CreateRemote or ChangeKind.DeleteRemote || e.Field.StartsWith("$restore:", StringComparison.Ordinal) || e.Field == "$restoreCreate" || e.Kind == ChangeKind.Conflict && r == Resolution.UseLocal;
    private async Task<SyncPreview> GetPlanAsync(JsonObject a)
    {
        var p = await sync.ReadPreparedPlanAsync(Guid.Parse(Required(a, "planId"))); configuration().Check(p.Root);
        if (!plans.TryGetValue(p.PlanId.ToString(), out var handle) || handle.BindingHash != await BindingHashAsync(p.Root, CancellationToken.None)) throw new SyncException("plan_stale", "Привязка папки изменилась; подготовьте новый план.");
        if (p.Account.Key != Connected().Key) throw new SyncException("account_changed", "Аккаунт изменился."); return p;
    }
    private static async Task<string> BindingHashAsync(string root, CancellationToken token) => JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, Path.Combine(root, ".contactmirror", "workspace.json"), 16 * 1024 * 1024, token));
    private static PlanChoice[] Choices(JsonObject a, bool required)
    {
        if (a["choices"] is not JsonArray arr) { if (required) throw new SyncException("invalid_selection", "Укажите choices."); return []; }
        return arr.Select(n =>
        {
            var c = n!.AsObject(); if (c.Any(p => p.Key is not ("entryKey" or "resolution"))) throw new SyncException("invalid_selection", "Неизвестный параметр choices.");
            if (!Enum.TryParse<Resolution>(Required(c, "resolution"), false, out var r) || r.ToString() != Required(c, "resolution")) throw new SyncException("invalid_selection", "Неизвестное разрешение изменения.");
            return new PlanChoice(Required(c, "entryKey"), r);
        }).ToArray();
    }
    private static void ValidateSelection(SyncPreview p, PlanChoice[] choices)
    {
        if (choices.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != choices.Length) throw new SyncException("invalid_selection", "Повторяющийся entryKey.");
        foreach (var c in choices)
        {
            var e = p.Entries.SingleOrDefault(e => e.Key == c.Key) ?? throw new SyncException("invalid_selection", "entryKey отсутствует в плане.");
            if (!e.IsSelectable && c.Resolution != Resolution.Skip || e.Kind == ChangeKind.Conflict && c.Resolution == Resolution.Automatic) throw new SyncException("unresolvedConflict", "Для конфликта выберите сторону; blocked запись применять нельзя.");
        }
    }
    private static string PlanDigest(SyncPreview p) => JsonSemantics.Hash(Canonical(JsonSerializer.SerializeToNode(new { p.PlanId, p.Root, account = p.Account.Key, p.PreparedAt, entries = p.Entries.Select(e => new { e.Key, e.EntityId, e.Entity, e.Kind, e.Field, e.Before, e.Local, e.Google, localPhoto = e.PhotoComparison?.LocalHash, remotePhoto = e.PhotoComparison?.GoogleHash }) }, JsonSemantics.Options))!.ToJsonString());
    private static string SelectionDigest(SyncPreview p, PlanChoice[] c) => JsonSemantics.Hash(PlanDigest(p) + "|" + string.Join("|", c.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + ":" + c.Resolution)));
    private JsonObject? PhotoComparisonDto(PhotoComparison? photo, string version, string plan)
    {
        if (photo is null) return null;
        return new() { ["status"] = photo.Status, ["local"] = photo.LocalBytes is null ? null : values.Store(photo.LocalBytes, version, "plan:" + plan + ":local", Mime(photo.LocalBytes)),
            ["google"] = photo.GoogleBytes is null ? null : values.Store(photo.GoogleBytes, version, "plan:" + plan + ":google", Mime(photo.GoogleBytes)),
            ["planned"] = photo.PlannedBytes is null ? null : values.Store(photo.PlannedBytes, version, "plan:" + plan + ":planned", Mime(photo.PlannedBytes)) };
    }
    private static AgentResult SyncResult(SyncRunResult r) => new(r.Unknown > 0 ? "unknown" : r.Failed > 0 ? "error" : "completed",
        JsonSerializer.SerializeToNode(new { r.RunId, r.Confirmed, r.Failed, r.Unknown, operations = r.Operations.Select(o => new { o.Key, o.Status }) }, JsonSemantics.Options),
        Error: r.IsComplete ? null : new("partial_result", "Не все шаги подтверждены; проверьте историю и подготовьте новый план."));

    private async Task<AgentResult> GroupAsync(AgentCall call, string root, CancellationToken token, Guid? restoreId = null, JsonObject? restoredGoogle = null)
    {
        var a = call.Arguments; var catalog = await CatalogAsync(root, token);
        if (call.Tool == "groups_list")
        {
            var rows = catalog.Labels.Keys.Order().Select(id => (Func<JsonNode>)(() =>
            {
                var version = GroupVersion(id, catalog.GroupDocumentHashes?.GetValueOrDefault(id) ?? throw new SyncException("catalog_unavailable", "Каталог не содержит версию ярлыка."), catalog);
                return new JsonObject { ["id"] = id.ToString(), ["name"] = values.Bound(JsonValue.Create(catalog.Labels[id]), version, "group:" + id), ["version"] = version };
            })).ToArray();
            return Ok(Page(a, rows, catalog.LabelsHash ?? ""));
        }
        if (call.Tool == "groups_get")
        {
            var g = await ReadGroupAsync(root, Uuid(a, "id"), token); return Ok(new { g.Id, version = GroupVersion(g, catalog), document = values.Bound(g.Document, GroupVersion(g, catalog), "group:" + g.Id) });
        }
        RequireWritable(catalog); var idValue = call.Tool == "groups_create" ? restoreId ?? Uuid(a, "_allocatedId") : Uuid(a, "id");
        await using var session = await workspaces.OpenAsync(root, Connected(), token);
        if (session.View.IsRecovery || session.View.Pending.Any() || session.View.Issues.Any()) throw new SyncException("recovery_required", "Сначала восстановите состояние папки.");
        using var bindingPin = new FileStream(Path.Combine(root, ".contactmirror", "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var old = session.View.Groups.GetValueOrDefault(idValue);
        if (call.Tool == "groups_create" && (old is not null || session.View.Contacts.ContainsKey(idValue))) throw new SyncException("identity_collision", "UUID уже занят.");
        if (call.Tool != "groups_create")
        {
            if (old is null) throw new SyncException("not_found", "Ярлык не найден."); CheckVersion(a, GroupVersion(old, catalog));
            if (old.Document["google"]?["groupType"]?.ToString() == "SYSTEM_CONTACT_GROUP") throw new SyncException("system_group_readonly", "Системные ярлыки Google доступны только для просмотра.");
        }
        using var relatedPins = new ReadPins();
        foreach (var other in session.View.Groups.Values.Where(g => g.Id != idValue)) relatedPins.Add(root, other.Path);
        if (call.Tool == "groups_delete")
        {
            foreach (var contact in session.View.Contacts.Values) relatedPins.Add(root, contact.Path);
            Confirm(a); var affected = session.View.Contacts.Values.Where(c => DocumentCodec.ContactForEditor(c.Document).Labels.Contains(idValue)).Select(c => JsonValue.Create(c.Id.ToString())!).Cast<JsonNode>().ToArray();
            if (affected.Length != 0) return new("error", Page(a, affected, catalog.LabelsHash ?? ""), Error: new("group_in_use", "Сначала явно уберите этот ярлык у контактов."));
        }
        var doc = old is null ? DocumentCodec.ToJson(new GroupDocument { Id = idValue }) : (JsonObject)old.Document.DeepClone();
        if (restoreId is not null && restoredGoogle is not null)
        {
            if (!session.View.States.TryGetValue(idValue, out var state) || state.Kind != EntityKind.Group || state.Baseline["google"] is not JsonObject snapshot) throw new SyncException("recovery_required", "Нет доверенного состояния удалённого ярлыка.");
            doc["google"] = snapshot.DeepClone();
        }
        foreach (var key in new[] { "name", "clientData", "extensions" }) if (a.ContainsKey(key)) doc[key] = a[key]!.DeepClone();
        if (call.Tool != "groups_delete") _ = DocumentCodec.Group(doc);
        async Task VerifyReferences()
        {
            var binding = JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, Path.Combine(root, ".contactmirror", "workspace.json"), 64 * 1024, token));
            var groupHashes = new List<string>(); var groupDir = Path.Combine(root, "groups");
            foreach (var path in FileWorkspaceStore.InventoryDirectory(root, groupDir).Files.Where(f => f.EndsWith(".group.json", StringComparison.OrdinalIgnoreCase)))
                groupHashes.Add(Path.GetRelativePath(root, path) + ":" + JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, path, 16 * 1024 * 1024, token)));
            var labels = JsonSemantics.Hash(string.Join("\n", groupHashes.Order(StringComparer.Ordinal)));
            var state = await session.ReadStateHashAsync(token);
            if (binding != catalog.BindingHash || labels != catalog.LabelsHash || state != catalog.StateHash) throw new SyncException("localDrift", "Связь или ярлыки изменились перед записью.");
            if (call.Tool == "groups_delete")
            {
                var paths = FileWorkspaceStore.InventoryDirectory(root, Path.Combine(root, "contacts")).Files.Where(f => f.EndsWith(".contact.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (paths.Length != session.View.Contacts.Count) throw new SyncException("localDrift", "Список контактов изменился перед удалением ярлыка.");
                foreach (var contact in session.View.Contacts.Values)
                    if (JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, contact.Path, 16 * 1024 * 1024, token)) != contact.Hash) throw new SyncException("localDrift", "Контакт изменился; проверьте ссылки ярлыка заново.");
            }
        }
        await VerifyReferences();
        WriteIntent(new() { ["action"] = call.Tool == "groups_delete" ? "delete" : "save", ["entityKind"] = "Group", ["entityId"] = idValue.ToString(), ["workspaceId"] = catalog.WorkspaceId.ToString(), ["expectedHash"] = old?.Hash, ["intendedHash"] = ContentHash(doc, null) });
        var editorBackup = await CreateGroupEditorBackupAsync(root, catalog, idValue, old, call.Tool, token); WriteIntent(new() { ["backupId"] = editorBackup });
        var run = await session.BeginRunAsync([new(EntityKind.Group, idValue, old?.Document, null, null, null, session.View.States.GetValueOrDefault(idValue), old?.RawBytes, old?.Path)], token);
        RunStarted(run);
        var operation = new JournalOperation("local-group:" + idValue, idValue, EntityKind.Group, "$localEditor", "started");
        await session.RecordAsync(run, operation, CancellationToken.None);
        try
        {
            await VerifyReferences();
            if (call.Tool == "groups_delete") await session.TrashAsync(EntityKind.Group, idValue, old!.Hash, run, token);
            else await session.WriteAsync(EntityKind.Group, idValue, doc, old?.Hash, cancellationToken: token);
            await session.RecordAsync(run, operation with { Status = "committed" }, CancellationToken.None);
            await session.CompleteRunAsync(run, new(run, 1, 0, 0, [new(operation.Key, "confirmed", "Локальный ярлык сохранён.")]), CancellationToken.None);
        }
        catch (Exception e) when (Recoverable(e))
        { await session.RecordAsync(run, operation with { Status = "unknown" }, CancellationToken.None); throw new SyncException("recovery_required", "Исход записи ярлыка неизвестен; проверьте историю.", true, e); }
        await session.DisposeAsync(); await CommittedAsync(root);
        var next = await CatalogAsync(root, token);
        return call.Tool == "groups_delete" ? Ok(new { id = idValue, deleted = true, outcome = "committed" }) : Ok(new { id = idValue, version = GroupVersion(await ReadGroupAsync(root, idValue, token), next), outcome = "committed" });
    }
    private sealed class ReadPins : IDisposable
    {
        private readonly List<FileStream> pins = [];
        public void Add(string root, string path) { FileWorkspaceStore.ValidatePath(root, path); pins.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
        public void Dispose() { foreach (var p in pins) p.Dispose(); }
    }
    private async Task<LocalEntity> ReadGroupAsync(string root, Guid id, CancellationToken token)
    {
        LocalEntity? found = null; var dir = Path.Combine(root, "groups"); FileWorkspaceStore.ValidatePath(root, dir);
        if (Directory.Exists(dir))
            foreach (var path in FileWorkspaceStore.InventoryDirectory(root, dir).Files.Where(f => f.EndsWith(".group.json", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, path, 16 * 1024 * 1024, token); var doc = JsonSemantics.ParseObject(bytes, "group"); var g = DocumentCodec.Group(doc);
                if (g.Id != id) continue; if (found is not null) throw new SyncException("duplicateId", "У ярлыка повторяется UUID."); found = new(id, EntityKind.Group, path, doc, JsonSemantics.Hash(bytes), null, null, bytes);
            }
        return found ?? throw new SyncException("not_found", "Ярлык не найден.");
    }
    private static string GroupVersion(LocalEntity g, ContactCatalog c) => GroupVersion(g.Id, g.Hash, c);
    private static string GroupVersion(Guid id, string hash, ContactCatalog c) => JsonSemantics.Hash(string.Join("|", c.WorkspaceId, id, hash, c.BindingHash, c.LabelsHash, c.StateHash));
}
