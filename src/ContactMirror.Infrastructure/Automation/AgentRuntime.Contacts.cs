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
    private static void ValidateArguments(AgentCall call)
    {
        if (!AgentTools.Names.Contains(call.Tool, StringComparer.Ordinal)) throw new SyncException("unknown_tool", "Инструмент не найден.");
        var schema = AgentToolSchema.Input(call.Tool); var properties = schema["properties"]!.AsObject();
        if (call.Arguments.Any(p => !properties.ContainsKey(p.Key))) throw new SyncException("invalid_arguments", "Переданы неизвестные параметры.");
        foreach (var required in schema["required"]!.AsArray()) if (!call.Arguments.ContainsKey(required!.GetValue<string>())) throw new SyncException("invalid_arguments", "Не заполнены обязательные параметры.");
        foreach (var (key, value) in call.Arguments)
        {
            var p = properties[key]!;
            if (value is null && key == "expectedAccountKey") continue;
            var expected = p["type"] is JsonValue t ? t.GetValue<string>() : "string";
            var valid = expected switch { "string" => value?.GetValueKind() == JsonValueKind.String, "integer" => value is JsonValue n && n.TryGetValue<int>(out _), "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False, "object" => value is JsonObject, "array" => value is JsonArray, _ => false };
            if (!valid) throw new SyncException("invalid_arguments", $"Неверный тип {key}.");
            if (value is JsonArray a && p["maxItems"] is JsonValue max && a.Count > max.GetValue<int>()) throw new SyncException("resource_limit", $"Слишком много значений {key}.");
            if (expected == "string" && value!.GetValue<string>().Length > p["maxLength"]!.GetValue<int>()) throw new SyncException("resource_limit", $"Слишком длинный параметр {key}.");
            if (expected == "integer" && (value!.GetValue<int>() < p["minimum"]!.GetValue<int>() || value.GetValue<int>() > p["maximum"]!.GetValue<int>())) throw new SyncException("invalid_arguments", $"Параметр {key} вне допустимых границ.");
        }
        _ = JsonNode.Parse(call.Arguments.ToJsonString(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
    }
    private async Task<AgentResult> DispatchAsync(AgentCall call, string? root, IProgress<SyncProgress>? progress, CancellationToken token)
    {
        var a = call.Arguments;
        values.SetContext(root, cachedAccount?.Key, root is not null && File.Exists(Path.Combine(root,".contactmirror","workspace.json")) ? await BindingHashAsync(root,token) : null);
        switch (call.Tool)
        {
            case "workspace_list": return Ok(configuration().AllowedRoots);
            case "workspace_open":
            {
                var catalog = await editor.LoadAsync(root!, cachedAccount, token);
                foreach (var old in handles.Where(p => p.Value.Root == root && (p.Value.WorkspaceId != catalog.WorkspaceId || p.Value.BindingHash != catalog.BindingHash)).ToArray()) handles.TryRemove(old.Key, out _);
                var handle = Guid.NewGuid().ToString("N"); handles[handle] = new(root!, catalog.WorkspaceId, cachedAccount?.Key, catalog.BindingHash);
                return Ok(new { workspace = handle, catalog.WorkspaceId, initialized = catalog.WorkspaceId != Guid.Empty, catalog.ReadOnlyReason, contacts = catalog.Contacts.Count, groups = catalog.Labels.Count });
            }
            case "workspace_get_status":
            {
                var c = await CatalogAsync(root, token); return Ok(new { c.WorkspaceId, c.ReadOnlyReason, blockedContacts = c.BlockedContacts, contacts = c.Contacts.Count, groups = c.Labels.Count,
                    issues = Page(a, c.Issues.Select(i => JsonSerializer.SerializeToNode(i, JsonSemantics.Options)!).ToArray(), JsonSemantics.Hash(c.BindingHash + c.LabelsHash)) });
            }
            case "workspace_initialize":
            {
                CheckAccount(a); Directory.CreateDirectory(root!); if (Directory.EnumerateFileSystemEntries(root!).Any()) throw new SyncException("workspace_not_empty", "Инициализировать можно только пустую папку.");
                await using var session = await workspaces.OpenAsync(root!, Connected(), token); handles.Clear(); return Ok(new { initialized = true });
            }
            case "contacts_get_schema": return Ok(new { apiVersion = 1, fields = ContactFieldSchema.Fields, columns = ContactTableQuery.Columns, operators = Enum.GetNames<ContactFilterOperator>(),
                singletonCategories = new[] { "names", "birthdays", "biographies", "genders" }, photo = new { mimeTypes = new[] { "image/jpeg", "image/png" }, maximumBytes = 20 * 1024 * 1024 }, documentMaximumBytes = 16 * 1024 * 1024 });
            case "contacts_get": return Contact(await editor.ReadAsync(root!, Uuid(a, "id"), cachedAccount, token));
            case "contacts_list":
            {
                var catalog = await CatalogAsync(root, token);
                var filters = (a["filters"] as JsonArray ?? []).Select(n =>
                {
                    var f = n!.AsObject(); if (f.Any(p => p.Key is not ("columnId" or "operator" or "value"))) throw new SyncException("invalid_filter", "Неизвестный параметр фильтра.");
                    if (!Enum.TryParse<ContactFilterOperator>(Required(f, "operator"), false, out var op) || op.ToString() != Required(f, "operator")) throw new SyncException("invalid_filter", "Неизвестный оператор.");
                    var col = ContactTableQuery.Columns.SingleOrDefault(c => c.Id == Required(f, "columnId")) ?? throw new SyncException("invalid_filter", "Неизвестная колонка.");
                    var operand = Optional(f, "value") ?? ""; if (operand.Length > 10000) throw new SyncException("invalid_filter", "Значение фильтра превышает 10000 символов.");
                    if (!ContactTableQuery.IsValidOperand(col, op, operand)) throw new SyncException("invalid_filter", "Оператор или значение не подходит типу колонки.");
                    return new ContactFilterRule(col.Id, op, operand);
                }).ToArray();
                var sorts = (a["sorts"] as JsonArray ?? []).Select(n =>
                {
                    var s = n!.AsObject(); if (s.Any(p => p.Key is not ("columnId" or "descending"))) throw new SyncException("invalid_sort", "Неизвестный параметр сортировки.");
                    var id = Required(s, "columnId"); if (!ContactTableQuery.Columns.Any(c => c.Id == id)) throw new SyncException("invalid_sort", "Неизвестная колонка."); return new ContactSortRule(id, Flag(s, "descending"));
                }).ToArray();
                var projection = (a["projection"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? new[] { "displayName", "data.phoneNumbers.value", "data.emailAddresses.value", "labels", "starred" };
                if (projection.Any(p => !ContactTableQuery.Columns.Any(c => c.Id == p))) throw new SyncException("invalid_projection", "Неизвестная колонка.");
                var config = new ContactTableConfiguration([], sorts.Length == 0 ? [new("displayName")] : sorts, filters, Flag(a, "matchAny"), Optional(a, "search") ?? "");
                var rows = new List<Func<JsonNode>>(); var rowVersions = new List<string>();
                foreach (var item in ContactTableQuery.Apply(catalog, config, token))
                {
                    string? photoHash = null;
                    if (item.Document["photo"] is JsonValue photo)
                        photoHash = JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root!, FileWorkspaceStore.SafePhotoPath(root!, photo.GetValue<string>()), 20 * 1024 * 1024, token));
                    var version = Version(new(root!, catalog.WorkspaceId, item.Id, item.Path, item.Document, null, item.DocumentHash ?? JsonSemantics.Hash(JsonSemantics.Serialize(item.Document)), null, photoHash, BindingHash: catalog.BindingHash, LabelsHash: catalog.LabelsHash, StateHash: catalog.StateHash));
                    rowVersions.Add(version);
                    rows.Add(() => new JsonObject { ["id"] = item.Id.ToString(), ["version"] = version, ["values"] = new JsonObject(projection.Select(p => KeyValuePair.Create(p, values.Bound(JsonValue.Create(ContactValuePresenter.FormatColumn(item.Document, p, catalog.Labels)), version, "contact:" + item.Id)))) });
                }
                return Ok(Page(a, rows.ToArray(), JsonSemantics.Hash(CatalogRevision(catalog) + string.Join("|", rowVersions))));
            }
            case "contacts_create":
            {
                var c = await CatalogAsync(root, token); RequireWritable(c);
                var id = Uuid(a, "_allocatedId"); var doc = DocumentCodec.ToJson(new ContactDocument { Id = id });
                doc["data"] = a["data"]!.DeepClone(); foreach (var k in new[] { "labels", "starred", "extensions" }) if (a.ContainsKey(k)) doc[k] = a[k]!.DeepClone();
                var original = new ContactSnapshot(root!, c.WorkspaceId, id, Path.Combine(root!, "contacts", id + ".contact.json"), DocumentCodec.ToJson(new ContactDocument { Id = id }), null, null, null, null, BindingHash: c.BindingHash, LabelsHash: c.LabelsHash, StateHash: c.StateHash);
                var photo = PhotoEdit(a, call.BridgeId); RecordContactIntent(original, doc, photo, "save");
                var saved = await editor.SaveAsync(original, doc, photo, cachedAccount, token); await CommittedAsync(root); return Saved(saved);
            }
            case "contacts_update":
            case "contacts_delete":
            {
                var original = await editor.ReadAsync(root!, Uuid(a, "id"), cachedAccount, token); CheckVersion(a, Version(original));
                if (call.Tool == "contacts_delete")
                { Confirm(a); RecordContactIntent(original, original.Document, new(), "delete"); await editor.DeleteAsync(original, cachedAccount, token); await CommittedAsync(root); return Ok(new { id = original.Id, outcome = "committed", deleted = true }); }
                var updated = AgentPatch.Apply(original.Document, a["patches"]!.AsArray());
                var photoChange = PhotoEdit(a, call.BridgeId); RecordContactIntent(original, updated, photoChange, "save");
                var saved = await editor.SaveAsync(original, updated, photoChange, cachedAccount, token); await CommittedAsync(root); return Saved(saved);
            }
            case "app_open_contact": await Ui.OpenContactAsync(root!, Uuid(a, "id")); return Ok(new { opened = true });
            case "account_connect":
                if (!Flag(a, "interactive")) throw new SyncException("interactive_required", "Вход открывает браузер; укажите interactive=true.");
                cachedAccount = await account.SignInAsync(token); AccountTransitioned(call); handles.Clear(); plans.Clear(); values.Clear(); ClearLocalRestores(); await sync.InvalidatePlansAsync(reason: "account_changed"); await Ui.ChangedAsync(null); return Ok(new { connected = true, identity = cachedAccount });
            case "account_disconnect":
                await account.SignOutAsync(Flag(a, "revoke"), token); cachedAccount = null; AccountTransitioned(call); handles.Clear(); plans.Clear(); values.Clear(); ClearLocalRestores(); await sync.InvalidatePlansAsync(reason: "account_changed"); await Ui.ChangedAsync(null); return Ok(new { connected = false });
            case "photo_upload_begin": return Ok(new { uploadId = uploads.Begin(call.BridgeId, Uuid(a, "uploadKey").ToString(), Number(a, "length"), Required(a, "sha256")) });
            case "photo_upload_chunk": uploads.Chunk(call.BridgeId, Required(a, "uploadId"), Number(a, "offset"), Convert.FromBase64String(Required(a, "base64"))); return Ok(new { received = true });
            case "photo_upload_complete":
                var bytes = uploads.Complete(call.BridgeId, Required(a, "uploadId")); return Ok(new { sealedUpload = true, sha256 = JsonSemantics.Hash(bytes), totalBytes = bytes.Length });
            case "photo_upload_abort": uploads.Abort(call.BridgeId, Required(a, "uploadId")); return Ok(new { aborted = true });
            case "values_read": return Ok(await values.ReadAsync(Required(a, "valueId"), Required(a, "version"), Number(a, "offset"), Number(a, "count", 1024 * 1024), scope => AuthorizeValueAsync(scope, token)));
            case "photo_get":
                if (a.ContainsKey("valueId")) return Ok(await values.ReadAsync(Required(a, "valueId"), Required(a, "version"), Number(a, "offset"), Number(a, "count", 1024 * 1024), scope => AuthorizeValueAsync(scope, token)));
                var snapshot = await editor.ReadAsync(root!, Uuid(a, "id"), cachedAccount, token); CheckVersion(a, Version(snapshot));
                return snapshot.PhotoBytes is null ? Ok(new { present = false }) : Ok(AgentValues.Chunk(snapshot.PhotoBytes, Number(a, "offset"), Number(a, "count", 1024 * 1024), Version(snapshot), Mime(snapshot.PhotoBytes)));
            default: return await DispatchWorkflowAsync(call, root, progress, token);
        }
    }
    private async Task<ContactCatalog> CatalogAsync(string? root, CancellationToken token)
    {
        if (root is null) throw new SyncException("invalid_arguments", "Укажите workspace.");
        var c = await editor.LoadAsync(root, cachedAccount, token);
        return c;
    }
    private static string CatalogRevision(ContactCatalog c) => JsonSemantics.Hash(c.WorkspaceId + "|" + c.BindingHash + "|" + c.LabelsHash + "|" + string.Join("|", c.Contacts.OrderBy(i => i.Id).Select(i => i.Id + ":" + JsonSemantics.Hash(JsonSemantics.Serialize(i.Document)))));
    private static void RequireWritable(ContactCatalog c) { if (c.ReadOnlyReason is not null) throw new SyncException("editorReadOnly", c.ReadOnlyReason); if (c.WorkspaceId == Guid.Empty) throw new SyncException("workspace_uninitialized", "Сначала явно инициализируйте пустую папку."); }
    private static void Confirm(JsonObject a) { if (!Flag(a, "confirmDelete")) throw new SyncException("confirmation_required", "Укажите confirmDelete=true после проверки удаления."); }
    private static void CheckVersion(JsonObject a, string version) { if (Required(a, "expectedVersion") != version) throw new SyncException("localDrift", "Версия изменилась. Прочитайте актуальные данные перед записью."); }
    private void CheckAccount(JsonObject a) { if (Required(a, "expectedAccountKey") != Connected().Key) throw new SyncException("account_changed", "Аккаунт изменился."); }
    private ContactPhotoEdit PhotoEdit(JsonObject a, string bridge)
    {
        if (a["photo"] is not JsonObject photo) return new();
        if (photo.Any(p => p.Key is not ("action" or "uploadId"))) throw new SyncException("invalid_arguments", "Неизвестный параметр photo.");
        return Required(photo, "action") switch { "remove" => new(true), "set" => new(true, uploads.Take(bridge, Required(photo, "uploadId"))), _ => throw new SyncException("invalid_arguments", "photo.action должен быть set/remove.") };
    }
    private AgentResult Contact(ContactSnapshot s)
    {
        var version = Version(s); var presentation = new JsonObject();
        foreach (var col in ContactTableQuery.Columns.Take(144)) presentation[col.Id] = values.Bound(JsonValue.Create(ContactValuePresenter.FormatColumn(s.Document, col.Id)), version, "contact:" + s.Id);
        return Ok(new { s.Id, version, document = values.Bound(s.Document, version, "contact:" + s.Id), friendly = values.Bound(presentation, version, "presentation:" + s.Id), s.ReadOnlyReason,
            protectedCategories = DocumentCodec.ProtectedCategories(s.Document), photo = s.PhotoBytes is null ? null : new { sha256 = s.PhotoHash, totalBytes = s.PhotoBytes.Length, mimeType = Mime(s.PhotoBytes) } });
    }
    private static AgentResult Saved(ContactSnapshot s) => Ok(new { id = s.Id, version = Version(s), photoHash = s.PhotoHash, outcome = "committed" });
    private void RecordContactIntent(ContactSnapshot original, JsonObject doc, ContactPhotoEdit photo, string action)
    {
        WriteIntent(new() { ["action"] = action, ["entityKind"] = "Contact", ["entityId"] = original.Id.ToString(), ["workspaceId"] = original.WorkspaceId.ToString(), ["expectedHash"] = original.Hash,
            ["expectedPhotoHash"] = original.PhotoHash, ["intendedHash"] = ContentHash(doc, photo.Changed ? photo.Bytes is null ? null : JsonSemantics.Hash(photo.Bytes) : original.PhotoHash) });
    }
    private static string ContentHash(JsonObject doc, string? photoHash)
    {
        var copy = (JsonObject)doc.DeepClone(); copy.Remove("photo"); return JsonSemantics.Hash(Canonical(copy)!.ToJsonString() + "|" + photoHash);
    }
    private async Task CommittedAsync(string? root) { await sync.InvalidatePlansAsync(root); foreach (var p in plans.Where(p => p.Value.Root == root).ToArray()) plans.TryRemove(p.Key, out _); await Ui.ChangedAsync(root); }
    private static string Mime(byte[] b) => b.Length > 2 && b[0] == 0xff && b[1] == 0xd8 ? "image/jpeg" : "image/png";
    private async Task AuthorizeValueAsync(AgentValues.Scope scope, CancellationToken token)
    {
        if (scope.Root is not null) configuration().Check(scope.Root);
        if (scope.Root is not null && scope.BindingHash != await BindingHashAsync(scope.Root,token)) throw new SyncException("value_stale", "Привязка папки изменилась; прочитайте данные заново.");
        if (scope.AccountKey != cachedAccount?.Key) throw new SyncException("account_changed", "Значение принадлежит прежнему аккаунту.");
        var parts = scope.Source.Split(':');
        if (parts[0] == "plan")
        {
            var p = await sync.ReadPreparedPlanAsync(Guid.Parse(parts[1])); if (PlanDigest(p) != scope.Version) throw new SyncException("value_stale", "План изменился.");
        }
        else if (parts[0] is "contact" or "presentation")
        { var current = await editor.ReadAsync(scope.Root!, Guid.Parse(parts[1]), cachedAccount, token); if (Version(current) != scope.Version) throw new SyncException("value_stale", "Контакт изменился."); }
        else if (parts[0] == "group")
        { var c = await CatalogAsync(scope.Root, token); if (GroupVersion(await ReadGroupAsync(scope.Root!, Guid.Parse(parts[1]), token), c) != scope.Version) throw new SyncException("value_stale", "Ярлык изменился."); }
        else if (parts[0] == "restore")
        { if (!localRestores.TryGetValue(parts[1], out var p) || p.Expires < Now || p.Owner != PlanOrigin.Current || p.Digest != scope.Version) throw new SyncException("value_stale", "Восстановление устарело."); }
        else if (parts[0] == "backup")
        {
            var actual = parts[1] == "local" ? (await ReadEditorBackupAsync(scope.Root!, Guid.Parse(parts[2]), token)).Version : BackupDigest(await workspaces.GetBackupAsync(scope.Root!, Guid.Parse(parts[2]), token));
            if (actual != scope.Version) throw new SyncException("value_stale", "Резервная копия изменилась.");
        }
    }
    private readonly ConcurrentDictionary<string, (string Revision, string Query, int Offset, DateTimeOffset Expires)> cursors = new();
    private JsonObject Page(JsonObject args, JsonNode[] rows, string revision) => Page(args, rows.Select(n => (Func<JsonNode>)(() => n)).ToArray(), revision);
    private JsonObject Page(JsonObject args, Func<JsonNode>[] rows, string revision)
    {
        var queryArgs = (JsonObject)args.DeepClone(); queryArgs.Remove("cursor"); queryArgs.Remove("take");
        var query = JsonSemantics.Hash(Canonical(queryArgs)!.ToJsonString()); var offset = 0;
        if (Optional(args, "cursor") is { } cursor)
        {
            if (!cursors.TryRemove(cursor, out var c) || c.Expires < Now || c.Revision != revision || c.Query != query) throw new SyncException("cursor_stale", "Страница устарела. Запросите первую страницу заново.");
            offset = c.Offset;
        }
        var page = new JsonArray(); long bytes = 0; var take = Number(args, "take", 50);
        for (; offset < rows.Length && page.Count < take; offset++)
        {
            var row = rows[offset](); var length = AgentWireBudget.Measure(row);
            if (bytes + length > AgentWireBudget.MaximumPayload && page.Count > 0) break;
            if (length > AgentWireBudget.MaximumPayload) throw new SyncException("resource_limit", "Одна запись превышает допустимый размер ответа.");
            page.Add(row.DeepClone()); bytes += length;
        }
        string? next = null;
        if (offset < rows.Length) { next = Guid.NewGuid().ToString("N"); cursors[next] = (revision, query, offset, Now.AddSeconds(60)); }
        foreach (var expired in cursors.Where(p => p.Value.Expires < Now).ToArray()) cursors.TryRemove(expired.Key, out _);
        if (cursors.Count > 256) foreach (var old in cursors.OrderBy(p => p.Value.Expires).Take(cursors.Count - 256)) cursors.TryRemove(old.Key, out _);
        return new() { ["items"] = page, ["total"] = rows.Length, ["nextCursor"] = next, ["revision"] = revision };
    }
}
