using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Automation;

public sealed partial class AgentRuntime
{
    private sealed record EditorBackup(string Id, string Directory, Guid WorkspaceId, Guid EntityId, EntityKind Kind, JsonObject? Document, byte[]? Photo, string Version);
    private sealed record LocalRestore(string Root, AccountIdentity Account, EditorBackup Backup, string? CurrentVersion, DateTimeOffset Expires, string Owner, string Digest, string? BindingHash);
    private readonly ConcurrentDictionary<string, LocalRestore> localRestores = new();
    private void RemoveLocalRestore(string id) { if (localRestores.TryRemove(id, out _)) sync.RetentionBudget.Release("restore:" + id); values.RemoveSource("restore:" + id); }
    private void ClearLocalRestores() { foreach (var id in localRestores.Keys.ToArray()) RemoveLocalRestore(id); }
    private async Task<AgentResult> HistoryAsync(AgentCall call, string root, IProgress<SyncProgress>? progress, CancellationToken token)
    {
        var a = call.Arguments;
        if (call.Tool == "history_apply_local_restore")
        {
            var id = Required(a, "planId");
            if (!localRestores.TryGetValue(id, out var plan) || plan.Owner != call.BridgeId || plan.Expires < Now || plan.Account.Key != Connected().Key) throw new SyncException("plan_expired", "План восстановления устарел.");
            configuration().Check(plan.Root); var choices = Choices(a, true);
            if (choices.Length != 1 || choices[0].Key != plan.Backup.EntityId.ToString() || choices[0].Resolution != Resolution.UseLocal || Required(a, "selectionDigest") != plan.Digest)
                throw new SyncException("invalid_selection", "Подтвердите запись из локальной копии с resolution=UseLocal и проверенным digest.");
            var current = await ExistingContactAsync(plan.Root, plan.Backup.EntityId, token);
            var catalog = await CatalogAsync(plan.Root, token); RequireWritable(catalog);
            if (catalog.BindingHash != plan.BindingHash) throw new SyncException("plan_stale", "Привязка папки изменилась; подготовьте новый план восстановления.");
            if (catalog.WorkspaceId != plan.Backup.WorkspaceId || plan.Backup.Document is null) throw new SyncException("backup_invalid", "Копия не принадлежит этой папке или не содержит прежних данных.");
            if (plan.Backup.Kind == EntityKind.Group) return await RestoreGroupAsync(call, plan, catalog, token);
            if ((current is null ? null : Version(current)) != plan.CurrentVersion) throw new SyncException("localDrift", "Контакт изменился после подготовки восстановления.");
            var doc = current is null ? (JsonObject)plan.Backup.Document.DeepClone() : (JsonObject)current.Document.DeepClone();
            if (current is null)
            {
                // A deleted trusted identity may be recreated; metadata is taken from its verified backup, never the caller.
                _ = DocumentCodec.ContactForEditor(doc);
                if (DocumentCodec.ProtectedCategories(doc).Count > 0) throw new SyncException("protectedCategory", "Копия содержит неподдерживаемую категорию; автоматическое создание запрещено.");
            }
            else foreach (var k in new[] { "data", "labels", "starred", "extensions" })
            { if (plan.Backup.Document.ContainsKey(k)) doc[k] = plan.Backup.Document[k]?.DeepClone(); else doc.Remove(k); }
            var original = current ?? new ContactSnapshot(plan.Root, catalog.WorkspaceId, plan.Backup.EntityId,
                Path.Combine(plan.Root, "contacts", plan.Backup.EntityId + ".contact.json"), (JsonObject)doc.DeepClone(), null, null, null, null, BindingHash: catalog.BindingHash, LabelsHash: catalog.LabelsHash, StateHash: catalog.StateHash);
            if (current is null)
            {
                if (catalog.Labels.ContainsKey(plan.Backup.EntityId) || catalog.TrustedStates?.GetValueOrDefault(plan.Backup.EntityId)?.Kind == EntityKind.Group) throw new SyncException("identity_collision", "UUID занят ярлыком.");
                var state = catalog.TrustedStates?.GetValueOrDefault(plan.Backup.EntityId);
                if (doc["google"] is not null && (state is null || state.Kind != EntityKind.Contact || state.Baseline["google"] is not JsonObject)) throw new SyncException("recovery_required", "Нет доверенной связи удалённого контакта.");
                doc["google"] = state?.Baseline["google"]?.DeepClone(); original = original with { Document = (JsonObject)doc.DeepClone() };
            }
            RecordContactIntent(original, doc, new(true, plan.Backup.Photo), "save");
            var saved = await editor.SaveAsync(original, doc, new(true, plan.Backup.Photo), cachedAccount, token);
            RemoveLocalRestore(id); await CommittedAsync(plan.Root); return Saved(saved);
        }
        var catalogView = await CatalogAsync(root, token);
        if (call.Tool == "history_list")
        {
            var rows = new List<JsonNode>();
            foreach (var r in await workspaces.GetAllHistoryAsync(root, token)) rows.Add(new JsonObject { ["backupId"] = "sync:" + r.Id, ["source"] = "sync", ["runId"] = r.Id.ToString(), ["startedAt"] = r.StartedAt.ToString("O"), ["status"] = r.Status,
                ["confirmed"] = r.Confirmed, ["failed"] = r.Failed, ["unknown"] = r.Unknown, ["available"] = r.BackupAvailable, ["bytes"] = r.BackupBytes });
            var folder = Path.Combine(root, ".contactmirror", "editor-backups"); FileWorkspaceStore.ValidatePath(root, folder);
            if (Directory.Exists(folder))
                foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
                {
                    if (!Guid.TryParse(Path.GetFileName(dir), out var uuid)) continue;
                    try
                    {
                        var b = await ReadEditorBackupAsync(root, uuid, token);
                        rows.Add(new JsonObject { ["backupId"] = b.Id, ["source"] = "localEditor", ["id"] = b.EntityId.ToString(), ["entity"] = b.Kind.ToString(), ["available"] = b.Document is not null, ["version"] = b.Version });
                    }
                    catch (SyncException) { rows.Add(new JsonObject { ["backupId"] = "local:" + uuid, ["source"] = "localEditor", ["available"] = false, ["reason"] = "backup_invalid" }); }
                }
            return Ok(Page(a, rows.ToArray(), JsonSemantics.Hash(new JsonArray(rows.Select(r => r.DeepClone()).ToArray()).ToJsonString())));
        }
        var backupId = Required(a, "backupId");
        if (backupId.StartsWith("local:", StringComparison.Ordinal))
        {
            var b = await ReadEditorBackupAsync(root, Guid.Parse(backupId[6..]), token);
            if (b.WorkspaceId != catalogView.WorkspaceId) throw new SyncException("backup_invalid", "Копия принадлежит другой рабочей папке.");
            if (call.Tool == "history_get") return Ok(new { backupId = b.Id, source = "localEditor", id = b.EntityId, entity = b.Kind.ToString(), version = b.Version, available = b.Document is not null,
                document = values.Bound(b.Document, b.Version, "backup:" + b.Id), photo = b.Photo is null ? null : values.Store(b.Photo, b.Version, "backup:" + b.Id, Mime(b.Photo)) });
            if (call.Tool == "history_cleanup_backup")
            {
                Confirm(a); CheckVersion(a, b.Version); RequireWritable(catalogView);
                await using var session = await workspaces.OpenAsync(root, Connected(), token);
                if (session.View.IsRecovery || session.View.Pending.Any() || receipts.Value.BackupIsReferenced(root, backupId)) throw new SyncException("recovery_required", "Нельзя удалять копию незавершённой операции.");
                var reread = await ReadEditorBackupAsync(root, Guid.Parse(backupId[6..]), token); if (b.Version != reread.Version) throw new SyncException("localDrift", "Копия изменилась.");
                var inventory = FileWorkspaceStore.InventoryDirectory(root, b.Directory);
                foreach (var file in inventory.Files) { FileWorkspaceStore.ValidatePath(root, file); File.Delete(file); }
                foreach (var dir in inventory.Directories.OrderByDescending(d => d.Length)) { FileWorkspaceStore.ValidatePath(root, dir); Directory.Delete(dir); }
                Directory.Delete(b.Directory); return Ok(new { backupId, cleaned = true });
            }
            if (call.Tool == "history_prepare_restore")
            {
                CheckAccount(a); if (b.Document is null) throw new SyncException("backup_unavailable", "До создания контакта записи не было; нечего восстанавливать.");
                string? currentVersion;
                if (b.Kind == EntityKind.Contact) currentVersion = (await ExistingContactAsync(root, b.EntityId, token)) is { } c ? Version(c) : null;
                else { try { currentVersion = GroupVersion(await ReadGroupAsync(root, b.EntityId, token), catalogView); } catch (SyncException e) when (e.Code == "not_found") { currentVersion = null; } }
                var planId = Guid.NewGuid().ToString("N"); var digest = JsonSemantics.Hash(b.Version + "|" + currentVersion + "|" + Connected().Key);
                foreach (var old in localRestores.Where(p => p.Value.Expires < Now).ToArray()) RemoveLocalRestore(old.Key);
                if (localRestores.Count >= 8) throw new SyncException("resource_limit", "Слишком много планов восстановления.");
                sync.RetentionBudget.Reserve("restore:" + planId, JsonSemantics.Serialize(b.Document).LongLength + (b.Photo?.LongLength ?? 0));
                localRestores[planId] = new(root, Connected(), b, currentVersion, Now.AddMinutes(15), call.BridgeId, digest, catalogView.BindingHash);
                return Ok(new { planId, source = "localEditor", selectionDigest = digest, entryKey = b.EntityId.ToString(), resolution = "UseLocal", before = currentVersion,
                    local = values.Bound(b.Document, digest, "restore:" + planId), photo = b.Photo is null ? null : values.Store(b.Photo, digest, "restore:" + planId, Mime(b.Photo)), writesGoogle = false });
            }
        }
        else if (backupId.StartsWith("sync:", StringComparison.Ordinal))
        {
            var run = Guid.Parse(backupId[5..]); var items = await workspaces.GetBackupAsync(root, run, token);
            var digest = BackupDigest(items);
            if (call.Tool == "history_get")
            {
                var page = Page(a, items.Select(i => (Func<JsonNode>)(() => JsonSerializer.SerializeToNode(new { i.Id, entity = i.Kind.ToString(), local = values.Bound(i.LocalDocument, digest, "backup:" + backupId + ":local"), google = values.Bound(i.Remote, digest, "backup:" + backupId + ":google"),
                    localPhoto = i.LocalPhoto is null ? null : values.Store(i.LocalPhoto, digest, "backup:" + backupId + ":local", Mime(i.LocalPhoto)), remotePhoto = i.RemotePhoto is null ? null : values.Store(i.RemotePhoto, digest, "backup:" + backupId + ":google", Mime(i.RemotePhoto)) }, JsonSemantics.Options)!)).ToArray(), digest);
                page["backupId"] = backupId; page["source"] = "sync"; page["version"] = digest; return Ok(page);
            }
            if (call.Tool == "history_prepare_restore")
            {
                CheckAccount(a); var p = await sync.PrepareRestoreAsync(root, Connected(), run, progress, token); plans[p.PlanId.ToString()] = new(p.Root, p.Account.Key, p.PreparedAt, await BindingHashAsync(root, token)); return Ok(new { planId = p.PlanId, planDigest = PlanDigest(p), source = "sync" });
            }
            if (call.Tool == "history_cleanup_backup")
            {
                Confirm(a); CheckVersion(a, digest); if (receipts.Value.BackupIsReferenced(root, backupId)) throw new SyncException("recovery_required", "Копия связана с неопределённой записью; проверьте её результат.");
                await sync.CleanupBackupAsync(root, Connected(), run, token); return Ok(new { backupId, cleaned = true });
            }
        }
        throw new SyncException("backup_invalid", "Используйте backupId из history_list.");
    }
    private static string BackupDigest(IReadOnlyList<BackupItem> items) => JsonSemantics.Hash(string.Join("|", items.OrderBy(i => i.Id).Select(i => i.Id + ":" + JsonSemantics.Hash(JsonSemantics.Serialize(i.LocalDocument)) + ":" + JsonSemantics.Hash(JsonSemantics.Serialize(i.Remote)) + ":" + (i.LocalPhoto is null ? "" : JsonSemantics.Hash(i.LocalPhoto)) + ":" + (i.RemotePhoto is null ? "" : JsonSemantics.Hash(i.RemotePhoto)))));
    private async Task<ContactSnapshot?> ExistingContactAsync(string root, Guid id, CancellationToken token)
    {
        var c = await CatalogAsync(root, token);
        if (c.BlockedContacts?.ContainsKey(id) == true || c.Issues.Any(i => i.Id == id)) throw new SyncException("identity_collision", "UUID связан с проблемным файлом; автоматическое восстановление запрещено.");
        return c.Contacts.Any(i => i.Id == id) ? await editor.ReadAsync(root, id, cachedAccount, token) : null;
    }
    private static async Task<EditorBackup> ReadEditorBackupAsync(string root, Guid id, CancellationToken token)
    {
        var dir = Path.Combine(root, ".contactmirror", "editor-backups", id.ToString("N")); FileWorkspaceStore.ValidatePath(root, dir);
        var manifestBytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, Path.Combine(dir, "manifest.json"), 64 * 1024, token);
        var manifest = JsonSemantics.ParseObject(manifestBytes, "backup manifest");
        if (manifest["schemaVersion"]?.GetValue<int>() != 1) throw new SyncException("backup_invalid", "Формат копии не поддерживается.");
        var workspaceId = Uuid(manifest, "workspaceId"); var entity = Uuid(manifest, "id"); var kind = Optional(manifest, "entityKind") == "Group" ? EntityKind.Group : EntityKind.Contact;
        if (Optional(manifest, "entityKind") is { } declaredKind && declaredKind is not ("Contact" or "Group")) throw new SyncException("backup_invalid", "Неизвестный тип копии.");
        if (Optional(manifest, "path") is { } declaredPath)
        {
            var resolved = Path.GetFullPath(Path.Combine(root, declaredPath)); var directory = Path.Combine(root, kind == EntityKind.Contact ? "contacts" : "groups") + Path.DirectorySeparatorChar;
            FileWorkspaceStore.ValidatePath(root, resolved);
            if (Path.IsPathRooted(declaredPath) || !resolved.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new SyncException("backup_invalid", "Путь копии выходит за каталог сущности.");
        }
        var before = Path.Combine(dir, "before.json"); JsonObject? doc = null; byte[]? photo = null;
        if (!File.Exists(before) && Optional(manifest, "jsonHash") is not null) throw new SyncException("backup_invalid", "Объявленный документ копии отсутствует.");
        if (File.Exists(before))
        {
            var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, before, 16 * 1024 * 1024, token);
            if (JsonSemantics.Hash(bytes) != Optional(manifest, "jsonHash")) throw new SyncException("backup_invalid", "SHA256 копии не совпал.");
            doc = JsonSemantics.ParseObject(bytes, "editor backup"); var docId = kind == EntityKind.Contact ? DocumentCodec.ContactForEditor(doc).Id : DocumentCodec.Group(doc).Id;
            if (docId != entity) throw new SyncException("backup_invalid", "UUID копии не совпал.");
        }
        var photoFile = Path.Combine(dir, "before.photo");
        if (!File.Exists(photoFile) && Optional(manifest, "photoHash") is not null) throw new SyncException("backup_invalid", "Объявленная фотография копии отсутствует.");
        if (File.Exists(photoFile))
        { photo = await FileWorkspaceStore.ReadBoundedBytesAsync(root, photoFile, 20 * 1024 * 1024, token); if (JsonSemantics.Hash(photo) != Optional(manifest, "photoHash")) throw new SyncException("backup_invalid", "SHA256 фото копии не совпал."); FileWorkspaceStore.ValidateImage(photo); }
        if (kind == EntityKind.Contact && doc is not null && ((doc["photo"] is null) != (photo is null) || !JsonNode.DeepEquals(doc["photo"], manifest["photoPath"]))) throw new SyncException("backup_invalid", "Документ и фотография копии не согласованы.");
        return new("local:" + id, dir, workspaceId, entity, kind, doc, photo, JsonSemantics.Hash(JsonSemantics.Hash(manifestBytes) + "|" + (doc is null ? "" : JsonSemantics.Hash(JsonSemantics.Serialize(doc))) + "|" + (photo is null ? "" : JsonSemantics.Hash(photo))));
    }
    private static async Task<string> CreateGroupEditorBackupAsync(string root, ContactCatalog catalog, Guid id, LocalEntity? old, string action, CancellationToken token)
    {
        var dir = Path.Combine(root, ".contactmirror", "editor-backups", Guid.NewGuid().ToString("N")); FileWorkspaceStore.ValidatePath(root, dir); Directory.CreateDirectory(dir);
        if (old is not null) await FileWorkspaceStore.AtomicNewAsync(root, Path.Combine(dir, "before.json"), old.RawBytes ?? JsonSemantics.Serialize(old.Document), token);
        var manifest = new JsonObject { ["schemaVersion"] = 1, ["workspaceId"] = catalog.WorkspaceId.ToString(), ["id"] = id.ToString(), ["entityKind"] = "Group", ["action"] = action, ["jsonHash"] = old?.Hash, ["path"] = old is null ? null : Path.GetRelativePath(root, old.Path) };
        await FileWorkspaceStore.AtomicNewAsync(root, Path.Combine(dir, "manifest.json"), JsonSemantics.Serialize(manifest), token);
        return "local:" + Guid.Parse(Path.GetFileName(dir));
    }
    private async Task<AgentResult> RestoreGroupAsync(AgentCall call, LocalRestore plan, ContactCatalog catalog, CancellationToken token)
    {
        LocalEntity? current; try { current = await ReadGroupAsync(plan.Root, plan.Backup.EntityId, token); } catch (SyncException e) when (e.Code == "not_found") { current = null; }
        if ((current is null ? null : GroupVersion(current, catalog)) != plan.CurrentVersion) throw new SyncException("localDrift", "Ярлык изменился.");
        var doc = plan.Backup.Document!; var args = new JsonObject { ["workspace"] = call.Arguments["workspace"]?.DeepClone(), ["name"] = doc["name"]!.DeepClone(), ["clientData"] = doc["clientData"]?.DeepClone() ?? new JsonArray(), ["extensions"] = doc["extensions"]?.DeepClone() ?? new JsonObject() };
        if (current is not null) { args["id"] = current.Id.ToString(); args["expectedVersion"] = plan.CurrentVersion; }
        var result = await GroupAsync(new(current is null ? "groups_create" : "groups_update", args, call.BridgeId), plan.Root, token, current is null ? plan.Backup.EntityId : null, doc["google"] as JsonObject);
        RemoveLocalRestore(Required(call.Arguments, "planId")); return result;
    }
}
