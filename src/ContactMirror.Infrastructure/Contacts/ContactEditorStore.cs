using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using Microsoft.Data.Sqlite;

namespace ContactMirror.Infrastructure.Contacts;

/// <summary>Local-only editor. Reads never initialize or migrate sync state.</summary>
public sealed class ContactEditorStore : IContactEditorStore
{
    public event Action<string>? BackupCreated;
    private const int JsonLimit = 16 * 1024 * 1024, PhotoLimit = 20 * 1024 * 1024;
    private readonly Action<string>? beforeReplace, afterReplace, afterBackup, afterStateCopy;
    public ContactEditorStore() { }
    internal ContactEditorStore(Action<string>? beforeReplace = null, Action<string>? afterReplace = null, Action<string>? afterBackup = null, Action<string>? afterStateCopy = null)
    { this.beforeReplace = beforeReplace; this.afterReplace = afterReplace; this.afterBackup = afterBackup; this.afterStateCopy = afterStateCopy; }
    public static async Task<string> ReadProblemFileAsync(string root, string path, CancellationToken token = default)
    {
        root = Normalize(root);
        var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, path, JsonLimit, token);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException error)
        { throw new SyncException("invalidEncoding", "Файл содержит недопустимые UTF-8 байты. Откройте его внешним редактором для исправления.", inner: error); }
    }
    private sealed record Inspection(ContactCatalog Catalog, string? BindingHash, string LabelsHash);
    public async Task<ContactCatalog> LoadAsync(string root, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default) =>
        (await InspectAsync(root, activeAccount, cancellationToken, afterStateCopy)).Catalog;
    public async Task<ContactSnapshot> ReadAsync(string root, Guid id, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(root, activeAccount, cancellationToken, afterStateCopy);
        var item = inspection.Catalog.Contacts.SingleOrDefault(c => c.Id == id);
        if (item is null) throw new SyncException("missingContact", "Контакт не найден или его идентификатор повторяется. Обновите список и исправьте ошибки папки.");
        var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(inspection.Catalog.Root, item.Path, JsonLimit, cancellationToken);
        var doc = JsonSemantics.ParseObject(bytes, item.Path);
        if (DocumentCodec.ContactForEditor(doc).Id != id) throw Drift();
        var photo = await ReadPhotoAsync(inspection.Catalog.Root, doc, cancellationToken);
        return new(inspection.Catalog.Root, inspection.Catalog.WorkspaceId, id, item.Path, doc, bytes, JsonSemantics.Hash(bytes), photo,
            photo is null ? null : JsonSemantics.Hash(photo), inspection.Catalog.ReadOnlyReason ?? inspection.Catalog.BlockedContacts?.GetValueOrDefault(id), inspection.BindingHash, inspection.LabelsHash, inspection.Catalog.StateHash);
    }
    public async Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo,
        AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default)
    {
        original = CopySnapshot(original);
        var output = (JsonObject)document.DeepClone(); var replacementPhoto = photo.Bytes?.ToArray();
        var parsed = original.IsNew ? DocumentCodec.Contact(output) : DocumentCodec.ContactForEditor(output, original.Document);
        if (parsed.Id != original.Id) throw new SyncException("identityChanged", "Идентификатор контакта не редактируется.");
        if (!JsonNode.DeepEquals(output["google"], original.Document["google"])) throw new SyncException("protectedField", "Служебные сведения Google доступны только для просмотра.");
        if (!photo.Changed && !JsonNode.DeepEquals(output["photo"], original.Document["photo"])) throw new SyncException("protectedField", "Меняйте фотографию через выбор файла.");
        if (photo.Changed && replacementPhoto is not null) FileWorkspaceStore.ValidateImage(replacementPhoto);
        var root = Normalize(original.Root);
        var initial = await InspectAsync(root, activeAccount, cancellationToken, afterStateCopy); RequireWritable(initial, original.Id);
        ValidateLabels(parsed.Labels, initial.Catalog);
        if (original.IsNew) await CheckIdentityAsync(original, initial, cancellationToken);
        if (initial.Catalog.WorkspaceId == Guid.Empty)
        {
            if (!original.IsNew || original.WorkspaceId != Guid.Empty || activeAccount is null)
                throw new SyncException("connectAccount", "Для создания папки контактов подключите Google-аккаунт; файл ещё не сохранён.");
            await using (var initialized = await new FileWorkspaceStore().OpenAsync(root, activeAccount, cancellationToken)) { }
            initial = await InspectAsync(root, activeAccount, cancellationToken, afterStateCopy);
            original = original with { WorkspaceId = initial.Catalog.WorkspaceId, BindingHash = initial.BindingHash, LabelsHash = initial.LabelsHash };
        }
        await using var lease = AcquireLease(root);
        await using var bindingGuard = PinBinding(root);
        using var stateGuards = LiveStatePins.Create(root);
        var inspection = await InspectAsync(root, activeAccount, cancellationToken, afterStateCopy); RequireWritable(inspection, original.Id); CheckBinding(original, inspection);
        ValidateLabels(parsed.Labels, inspection.Catalog); await CheckIdentityAsync(original, inspection, cancellationToken);
        if (!original.IsNew && !photo.Changed && JsonNode.DeepEquals(output, original.Document)) return original;
        await using var oldPhoto = await PinPhotoAsync(original, cancellationToken);
        var backup = await BackupAsync(original, "save", cancellationToken); BackupCreated?.Invoke(backup); afterBackup?.Invoke(backup);
        await RecheckAsync(original, activeAccount, cancellationToken);
        if (photo.Changed)
        {
            if (replacementPhoto is null) output["photo"] = null;
            else
            {
                var relative = $"photos/{original.Id}-{JsonSemantics.Hash(replacementPhoto)[..12].ToLowerInvariant()}.{(replacementPhoto[0] == 0x89 ? "png" : "jpg")}";
                var path = FileWorkspaceStore.SafePhotoPath(root, relative);
                if (!File.Exists(path)) await FileWorkspaceStore.AtomicNewAsync(root, path, replacementPhoto, cancellationToken);
                else if (JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, path, PhotoLimit, cancellationToken)) != JsonSemantics.Hash(replacementPhoto))
                    throw new SyncException("photoCollision", "Файл фотографии с таким именем содержит другие данные.");
                output["photo"] = relative;
            }
        }
        var selectedPhoto = await ReadPhotoAsync(root, output, cancellationToken);
        await using var selectedGuard = await PinPhotoAsync(original with { Document = output, PhotoBytes = selectedPhoto,
            PhotoHash = selectedPhoto is null ? null : JsonSemantics.Hash(selectedPhoto) }, cancellationToken);
        var bytes = JsonSemantics.Serialize(output);
        var committed = original with { Document = output, RawBytes = bytes, Hash = JsonSemantics.Hash(bytes), PhotoBytes = selectedPhoto,
            PhotoHash = selectedPhoto is null ? null : JsonSemantics.Hash(selectedPhoto), ReadOnlyReason = null };
        try
        {
            await FileWorkspaceStore.AtomicReplaceAsync(root, original.Path, bytes, original.Hash, cancellationToken, beforeReplace, afterReplace);
            return committed;
        }
        catch (Exception error)
        {
            throw await ClassifySaveFailureAsync(original, committed, activeAccount, error);
        }
    }
    public async Task DeleteAsync(ContactSnapshot original, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default)
    {
        original = CopySnapshot(original);
        if (original.IsNew) throw new SyncException("missingContact", "Новый контакт ещё не сохранён.");
        var root = Normalize(original.Root);
        await using var lease = AcquireLease(root);
        await using var bindingGuard = PinBinding(root);
        using var stateGuards = LiveStatePins.Create(root);
        var inspection = await InspectAsync(root, activeAccount, cancellationToken, afterStateCopy); RequireWritable(inspection, original.Id); CheckBinding(original, inspection);
        await CheckIdentityAsync(original, inspection, cancellationToken);
        await using var photoGuard = await PinPhotoAsync(original, cancellationToken);
        await using var jsonGuard = new FileStream(original.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var backup = await BackupAsync(original, "delete", cancellationToken); BackupCreated?.Invoke(backup); afterBackup?.Invoke(backup);
        await RecheckAsync(original, activeAccount, cancellationToken);
        var trash = Path.Combine(root, ".contactmirror", "editor-trash", Path.GetFileName(backup));
        FileWorkspaceStore.ValidatePath(root, trash); Directory.CreateDirectory(trash);
        if (original.PhotoBytes is not null) await FileWorkspaceStore.AtomicNewAsync(root, Path.Combine(trash, "before.photo"), original.PhotoBytes, cancellationToken);
        await FileWorkspaceStore.AtomicNewAsync(root, Path.Combine(trash, "manifest.json"), await FileWorkspaceStore.ReadBoundedBytesAsync(root, Path.Combine(backup, "manifest.json"), JsonLimit, cancellationToken), cancellationToken);
        var destination = Path.Combine(trash, "contact.json");
        try
        {
            beforeReplace?.Invoke(original.Path);
            FileWorkspaceStore.ValidatePath(root, original.Path); FileWorkspaceStore.ValidatePath(root, destination);
            File.Move(original.Path, destination, overwrite: false); afterReplace?.Invoke(destination);
            if (JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, destination, JsonLimit, CancellationToken.None)) != original.Hash)
                throw new SyncException("localDrift", "Во время удаления файл был подменён. Перемещённая версия сохранена в корзине, исходная — в резервной копии.");
        }
        catch (Exception error)
        {
            throw await ClassifyDeleteFailureAsync(original, destination, error);
        }
    }
    // Classification uses only the immutable intended bytes, never an arbitrary current file
    // as a replacement baseline. Caller retains the draft whenever the result cannot be proved.
    private static async Task<ContactEditorWriteException> ClassifySaveFailureAsync(ContactSnapshot original, ContactSnapshot intended, AccountIdentity? account, Exception error)
    {
        try
        {
            if (await MatchesSnapshotAsync(intended))
            {
                var inspection = await InspectAsync(original.Root, account, CancellationToken.None);
                var reason = inspection.Catalog.ReadOnlyReason ?? inspection.Catalog.BlockedContacts?.GetValueOrDefault(original.Id);
                if (inspection.Catalog.WorkspaceId != original.WorkspaceId || inspection.BindingHash != original.BindingHash)
                    reason = "Привязка папки изменилась. Проверьте сохранённую версию перед дальнейшей записью.";
                var confirmed = intended with { ReadOnlyReason = reason };
                return new(ContactWriteOutcome.Committed, confirmed,
                    "Контакт записан в папку, но завершение операции не подтверждено обычным способом. Исходная версия и журнал сохранены; проверьте состояние восстановления.", error);
            }
            if (original.IsNew ? !File.Exists(original.Path) : await MatchesSnapshotAsync(original))
                return new(ContactWriteOutcome.Unchanged, null, "Контакт не записан; исходная версия и черновик сохранены.", error);
        }
        catch (Exception inspectionError) when (ReadFailure(inspectionError) || inspectionError is SqliteException) { }
        return new(ContactWriteOutcome.Uncertain, null,
            "Не удалось подтвердить результат записи. Файл мог измениться; черновик и резервная копия сохранены. Обновите каталог и проверьте версии перед повторной записью.", error);
    }
    private static async Task<ContactEditorWriteException> ClassifyDeleteFailureAsync(ContactSnapshot original, string destination, Exception error)
    {
        try
        {
            FileWorkspaceStore.ValidatePath(original.Root, original.Path); FileWorkspaceStore.ValidatePath(original.Root, destination);
            if (!File.Exists(original.Path) && File.Exists(destination)
                && JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(original.Root, destination, JsonLimit, CancellationToken.None)) == original.Hash)
            {
                var trashPhoto = Path.Combine(Path.GetDirectoryName(destination)!, "before.photo"); FileWorkspaceStore.ValidatePath(original.Root, trashPhoto);
                var hash = File.Exists(trashPhoto) ? JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(original.Root, trashPhoto, PhotoLimit, CancellationToken.None)) : null;
                if (hash == original.PhotoHash)
                    return new(ContactWriteOutcome.Committed, null, "Контакт перемещён из папки в корзину, но обычное подтверждение операции не получено. JSON и фото сохранены в резервной копии и корзине.", error);
            }
            if (await MatchesSnapshotAsync(original))
                return new(ContactWriteOutcome.Unchanged, null, "Контакт не удалён; исходная версия сохранена в папке.", error);
        }
        catch (Exception inspectionError) when (ReadFailure(inspectionError)) { }
        return new(ContactWriteOutcome.Uncertain, null,
            "Не удалось подтвердить результат удаления. Проверьте папку, корзину и резервную копию; ни одна обнаруженная версия не подменяет показанный исходный контакт.", error);
    }
    private static async Task<bool> MatchesSnapshotAsync(ContactSnapshot expected)
    {
        FileWorkspaceStore.ValidatePath(expected.Root, expected.Path);
        if (!File.Exists(expected.Path)) return false;
        var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(expected.Root, expected.Path, JsonLimit, CancellationToken.None);
        if (JsonSemantics.Hash(bytes) != expected.Hash) return false;
        var doc = JsonSemantics.ParseObject(bytes, expected.Path);
        if (DocumentCodec.ContactForEditor(doc).Id != expected.Id) return false;
        var photo = await ReadPhotoAsync(expected.Root, doc, CancellationToken.None);
        return (photo is null ? null : JsonSemantics.Hash(photo)) == expected.PhotoHash;
    }
    private static ContactSnapshot CopySnapshot(ContactSnapshot value)
    {
        if (value.Id == Guid.Empty) throw new SyncException("invalidId", "У контакта нет UUID.");
        var copied = value with { Document = (JsonObject)value.Document.DeepClone(), RawBytes = value.RawBytes?.ToArray(), PhotoBytes = value.PhotoBytes?.ToArray() };
        if (copied.Hash is not null && (copied.RawBytes is null || JsonSemantics.Hash(copied.RawBytes) != copied.Hash
            || !JsonNode.DeepEquals(JsonSemantics.ParseObject(copied.RawBytes, copied.Path), copied.Document))) throw Drift();
        if ((copied.PhotoBytes is null ? null : JsonSemantics.Hash(copied.PhotoBytes)) != copied.PhotoHash) throw Drift();
        return copied;
    }
    private static string Normalize(string root)
    {
        FileWorkspaceStore.ValidateControlPaths(root); root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new SyncException("missingFolder", "Папка контактов не найдена.");
        return root;
    }
    private static FileStream AcquireLease(string root)
    {
        FileWorkspaceStore.ValidateControlPaths(root);
        try { return new FileStream(Path.Combine(root, ".contactmirror", "workspace.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new SyncException("workspaceBusy", "Папка используется другим окном. Дождитесь завершения операции.", inner: ex); }
    }
    private static FileStream PinBinding(string root)
    {
        var path = Path.Combine(root, ".contactmirror", "workspace.json"); FileWorkspaceStore.ValidatePath(root, path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static void RequireWritable(Inspection inspection, Guid id)
    {
        var reason = inspection.Catalog.ReadOnlyReason ?? inspection.Catalog.BlockedContacts?.GetValueOrDefault(id);
        if (reason is not null) throw new SyncException("editorReadOnly", reason);
    }
    private static void CheckBinding(ContactSnapshot original, Inspection inspection)
    {
        if (original.WorkspaceId != inspection.Catalog.WorkspaceId || original.BindingHash != inspection.BindingHash || original.LabelsHash != inspection.LabelsHash || original.StateHash is not null && original.StateHash != inspection.Catalog.StateHash) throw Drift();
    }
    private static void ValidateLabels(IEnumerable<Guid> labels, ContactCatalog catalog)
    {
        foreach (var id in labels) if (!catalog.Labels.ContainsKey(id)) throw new SyncException("missingLabel", "Выбранный ярлык отсутствует или заблокирован. Исправьте файл ярлыка и обновите каталог.");
    }
    private static async Task CheckIdentityAsync(ContactSnapshot original, Inspection inspection, CancellationToken token)
    {
        var matches = inspection.Catalog.Contacts.Where(c => c.Id == original.Id).ToArray();
        if (original.IsNew)
        {
            if (matches.Length != 0 || File.Exists(original.Path) || inspection.Catalog.Labels.ContainsKey(original.Id) || inspection.Catalog.TrustedStates?.GetValueOrDefault(original.Id)?.Kind == EntityKind.Group) throw Drift();
            var expected = Path.Combine(original.Root, "contacts", $"{original.Id}.contact.json");
            if (!Path.GetFullPath(original.Path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new SyncException("unsafePath", "Новый контакт должен сохраняться в contacts/ с его UUID.");
            return;
        }
        if (matches.Length != 1 || !matches[0].Path.Equals(original.Path, StringComparison.OrdinalIgnoreCase)) throw Drift();
        var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(original.Root, original.Path, JsonLimit, token);
        if (JsonSemantics.Hash(bytes) != original.Hash || DocumentCodec.ContactForEditor(JsonSemantics.ParseObject(bytes, original.Path)).Id != original.Id) throw Drift();
        var photo = await ReadPhotoAsync(original.Root, JsonSemantics.ParseObject(bytes, original.Path), token);
        if ((photo is null ? null : JsonSemantics.Hash(photo)) != original.PhotoHash) throw Drift();
    }
    private static async Task RecheckAsync(ContactSnapshot original, AccountIdentity? account, CancellationToken token)
    {
        var inspection = await InspectAsync(original.Root, account, token); RequireWritable(inspection, original.Id); CheckBinding(original, inspection);
        await CheckIdentityAsync(original, inspection, token);
    }
    public static Task<byte[]?> ReadPhotoPreviewAsync(string root, string relativePath, CancellationToken token = default) =>
        ReadPhotoAsync(root, new JsonObject { ["photo"] = relativePath }, token);
    private static async Task<byte[]?> ReadPhotoAsync(string root, JsonObject doc, CancellationToken token)
    {
        if (doc["photo"]?.GetValue<string>() is not string relative) return null;
        var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, FileWorkspaceStore.SafePhotoPath(root, relative), PhotoLimit, token);
        token.ThrowIfCancellationRequested();
        FileWorkspaceStore.ValidateImage(bytes); return bytes;
    }
    private static async Task<FileStream?> PinPhotoAsync(ContactSnapshot original, CancellationToken token)
    {
        if (original.Document["photo"]?.GetValue<string>() is not string relative) return null;
        var stream = new FileStream(FileWorkspaceStore.SafePhotoPath(original.Root, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length > PhotoLimit) throw Drift();
            using var memory = new MemoryStream(); await stream.CopyToAsync(memory, token);
            if (JsonSemantics.Hash(memory.ToArray()) != original.PhotoHash) throw Drift(); return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private static async Task<string> BackupAsync(ContactSnapshot original, string action, CancellationToken token)
    {
        var folder = Path.Combine(original.Root, ".contactmirror", "editor-backups", Guid.NewGuid().ToString("N"));
        FileWorkspaceStore.ValidatePath(original.Root, folder); Directory.CreateDirectory(folder);
        if (original.RawBytes is not null) await FileWorkspaceStore.AtomicNewAsync(original.Root, Path.Combine(folder, "before.json"), original.RawBytes, token);
        if (original.PhotoBytes is not null) await FileWorkspaceStore.AtomicNewAsync(original.Root, Path.Combine(folder, "before.photo"), original.PhotoBytes, token);
        var manifest = new JsonObject { ["schemaVersion"] = 1, ["action"] = action, ["workspaceId"] = original.WorkspaceId.ToString(), ["id"] = original.Id.ToString(),
            ["path"] = Path.GetRelativePath(original.Root, original.Path), ["jsonHash"] = original.Hash, ["photoHash"] = original.PhotoHash, ["photoPath"] = original.Document["photo"]?.DeepClone() };
        await FileWorkspaceStore.AtomicNewAsync(original.Root, Path.Combine(folder, "manifest.json"), JsonSemantics.Serialize(manifest), token); return folder;
    }
    private static SyncException Drift() => new("localDrift", "Контакт, фотография, путь или привязка папки изменились. Черновик сохранён в карточке; обновите исходную версию перед записью.");
    private static async Task<Inspection> InspectAsync(string root, AccountIdentity? account, CancellationToken token, Action<string>? afterStateCopy = null)
    {
        root = Normalize(root);
        var issues = new List<WorkspaceIssue>(); var blocked = new Dictionary<Guid, string>();
        var contacts = new List<ContactCatalogItem>(); var labels = new Dictionary<Guid, string>(); var groupHashes = new List<string>(); var boundGroups = new HashSet<Guid>();
        var groupDocumentHashes = new Dictionary<Guid, string>();
        var duplicateContacts = new HashSet<Guid>(); var duplicateGroups = new HashSet<Guid>(); var seenContacts = new HashSet<Guid>();
        string? reason = null, bindingHash = null, stateHash = null; Guid workspaceId = Guid.Empty;
        IReadOnlyDictionary<Guid, EntityState>? trustedStates = null;
        foreach (var kind in new[] { EntityKind.Contact, EntityKind.Group })
        {
            var folder = Path.Combine(root, kind == EntityKind.Contact ? "contacts" : "groups");
            foreach (var path in Enumerate(root, folder))
            {
                token.ThrowIfCancellationRequested();
                if (!path.EndsWith(kind == EntityKind.Contact ? ".contact.json" : ".group.json", StringComparison.OrdinalIgnoreCase)) continue;
                Guid? issueId = Guid.TryParse(Path.GetFileName(path).Split('.')[0], out var filenameId) ? filenameId : null;
                try
                {
                    var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, path, JsonLimit, token);
                    if (kind == EntityKind.Group) groupHashes.Add(Path.GetRelativePath(root, path) + ":" + JsonSemantics.Hash(bytes));
                    var doc = JsonSemantics.ParseObject(bytes, path);
                    if (Guid.TryParse(doc["id"]?.ToString(), out var parsedId)) issueId = parsedId;
                    if (kind == EntityKind.Contact)
                    {
                        var contact = DocumentCodec.ContactForEditor(doc);
                        if (contact.Photo is not null) FileWorkspaceStore.SafePhotoPath(root, contact.Photo); // Bytes are loaded only for an opened card.
                        if (duplicateContacts.Contains(contact.Id) || !seenContacts.Add(contact.Id))
                        {
                            duplicateContacts.Add(contact.Id); blocked[contact.Id] = "Два файла содержат одинаковый UUID. Исправьте дубликат перед редактированием.";
                            foreach (var earlier in contacts.Where(c => c.Id == contact.Id)) issues.Add(new(earlier.Path, blocked[contact.Id], contact.Id));
                            contacts.RemoveAll(c => c.Id == contact.Id); issues.Add(new(path, blocked[contact.Id], contact.Id));
                        }
                        else contacts.Add(new(contact.Id, path, doc, JsonSemantics.Hash(bytes)));
                    }
                    else
                    {
                        var group = DocumentCodec.Group(doc); if (group.Google is not null) boundGroups.Add(group.Id);
                        if (duplicateGroups.Contains(group.Id) || !labels.TryAdd(group.Id, group.Name))
                        {
                            duplicateGroups.Add(group.Id); labels.Remove(group.Id);
                            groupDocumentHashes.Remove(group.Id);
                            blocked[group.Id] = "Ярлык имеет повторяющийся UUID."; issues.Add(new(path, blocked[group.Id], group.Id));
                        }
                        else groupDocumentHashes[group.Id] = JsonSemantics.Hash(bytes);
                    }
                }
                catch (Exception ex) when (ReadFailure(ex))
                {
                    issues.Add(new(path, ex.Message, issueId));
                    if (issueId is Guid id) blocked[id] = "Файл контакта или ярлыка требует исправления: " + ex.Message;
                    if (kind == EntityKind.Group) groupHashes.Add(Path.GetRelativePath(root, path) + ":invalid");
                }
            }
        }
        var labelsHash = JsonSemantics.Hash(string.Join("\n", groupHashes.Order(StringComparer.Ordinal)));
        var manifestPath = Path.Combine(root, ".contactmirror", "workspace.json");
        var metadata = Path.Combine(root, ".contactmirror");
        if (!File.Exists(manifestPath))
        {
            if (Directory.Exists(metadata) && Directory.EnumerateFileSystemEntries(metadata).Any()) reason = "Отсутствует workspace.json при существующем состоянии. Восстановите папку через синхронизацию.";
            else if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) is not ("contacts" or "groups" or "photos" or ".contactmirror"))) reason = "Папка содержит другие файлы. Выберите папку контактов.";
            else if (account is null) reason = "Подключите аккаунт для первоначальной привязки папки. Чтение доступно без подключения.";
            else if (boundGroups.Count > 0 || contacts.Any(c => c.Document["google"] is not null) || issues.Count > 0) reason = "У контактов есть сведения Google без состояния папки. Сначала восстановите синхронизацию.";
            return Result();
        }
        try
        {
            var bytes = await FileWorkspaceStore.ReadBoundedBytesAsync(root, manifestPath, JsonLimit, token); bindingHash = JsonSemantics.Hash(bytes);
            var manifest = JsonSemantics.ParseObject(bytes, "workspace.json");
            if (manifest["schemaVersion"]?.GetValue<int>() != 1 || !Guid.TryParse(manifest["workspaceId"]?.ToString(), out workspaceId)
                || workspaceId == Guid.Empty || string.IsNullOrWhiteSpace(manifest["accountKey"]?.GetValue<string>())) throw new SyncException("invalidManifest", "Манифест папки не поддерживается или повреждён.");
            if (account is not null && manifest["accountKey"]!.GetValue<string>() != account.Key) reason = "Эта папка привязана к другому Google-аккаунту. Запись запрещена.";
            var dbPath = Path.Combine(metadata, "state.db");
            if (!File.Exists(dbPath)) { reason ??= "Отсутствует state.db. Сначала восстановите состояние через синхронизацию."; return Result(); }
            // SQLite read-only WAL access can create a shared-memory file. Inspect bounded copies,
            // including the committed WAL, so merely opening the editor never touches sync files.
            await using var stateCopy = await ReadOnlyStateCopy.CreateAsync(root, dbPath, token, afterStateCopy);
            var db = stateCopy.Connection;
            async Task<object?> Scalar(string sql)
            { using var command = db.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(token); }
            if (Convert.ToInt32(await Scalar("PRAGMA user_version")) != 1) throw new SyncException("schemaVersion", "Версия состояния не поддерживается редактором.");
            if (Convert.ToString(await Scalar("PRAGMA quick_check")) != "ok") throw new SyncException("corruptState", "Состояние папки повреждено.");
            var tableCount = Convert.ToInt32(await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('entities','runs','operations','flags')"));
            if (tableCount != 4) throw new SyncException("corruptState", "В состоянии папки отсутствуют обязательные таблицы.");
            if (Convert.ToString(await Scalar("SELECT value FROM flags WHERE name='recovery'")) == "1") reason ??= "Папка находится в восстановлении. Сначала завершите его через синхронизацию.";
            var states = new Dictionary<Guid, EntityState>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,json FROM entities";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var state = JsonSerializer.Deserialize<EntityState>(reader.GetString(1), JsonSemantics.Options);
                    if (state is null || state.Id == Guid.Empty || !Enum.IsDefined(state.Kind) || state.Id.ToString() != reader.GetString(0)
                        || string.IsNullOrWhiteSpace(state.ResourceName) || !states.TryAdd(state.Id, state)) throw new SyncException("corruptState", "Запись состояния не позволяет определить контакт или ярлык.");
                }
            }
            trustedStates = states;
            stateHash = JsonSemantics.Hash(JsonSemantics.Serialize(states.OrderBy(p => p.Key).Select(p => p.Value).ToArray()));
            foreach (var contact in contacts)
                if (contact.Document["google"] is not null && (!states.TryGetValue(contact.Id, out var state) || state.Kind != EntityKind.Contact))
                    reason ??= "Есть контакты Google без доверенного состояния. Сначала восстановите синхронизацию.";
            if (boundGroups.Any(id => !states.TryGetValue(id, out var state) || state.Kind != EntityKind.Group))
                reason ??= "Есть ярлыки Google без доверенного состояния. Сначала восстановите синхронизацию.";
            var pendingGroups = new HashSet<Guid>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT o.json,r.id FROM operations o LEFT JOIN runs r ON r.id=o.run_id";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var operation = JsonSerializer.Deserialize<JournalOperation>(reader.GetString(0), JsonSemantics.Options);
                    if (operation is null || reader.IsDBNull(1) || operation.EntityId == Guid.Empty || !Enum.IsDefined(operation.Kind)
                        || string.IsNullOrWhiteSpace(operation.Field) || operation.Status is not ("started" or "unknown" or "confirmed" or "committed" or "reconciled" or "failed"))
                        throw new SyncException("corruptState", "Незавершённая операция не позволяет доказать безопасную запись.");
                    if (operation.Status is "started" or "unknown" or "confirmed")
                    {
                        if (operation.Kind == EntityKind.Group) { pendingGroups.Add(operation.EntityId); labels.Remove(operation.EntityId); }
                        else blocked[operation.EntityId] = "У контакта есть незавершённая операция синхронизации. Сначала восстановите её результат.";
                    }
                }
            }
            // A running history without a scoped operation cannot be assumed harmless.
            if (Convert.ToInt32(await Scalar("SELECT count(*) FROM runs r WHERE r.status='running' AND NOT EXISTS (SELECT 1 FROM operations o WHERE o.run_id=r.id)")) > 0)
                reason ??= "Незавершённый запуск не содержит определимых операций. Сначала восстановите синхронизацию.";
            var displaced = Path.Combine(metadata, "recovery", "displaced");
            foreach (var marker in Enumerate(root, displaced).Where(p => p.EndsWith(".recovery.json", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".intent.json", StringComparison.OrdinalIgnoreCase)))
            {
                Guid? id = null;
                try
                {
                    var markerDoc = JsonSemantics.ParseObject(await FileWorkspaceStore.ReadBoundedBytesAsync(root, marker, JsonLimit, token), marker);
                    if (Guid.TryParse(markerDoc["entityId"]?.ToString(), out var scoped) && scoped != Guid.Empty) id = scoped;
                }
                catch (Exception ex) when (ReadFailure(ex)) { }
                var message = "Есть незавершённая атомарная запись. Проверьте сохранённые версии через восстановление синхронизации.";
                issues.Add(new(marker, message, id));
                if (id is null) reason ??= message;
                else if (contacts.Any(c => c.Id == id)) blocked[id.Value] = message;
                else if (labels.ContainsKey(id.Value) || states.GetValueOrDefault(id.Value)?.Kind == EntityKind.Group) { pendingGroups.Add(id.Value); labels.Remove(id.Value); }
                else if (states.GetValueOrDefault(id.Value)?.Kind == EntityKind.Contact) blocked[id.Value] = message;
                else reason ??= message; // Unknown entity type is global, never guessed.
            }
            foreach (var contact in contacts)
            {
                var labelIds = DocumentCodec.ContactForEditor(contact.Document).Labels;
                if (labelIds.Any(id => !labels.ContainsKey(id) || pendingGroups.Contains(id))) blocked[contact.Id] = "Ярлык контакта отсутствует, повторяется или ожидает восстановления. Сначала исправьте ярлык.";
            }
        }
        catch (Exception ex) when (ReadFailure(ex) || ex is SqliteException)
        { reason ??= "Состояние папки нельзя безопасно прочитать. Контакты доступны для просмотра; запись запрещена. " + ex.Message; }
        return Result();
        Inspection Result() => new(new(root, workspaceId, contacts, labels, issues, reason, blocked, bindingHash, labelsHash, stateHash, trustedStates, groupDocumentHashes), bindingHash, labelsHash);
    }
    private static IEnumerable<string> Enumerate(string root, string folder)
    {
        FileWorkspaceStore.ValidatePath(root, folder);
        if (!Directory.Exists(folder)) yield break;
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((folder, 0)); var count = 0;
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 64 || count > 1_000_000) throw new SyncException("folderTooLarge", "Структура папки превышает допустимые пределы.");
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                FileWorkspaceStore.ValidatePath(root, path); count++;
                if (Directory.Exists(path)) pending.Push((path, current.Depth + 1)); else yield return path;
            }
        }
    }
    // Deny-write handles make DB + WAL one coherent point in time on Windows. An
    // already-open SQLite writer prevents capture and therefore blocks editing, rather
    // than letting a checkpoint/truncated WAL produce a valid but stale inspection.
    private sealed class LiveStatePins : IDisposable
    {
        private readonly List<FileStream> guards = [];
        public static LiveStatePins Create(string root)
        {
            FileWorkspaceStore.ValidateControlPaths(root);
            var pins = new LiveStatePins();
            try
            {
                var dbPath = Path.Combine(root, ".contactmirror", "state.db");
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                {
                    var path = dbPath + suffix; FileWorkspaceStore.ValidatePath(root, path);
                    if (suffix.Length > 0 && !File.Exists(path)) continue;
                    pins.guards.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                }
                return pins;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                pins.Dispose();
                throw new SyncException("editorReadOnly", "Состояние папки недоступно или открыто другой программой. Пока нельзя доказать безопасную запись, редактор доступен только для просмотра.", inner: error);
            }
            catch { pins.Dispose(); throw; }
        }
        public void Dispose() { foreach (var guard in guards) guard.Dispose(); guards.Clear(); }
    }
    private sealed class ReadOnlyStateCopy(string folder, SqliteConnection connection) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public static async Task<ReadOnlyStateCopy> CreateAsync(string root, string dbPath, CancellationToken token, Action<string>? afterStateCopy)
        {
            var folder = Path.Combine(Path.GetTempPath(), "ContactMirror-StateInspection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            SqliteConnection? connection = null;
            try
            {
                using var pins = LiveStatePins.Create(root);
                var copyPath = Path.Combine(folder, "state.db");
                foreach (var suffix in new[] { "", "-wal", "-journal" })
                {
                    var source = dbPath + suffix; FileWorkspaceStore.ValidatePath(root, source);
                    if (!File.Exists(source)) continue;
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length > 512L * 1024 * 1024) throw new SyncException("stateTooLarge", "Состояние превышает предел безопасного чтения редактором (512 МБ).");
                    await using var output = new FileStream(copyPath + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                    var buffer = new byte[65536]; long copied = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, token); if (count == 0) break;
                        copied += count;
                        if (copied > 512L * 1024 * 1024) throw new SyncException("stateTooLarge", "Состояние изменилось и превышает предел безопасного чтения.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                    }
                    await output.FlushAsync(token);
                    afterStateCopy?.Invoke(suffix);
                }
                connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copyPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await connection.OpenAsync(token); return new(folder, connection);
            }
            catch { if (connection is not null) await connection.DisposeAsync(); Directory.Delete(folder, true); throw; }
        }
        public async ValueTask DisposeAsync() { await Connection.DisposeAsync(); Directory.Delete(folder, true); }
    }
    private static bool ReadFailure(Exception ex) => ex is SyncException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException;
}
