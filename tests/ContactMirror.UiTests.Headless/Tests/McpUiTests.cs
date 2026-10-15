using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.Core;
using ContactMirror.Desktop;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Contacts;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    private sealed class McpUiAccount : IAccountConnector
    {
        public bool IsConfigured => true;
        public string ConfigurationHint => "Синтетическая проверка MCP";
        public Task<AccountIdentity?> GetAccountAsync(CancellationToken t = default) => Task.FromResult<AccountIdentity?>(TestAccountConnector.Identity);
        public Task<AccountIdentity> SignInAsync(CancellationToken t = default) => Task.FromResult(TestAccountConnector.Identity);
        public Task SignOutAsync(bool revoke = false, CancellationToken t = default) => Task.CompletedTask;
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task MCP_preserves_real_unsaved_field_then_updates_clean_card_and_renders_settings()
    {
        var fixture = await PrepareRealEditor(); MainWindowViewModel model = null!;
        HeadlessRuntime.Dispatch(() => model = UxModel());
        var configuration = new AgentConfiguration(true,true,false,[fixture.Root],Guid.NewGuid()); var temp = Path.Combine(fixture.Root,"mcp-test-state");
        var coordinator = new SyncCoordinator(new GuardedGoogleGateway(new DemoGoogleGateway(Path.Combine(temp,"remote.json"))),new FileWorkspaceStore());
        using var runtime = new AgentRuntime(new McpUiAccount(),coordinator,new ContactEditorStore(),new FileWorkspaceStore(),model.Activity,()=>configuration,temp);
        runtime.Ui = new DesktopAgentUi(()=>Task.CompletedTask) { Model = model };
        HeadlessRuntime.Dispatch(()=>model.AttachAutomation(runtime)); var bridge = runtime.ConnectBridge();
        async Task<AgentResult> Call(string tool,JsonObject args) => await EditorUi(()=>runtime.CallAsync(new(tool,args,bridge)));
        var workspace = (await Call("workspace_open",new(){["root"]=fixture.Root})).Data!["workspace"]!.ToString();
        var before = await Call("contacts_get",new(){["workspace"]=workspace,["id"]=fixture.Id.ToString()});
        HeadlessRuntime.Dispatch(()=>model.LocalEditorOpen=true);
        var modal=await Call("contacts_update",new(){["workspace"]=workspace,["id"]=fixture.Id.ToString(),["expectedVersion"]=before.Data!["version"]!.DeepClone(),["requestKey"]=Guid.NewGuid().ToString(),["patches"]=new JsonArray(new JsonObject{["op"]="replace",["path"]="/data/names/0/givenName",["value"]="Правка агента"})});
        await Assert.That(modal.Error!.Code).IsEqualTo("ui_modal_busy");
        var modalState=await EditorUi(()=>runtime.Ui.GetStateAsync());await Assert.That(modalState.Modal).IsTrue();
        HeadlessRuntime.Dispatch(()=>model.LocalEditorOpen=false);
        HeadlessRuntime.Dispatch(()=>{ model.Contacts.BeginEdit(); model.Contacts.Mutate(d=>d["data"]!["names"]![0]!["givenName"]="Несохранённое имя" ); });
        var blocked = await Call("contacts_update",new(){["workspace"]=workspace,["id"]=fixture.Id.ToString(),["expectedVersion"]=before.Data!["version"]!.DeepClone(),["requestKey"]=Guid.NewGuid().ToString(),["patches"]=new JsonArray(new JsonObject{["op"]="replace",["path"]="/data/names/0/givenName",["value"]="Правка агента"})});
        await Assert.That(blocked.Error!.Code).IsEqualTo("draft_conflict"); var kept=""; HeadlessRuntime.Dispatch(()=>kept=model.Contacts.Document!["data"]!["names"]![0]!["givenName"]!.ToString()); await Assert.That(kept).IsEqualTo("Несохранённое имя"); CaptureCheckpoint("mcp-protected-draft");
        HeadlessRuntime.Dispatch(model.Contacts.CancelEdit);
        var clean = await Call("contacts_get",new(){["workspace"]=workspace,["id"]=fixture.Id.ToString()});
        var saved = await Call("contacts_update",new(){["workspace"]=workspace,["id"]=fixture.Id.ToString(),["expectedVersion"]=clean.Data!["version"]!.DeepClone(),["requestKey"]=Guid.NewGuid().ToString(),["patches"]=new JsonArray(new JsonObject{["op"]="replace",["path"]="/data/names/0/givenName",["value"]="Правка агента"})}); await Assert.That(saved.Status).IsEqualTo("completed");
        for(var i=0;i<100;i++){HeadlessRuntime.Dispatch(()=>kept=model.Contacts.Document?["data"]?["names"]?[0]?["givenName"]?.ToString()??"");if(kept=="Правка агента")break;await Task.Delay(30);}
        await Assert.That(kept).IsEqualTo("Правка агента"); CaptureCheckpoint("mcp-clean-card-refreshed");
        using var bitmap = new SkiaSharp.SKBitmap(32,32); bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100); var photo = encoded.ToArray();
        var upload = await Call("photo_upload_begin",new(){["workspace"]=workspace,["uploadKey"]=Guid.NewGuid().ToString(),["length"]=photo.Length,["sha256"]=JsonSemantics.Hash(photo)}); var uploadId=upload.Data!["uploadId"]!.ToString();
        await Call("photo_upload_chunk",new(){["uploadId"]=uploadId,["offset"]=0,["base64"]=Convert.ToBase64String(photo)}); await Call("photo_upload_complete",new(){["uploadId"]=uploadId});
        var photoSaved = await Call("contacts_update",new(){["workspace"]=workspace,["id"]=fixture.Id.ToString(),["expectedVersion"]=saved.Data!["version"]!.DeepClone(),["requestKey"]=Guid.NewGuid().ToString(),["patches"]=new JsonArray(),["photo"]=new JsonObject{["action"]="set",["uploadId"]=uploadId}}); await Assert.That(photoSaved.Status).IsEqualTo("completed");
        byte[]? shownPhoto=null; bool decoded=false;
        for(var i=0;i<100;i++){HeadlessRuntime.Dispatch(()=>{shownPhoto=model.Contacts.PhotoBytes;decoded=model.Contacts.PhotoPreview is not null;});if(shownPhoto?.SequenceEqual(photo)==true && decoded)break;await Task.Delay(30);}
        await Assert.That(JsonSemantics.Hash(shownPhoto!)).IsEqualTo(JsonSemantics.Hash(photo)); await Assert.That(decoded).IsTrue(); CaptureCheckpoint("mcp-photo-refreshed");
        HeadlessRuntime.Dispatch(()=>{model.ShowSettings=true;model.ShowHistory=false;model.ShowCapabilities=false;model.McpRoots=fixture.Root;model.McpEnabled=true;model.McpConfiguration=model.BuildMcpConfiguration();Session.Inner.MainWindow.Width=1100;Session.Inner.MainWindow.Height=820;});
        await Task.Delay(60); HeadlessRuntime.Dispatch(()=>Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>().Single(c=>AutomationProperties.GetAutomationId(c)=="McpSettingsPanel").BringIntoView()); await Task.Delay(80); CaptureCheckpoint("mcp-settings-rendered");
        runtime.DisconnectBridge(bridge);
    }
}
