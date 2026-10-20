using System.Diagnostics;
using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Application.Updates;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.Core;
using ContactMirror.Desktop.Contacts;
using ContactMirror.Infrastructure.Contacts;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Dirty_card_modal_ignores_CtrlS_Escape_stays_and_CtrlS_then_saves_the_real_file()
    {
        var fixture = await PrepareRealEditor(); var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        var before = await File.ReadAllBytesAsync(file); Task transition = null!;
        try
        {
            HeadlessRuntime.Dispatch(() => UxModel().Contacts.BeginEdit()); await Task.Delay(60);
            HeadlessRuntime.Dispatch(() =>
            {
                var input = (TextBox)EditorControl("Editor:data.names.0.familyName"); input.BringIntoView(); input.Focus(); input.SelectAll();
                Session.Inner.MainWindow.KeyTextInput("Клавиатурова");
            });
            await LifecycleWait(() => UxModel().Contacts.IsDirty, "typed contact draft");
            HeadlessRuntime.Dispatch(() => { transition = UxModel().ShowSyncCommand.ExecuteAsync(null); });
            await LifecycleWait(() => LifecycleDecisionReady(UxModel().Contacts), "dirty decision modal");
            HeadlessRuntime.Dispatch(() =>
            {
                var stay = EditorControl("ContactDecisionStay"); stay.Focus(); LifecycleKey(stay, Key.S, KeyModifiers.Control);
            });
            await Task.Delay(70);
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
            var stillPending = false;
            HeadlessRuntime.Dispatch(() => stillPending = UxModel().Contacts.ShowDecision && UxModel().Contacts.IsDirty && UxModel().ContactsMode);
            await Assert.That(stillPending).IsTrue();
            CaptureCheckpoint("editor-dirty-keyboard-modal");
            HeadlessRuntime.Dispatch(() => LifecycleKey(EditorControl("ContactDecisionStay"), Key.Escape));
            await EditorUi(()=>transition.WaitAsync(TimeSpan.FromSeconds(5)));
            var stayed = false;
            HeadlessRuntime.Dispatch(() => stayed = UxModel().ContactsMode && UxModel().Contacts.IsDirty && !UxModel().Contacts.ShowDecision);
            await Assert.That(stayed).IsTrue();
            HeadlessRuntime.Dispatch(() =>
            {
                var input = EditorControl("Editor:data.names.0.familyName"); input.Focus(); LifecycleKey(input, Key.S, KeyModifiers.Control);
            });
            await LifecycleWait(() => !UxModel().Contacts.IsBusy && !UxModel().Contacts.IsEditing && !UxModel().Contacts.IsDirty, "CtrlS completed local save");
            var written = JsonNode.Parse(await File.ReadAllBytesAsync(file))!;
            await Assert.That(written["data"]!["names"]![0]!["familyName"]!.GetValue<string>()).IsEqualTo("Клавиатурова");
            await EditorUi(() => UxModel().Contacts.ReloadSelectedAsync());
            var reopened = "";
            HeadlessRuntime.Dispatch(() => reopened = UxModel().Contacts.Document!["data"]!["names"]![0]!["familyName"]!.GetValue<string>());
            await Assert.That(reopened).IsEqualTo("Клавиатурова");
        }
        finally
        {
            HeadlessRuntime.Dispatch(() => { if (UxModel().Contacts.ShowDecision) UxModel().Contacts.Decide("stay"); else UxModel().Contacts.CancelEdit(); });
        }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Main_window_close_Escape_stays_with_draft_then_Discard_closes_without_any_file_write()
    {
        var fixture = await PrepareRealEditor(); var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        var before = await File.ReadAllBytesAsync(file); var closed = 0;
        var closedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            HeadlessRuntime.Dispatch(() =>
            {
                Session.Inner.MainWindow.Closed += (_, _) => { closed++; closedSignal.TrySetResult(); };
                UxModel().Contacts.BeginEdit(); UxModel().Contacts.Mutate(doc => doc["data"]!["names"]![0]!["familyName"] = "Несохранённая фамилия");
                Session.Inner.MainWindow.Close();
            });
            await LifecycleWait(() => LifecycleDecisionReady(UxModel().Contacts), "closing dirty window asks first");
            var alive = false; HeadlessRuntime.Dispatch(() => alive = Session.Inner.MainWindow.IsVisible && UxModel().Contacts.IsDirty);
            await Assert.That(alive).IsTrue(); await Assert.That(closed).IsEqualTo(0);
            // Exercise Avalonia's real key routing without assigning focus to an expected button.
            await LifecycleWait(() => DecisionHasKeyboardFocus(), "closing modal receives keyboard focus");
            var visited = new HashSet<string>();
            for (var index = 0; index < 10; index++)
            {
                string? focusedId = null;
                HeadlessRuntime.Dispatch(() =>
                {
                    var modifiers = index < 5 ? RawInputModifiers.None : RawInputModifiers.Shift;
                    Session.Inner.MainWindow.KeyPress(Key.Tab, modifiers, PhysicalKey.Tab, null);
                    Session.Inner.MainWindow.KeyRelease(Key.Tab, modifiers, PhysicalKey.Tab, null);
                    if (Session.Inner.MainWindow.FocusManager!.GetFocusedElement() is Control control)
                        focusedId = AutomationProperties.GetAutomationId(control);
                });
                await Assert.That(focusedId is "ContactDecisionSave" or "ContactDecisionDiscard" or "ContactDecisionStay").IsTrue();
                visited.Add(focusedId!);
            }
            await Assert.That(visited.Count).IsEqualTo(3);
            HeadlessRuntime.Dispatch(() =>
            {
                Session.Inner.MainWindow.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
                Session.Inner.MainWindow.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
            });
            var modalStillDirty = false;
            HeadlessRuntime.Dispatch(() => modalStillDirty = UxModel().Contacts.ShowDecision && UxModel().Contacts.IsDirty);
            await Assert.That(modalStillDirty).IsTrue();
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
            CaptureCheckpoint("editor-window-close-decision");
            HeadlessRuntime.Dispatch(() =>
            {
                Session.Inner.MainWindow.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Session.Inner.MainWindow.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            });
            await LifecycleWait(() => !UxModel().Contacts.ShowDecision, "Escape canceled window close");
            await Task.Delay(30);
            HeadlessRuntime.Dispatch(() => alive = Session.Inner.MainWindow.IsVisible && UxModel().Contacts.IsDirty && UxModel().Contacts.Document!["data"]!["names"]![0]!["familyName"]!.ToString() == "Несохранённая фамилия");
            await Assert.That(alive).IsTrue(); await Assert.That(closed).IsEqualTo(0);
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
            HeadlessRuntime.Dispatch(Session.Inner.MainWindow.Close);
            await LifecycleWait(() => LifecycleDecisionReady(UxModel().Contacts), "second close asks again");
            HeadlessRuntime.Dispatch(() => ((Button)EditorControl("ContactDecisionDiscard")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await EditorUi(()=>closedSignal.Task.WaitAsync(TimeSpan.FromSeconds(8)));
            await Assert.That(closed).IsEqualTo(1);
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(before)).IsTrue();
        }
        finally
        {
            if (closed == 0) HeadlessRuntime.Dispatch(() => { if (UxModel().Contacts.ShowDecision) UxModel().Contacts.Decide("discard"); else UxModel().Contacts.CancelEdit(); });
        }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Update_service_dirty_Save_choice_that_fails_file_CAS_never_invokes_restart_backend()
    {
        var fixture = await PrepareRealEditor(); var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        var activity = new ApplicationActivity(); var backend = new LifecycleUpdateBackend();
        ContactsViewModel model = null!; object? previousContent = null; Task restarting = null!;
        HeadlessRuntime.Dispatch(() =>
        {
            model = new(new ContactEditorService(new ContactEditorStore(), activity), activity, () => TestAccountConnector.Identity, () => { }, demo: true, persist: false);
            previousContent = Session.Inner.MainWindow.Content;
            Session.Inner.MainWindow.Content = new ContactsView(model);
        });
        var update = new ApplicationUpdateService(backend, activity, model.Guard);
        try
        {
            await EditorUi(() => model.LoadAsync(fixture.Root)); await EditorUi(() => model.SelectAsync(fixture.Id));
            HeadlessRuntime.Dispatch(() => { model.BeginEdit(); model.Mutate(doc => doc["data"]!["names"]![0]!["familyName"] = "Черновик перед обновлением"); });
            var external = JsonNode.Parse(await File.ReadAllBytesAsync(file))!; external["data"]!["names"]![0]!["familyName"] = "Внешняя версия";
            var externalBytes = JsonSemantics.Serialize(external); await File.WriteAllBytesAsync(file, externalBytes);
            await update.CheckAsync(); await update.DownloadAsync();
            await Assert.That(update.Snapshot.State).IsEqualTo(UpdateState.ReadyToRestart);
            HeadlessRuntime.Dispatch(() => { restarting = update.ApplyAndRestartAsync(); });
            await LifecycleWait(() => LifecycleDecisionReady(model), "updater asks how to resolve dirty card");
            HeadlessRuntime.Dispatch(() => ((Button)EditorControl("ContactDecisionSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await EditorUi(()=>restarting.WaitAsync(TimeSpan.FromSeconds(10)));
            var preserved = false;
            HeadlessRuntime.Dispatch(() => preserved = model.IsDirty && model.IsEditing && !model.ShowDecision && model.Document!["data"]!["names"]![0]!["familyName"]!.ToString() == "Черновик перед обновлением");
            await Assert.That(preserved).IsTrue(); await Assert.That(backend.ApplyCalls).IsEqualTo(0);
            await Assert.That(update.Snapshot.State).IsEqualTo(UpdateState.ReadyToRestart);
            await Assert.That(activity.IsActive).IsFalse(); await Assert.That(activity.IsApplying).IsFalse();
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(externalBytes)).IsTrue();
            CaptureCheckpoint("editor-update-failed-save-draft-preserved");
        }
        finally
        {
            HeadlessRuntime.Dispatch(() => { if (model.ShowDecision) model.Decide("stay"); else model.CancelEdit(); Session.Inner.MainWindow.Content = previousContent; model.Dispose(); });
        }
    }

    private static void LifecycleKey(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    private bool LifecycleDecisionReady(ContactsViewModel model) => model.ShowDecision && Session.Inner.MainWindow.GetVisualDescendants().OfType<Button>()
        .Any(button => AutomationProperties.GetAutomationId(button) == "ContactDecisionStay");
    private bool DecisionHasKeyboardFocus() => Session.Inner.MainWindow.FocusManager!.GetFocusedElement() is Control control
        && AutomationProperties.GetAutomationId(control) is "ContactDecisionSave" or "ContactDecisionDiscard" or "ContactDecisionStay";
    private static async Task LifecycleWait(Func<bool> condition, string step)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            var ready = false; HeadlessRuntime.Dispatch(() => ready = condition()); if (ready) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Contact editor lifecycle: " + step);
    }
    private sealed class LifecycleUpdateBackend : IApplicationUpdateBackend
    {
        public int ApplyCalls { get; private set; }
        public bool IsSupported => true;
        public bool IsConfigured => true;
        public string CurrentVersion => "0.3.0";
        public bool HasAmbiguousApply => false;
        public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("0.4.0");
        public Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken) { progress.Report(100); return Task.CompletedTask; }
        public void ApplyAndRestart() => ApplyCalls++;
    }
}
