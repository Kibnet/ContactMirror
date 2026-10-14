using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Desktop;
using ContactMirror.Desktop.Contacts;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Demo;
using TUnit.Core;
using TUnit.Assertions;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class IntegratedDemoHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Busy_local_save_rejects_editor_close_and_application_waits_for_commit()
    {
        var contact = await SyncedContact(); contact.Document["data"]!["phoneNumbers"]![0]!["value"] = "До задержки";
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PausedLocalSave(new SyncCoordinator(new DemoGoogleGateway(Path.Combine(IntegrationRoot, "remote.json")), new FileWorkspaceStore()), entered, release);
        MainWindowViewModel model = null!; SyncChangeEditor view = null!; Window host = null!; var closed = 0;
        await HeadlessEditorUi.RunAsync(async () =>
        {
            model = new(new DemoAccountConnector(), coordinator, demo: true, persist: false) { Folder = Path.Combine(IntegrationRoot, "workspace") };
            await model.InitializeAsync(); await model.CheckCommand.ExecuteAsync(null);
            model.SelectedEntry = model.Entries.First(e => e.Entry.Field == "phoneNumbers"); var target = await model.OpenSyncEditorAsync() ?? throw new InvalidOperationException(model.Error);
            view = new(model, target, () => { closed++; model.CloseSyncEditor(); }); host = new Window { Content = view, Width = 800, Height = 650 }; host.Show(); return true;
        });
        try
        {
            await Task.Delay(100);
            HeadlessRuntime.Dispatch(() =>
            {
                ((TextBox)view.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == "Editor:data.phoneNumbers.0.value")).Text = "После задержки";
                view.GetVisualDescendants().OfType<Button>().Single(c => AutomationProperties.GetAutomationId(c) == "SyncEditSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await SyncWait(() => entered.Task.IsCompleted); Task closing = null!;
            HeadlessRuntime.Dispatch(() => { view.RequestClose(); closing = model.RequestCloseAsync(); });
            await Assert.That(closed).IsEqualTo(0); await Assert.That(closing.IsCompleted).IsFalse();
            release.TrySetResult(); await SyncWait(() => closing.IsCompleted);
            await SyncWait(() => closed > 0);
            await Assert.That(closed).IsEqualTo(1);
            await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!["data"]!["phoneNumbers"]![0]!["value"]!.ToString()).IsEqualTo("После задержки");
        }
        finally { release.TrySetResult(); HeadlessRuntime.Dispatch(() => { view.Dispose(); host.Close(); model.Detach(); }); }
    }
    private sealed class PausedLocalSave(ISyncCoordinator inner, TaskCompletionSource entered, TaskCompletionSource release) : ISyncCoordinator
    {
        public Task<SyncLocalEdit> OpenLocalEditAsync(SyncPreview p, string key, CancellationToken c = default) => inner.OpenLocalEditAsync(p, key, c);
        public async Task<SyncLocalEditResult> SaveLocalEditAsync(SyncLocalEdit e, JsonObject d, ContactPhotoEdit p, CancellationToken c = default) { entered.TrySetResult(); await release.Task; return await inner.SaveLocalEditAsync(e, d, p, c); }
        public Task<SyncPreview> PrepareAsync(string r, AccountIdentity a, IProgress<SyncProgress>? p = null, CancellationToken c = default) => inner.PrepareAsync(r, a, p, c);
        public Task<SyncRunResult> ApplyAsync(SyncPreview p, IReadOnlyList<PlanChoice> choices, IProgress<SyncProgress>? progress = null, CancellationToken c = default) => inner.ApplyAsync(p, choices, progress, c);
        public Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string r, CancellationToken c = default) => inner.GetHistoryAsync(r, c);
        public Task<string> CreateContactFileAsync(string r, CancellationToken c = default) => inner.CreateContactFileAsync(r, c);
        public Task<SyncPreview> PrepareRestoreAsync(string r, AccountIdentity a, Guid id, IProgress<SyncProgress>? p = null, CancellationToken c = default) => inner.PrepareRestoreAsync(r, a, id, p, c);
    }
}
