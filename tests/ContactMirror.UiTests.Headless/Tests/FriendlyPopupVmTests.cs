using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Desktop.Contacts;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    private async Task<FriendlyVmFixture> FriendlyVm(bool edit = true, string? readOnlyReason = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-friendly-vm-" + Guid.NewGuid().ToString("N"))).FullName;
        var store = new FriendlyVmStore(root,readOnlyReason); ContactsViewModel vm = null!;
        HeadlessRuntime.Dispatch(() => vm = new(new ContactEditorService(store,store.Activity),store.Activity,() => null,() => { },persist:false));
        await EditorUi(() => vm.LoadAsync(root)); await EditorUi(() => vm.SelectAsync(store.Id));
        if (edit) HeadlessRuntime.Dispatch(vm.BeginEdit);
        return new(vm,store,root);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Owned_popup_blocks_competing_actions_then_commits_one_patch_without_retiring_live_document()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model;
        EditorPopupContext token = null!; JsonObject document = null!; string untouched = "";
        bool modal = false, ordinaryBlocked = false, secondBlocked = false;
        HeadlessRuntime.Dispatch(() =>
        {
            document = vm.Document!; untouched = document["extensions"]!.ToJsonString();
            token = vm.BeginEditorPopup("data.names.0.familyName")!;
            modal = vm.HasModal;
            ordinaryBlocked = !vm.Mutate(doc => doc["starred"] = true);
            secondBlocked = vm.BeginEditorPopup("labels") is null;
        });
        await Assert.That(token is not null).IsTrue(); await Assert.That(modal).IsTrue();
        await Assert.That(ordinaryBlocked).IsTrue(); await Assert.That(secondBlocked).IsTrue();
        await Assert.That(await EditorUi(() => vm.SaveAsync())).IsFalse();
        await Assert.That(await EditorUi(() => vm.ResolveDraftAsync())).IsFalse();
        await Assert.That(await EditorUi(() => vm.Guard.PrepareAsync())).IsNull();
        await Assert.That(vm.Guard.TryAuthorize(vm.Guard.Generation) is null).IsTrue();
        bool accepted = false, retained = false, repeatRejected = false;
        HeadlessRuntime.Dispatch(() =>
        {
            accepted = vm.EndEditorPopup(token!, doc => doc["data"]!["names"]![0]!["familyName"] = "Выбранная фамилия");
            retained = ReferenceEquals(document,vm.Document) && !vm.HasModal && vm.IsDirty;
            repeatRejected = !vm.EndEditorPopup(token!,doc => doc["starred"] = true);
        });
        await Assert.That(accepted).IsTrue(); await Assert.That(retained).IsTrue(); await Assert.That(repeatRejected).IsTrue();
        await Assert.That(document["data"]!["names"]![0]!["familyName"]!.ToString()).IsEqualTo("Выбранная фамилия");
        await Assert.That(document["extensions"]!.ToJsonString()).IsEqualTo(untouched);
        await Assert.That(fixture.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Popup_cancel_noop_and_view_only_result_leave_original_draft_clean()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model; var before = vm.Document!.ToJsonString();
        bool cancel = false, noop = false, clean = false;
        HeadlessRuntime.Dispatch(() =>
        {
            cancel = vm.EndEditorPopup(vm.BeginEditorPopup()!);
            noop = vm.EndEditorPopup(vm.BeginEditorPopup("labels")!,doc => { });
            clean = !vm.IsDirty && !vm.HasModal;
        });
        await Assert.That(cancel).IsTrue(); await Assert.That(noop).IsTrue(); await Assert.That(clean).IsTrue();
        await Assert.That(vm.Document!.ToJsonString()).IsEqualTo(before);
        using var view = await FriendlyVm(edit:false);
        bool viewCancel = false, viewMutation = true;
        HeadlessRuntime.Dispatch(() =>
        {
            viewCancel = view.Model.EndEditorPopup(view.Model.BeginEditorPopup()!);
            viewMutation = view.Model.EndEditorPopup(view.Model.BeginEditorPopup()!,doc => doc["starred"] = true);
        });
        await Assert.That(viewCancel).IsTrue(); await Assert.That(viewMutation).IsFalse();
        await Assert.That(view.Model.IsDirty).IsFalse(); await Assert.That(view.Model.Starred).IsFalse();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Stale_busy_foreign_modal_and_failed_popup_payload_never_replace_current_values()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model;
        bool stale = true, busy = true, foreign = true, failed = true;
        HeadlessRuntime.Dispatch(() =>
        {
            var token = vm.BeginEditorPopup()!;
            vm.Guard.TryMutate(() => vm.Document!["data"]!["names"]![0]!["familyName"] = "Более новый черновик");
            stale = vm.EndEditorPopup(token,doc => doc["data"]!["names"]![0]!["familyName"] = "Устаревшее значение");
            token = vm.BeginEditorPopup()!;
            using (fixture.Store.Activity.TryEnter()) busy = vm.EndEditorPopup(token,doc => doc["starred"] = true);
            token = vm.BeginEditorPopup()!; vm.ShowConfiguration = true;
            foreign = vm.EndEditorPopup(token,doc => doc["starred"] = true); vm.ShowConfiguration = false;
            token = vm.BeginEditorPopup()!;
            failed = vm.EndEditorPopup(token,doc => { doc["starred"] = true; throw new InvalidOperationException("synthetic invalid payload"); });
        });
        await Assert.That(stale).IsFalse(); await Assert.That(busy).IsFalse(); await Assert.That(foreign).IsFalse(); await Assert.That(failed).IsFalse();
        await Assert.That(vm.Document!["data"]!["names"]![0]!["familyName"]!.ToString()).IsEqualTo("Более новый черновик");
        await Assert.That(vm.Starred).IsFalse(); await Assert.That(vm.IsDirty).IsTrue(); await Assert.That(vm.HasModal).IsFalse();
        await Assert.That(fixture.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Invalid_buffers_survive_rebuild_other_field_edits_and_save_reveals_first_error_without_writing()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model; var rawDate = vm.Document!["data"]!["birthdays"]![0]!["date"]!.ToJsonString();
        string focus = ""; bool month = false, percent = false;
        HeadlessRuntime.Dispatch(() =>
        {
            vm.ErrorFocusRequested += path => focus = path;
            month = vm.SetInvalidInput("data.birthdays.0.date.month","не месяц","Введите месяц");
            percent = vm.SetInvalidInput("data.occupations.0.fullTimeEquivalentMillipercent","12,3456","Не более трёх знаков");
            vm.ShowAllFields = true; vm.ShowAllFields = false; vm.FieldSearch = "не менять запрос";
            vm.Mutate(doc => doc["data"]!["names"]![0]!["givenName"] = "Другое поле");
        });
        await Assert.That(month).IsTrue(); await Assert.That(percent).IsTrue(); await Assert.That(vm.IsDirty).IsTrue();
        await Assert.That(vm.ValidationErrors.Count).IsEqualTo(2);
        await Assert.That(vm.GetInput("data.birthdays.0.date.month","fallback")).IsEqualTo("не месяц");
        await Assert.That(vm.GetInput("data.occupations.0.fullTimeEquivalentMillipercent","fallback")).IsEqualTo("12,3456");
        await Assert.That(vm.Document!["data"]!["birthdays"]![0]!["date"]!.ToJsonString()).IsEqualTo(rawDate);
        await Assert.That(await EditorUi(() => vm.SaveAsync())).IsFalse();
        await Assert.That(focus).IsEqualTo("data.birthdays.0.date.month"); await Assert.That(vm.FieldSearch).IsEqualTo("не менять запрос");
        HeadlessRuntime.Dispatch(() => { vm.Mutate(doc => doc["data"]!["birthdays"]![0]!["date"]!["month"] = 3); vm.ClearInput("data.birthdays.0.date.month"); });
        await Assert.That(vm.ValidationErrors.Count).IsEqualTo(1);
        await Assert.That(await EditorUi(() => vm.SaveAsync())).IsFalse();
        await Assert.That(focus).IsEqualTo("data.occupations.0.fullTimeEquivalentMillipercent");
        await Assert.That(fixture.Store.Saves).IsEqualTo(0);
        HeadlessRuntime.Dispatch(vm.CancelEdit);
        await Assert.That(vm.IsDirty).IsFalse(); await Assert.That(vm.ValidationErrors.Count).IsEqualTo(0);
        await Assert.That(vm.GetInput("data.birthdays.0.date.month","2")).IsEqualTo("2");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Clearing_error_metadata_cannot_save_an_unresolved_input_buffer()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model; string focus = "";
        HeadlessRuntime.Dispatch(() =>
        {
            vm.ErrorFocusRequested += path => focus = path;
            vm.SetInvalidInput("data.birthdays.0.date.year","не год","Введите год");
            vm.ValidationError("","data.birthdays");
        });
        await Assert.That(vm.ValidationErrors.Count).IsEqualTo(0);
        await Assert.That(await EditorUi(() => vm.SaveAsync())).IsFalse();
        await Assert.That(vm.GetInput("data.birthdays.0.date.year","")).IsEqualTo("не год");
        await Assert.That(vm.ValidationErrors.ContainsKey("data.birthdays.0.date.year")).IsTrue();
        await Assert.That(focus).IsEqualTo("data.birthdays.0.date.year");
        await Assert.That(vm.IsDirty).IsTrue(); await Assert.That(fixture.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Owned_popup_applies_json_and_validation_buffer_as_one_guarded_result()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model;
        bool accepted = false, nestedMutationBlocked = false, authorizationBlocked = false, observedCoherent = false;
        const string path = "data.birthdays.0.date.year";
        HeadlessRuntime.Dispatch(() =>
        {
            var token = vm.BeginEditorPopup("data.birthdays.0.date")!;
            void Observe() => observedCoherent = vm.IsDirty && !vm.HasEditorPopup
                && vm.Document!["data"]!["names"]![0]!["givenName"]!.ToString() == "Один результат"
                && vm.GetInput(path,"") == "не год" && vm.ValidationErrors.ContainsKey(path);
            vm.Guard.Changed += Observe;
            try
            {
                accepted = vm.EndEditorPopup(token,doc =>
                {
                    nestedMutationBlocked = !vm.Guard.TryMutate(() => vm.Document!["starred"] = true);
                    authorizationBlocked = vm.Guard.TryAuthorize(token.Generation) is null;
                    doc["data"]!["names"]![0]!["givenName"] = "Один результат";
                },inputChange:new(Invalid:new Dictionary<string,EditorInvalidInput> { [path] = new("не год","Введите год") }));
            }
            finally { vm.Guard.Changed -= Observe; }
        });
        await Assert.That(accepted).IsTrue(); await Assert.That(nestedMutationBlocked).IsTrue();
        await Assert.That(authorizationBlocked).IsTrue(); await Assert.That(observedCoherent).IsTrue();
        await Assert.That(vm.Starred).IsFalse(); await Assert.That(await EditorUi(() => vm.SaveAsync())).IsFalse();
        await Assert.That(fixture.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Owned_invalid_only_result_is_dirty_and_stale_clear_or_invalid_payload_cannot_erase_buffers()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model; var before = vm.Document!.ToJsonString();
        const string path = "data.birthdays.0.date.year";
        bool accepted = false, stale = true, malformed = true;
        HeadlessRuntime.Dispatch(() =>
        {
            accepted = vm.EndEditorPopup(vm.BeginEditorPopup("data.birthdays.0.date")!,inputChange:new(Invalid:new Dictionary<string,EditorInvalidInput> { [path] = new("invalid original","Введите год") }));
            var token = vm.BeginEditorPopup("data.birthdays.0.date")!;
            vm.Guard.TryMutate(() => { });
            stale = vm.EndEditorPopup(token,inputChange:new("data.birthdays.0.date",new Dictionary<string,EditorInvalidInput> { [path] = new("stale replacement","stale error") }));
            token = vm.BeginEditorPopup()!;
            malformed = vm.EndEditorPopup(token,doc => doc["starred"] = true,inputChange:new("data.birthdays",new Dictionary<string,EditorInvalidInput> { [path] = new("must not replace","") }));
        });
        await Assert.That(accepted).IsTrue(); await Assert.That(stale).IsFalse(); await Assert.That(malformed).IsFalse();
        await Assert.That(vm.IsDirty).IsTrue(); await Assert.That(vm.Document!.ToJsonString()).IsEqualTo(before);
        await Assert.That(vm.GetInput(path,"")).IsEqualTo("invalid original");
        await Assert.That(vm.ValidationErrors[path]).IsEqualTo("Введите год");
        await Assert.That(vm.HasModal).IsFalse();
        HeadlessRuntime.Dispatch(() =>
        {
            var token = vm.BeginEditorPopup("data.birthdays.0.date")!;
            vm.EndEditorPopup(token,doc => doc["data"]!["birthdays"]![0]!["date"]!["year"] = 2024,inputChange:new("data.birthdays.0.date"));
        });
        await Assert.That(vm.ValidationErrors.Count).IsEqualTo(0); await Assert.That(vm.GetInput(path,"2024")).IsEqualTo("2024");
        await Assert.That(vm.Document!["data"]!["birthdays"]![0]!["date"]!["year"]!.GetValue<int>()).IsEqualTo(2024);
        await Assert.That(fixture.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Readonly_view_row_menu_offers_copy_and_never_offers_edit_or_changes_the_contact()
    {
        using var fixture = await FriendlyVm(edit:false,readOnlyReason:"Синтетическая причина запрета записи");
        var vm = fixture.Model; var before = vm.Document!.ToJsonString(); object? previous = null; ContactsView view = null!;
        HeadlessRuntime.Dispatch(() =>
        {
            previous = Session.Inner.MainWindow.Content;
            view = new ContactsView(vm); Session.Inner.MainWindow.Content = view;
        });
        try
        {
            await Task.Delay(100);
            HeadlessRuntime.Dispatch(() => ((Button)EditorControl("RowMenu:phoneNumbers:0")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            bool copy = false, edit = true;
            HeadlessRuntime.Dispatch(() =>
            {
                copy = Session.Inner.MainWindow.GetVisualDescendants().OfType<Button>().Any(control => AutomationProperties.GetAutomationId(control) == "Copy:data.phoneNumbers.0");
                edit = Session.Inner.MainWindow.GetVisualDescendants().OfType<Button>().Any(control => AutomationProperties.GetAutomationId(control) == "EditRow:phoneNumbers:0");
                vm.BeginEdit();
            });
            await Assert.That(copy).IsTrue(); await Assert.That(edit).IsFalse();
            await Assert.That(vm.IsEditing).IsFalse(); await Assert.That(vm.IsDirty).IsFalse(); await Assert.That(vm.HasEditorPopup).IsTrue();
            HeadlessRuntime.Dispatch(() => view.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape }));
            await Assert.That(vm.HasModal).IsFalse(); await Assert.That(vm.Document!.ToJsonString()).IsEqualTo(before);
            await Assert.That(fixture.Store.Saves).IsEqualTo(0);
        }
        finally { HeadlessRuntime.Dispatch(() => Session.Inner.MainWindow.Content = previous); }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Foreign_owner_and_changed_workspace_identity_cannot_apply_a_popup_result()
    {
        using var first = await FriendlyVm(); using var second = await FriendlyVm();
        EditorPopupContext popup = null!; bool foreignResult = true;
        HeadlessRuntime.Dispatch(() =>
        {
            popup = second.Model.BeginEditorPopup("labels")!;
            foreignResult = first.Model.EndEditorPopup(popup,doc => doc["starred"] = true);
        });
        await Assert.That(foreignResult).IsFalse(); await Assert.That(second.Model.HasEditorPopup).IsTrue();
        second.Store.WorkspaceId = Guid.NewGuid();
        await EditorUi(() => second.Model.LoadAsync(second.Model.Root!,reloadSelected:false));
        bool reboundResult = true;
        HeadlessRuntime.Dispatch(() => reboundResult = second.Model.EndEditorPopup(popup,doc => doc["starred"] = true));
        await Assert.That(reboundResult).IsFalse(); await Assert.That(second.Model.Starred).IsFalse();
        await Assert.That(second.Model.HasEditorPopup).IsFalse(); await Assert.That(second.Model.IsDirty).IsFalse();
        await Assert.That(first.Model.HasEditorPopup).IsFalse();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Buffer_edits_are_fenced_during_popup_and_labels_preserve_unknown_order_and_unchanged_selection()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model; var store = fixture.Store;
        var before = vm.Document!["labels"]!.ToJsonString(); bool bufferRejected = false;
        HeadlessRuntime.Dispatch(() =>
        {
            vm.SetLabels([store.FirstLabel,store.SecondLabel]); vm.SetLabel(store.FirstLabel,true);
            var popup = vm.BeginEditorPopup("labels")!;
            bufferRejected = !vm.SetInvalidInput("date","bad","bad date");
            vm.EndEditorPopup(popup);
        });
        await Assert.That(vm.IsDirty).IsFalse(); await Assert.That(bufferRejected).IsTrue();
        await Assert.That(vm.Document!["labels"]!.ToJsonString()).IsEqualTo(before);
        HeadlessRuntime.Dispatch(() => vm.SetLabels([store.FirstLabel]));
        await Assert.That(vm.Document!["labels"]!.AsArray().Select(node => node!.ToString()).ToArray()).IsEquivalentTo(new[] { store.UnknownLabel.ToString(),store.FirstLabel.ToString().ToUpperInvariant() });
        await Assert.That(vm.Document!["labels"]![0]!.ToString()).IsEqualTo(store.UnknownLabel.ToString());
        HeadlessRuntime.Dispatch(() => vm.SetLabel(store.UnknownLabel,false));
        await Assert.That(vm.Document!["labels"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Deleting_an_earlier_record_moves_remaining_invalid_buffers_to_their_current_paths()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model;
        HeadlessRuntime.Dispatch(() =>
        {
            vm.Mutate(doc => doc["data"]!["events"] = new JsonArray(new JsonObject { ["type"] = "anniversary" },new JsonObject { ["type"] = "custom-retained" }));
            vm.SetInvalidInput("data.events.0.date.year","old invalid","Удаляемая ошибка");
            vm.SetInvalidInput("data.events.1.date.year","retained invalid","Сохранённая ошибка");
            vm.SetInvalidInput("data.names.0.familyName","other category","Другая категория");
            var popup = vm.BeginEditorPopup("data.events.0")!;
            vm.EndEditorPopup(popup,doc => doc["data"]!["events"]!.AsArray().RemoveAt(0));
            vm.RemoveRowInputs("data.events",0);
        });
        await Assert.That(vm.GetInput("data.events.0.date.year","")).IsEqualTo("retained invalid");
        await Assert.That(vm.GetInput("data.events.1.date.year","missing")).IsEqualTo("missing");
        await Assert.That(vm.GetInput("data.names.0.familyName","")).IsEqualTo("other category");
        await Assert.That(vm.ValidationErrors["data.events.0.date.year"]).IsEqualTo("Сохранённая ошибка");
        await Assert.That(vm.Document!["data"]!["events"]![0]!["type"]!.ToString()).IsEqualTo("custom-retained");
        await Assert.That(vm.ValidationErrors.Count).IsEqualTo(2);
        await Assert.That(fixture.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Drift_stay_preserves_exact_draft_reload_waits_for_discard_and_failed_read_preserves_card()
    {
        using var fixture = await FriendlyVm(); var vm = fixture.Model;
        fixture.Store.WriteFailure = new SyncException("localDrift","Файл изменён внешним редактором");
        HeadlessRuntime.Dispatch(() => vm.Mutate(doc => doc["data"]!["names"]![0]!["givenName"] = "Не терять черновик"));
        await Assert.That(await EditorUi(() => vm.SaveAsync())).IsFalse();
        var draft = vm.Document!.ToJsonString(); HeadlessRuntime.Dispatch(vm.StayWithDraft);
        await Assert.That(vm.HasExternalDrift).IsTrue(); await Assert.That(vm.Document!.ToJsonString()).IsEqualTo(draft);
        Task reload = null!; HeadlessRuntime.Dispatch(() => { reload = vm.ReloadSelectedAsync(); });
        HeadlessRuntime.Dispatch(() => vm.Decide("stay")); await EditorUi(() => reload);
        await Assert.That(vm.Document!.ToJsonString()).IsEqualTo(draft); await Assert.That(vm.IsDirty).IsTrue();
        fixture.Store.Document["data"]!["names"]![0]!["givenName"] = "Актуальная внешняя версия";
        HeadlessRuntime.Dispatch(() => { reload = vm.ReloadSelectedAsync(); }); HeadlessRuntime.Dispatch(() => vm.Decide("discard")); await EditorUi(() => reload);
        await Assert.That(vm.Document!["data"]!["names"]![0]!["givenName"]!.ToString()).IsEqualTo("Актуальная внешняя версия");
        await Assert.That(vm.IsDirty).IsFalse(); await Assert.That(vm.HasExternalDrift).IsFalse();
        var selected = vm.Document!.ToJsonString(); fixture.Store.ReadFailure = new IOException("synthetic missing file");
        await EditorUi(() => vm.ReloadSelectedAsync());
        await Assert.That(vm.HasContact).IsTrue(); await Assert.That(vm.HasExternalDrift).IsTrue();
        await Assert.That(vm.Document!.ToJsonString()).IsEqualTo(selected); await Assert.That(vm.IsBusy).IsFalse();
    }

    private sealed class FriendlyVmFixture(ContactsViewModel model,FriendlyVmStore store,string root) : IDisposable
    {
        public ContactsViewModel Model => model;
        public FriendlyVmStore Store => store;
        public void Dispose() { HeadlessRuntime.Dispatch(model.Dispose); Directory.Delete(root); }
    }
    private sealed class FriendlyVmStore : IContactEditorStore
    {
        private readonly string root;
        private readonly string? readOnlyReason;
        public Guid WorkspaceId { get; set; } = Guid.NewGuid();
        public ApplicationActivity Activity { get; } = new();
        public Guid Id { get; } = Guid.NewGuid();
        public Guid FirstLabel { get; } = Guid.NewGuid();
        public Guid SecondLabel { get; } = Guid.NewGuid();
        public Guid UnknownLabel { get; } = Guid.NewGuid();
        public JsonObject Document { get; private set; }
        public int Saves { get; private set; }
        public Exception? WriteFailure { get; set; }
        public Exception? ReadFailure { get; set; }
        public FriendlyVmStore(string root,string? readOnlyReason = null)
        {
            this.root = root; this.readOnlyReason = readOnlyReason;
            Document = DocumentCodec.ToJson(new ContactDocument { Id = Id, Labels = [SecondLabel,UnknownLabel,FirstLabel],
                Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Анна",["familyName"] = "Исходная" }), ["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "+1 202 555 0199",["type"] = "mobile" }), ["birthdays"] = new JsonArray(new JsonObject { ["date"] = new JsonObject { ["month"] = 2,["day"] = 29 } }) },
                Extensions = new JsonObject { ["unknown"] = new JsonArray("a","a",null),["typed"] = 12.5 } });
            Document["labels"]![2] = FirstLabel.ToString().ToUpperInvariant();
        }
        private ContactSnapshot Snapshot()
        { var bytes = JsonSemantics.Serialize(Document); return new(root,WorkspaceId,Id,Path.Combine(root,Id + ".contact.json"),(JsonObject)Document.DeepClone(),bytes,JsonSemantics.Hash(bytes),null,null,readOnlyReason); }
        public Task<ContactCatalog> LoadAsync(string path,AccountIdentity? activeAccount = null,CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContactCatalog(root,WorkspaceId,[new(Id,Snapshot().Path,(JsonObject)Document.DeepClone())],new Dictionary<Guid,string> { [FirstLabel] = "Первый",[SecondLabel] = "Второй" },[],readOnlyReason));
        public Task<ContactSnapshot> ReadAsync(string path,Guid id,AccountIdentity? activeAccount = null,CancellationToken cancellationToken = default) =>
            ReadFailure is { } error ? Task.FromException<ContactSnapshot>(error) : Task.FromResult(Snapshot());
        public Task<ContactSnapshot> SaveAsync(ContactSnapshot original,JsonObject document,ContactPhotoEdit photo,AccountIdentity? activeAccount = null,CancellationToken cancellationToken = default)
        {
            if (WriteFailure is { } error) return Task.FromException<ContactSnapshot>(error);
            Saves++; Document = (JsonObject)document.DeepClone(); return Task.FromResult(Snapshot());
        }
        public Task DeleteAsync(ContactSnapshot original,AccountIdentity? activeAccount = null,CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
