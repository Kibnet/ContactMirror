using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Contacts;
using Microsoft.Data.Sqlite;
using SkiaSharp;
using Xunit;

namespace ContactMirror.GoogleTests;

public sealed class ContactEditorStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ContactMirror-Editor-" + Guid.NewGuid().ToString("N"));
    private static readonly AccountIdentity Account = new("editor-synthetic", "test@example.test");
    private readonly ContactEditorStore store = new();
    public ContactEditorStoreTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static JsonObject Contact(Guid id, string name = "Тестовый") => DocumentCodec.ToJson(new ContactDocument { Id = id,
        Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = name }) } });
    private static byte[] Image(SKColor? color = null)
    {
        using var bitmap = new SKBitmap(3, 3); bitmap.Erase(color ?? SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private async Task<Guid> SetupAsync(bool photo = false)
    {
        await using (var session = await new FileWorkspaceStore().OpenAsync(root, Account)) { }
        var id = Guid.NewGuid(); var doc = Contact(id); if (photo) { doc["photo"] = "photos/test.png"; await File.WriteAllBytesAsync(Path.Combine(root, "photos/test.png"), Image()); }
        await File.WriteAllBytesAsync(Path.Combine(root, "contacts", id + ".contact.json"), JsonSemantics.Serialize(doc)); return id;
    }
    private async Task SqlAsync(string sql)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".contactmirror/state.db"), Pooling = false }.ToString());
        await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private Dictionary<string, string> StateBytes() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), p => JsonSemantics.Hash(ReadShared(p)), StringComparer.Ordinal);
    private static byte[] ReadShared(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray(); }
    private void AssertSameState(Dictionary<string, string> before)
    {
        var after = StateBytes(); Assert.Equal(before.Count, after.Count); foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
    }
    private async Task<ContactSnapshot> NewSnapshotAsync(Guid? selectedId = null, AccountIdentity? account = null)
    {
        var catalog = await store.LoadAsync(root, account); var id = selectedId ?? Guid.NewGuid();
        return new(root, catalog.WorkspaceId, id, Path.Combine(root, "contacts", id + ".contact.json"), Contact(id), null, null, null, null,
            catalog.ReadOnlyReason, catalog.BindingHash, catalog.LabelsHash);
    }
    [Fact]
    public async Task ReadingUnboundFolderDoesNotCreateMetadataOrPhotos()
    {
        Directory.CreateDirectory(Path.Combine(root, "contacts")); var id = Guid.NewGuid();
        var doc = Contact(id); doc["photo"] = "photos/missing.png"; // Catalog must not decode every photo.
        await File.WriteAllBytesAsync(Path.Combine(root, "contacts", id + ".contact.json"), JsonSemantics.Serialize(doc));
        var before = StateBytes(); var catalog = await store.LoadAsync(root);
        Assert.Single(catalog.Contacts); Assert.Contains("Подключите", catalog.ReadOnlyReason); AssertSameState(before); Assert.False(Directory.Exists(Path.Combine(root, ".contactmirror")));
    }
    [Fact]
    public async Task NewFileOnlyAppearsOnExplicitSaveAndInitializesWithActiveAccount()
    {
        var draft = await NewSnapshotAsync(); Assert.False(Directory.Exists(Path.Combine(root, ".contactmirror")));
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(draft, draft.Document, new()));
        Assert.False(Directory.Exists(Path.Combine(root, ".contactmirror")));
        var saved = await store.SaveAsync(draft, draft.Document, new(), Account);
        Assert.False(saved.IsNew); Assert.True(File.Exists(saved.Path)); Assert.NotEqual(Guid.Empty, saved.WorkspaceId);
        Assert.Equal(saved.Hash, (await store.ReadAsync(root, saved.Id)).Hash);
    }
    [Fact]
    public async Task OfflineSaveKeepsExactRawBackupAndSyncStateBytes()
    {
        var id = await SetupAsync(true); var original = await store.ReadAsync(root, id); var beforeDb = File.ReadAllBytes(Path.Combine(root, ".contactmirror/state.db"));
        var changed = (JsonObject)original.Document.DeepClone(); changed["data"]!["names"]![0]!["givenName"] = "Исправленный";
        changed["extensions"] = new JsonObject { ["nested"] = new JsonArray(1, null, new JsonObject { ["ok"] = true }) };
        var saved = await store.SaveAsync(original, changed, new());
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-backups")));
        Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "before.json")));
        Assert.Equal(original.PhotoBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "before.photo")));
        Assert.Equal(beforeDb, await File.ReadAllBytesAsync(Path.Combine(root, ".contactmirror/state.db")));
        Assert.Equal(saved.RawBytes, await File.ReadAllBytesAsync(saved.Path));
        Assert.True(JsonNode.DeepEquals(changed, (await store.ReadAsync(root, id)).Document));
    }
    [Fact]
    public async Task NoOpSaveDoesNotCreateBackupOrRewriteAnyBytes()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var before = StateBytes();
        await store.SaveAsync(original, original.Document, new()); AssertSameState(before);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("newer")]
    [InlineData("global")]
    [InlineData("unscoped")]
    [InlineData("pendingUnknownKind")]
    public async Task UnknownStateIsReadOnlyAndCannotMutateOrInitialize(string defect)
    {
        var id = await SetupAsync(); var snapshot = await store.ReadAsync(root, id);
        var dbPath = Path.Combine(root, ".contactmirror/state.db");
        switch (defect)
        {
            case "missing": File.Delete(dbPath); break;
            case "corrupt": await File.WriteAllBytesAsync(dbPath, "not a database"u8.ToArray()); break;
            case "newer": await SqlAsync("PRAGMA user_version=9"); break;
            case "global": await SqlAsync("INSERT INTO flags VALUES('recovery','1')"); break;
            case "unscoped": await File.WriteAllTextAsync(Path.Combine(root, ".contactmirror/recovery/displaced/unknown.intent.json"), "{}"); break;
            case "pendingUnknownKind": await SqlAsync("INSERT INTO runs(id,started,status) VALUES('run','2026-10-09','partial'); INSERT INTO operations VALUES('run','key','{\"key\":\"key\",\"entityId\":\"" + id + "\",\"kind\":17,\"field\":\"names\",\"status\":\"unknown\"}')"); break;
        }
        var before = StateBytes(); var catalog = await store.LoadAsync(root); Assert.NotNull(catalog.ReadOnlyReason); Assert.Single(catalog.Contacts);
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(snapshot, Contact(id, "Другая версия"), new()));
        await Assert.ThrowsAsync<SyncException>(() => store.DeleteAsync(snapshot));
        var newDraft = await NewSnapshotAsync(); await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(newDraft, newDraft.Document, new(), Account));
        AssertSameState(before);
    }
    [Fact]
    public async Task ScopedPendingBlocksOnlyItsContactAndGroupReferences()
    {
        var first = await SetupAsync(); var second = Guid.NewGuid(); await File.WriteAllBytesAsync(Path.Combine(root, "contacts", second + ".contact.json"), JsonSemantics.Serialize(Contact(second)));
        await SqlAsync("INSERT INTO runs(id,started,status) VALUES('run','2026-10-09','partial'); INSERT INTO operations VALUES('run','key','{\"key\":\"key\",\"entityId\":\"" + first + "\",\"kind\":0,\"field\":\"names\",\"status\":\"unknown\"}')");
        var catalog = await store.LoadAsync(root); Assert.Null(catalog.ReadOnlyReason); Assert.Contains(first, catalog.BlockedContacts!.Keys); Assert.DoesNotContain(second, catalog.BlockedContacts.Keys);
        var blocked = await store.ReadAsync(root, first); await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(blocked, Contact(first, "Нельзя"), new()));
        var allowed = await store.ReadAsync(root, second); await store.SaveAsync(allowed, Contact(second, "Можно"), new());
    }
    [Fact]
    public async Task DuplicateUuidAndInvalidJsonAppearAsCatalogIssuesNeverOverwrite()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id);
        await File.WriteAllBytesAsync(Path.Combine(root, "contacts/duplicate.contact.json"), original.RawBytes!);
        await File.WriteAllTextAsync(Path.Combine(root, "contacts/broken.contact.json"), "{bad"); var before = StateBytes();
        var catalog = await store.LoadAsync(root); Assert.Empty(catalog.Contacts); Assert.Equal(3, catalog.Issues.Count);
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, Contact(id, "Нельзя"), new())); AssertSameState(before);
    }
    [Fact]
    public async Task AccountAndBindingDriftAreRejectedIncludingOfflineEditor()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id);
        Assert.NotNull((await store.LoadAsync(root, new("other", "other@example.test"))).ReadOnlyReason);
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, Contact(id, "Нельзя"), new(), new("other", "other@example.test")));
        var path = Path.Combine(root, ".contactmirror/workspace.json"); var manifest = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(path), path);
        manifest["accountKey"] = "changed"; await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(manifest)); var before = StateBytes();
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, Contact(id, "Нельзя"), new())); AssertSameState(before);
    }
    [Theory]
    [InlineData("json")]
    [InlineData("photo")]
    [InlineData("rename")]
    [InlineData("uuid")]
    public async Task ShownSnapshotRejectsExternalChangeOnSaveAndDelete(string field)
    {
        var id = await SetupAsync(true); var original = await store.ReadAsync(root, id);
        if (field == "photo") await File.WriteAllBytesAsync(Path.Combine(root, "photos/test.png"), Image(SKColors.Lime));
        else if (field == "rename") File.Move(original.Path, Path.Combine(root, "contacts/renamed.contact.json"));
        else await File.WriteAllBytesAsync(original.Path, JsonSemantics.Serialize(Contact(field == "uuid" ? Guid.NewGuid() : id, "Внешняя правка")));
        var before = StateBytes(); await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, Contact(id, "Черновик"), new()));
        await Assert.ThrowsAsync<SyncException>(() => store.DeleteAsync(original)); AssertSameState(before);
    }
    [Fact]
    public async Task PhotoReplacementRemovalAndDeletePreserveBeforeImagesAndSharedPhoto()
    {
        var id = await SetupAsync(true); var old = await store.ReadAsync(root, id); var replacement = Image(SKColors.Blue);
        var saved = await store.SaveAsync(old, old.Document, new(true, replacement)); Assert.Equal(replacement, saved.PhotoBytes); Assert.NotEqual(old.Document["photo"]!.ToString(), saved.Document["photo"]!.ToString());
        Assert.Equal(old.PhotoBytes, await File.ReadAllBytesAsync(Path.Combine(root, "photos/test.png")));
        var removed = await store.SaveAsync(saved, saved.Document, new(true)); Assert.Null(removed.PhotoBytes); Assert.Null(removed.Document["photo"]);
        await store.DeleteAsync(removed); Assert.False(File.Exists(removed.Path));
        var trash = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-trash"))); Assert.Equal(removed.RawBytes, await File.ReadAllBytesAsync(Path.Combine(trash, "contact.json")));
        Assert.True(File.Exists(Path.Combine(root, "photos/test.png"))); Assert.True(File.Exists(FileWorkspaceStore.SafePhotoPath(root, saved.Document["photo"]!.GetValue<string>())));
    }
    [Fact]
    public async Task DeleteCopiesExactShownPhotoBeforeMovingJson()
    {
        var id = await SetupAsync(true); var shown = await store.ReadAsync(root, id); await store.DeleteAsync(shown);
        var trash = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-trash")));
        Assert.Equal(shown.PhotoBytes, await File.ReadAllBytesAsync(Path.Combine(trash, "before.photo"))); Assert.Equal(shown.RawBytes, await File.ReadAllBytesAsync(Path.Combine(trash, "contact.json")));
    }
    [Fact]
    public async Task BackupFailureAndPostBackupJsonDriftNeverOverwriteExternalBytes()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var target = Path.Combine(root, ".contactmirror/editor-backups"); await File.WriteAllTextAsync(target, "blocked");
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(original, Contact(id, "Не записать"), new())); Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(original.Path)); File.Delete(target);
        var external = JsonSemantics.Serialize(Contact(id, "Внешняя версия"));
        var injected = new ContactEditorStore(afterBackup: _ => File.WriteAllBytes(original.Path, external));
        await Assert.ThrowsAsync<SyncException>(() => injected.SaveAsync(original, Contact(id, "Черновик"), new())); Assert.Equal(external, await File.ReadAllBytesAsync(original.Path));
        var backup = Assert.Single(Directory.GetDirectories(target)); Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "before.json")));
    }
    [Fact]
    public async Task PhotoCannotBeReplacedBetweenBackupAndDeleteMove()
    {
        var id = await SetupAsync(true); var original = await store.ReadAsync(root, id); var attempts = 0;
        var injected = new ContactEditorStore(afterBackup: _ => { attempts++; Assert.Throws<IOException>(() => File.WriteAllBytes(Path.Combine(root, "photos/test.png"), Image(SKColors.Pink))); });
        await injected.DeleteAsync(original); Assert.Equal(1, attempts); Assert.Equal(original.PhotoBytes, await File.ReadAllBytesAsync(Path.Combine(root, "photos/test.png")));
    }
    [Fact]
    public async Task CompetingAtomicReplaceRetainsActualExternalDisplacedVersionAndBlocksNextEdit()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var external = JsonSemantics.Serialize(Contact(id, "Конкурентная"));
        var injected = new ContactEditorStore(beforeReplace: path => { var temp = path + ".external"; File.WriteAllBytes(temp, external); File.Replace(temp, path, path + ".previous", false); });
        var outcome = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.SaveAsync(original, Contact(id, "Черновик"), new()));
        Assert.Equal(ContactWriteOutcome.Committed, outcome.Outcome); Assert.NotNull(outcome.Snapshot); Assert.NotNull(outcome.Snapshot.ReadOnlyReason);
        var displaced = Directory.GetFiles(Path.Combine(root, ".contactmirror/recovery/displaced"), "*.json").Where(p => !p.EndsWith(".recovery.json") && !p.EndsWith(".intent.json")).ToArray();
        Assert.Equal(external, await File.ReadAllBytesAsync(Assert.Single(displaced))); Assert.NotNull((await store.ReadAsync(root, id)).ReadOnlyReason);
    }
    [Fact]
    public async Task GroupChangeMissingLabelAndConcurrentWorkspaceLeaseBlockSave()
    {
        var id = await SetupAsync(); var groupId = Guid.NewGuid(); var groupPath = Path.Combine(root, "groups", groupId + ".group.json");
        await File.WriteAllBytesAsync(groupPath, JsonSemantics.Serialize(new GroupDocument { Id = groupId, Name = "Тест" })); var original = await store.ReadAsync(root, id);
        await using (var other = await new FileWorkspaceStore().OpenAsync(root, Account))
            await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, Contact(id, "Нельзя"), new()));
        await File.WriteAllBytesAsync(groupPath, JsonSemantics.Serialize(new GroupDocument { Id = groupId, Name = "Изменён" }));
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, Contact(id, "Нельзя"), new()));
        original = await store.ReadAsync(root, id); var invalid = Contact(id); invalid["labels"] = new JsonArray(Guid.NewGuid().ToString());
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(original, invalid, new()));
    }
    [Fact]
    public async Task WalInspectionSeesPendingWithoutChangingLiveDatabaseWalOrSharedMemory()
    {
        var id = await SetupAsync();
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".contactmirror/state.db"), Pooling = false }.ToString());
        await writer.OpenAsync(); using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO flags VALUES('recovery','1')"; await command.ExecuteNonQueryAsync();
        var before = StateBytes(); var catalog = await store.LoadAsync(root);
        Assert.NotNull(catalog.ReadOnlyReason); AssertSameState(before); Assert.Single(catalog.Contacts);
    }
    [Fact]
    public async Task FailureAfterAtomicCommitKeepsOriginalBackupAndDurableRecoveryIntent()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id);
        var changed = Contact(id, "Записано, подтверждение потеряно");
        var injected = new ContactEditorStore(afterReplace: _ => throw new IOException("injected after commit"));
        var outcome = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.SaveAsync(original, changed, new()));
        Assert.Equal(ContactWriteOutcome.Committed, outcome.Outcome); Assert.NotNull(outcome.Snapshot);
        Assert.Equal(JsonSemantics.Serialize(changed), outcome.Snapshot.RawBytes); Assert.NotNull(outcome.Snapshot.ReadOnlyReason);
        Assert.Equal(JsonSemantics.Serialize(changed), await File.ReadAllBytesAsync(original.Path));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-backups")));
        Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "before.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, ".contactmirror/recovery/displaced"), "*.intent.json"));
        Assert.NotNull((await store.ReadAsync(root, id)).ReadOnlyReason);
    }

    [Fact]
    public async Task All23CategoriesSaveAndReopenWithoutLosingOtherFieldsOrExtensions()
    {
        var id = await SetupAsync(); var snapshot = await store.ReadAsync(root, id);
        var document = (JsonObject)snapshot.Document.DeepClone();
        document["extensions"] = new JsonObject { ["source"] = new JsonObject { ["unicode"] = "Ёж", ["array"] = new JsonArray(null, 7, true) } };
        foreach (var field in CapabilityRegistry.WritableFields)
        {
            var sample = JsonNode.Parse(field switch
            {
                "names" => "[{\"givenName\":\"Иван\",\"familyName\":\"Петренко\"}]",
                "addresses" => "[{\"city\":\"Москва\",\"streetAddress\":\"Тестовая 17\",\"postalCode\":\"123456\"}]",
                "birthdays" or "events" => "[{\"date\":{\"month\":5,\"day\":12}}]",
                "clientData" or "userDefined" => "[{\"key\":\"test\",\"value\":\"Ёж и 17\"}]",
                "organizations" => "[{\"name\":\"Организация\",\"title\":\"Инженер\",\"department\":\"Разработка\"}]",
                "calendarUrls" => "[{\"url\":\"https://example.test/calendar\"}]",
                "urls" => "[{\"value\":\"https://example.test/site\"}]",
                "imClients" => "[{\"username\":\"ivan_test\",\"protocol\":\"custom\"}]",
                "relations" => "[{\"person\":\"Павел\",\"type\":\"friend\"}]",
                "biographies" => "[{\"value\":\"Многострочная заметка\\nВторая строка\",\"contentType\":\"TEXT_PLAIN\"}]",
                "locales" => "[{\"value\":\"ru-RU\"}]",
                "phoneNumbers" => "[{\"value\":\"+7 (999) 123-45-67\",\"type\":\"mobile\"}]",
                "emailAddresses" => "[{\"value\":\"ivan@example.test\",\"type\":\"work\"}]",
                _ => "[{\"value\":\"Тестовое значение\"}]"
            });
            Assert.Empty(CapabilityRegistry.ValidateEditable(field, sample)); document["data"]![field] = sample;
            snapshot = await store.SaveAsync(snapshot, document, new()); snapshot = await store.ReadAsync(root, id);
            Assert.True(JsonNode.DeepEquals(document, snapshot.Document), field);
        }
        Assert.Equal(23, CapabilityRegistry.WritableFields.Count());
    }
    [Fact]
    public async Task BoundGoogleGroupWithoutEntityStateBlocksWholeWorkspace()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id);
        var group = new GroupDocument { Id = Guid.NewGuid(), Name = "Служебный ярлык", Google = new JsonObject { ["resourceName"] = "contactGroups/synthetic" } };
        await File.WriteAllBytesAsync(Path.Combine(root, "groups", group.Id + ".group.json"), JsonSemantics.Serialize(group)); var before = StateBytes();
        Assert.NotNull((await store.LoadAsync(root)).ReadOnlyReason);
        await Assert.ThrowsAsync<SyncException>(() => store.DeleteAsync(original)); AssertSameState(before);
    }

    [Fact]
    public async Task BindingCannotChangeBetweenBackupAndCommit()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var attempted = false;
        var injected = new ContactEditorStore(afterBackup: _ =>
        {
            attempted = true;
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(root, ".contactmirror/workspace.json"), "{}"));
        });
        await injected.SaveAsync(original, Contact(id, "Обновлён"), new()); Assert.True(attempted);
        Assert.Equal(original.BindingHash, (await store.ReadAsync(root, id)).BindingHash);
    }

    [Fact]
    public async Task FailureBeforeReplaceIsExplicitlyUnchangedAndRetainsDraftBaseline()
    {
        var id = await SetupAsync(true); var original = await store.ReadAsync(root, id);
        var injected = new ContactEditorStore(beforeReplace: _ => throw new IOException("injected before replace"));
        var changed = (JsonObject)original.Document.DeepClone(); changed["data"]!["names"]![0]!["givenName"] = "Черновик";
        var failure = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.SaveAsync(original, changed, new()));
        Assert.Equal(ContactWriteOutcome.Unchanged, failure.Outcome); Assert.Null(failure.Snapshot);
        Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(original.Path)); Assert.Equal(original.PhotoBytes, await File.ReadAllBytesAsync(Path.Combine(root, "photos/test.png")));
    }
    [Fact]
    public async Task ExternalVersionAfterCommittedReplaceIsUncertainNeverRebaselinedAsOurs()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var external = JsonSemantics.Serialize(Contact(id, "Поздняя чужая версия"));
        var injected = new ContactEditorStore(afterReplace: path => { File.WriteAllBytes(path, external); throw new IOException("injected after external write"); });
        var failure = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.SaveAsync(original, Contact(id, "Черновик"), new()));
        Assert.Equal(ContactWriteOutcome.Uncertain, failure.Outcome); Assert.Null(failure.Snapshot); Assert.Equal(external, await File.ReadAllBytesAsync(original.Path));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-backups")));
        Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "before.json")));
    }
    [Fact]
    public async Task FailureAfterDeleteMoveIsCommittedWithExactJsonPhotoTrash()
    {
        var id = await SetupAsync(true); var original = await store.ReadAsync(root, id);
        var injected = new ContactEditorStore(afterReplace: _ => throw new IOException("injected after delete move"));
        var failure = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.DeleteAsync(original));
        Assert.Equal(ContactWriteOutcome.Committed, failure.Outcome); Assert.Null(failure.Snapshot); Assert.False(File.Exists(original.Path));
        var trash = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-trash")));
        Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(Path.Combine(trash, "contact.json")));
        Assert.Equal(original.PhotoBytes, await File.ReadAllBytesAsync(Path.Combine(trash, "before.photo")));
    }
    [Fact]
    public async Task FailureBeforeDeleteMoveIsUnchangedAndKeepsContactInFolder()
    {
        var id = await SetupAsync(true); var original = await store.ReadAsync(root, id);
        var injected = new ContactEditorStore(beforeReplace: _ => throw new IOException("injected before move"));
        var failure = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.DeleteAsync(original));
        Assert.Equal(ContactWriteOutcome.Unchanged, failure.Outcome); Assert.Null(failure.Snapshot);
        Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(original.Path));
    }
    [Fact]
    public async Task ForeignJsonMovedToTrashIsUncertainAndNeverReplacesShownBaseline()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var foreign = JsonSemantics.Serialize(Contact(id, "Чужая версия перед удалением"));
        var injected = new ContactEditorStore(beforeReplace: path => { var temp = path + ".foreign"; File.WriteAllBytes(temp, foreign); File.Replace(temp, path, path + ".previous"); });
        var failure = await Assert.ThrowsAsync<ContactEditorWriteException>(() => injected.DeleteAsync(original));
        Assert.Equal(ContactWriteOutcome.Uncertain, failure.Outcome); Assert.Null(failure.Snapshot);
        var trash = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-trash"))); Assert.Equal(foreign, await File.ReadAllBytesAsync(Path.Combine(trash, "contact.json")));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".contactmirror/editor-backups"))); Assert.Equal(original.RawBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "before.json")));
    }

    private void AttemptRecoveryCommitAndCheckpoint()
    {
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".contactmirror/state.db"), Pooling = false }.ToString());
        writer.Open(); using var command = writer.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO flags(name,value) VALUES('recovery','1'); PRAGMA wal_checkpoint(TRUNCATE);"; command.ExecuteNonQuery();
    }
    [Fact]
    public async Task SqliteCommitAndCheckpointBetweenBaseAndWalCopiesAreDeniedByCoherentPin()
    {
        var id = await SetupAsync(); var before = StateBytes(); var attempted = false;
        var injected = new ContactEditorStore(afterStateCopy: suffix =>
        {
            if (suffix != "") return;
            attempted = true; Assert.Throws<SqliteException>(AttemptRecoveryCommitAndCheckpoint);
        });
        var catalog = await injected.LoadAsync(root); Assert.True(attempted); Assert.Null(catalog.ReadOnlyReason); Assert.Single(catalog.Contacts);
        var after = StateBytes(); foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
        // The external SQLite connection can create empty WAL/SHM while falling back
        // to read-only; its INSERT and checkpoint are denied. The editor changes none.
        Assert.All(after.Keys.Except(before.Keys), path => Assert.True(path.EndsWith("state.db-wal") || path.EndsWith("state.db-shm"), path));
    }
    [Fact]
    public async Task SqlitePendingCannotBeInsertedBetweenInspectionAndEditorCommit()
    {
        var id = await SetupAsync(); var original = await store.ReadAsync(root, id); var dbBefore = ReadShared(Path.Combine(root, ".contactmirror/state.db")); var attempted = false;
        var injected = new ContactEditorStore(afterBackup: _ => { attempted = true; Assert.Throws<SqliteException>(AttemptRecoveryCommitAndCheckpoint); });
        await injected.SaveAsync(original, Contact(id, "Обновлён"), new()); Assert.True(attempted);
        Assert.Equal(dbBefore, ReadShared(Path.Combine(root, ".contactmirror/state.db")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnboundGoogleGroupOrUninspectableJsonNeverInitializesMetadataOnSave(bool invalidJson)
    {
        Directory.CreateDirectory(Path.Combine(root, "groups"));
        var group = new GroupDocument { Id = Guid.NewGuid(), Name = "Google без состояния", Google = new JsonObject { ["resourceName"] = "contactGroups/synthetic" } };
        var path = Path.Combine(root, "groups", group.Id + ".group.json");
        await File.WriteAllBytesAsync(path, invalidJson ? "{uninspectable"u8.ToArray() : JsonSemantics.Serialize(group)); var before = StateBytes();
        var catalog = await store.LoadAsync(root, Account); Assert.NotNull(catalog.ReadOnlyReason);
        var draft = await NewSnapshotAsync(account: Account);
        await Assert.ThrowsAsync<SyncException>(() => store.SaveAsync(draft, draft.Document, new(), Account));
        AssertSameState(before); Assert.False(Directory.Exists(Path.Combine(root, ".contactmirror")));
    }

    [Fact]
    public async Task ProblemFileReadIsBoundedUtf8AndNeverReadsOutsideWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(root, "contacts")); var path = Path.Combine(root, "contacts/broken.contact.json");
        await File.WriteAllTextAsync(path, "{ошибка JSON"); Assert.Equal("{ошибка JSON", await ContactEditorStore.ReadProblemFileAsync(root, path));
        await Assert.ThrowsAsync<SyncException>(() => ContactEditorStore.ReadProblemFileAsync(root, Path.Combine(root, "..", "outside.json")));
        await File.WriteAllBytesAsync(path, new byte[] { 0xff });
        Assert.Equal("invalidEncoding", (await Assert.ThrowsAsync<SyncException>(() => ContactEditorStore.ReadProblemFileAsync(root, path))).Code);
        await using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) oversized.SetLength(16 * 1024 * 1024 + 1);
        Assert.Equal("fileTooLarge", (await Assert.ThrowsAsync<SyncException>(() => ContactEditorStore.ReadProblemFileAsync(root, path))).Code);
    }

}
