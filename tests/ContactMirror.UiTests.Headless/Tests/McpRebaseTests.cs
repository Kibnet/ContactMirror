using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Desktop;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Contacts;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class IntegratedDemoHeadlessTests
{
    private sealed class PausedRefreshAccount : IAccountConnector
    {
        private readonly DemoAccountConnector inner = new();
        public bool IsConfigured => inner.IsConfigured;
        public string ConfigurationHint => inner.ConfigurationHint;
        public bool PauseNext;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AccountIdentity?> GetAccountAsync(CancellationToken token = default)
        {
            if(PauseNext) { PauseNext=false;Entered.TrySetResult();await Release.Task.WaitAsync(token); }
            return await inner.GetAccountAsync(token);
        }
        public Task<AccountIdentity> SignInAsync(CancellationToken token = default) => inner.SignInAsync(token);
        public Task SignOutAsync(bool revoke=false,CancellationToken token=default) => inner.SignOutAsync(revoke,token);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Sync_editor_blocks_restart_and_old_agent_refresh_cannot_erase_saved_preview()
    {
        var contact=await SyncedContact();contact.Document["data"]!["phoneNumbers"]![0]!["value"]="До правки";
        await File.WriteAllTextAsync(contact.Path,contact.Document.ToJsonString());
        var root=Path.Combine(IntegrationRoot,"workspace");var account=new PausedRefreshAccount();
        var coordinator=new SyncCoordinator(new DemoGoogleGateway(Path.Combine(IntegrationRoot,"remote.json")),new FileWorkspaceStore());
        MainWindowViewModel model=null!;
        HeadlessRuntime.Dispatch(()=>model=new(account,coordinator,demo:true,persist:false){Folder=root});
        using var runtime=new AgentRuntime(account,coordinator,new ContactEditorStore(),new FileWorkspaceStore(),model.Activity,()=>new(true,true,false,[root]),Path.Combine(IntegrationRoot,"mcp-rebase-state"));
        try
        {
            long? generation=null;
            await HeadlessEditorUi.RunAsync(async()=>{await model.InitializeAsync();await model.CheckCommand.ExecuteAsync(null);generation=await model.Contacts.Guard.PrepareAsync();model.AttachAutomation(runtime);return true;});
            await Assert.That(generation).IsNotNull();await Assert.That(runtime.ConnectedClients).IsEqualTo(0);
            account.PauseNext=true;model.RequestAgentRefresh(root);await SyncWait(()=>account.Entered.Task.IsCompleted);
            await HeadlessEditorUi.RunAsync(async()=>
            {
                model.SelectedEntry=model.Entries.First(e=>e.Entry.Field=="phoneNumbers");
                var edit=await model.OpenSyncEditorAsync() ?? throw new InvalidOperationException(model.Error);
                await Assert.That(await model.Contacts.Guard.PrepareAsync()).IsNull();
                await Assert.That(model.Contacts.Guard.TryAuthorize(model.Contacts.Guard.Generation)).IsNull();
                await Assert.That(model.Contacts.Guard.BlockedReason.Contains("синхронизации",StringComparison.Ordinal)).IsTrue();
                var output=(JsonObject)edit.Document.DeepClone();output["data"]!["phoneNumbers"]![0]!["value"]="Сохранённая правка";
                model.RequestAgentRefresh(root);
                await model.SaveSyncEditorAsync(edit,output,new());await model.RefreshAfterSyncEditAsync(edit);
                await Assert.That(model.HasPreview).IsTrue();return true;
            });
            account.Release.TrySetResult();await Task.Delay(150);
            HeadlessRuntime.Dispatch(()=>{});var retained=false;HeadlessRuntime.Dispatch(()=>retained=model.HasPreview);await Assert.That(retained).IsTrue();
            await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!["data"]!["phoneNumbers"]![0]!["value"]!.ToString()).IsEqualTo("Сохранённая правка");
            await HeadlessEditorUi.RunAsync(async()=>{model.SelectedEntry=model.Entries.First(e=>e.Entry.Field=="phoneNumbers");_ = await model.OpenSyncEditorAsync() ?? throw new InvalidOperationException(model.Error);model.RequestAgentRefresh(root);return true;});
            await Task.Delay(60);HeadlessRuntime.Dispatch(model.CloseSyncEditor);await SyncWait(()=>!model.HasPreview);
        }
        finally {account.Release.TrySetResult();HeadlessRuntime.Dispatch(model.Detach);}
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Sync_editor_rechecks_broker_lease_after_waiting_for_contact_draft_resolution()
    {
        var contact=await SyncedContact();contact.Document["data"]!["phoneNumbers"]![0]!["value"]="Перед сравнением";
        await File.WriteAllTextAsync(contact.Path,contact.Document.ToJsonString());
        var root=Path.Combine(IntegrationRoot,"workspace");MainWindowViewModel model=null!;Task<ContactMirror.Application.Contacts.SyncLocalEdit?>? opening=null;
        await HeadlessEditorUi.RunAsync(async()=>
        {
            model=new(new DemoAccountConnector(),new SyncCoordinator(new DemoGoogleGateway(Path.Combine(IntegrationRoot,"remote.json")),new FileWorkspaceStore()),demo:true,persist:false){Folder=root};
            await model.InitializeAsync();await model.CheckCommand.ExecuteAsync(null);model.SelectedEntry=model.Entries.First(e=>e.Entry.Field=="phoneNumbers");
            await model.Contacts.LoadAsync(root);await model.Contacts.SelectAsync(Guid.Parse(contact.Document["id"]!.ToString()));model.Contacts.BeginEdit();model.Contacts.Mutate(d=>d["data"]!["names"]![0]!["givenName"]="Черновик");
            opening=model.OpenSyncEditorAsync();return true;
        });
        IDisposable? lease=null;
        try
        {
            await SyncWait(()=>model.Contacts.ShowDecision);
            HeadlessRuntime.Dispatch(()=>{lease=model.Activity.TryEnter() ?? throw new InvalidOperationException("Broker lease unavailable.");model.Contacts.Decide("discard");});
            await SyncWait(()=>opening!.IsCompleted);await Assert.That(await opening!).IsNull();
            var opened=true;HeadlessRuntime.Dispatch(()=>opened=model.LocalEditorOpen);await Assert.That(opened).IsFalse();
            var dirty=false;HeadlessRuntime.Dispatch(()=>dirty=model.Contacts.IsDirty);await Assert.That(dirty).IsTrue();
        }
        finally {lease?.Dispose();HeadlessRuntime.Dispatch(model.Detach);}
    }
}
