using System.Text.Json;
using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Automation;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.Core;
using ContactMirror.Desktop;
using ContactMirror.Desktop.Contacts;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Returning_from_sync_refreshes_selected_card_and_same_path_photo_then_clears_deleted_contact()
    {
        var fixture=await PrepareRealEditor();var file=Path.Combine(fixture.Root,"contacts",fixture.Id+".contact.json");
        var photoPath=Path.Combine(fixture.Root,"photos","refresh-same-path.png");
        var initial=ThumbnailPng(8,8);await File.WriteAllBytesAsync(photoPath,initial);
        var document=JsonNode.Parse(await File.ReadAllBytesAsync(file))!.AsObject();document["photo"]="photos/refresh-same-path.png";await File.WriteAllBytesAsync(file,JsonSemantics.Serialize(document));
        await EditorUi(()=>UxModel().Contacts.ReloadSelectedAsync());
        await EditorUi(()=>UxModel().ShowSyncCommand.ExecuteAsync(null));
        var replacement=ThumbnailPng(9,8);document["data"]!["names"]![0]!["familyName"]="Обновлено в скрытом каталоге";
        await EditorUi(()=>UxModel().ConfigureAsync(async token=>{await File.WriteAllBytesAsync(photoPath,replacement,token);await File.WriteAllBytesAsync(file,JsonSemantics.Serialize(document),token);}));
        bool ready=false;HeadlessRuntime.Dispatch(()=>ready=UxModel().CanCheck&&!UxModel().Contacts.IsBusy);await Assert.That(ready).IsTrue();
        await EditorUi(()=>UxModel().ShowContactsCommand.ExecuteAsync(null));
        var fresh=false;HeadlessRuntime.Dispatch(()=>{var contacts=UxModel().Contacts;fresh=contacts.Document!["data"]!["names"]![0]!["familyName"]!.ToString()=="Обновлено в скрытом каталоге"&&contacts.PhotoBytes!.SequenceEqual(replacement)&&!contacts.IsDirty;});await Assert.That(fresh).IsTrue();
        await EditorUi(()=>UxModel().ShowSyncCommand.ExecuteAsync(null));await EditorUi(()=>UxModel().ConfigureAsync(_=>{File.Delete(file);return Task.CompletedTask;}));
        await EditorUi(()=>UxModel().ShowContactsCommand.ExecuteAsync(null));
        bool cleared=false;HeadlessRuntime.Dispatch(()=>cleared=!UxModel().Contacts.HasContact&&UxModel().Contacts.Document is null&&UxModel().Contacts.Rows.Count==0);await Assert.That(cleared).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Delete_confirmation_refuses_external_file_drift_preserves_photo_and_reports_completed_failure()
    {
        var fixture=await PrepareRealEditor();var file=Path.Combine(fixture.Root,"contacts",fixture.Id+".contact.json");
        byte[] photo;
        using(var bitmap=new SkiaSharp.SKBitmap(8,8)){bitmap.Erase(SkiaSharp.SKColors.Teal);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);photo=encoded.ToArray();}
        var photoPath=Path.Combine(fixture.Root,"photos","delete-cas.png");await File.WriteAllBytesAsync(photoPath,photo);
        var document=JsonNode.Parse(await File.ReadAllBytesAsync(file))!.AsObject();document["photo"]="photos/delete-cas.png";await File.WriteAllBytesAsync(file,JsonSemantics.Serialize(document));
        await EditorUi(()=>UxModel().Contacts.ReloadSelectedAsync());
        HeadlessRuntime.Dispatch(()=>UxModel().Contacts.AskDelete());
        document["data"]!["names"]![0]!["familyName"]="Внешняя версия перед удалением";var external=JsonSemantics.Serialize(document);await File.WriteAllBytesAsync(file,external);
        await EditorUi(()=>UxModel().Contacts.ConfirmDeleteAsync());
        bool retained=false;string status="";
        HeadlessRuntime.Dispatch(()=>{var model=UxModel().Contacts;retained=model.HasContact&&model.ShowDelete&&!model.IsBusy&&model.Error.Length>0;status=model.Status;});
        await Assert.That(retained).IsTrue();await Assert.That(status.StartsWith("Удаление не выполнено.",StringComparison.Ordinal)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(external)).IsTrue();await Assert.That((await File.ReadAllBytesAsync(photoPath)).SequenceEqual(photo)).IsTrue();
        HeadlessRuntime.Dispatch(()=>UxModel().Contacts.DismissDelete());
    }

    [Test, NotInParallel("DesktopUi")]
    public Task Unchanged_write_keeps_dirty_draft_and_invalidates_previous_sync_plan() => AssertEditorWriteOutcome(ContactWriteOutcome.Unchanged);

    [Test, NotInParallel("DesktopUi")]
    public Task Committed_write_reopens_proven_snapshot_and_reports_recovery_without_dirty_draft() => AssertEditorWriteOutcome(ContactWriteOutcome.Committed);

    [Test, NotInParallel("DesktopUi")]
    public Task Uncertain_write_keeps_dirty_draft_without_adopting_external_bytes_and_invalidates_plan() => AssertEditorWriteOutcome(ContactWriteOutcome.Uncertain);

    [Test, NotInParallel("DesktopUi")]
    public async Task Committed_contact_remains_clean_and_not_busy_when_restarting_folder_watch_fails()
    {
        var fixture = await PrepareRealEditor();
        const string watchFailure = "Injected synthetic watcher restart failure";
        try
        {
            HeadlessRuntime.Dispatch(() =>
            {
                var contacts = UxModel().Contacts;
                contacts.BeginEdit();
                contacts.Mutate(doc => doc["data"]!["names"]![0]!["familyName"] = "Записано до сбоя наблюдения");
                contacts.BeforeWatch = () => throw new IOException(watchFailure);
            });
            await Assert.That(await EditorUi(() => UxModel().Contacts.SaveAsync())).IsTrue();
            bool busy = true, dirty = true, editing = true;
            string status = "", error = "", hash = "";
            HeadlessRuntime.Dispatch(() =>
            {
                var contacts = UxModel().Contacts;
                busy = contacts.IsBusy; dirty = contacts.IsDirty; editing = contacts.IsEditing;
                status = contacts.Status; error = contacts.Error; hash = contacts.Original!.Hash!;
            });
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json"));
            await Assert.That(JsonNode.Parse(bytes)!["data"]!["names"]![0]!["familyName"]!.ToString()).IsEqualTo("Записано до сбоя наблюдения");
            await Assert.That(hash).IsEqualTo(JsonSemantics.Hash(bytes));
            await Assert.That(busy).IsFalse();
            await Assert.That(dirty).IsFalse();
            await Assert.That(editing).IsFalse();
            await Assert.That(status).IsEqualTo("Сохранено в папку. Для отправки в Google проверьте изменения.");
            await Assert.That(error.Contains(watchFailure, StringComparison.Ordinal)).IsTrue();
            HeadlessRuntime.Dispatch(() => UxModel().Contacts.BeforeWatch = null);
            await Assert.That(await EditorUi(() => UxModel().Contacts.ResolveDraftAsync())).IsTrue();
        }
        finally { HeadlessRuntime.Dispatch(() => UxModel().Contacts.BeforeWatch = null); }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task External_change_to_another_file_during_failed_write_is_reported_and_preserved_with_dirty_draft()
    {
        foreach (var outcome in new[] { ContactWriteOutcome.Unchanged, ContactWriteOutcome.Uncertain })
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-editor-other-file-" + Guid.NewGuid().ToString("N"))).FullName;
            var store = new OutcomeEditorStore(root, outcome); store.EnsureDocument();
            await File.WriteAllBytesAsync(store.FilePath, JsonSemantics.Serialize(store.Document));
            var otherId = Guid.NewGuid(); var otherFile = Path.Combine(root, otherId + ".contact.json");
            var other = DocumentCodec.ToJson(new ContactDocument { Id = otherId, Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Другой контакт" }) } });
            await File.WriteAllBytesAsync(otherFile, JsonSemantics.Serialize(other));
            var external = (JsonObject)other.DeepClone(); external["data"]!["names"]![0]!["givenName"] = "Внешняя правка другого контакта";
            var externalBytes = JsonSemantics.Serialize(external);
            var activity = new ApplicationActivity(); ContactsViewModel contacts = null!;
            HeadlessRuntime.Dispatch(() => contacts = new(new ContactEditorService(store, activity), activity, () => null, () => { }, persist: false));
            try
            {
                await EditorUi(() => contacts.LoadAsync(root));
                await EditorUi(() => contacts.SelectAsync(store.Id));
                var queuedEvent = typeof(ContactsViewModel).GetField("externalWriteEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var observedByEditor = false;
                store.BeforeFailure = async () =>
                {
                    await File.WriteAllBytesAsync(otherFile, externalBytes);
                    // Wait for the editor's real watcher to queue the other-file event before releasing the failed write.
                    // An independent watcher would only prove OS delivery, not that the editor observed it while writing.
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        if ((int)queuedEvent.GetValue(contacts)! != 0) { observedByEditor = true; break; }
                        await Task.Delay(20);
                    }
                };
                HeadlessRuntime.Dispatch(() =>
                {
                    contacts.BeginEdit();
                    contacts.Mutate(doc => doc["data"]!["names"]![0]!["givenName"] = "Черновик после ошибки");
                });
                await Assert.That(await EditorUi(() => contacts.SaveAsync())).IsFalse();
                bool dirty = false, busy = true; string status = "", draft = "";
                HeadlessRuntime.Dispatch(() =>
                {
                    dirty = contacts.IsDirty; busy = contacts.IsBusy; status = contacts.Status;
                    draft = contacts.Document!["data"]!["names"]![0]!["givenName"]!.ToString();
                });
                await Assert.That(observedByEditor).IsTrue();
                await Assert.That(dirty).IsTrue();
                await Assert.That(busy).IsFalse();
                await Assert.That(draft).IsEqualTo("Черновик после ошибки");
                await Assert.That(status.Contains("Другие файлы могли измениться. Обновите список; черновик сохранён.", StringComparison.Ordinal)).IsTrue();
                await Assert.That((await File.ReadAllBytesAsync(otherFile)).SequenceEqual(externalBytes)).IsTrue();
            }
            finally { HeadlessRuntime.Dispatch(contacts.Dispose); }
        }
    }

    private async Task AssertEditorWriteOutcome(ContactWriteOutcome outcome)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-editor-outcome-" + Guid.NewGuid().ToString("N"))).FullName;
        var store = new OutcomeEditorStore(root, outcome);
        store.EnsureDocument();
        await File.WriteAllBytesAsync(store.FilePath, JsonSemantics.Serialize(store.Document));
        var coordinator = new TestSyncCoordinator();
        MainWindowViewModel model = null!;
        HeadlessRuntime.Dispatch(() => model = new(new TestAccountConnector(), coordinator, persist: false, editorStore: store) { Folder = root });
        try
        {
            await EditorUi(() => model.ConnectCommand.ExecuteAsync(null));
            await EditorUi(() => model.CheckCommand.ExecuteAsync(null));
            bool applicable = false;
            HeadlessRuntime.Dispatch(() =>
            {
                model.Entries.First(entry => entry.Entry.Kind == ChangeKind.Upload).IsSelected = true;
                applicable = model.HasPreview && model.CanApply;
            });
            await Assert.That(applicable).IsTrue();
            await EditorUi(() => model.Contacts.SelectAsync(store.Id));
            bool draftWasDirty = false;
            HeadlessRuntime.Dispatch(() =>
            {
                model.Contacts.BeginEdit();
                model.Contacts.Mutate(doc => doc["data"]!["names"]![0]!["givenName"] = "Черновик");
                draftWasDirty = model.Contacts.IsDirty;
            });
            await Assert.That(draftWasDirty).IsTrue();
            var saved = await EditorUi(() => model.Contacts.SaveAsync());
            bool dirty = false, editing = false, preview = true, canApply = true, canEdit = true;
            string status = "", error = "", draft = "", originalHash = "", readOnly = "";
            HeadlessRuntime.Dispatch(() =>
            {
                dirty = model.Contacts.IsDirty; editing = model.Contacts.IsEditing;
                preview = model.HasPreview; canApply = model.CanApply; canEdit = model.Contacts.CanEdit;
                status = model.Contacts.Status; error = model.Contacts.Error; readOnly = model.Contacts.ReadOnlyReason;
                draft = model.Contacts.Document!["data"]!["names"]![0]!["givenName"]!.ToString();
                originalHash = model.Contacts.Original!.Hash!;
            });
            await Assert.That(saved).IsEqualTo(outcome == ContactWriteOutcome.Committed);
            await Assert.That(dirty).IsEqualTo(outcome != ContactWriteOutcome.Committed);
            await Assert.That(editing).IsEqualTo(outcome != ContactWriteOutcome.Committed);
            await Assert.That(preview).IsFalse();
            await Assert.That(canApply).IsFalse();
            await Assert.That(draft).IsEqualTo("Черновик");
            await Assert.That(error).IsEqualTo(OutcomeEditorStore.FailureMessage);
            await EditorUi(() => model.ApplyCommand.ExecuteAsync(null));
            await Assert.That(coordinator.AppliedCount).IsEqualTo(0);
            var diskName = JsonNode.Parse(await File.ReadAllBytesAsync(store.FilePath))!["data"]!["names"]![0]!["givenName"]!.ToString();
            if (outcome == ContactWriteOutcome.Committed)
            {
                await Assert.That(status).IsEqualTo("Контакт записан в папку. Завершение записи требует проверки восстановления.");
                await Assert.That(diskName).IsEqualTo("Черновик");
                await Assert.That(originalHash).IsEqualTo(JsonSemantics.Hash(await File.ReadAllBytesAsync(store.FilePath)));
                await Assert.That(readOnly).IsEqualTo(OutcomeEditorStore.RecoveryReason);
                await Assert.That(canEdit).IsFalse();
            }
            else
            {
                await Assert.That(status).IsEqualTo(outcome == ContactWriteOutcome.Unchanged
                    ? "Запись не выполнена. Черновик остаётся в карточке."
                    : "Результат записи не подтверждён. Черновик сохранён; перечитайте файлы и проверьте восстановление.");
                await Assert.That(originalHash).IsEqualTo(store.InitialHash);
                await Assert.That(diskName).IsEqualTo(outcome == ContactWriteOutcome.Unchanged ? "Исходный" : "Внешняя версия");
            }
        }
        finally { HeadlessRuntime.Dispatch(model.Detach); }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Problem_file_modal_shows_exact_malformed_text_readonly_and_rejects_unlisted_paths()
    {
        var fixture = await PrepareRealEditor();
        var goodFile = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        var goodBefore = await File.ReadAllBytesAsync(goodFile);
        var malformedFile = Path.Combine(fixture.Root, "contacts", "malformed.contact.json");
        const string malformed = "{\n  \"data\": [ это исходный сломанный JSON\n";
        await File.WriteAllTextAsync(malformedFile, malformed);
        var malformedBefore = await File.ReadAllBytesAsync(malformedFile);
        await EditorUi(() => UxModel().Contacts.LoadAsync(fixture.Root));
        WorkspaceIssue issue = null!;
        HeadlessRuntime.Dispatch(() => issue = UxModel().Contacts.Issues.Single(item => item.Path == malformedFile));
        await Assert.That(await EditorUi(() => UxModel().Contacts.ReadProblemFileAsync(issue))).IsEqualTo(malformed);
        var rejected = false;
        try { await EditorUi(() => UxModel().Contacts.ReadProblemFileAsync(new WorkspaceIssue(goodFile, "Не входит в список ошибок"))); }
        catch (InvalidOperationException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
        HeadlessRuntime.Dispatch(() => ((Button)EditorControl("ContactIssuesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        try
        {
            HeadlessRuntime.Dispatch(() => ((ListBox)EditorControl("ContactIssuesList")).SelectedItem = issue);
            string raw = ""; bool readOnly = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                HeadlessRuntime.Dispatch(() => { var box = (TextBox)EditorControl("ContactIssueRaw"); raw = box.Text ?? ""; readOnly = box.IsReadOnly; });
                if (raw == malformed) break;
                await Task.Delay(30);
            }
            await Assert.That(raw).IsEqualTo(malformed);
            await Assert.That(readOnly).IsTrue();
            CaptureCheckpoint("editor-problem-file-readonly");
            await Assert.That((await File.ReadAllBytesAsync(goodFile)).SequenceEqual(goodBefore)).IsTrue();
            await Assert.That((await File.ReadAllBytesAsync(malformedFile)).SequenceEqual(malformedBefore)).IsTrue();
        }
        finally { HeadlessRuntime.Dispatch(() => ((Button)EditorControl("ContactConfigurationClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))); }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Malformed_preferences_restore_usable_defaults_without_rewriting_source_file()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-editor-malformed-prefs-" + Guid.NewGuid().ToString("N"))).FullName;
        var file = Path.Combine(root, "preferences.json");
        await File.WriteAllTextAsync(file, "{ malformed preferences");
        var before = await File.ReadAllBytesAsync(file);
        var preferences = new ContactTablePreferences(false, true, file);
        var restored = preferences.Load(Guid.NewGuid(), root);
        await Assert.That(string.Join("|", restored.Table.Columns)).IsEqualTo(string.Join("|", ContactTableConfiguration.Default.Columns));
        await Assert.That(preferences.Notice).IsEqualTo("Настройки таблицы не удалось прочитать. Восстановлен обычный набор столбцов.");
        await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Old_preference_version_restores_defaults_without_reusing_stale_filters_or_search()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-editor-old-prefs-" + Guid.NewGuid().ToString("N"))).FullName;
        var file = Path.Combine(root, "preferences.json"); var id = Guid.NewGuid();
        var old = ContactTableConfiguration.Default with { Version = 0, Columns = [new("removed-column", 10)], Filters = [new("displayName", ContactFilterOperator.Equal, "Старая версия")], Search = "Скрыть контакты" };
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new Dictionary<string, ContactWorkspacePreferences> { [id.ToString("N")] = new(old, .67) }));
        var before = await File.ReadAllBytesAsync(file);
        var restored = new ContactTablePreferences(false, true, file).Load(id, root);
        await Assert.That(string.Join("|", restored.Table.Columns)).IsEqualTo(string.Join("|", ContactTableConfiguration.Default.Columns));
        await Assert.That(restored.Table.Filters.Count).IsEqualTo(0);
        await Assert.That(restored.Table.Search).IsEqualTo("");
        await Assert.That(restored.Table.Version).IsEqualTo(1);
        await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Preferences_drop_unknown_columns_filters_and_sorts_preserving_valid_user_configuration()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-editor-unknown-prefs-" + Guid.NewGuid().ToString("N"))).FullName;
        var file = Path.Combine(root, "preferences.json"); var id = Guid.NewGuid();
        var table = ContactTableConfiguration.Default with { Columns = [new("removed-column"), new("starred", 117)], Sorts = [new("removed-column"), new("starred", true)], Filters = [new("removed-column", ContactFilterOperator.Equal, "bad"), new("displayName", ContactFilterOperator.Contains, "Анна")], Search = "transient" };
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new Dictionary<string, ContactWorkspacePreferences> { [id.ToString("N")] = new(table, 9) }));
        var before = await File.ReadAllBytesAsync(file);
        var restored = new ContactTablePreferences(false, true, file).Load(id, root);
        await Assert.That(restored.Table.Columns.Single()).IsEqualTo(new ContactColumnSetting("starred", 117));
        await Assert.That(restored.Table.Sorts.Single()).IsEqualTo(new ContactSortRule("starred", true));
        await Assert.That(restored.Table.Filters.Single()).IsEqualTo(new ContactFilterRule("displayName", ContactFilterOperator.Contains, "Анна"));
        await Assert.That(restored.Table.Search).IsEqualTo("");
        await Assert.That(restored.TableFraction).IsEqualTo(.8);
        await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
    }

    private sealed class OutcomeEditorStore(string root, ContactWriteOutcome outcome) : IContactEditorStore
    {
        public const string FailureMessage = "Сбой завершения локальной записи.";
        public const string RecoveryReason = "Требуется проверить восстановление.";
        public Guid Id { get; } = Guid.NewGuid();
        private readonly Guid workspace = Guid.NewGuid();
        public JsonObject Document { get; private set; } = null!;
        public string FilePath => Path.Combine(root, Id + ".contact.json");
        public string InitialHash { get; private set; } = "";
        public Func<Task>? BeforeFailure { get; set; }
        private string? readOnly;

        private ContactSnapshot Snapshot()
        {
            Document ??= DocumentCodec.ToJson(new ContactDocument { Id = Id, Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Исходный" }) } });
            var bytes = JsonSemantics.Serialize(Document);
            var hash = JsonSemantics.Hash(bytes);
            if (InitialHash.Length == 0) InitialHash = hash;
            return new(root, workspace, Id, FilePath, (JsonObject)Document.DeepClone(), bytes, hash, null, null, readOnly);
        }
        public Task<ContactCatalog> LoadAsync(string path, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default)
        {
            var snapshot = Snapshot();
            return Task.FromResult(new ContactCatalog(root, workspace, [new(Id, FilePath, snapshot.Document)], new Dictionary<Guid, string>(), [], readOnly));
        }
        public Task<ContactSnapshot> ReadAsync(string path, Guid id, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public async Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default)
        {
            if (BeforeFailure is { } callback) await callback();
            ContactSnapshot? committed = null;
            if (outcome == ContactWriteOutcome.Committed)
            {
                Document = (JsonObject)document.DeepClone(); readOnly = RecoveryReason;
                committed = Snapshot(); await File.WriteAllBytesAsync(FilePath, committed.RawBytes!, cancellationToken);
            }
            else if (outcome == ContactWriteOutcome.Uncertain)
            {
                var external = (JsonObject)original.Document.DeepClone(); external["data"]!["names"]![0]!["givenName"] = "Внешняя версия";
                await File.WriteAllBytesAsync(FilePath, JsonSemantics.Serialize(external), cancellationToken);
            }
            throw new ContactEditorWriteException(outcome, committed, FailureMessage, new IOException("Injected synthetic completion failure"));
        }
        public Task DeleteAsync(ContactSnapshot original, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void EnsureDocument() => _ = Snapshot();
    }
}
