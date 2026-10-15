using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Contacts;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.McpTests;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task Pending_unknown_and_completed_receipts_are_not_visible_after_UI_account_switch()
    {
        await using var f=new Fixture();await f.InitializeAsync();var receipt=new AgentReceipts(Path.Combine(f.Temp,"agent-state"));
        var running=Key();var unknown=Key();var completed=Key();
        receipt.Begin(running,"hash",Key(),DemoAccountConnector.Identity.Key,f.Root,new JsonObject{["tool"]="contacts_update"});
        receipt.Begin(unknown,"hash",Key(),DemoAccountConnector.Identity.Key,f.Root,new JsonObject{["tool"]="contacts_update"});receipt.Complete(unknown,new("unknown"),DemoAccountConnector.Identity.Key);
        receipt.Begin(completed,"hash",Key(),DemoAccountConnector.Identity.Key,f.Root,new JsonObject{["tool"]="contacts_update"});receipt.Complete(completed,new("completed"),DemoAccountConnector.Identity.Key);
        await f.Account.SignOutAsync();await f.Runtime.NotifyUserChangeAsync(null,null);var other=f.Runtime.ConnectBridge();
        try {foreach(var key in new[]{running,unknown,completed}){var result=await f.Call("operations_get",new(){["requestKey"]=key},other);await Assert.That(result.Error!.Code).IsEqualTo("account_changed");}}
        finally {f.Runtime.DisconnectBridge(other);}
    }
    [Test]
    public async Task Receipt_finalization_failure_finishes_jobs_and_reconciles_local_effect_without_duplicate()
    {
        await using var f=new Fixture(); await f.InitializeAsync(); var fail=true;
        using var runtime=new AgentRuntime(f.Account,f.Coordinator,new ContactEditorStore(),f.Store,new ApplicationActivity(),()=>f.Configuration,Path.Combine(f.Temp,"completion-fault"),()=> { if(fail) throw new IOException("Injected receipt completion failure"); }); var bridge=runtime.ConnectBridge();
        var workspace=Check(await runtime.CallAsync(new("workspace_open",new() { ["root"] = f.Root },bridge))).Data!["workspace"]!.ToString(); var key=Key(); var args=new JsonObject { ["workspace"] = workspace,["requestKey"] = key,["data"] = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Сохранённый контакт" }) } };
        var first=await runtime.CallAsync(new("contacts_create",args,bridge)); await Assert.That(first.Status).IsEqualTo("unknown"); await Assert.That(first.Error!.Code).IsEqualTo("receipt_persistence_failed");
        var pending=await runtime.CallAsync(new("contacts_create",args,bridge)); await Assert.That(pending.Status).IsEqualTo("unknown"); await Assert.That(Directory.GetFiles(Path.Combine(f.Root,"contacts"),"*.contact.json").Length).IsEqualTo(1);
        fail=false; var saved=Check(await runtime.CallAsync(new("contacts_create",args,bridge))); await Assert.That(saved.Data!["reconciled"]!.GetValue<bool>()).IsTrue();
        Check(await runtime.CallAsync(new("contacts_update",new() { ["workspace"] = workspace,["id"] = saved.Data!["id"]!.DeepClone(),["expectedVersion"] = saved.Data!["version"]!.DeepClone(),["requestKey"] = Key(),["patches"] = Patch("/starred",JsonValue.Create(true)) },bridge)));
        await Assert.That(Check(await runtime.CallAsync(new("contacts_create",args,bridge))).Data!.ToJsonString()).IsEqualTo(saved.Data.ToJsonString());
        await f.Account.SignOutAsync(); fail=true;var connectKey=Key();var connectArgs=new JsonObject { ["expectedAccountKey"] = null,["interactive"] = true,["requestKey"] = connectKey };var job=await runtime.CallAsync(new("account_connect",connectArgs,bridge)); await Assert.That(job.Status).IsEqualTo("accepted");
        for(var i=0;runtime.HasRunningJobs && i<200;i++) await Task.Delay(10); await Assert.That(runtime.HasRunningJobs).IsFalse();
        var status=Check(await runtime.CallAsync(new("operations_get",new() { ["operationId"] = job.OperationId },bridge))); await Assert.That(status.Data!["state"]!.ToString()).IsEqualTo("unknown");runtime.DisconnectBridge(bridge);
        var reconnect=runtime.ConnectBridge();try {
            var byKey=await runtime.CallAsync(new("operations_get",new(){["requestKey"]=connectKey},reconnect));await Assert.That(byKey.Status).IsEqualTo("unknown");await Assert.That((await runtime.CallAsync(new("account_connect",connectArgs,reconnect))).Status).IsEqualTo("unknown");
            var disconnectKey=Key();var disconnectArgs=new JsonObject{["expectedAccountKey"]=DemoAccountConnector.Identity.Key,["requestKey"]=disconnectKey};await Assert.That((await runtime.CallAsync(new("account_disconnect",disconnectArgs,reconnect))).Status).IsEqualTo("unknown");
            await Assert.That((await runtime.CallAsync(new("operations_get",new(){["requestKey"]=disconnectKey},reconnect))).Status).IsEqualTo("unknown");await Assert.That((await runtime.CallAsync(new("account_disconnect",disconnectArgs,reconnect))).Status).IsEqualTo("unknown");
        }finally {runtime.DisconnectBridge(reconnect);}
    }
    [Test]
    public async Task Replaced_workspace_rejects_old_handle_and_reopen_accepts_new_identity()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create(); var old = f.Workspace;
        var path = Path.Combine(f.Root,".contactmirror","workspace.json"); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); manifest["workspaceId"] = Guid.NewGuid().ToString(); await File.WriteAllBytesAsync(path,JsonSemantics.Serialize(manifest));
        var stale = await f.Get(Id(c)); await Assert.That(stale.Error!.Code).IsEqualTo("workspace_stale");
        await f.OpenAsync(); Check(await f.Get(Id(c)));
        var stillOld = await f.Call("contacts_get",new() { ["workspace"] = old, ["id"] = Id(c) }); await Assert.That(stillOld.Error!.Code).IsEqualTo("workspace_stale");
    }
    [Test]
    public async Task Binding_swap_invalidates_plan_and_its_large_value_before_any_write()
    {
        await using var f=new Fixture(); await f.InitializeAsync(); var c=await f.Create(data:new() { ["biographies"] = new JsonArray(new JsonObject { ["value"] = new string('X',100000) }) }); var p=await Prepare(f); var planId=p.Data!["planId"]!.ToString();
        var read=Check(await f.Call("sync_get_plan",new() { ["planId"] = planId,["take"] = 200 })); var value=read.Data!["items"]!.AsArray().First(n=>n!["id"]!.ToString()==Id(c))!["local"]!.DeepClone();
        var path=Path.Combine(f.Root,".contactmirror","workspace.json"); var manifest=JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); manifest["workspaceId"]=Guid.NewGuid().ToString(); await File.WriteAllBytesAsync(path,JsonSemantics.Serialize(manifest));
        var stale=await f.Call("sync_get_plan",new() { ["planId"] = planId }); await Assert.That(stale.Error!.Code).IsEqualTo("plan_stale");
        var staleValue=await f.Call("values_read",new() { ["valueId"] = value["valueId"]!.DeepClone(),["version"] = value["version"]!.DeepClone() }); await Assert.That(staleValue.Error!.Code).IsEqualTo("value_stale");
        await f.OpenAsync(); Check(await f.Get(Id(c)));
    }
    [Test]
    public async Task Unresolved_old_receipt_keeps_backup_reference_but_completed_result_expires()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var directory = Path.Combine(f.Temp,"expiry-receipts"); var receipts = new AgentReceipts(directory); var unknownKey = Key(); var doneKey = Key();
        receipts.Begin(unknownKey,"hash","op",DemoAccountConnector.Identity.Key,f.Root,new() { ["backupId"] = "local:test" }); receipts.Complete(unknownKey,new("unknown"),DemoAccountConnector.Identity.Key);
        receipts.Begin(doneKey,"hash","done",DemoAccountConnector.Identity.Key,f.Root,new()); receipts.Complete(doneKey,new("ok"),DemoAccountConnector.Identity.Key);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source="+Path.Combine(directory,"receipts.db"))) { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE receipts SET created='2020-01-01T00:00:00.0000000Z'"; command.ExecuteNonQuery(); }
        var restarted = new AgentReceipts(directory); await Assert.That(restarted.Get(unknownKey)!.State).IsEqualTo("unknown"); await Assert.That(restarted.BackupIsReferenced(f.Root,"local:test")).IsTrue(); await Assert.That(restarted.Get(doneKey)!.State).IsEqualTo("expired");
    }
    [Test]
    public async Task Recovery_never_reports_success_when_selected_journal_entry_is_missing()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var key = Key(); Guid run;
        await using (var session = await f.Store.OpenAsync(f.Root,DemoAccountConnector.Identity)) { run = await session.BeginRunAsync([]); await session.RecordAsync(run,new("first",Guid.NewGuid(),EntityKind.Contact,"names","committed")); }
        var receipts = new AgentReceipts(Path.Combine(f.Temp,"agent-state")); receipts.Begin(key,"hash","op",DemoAccountConnector.Identity.Key,f.Root,new() { ["runId"] = run.ToString(), ["entryKeys"] = new JsonArray("first","missing-second") }); receipts.Complete(key,new("unknown"),DemoAccountConnector.Identity.Key);
        var result = await f.Call("operations_get",new() { ["requestKey"] = key }); await Assert.That(result.Status).IsEqualTo("unknown"); await Assert.That(receipts.Get(key)!.State).IsEqualTo("unknown");
    }
    private sealed class LoseReplyStore(IContactEditorStore inner) : IContactEditorStore
    {
        public int Saves { get; private set; }
        public Task<ContactCatalog> LoadAsync(string root, AccountIdentity? a = null, CancellationToken t = default) => inner.LoadAsync(root,a,t);
        public Task<ContactSnapshot> ReadAsync(string root, Guid id, AccountIdentity? a = null, CancellationToken t = default) => inner.ReadAsync(root,id,a,t);
        public async Task<ContactSnapshot> SaveAsync(ContactSnapshot s, JsonObject d, ContactPhotoEdit p, AccountIdentity? a = null, CancellationToken t = default)
        { Saves++; _ = await inner.SaveAsync(s,d,p,a,t); throw new SyncException("lost_reply", "Fixture lost reply after durable save", true); }
        public Task DeleteAsync(ContactSnapshot s, AccountIdentity? a = null, CancellationToken t = default) => inner.DeleteAsync(s,a,t);
    }
    [Test]
    public async Task Crash_or_lost_reply_after_create_is_reconciled_by_frozen_UUID_and_hash_without_second_save()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var wrapped = new LoseReplyStore(new ContactEditorStore());
        using var runtime = new AgentRuntime(f.Account, f.Coordinator, wrapped, f.Store, f.Activity, () => f.Configuration, Path.Combine(f.Temp,"fault-receipts")); var bridge = runtime.ConnectBridge();
        var opened = Check(await runtime.CallAsync(new("workspace_open", new() { ["root"] = f.Root }, bridge))); var key = Key();
        var args = new JsonObject { ["workspace"] = opened.Data!["workspace"]!.DeepClone(), ["requestKey"] = key, ["data"] = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Запись после сбоя" }) } };
        var first = await runtime.CallAsync(new("contacts_create", args, bridge)); await Assert.That(first.Status).IsEqualTo("unknown");
        var receipt = new AgentReceipts(Path.Combine(f.Temp,"fault-receipts")).Get(key)!;
        await Assert.That(receipt.Intent!["entityId"]!.ToString().Length).IsEqualTo(36); await Assert.That(receipt.Intent["intendedHash"]!.ToString().Length).IsEqualTo(64);
        var replay = Check(await runtime.CallAsync(new("contacts_create", args, bridge))); await Assert.That(replay.Data!["reconciled"]!.GetValue<bool>()).IsTrue();
        await Assert.That(wrapped.Saves).IsEqualTo(1); await Assert.That(Directory.GetFiles(Path.Combine(f.Root,"contacts"),"*.contact.json").Length).IsEqualTo(1); runtime.DisconnectBridge(bridge);
    }
    [Test]
    public async Task Missing_declared_backup_photo_is_corruption_and_never_removes_current_photo()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create(); var upload = await Upload(f,Png);
        var saved = Check(await f.Call("contacts_update", new() { ["workspace"] = f.Workspace, ["id"] = Id(c), ["expectedVersion"] = c["version"]!.DeepClone(), ["requestKey"] = Key(), ["patches"] = new JsonArray(), ["photo"] = new JsonObject { ["action"] = "set", ["uploadId"] = upload } }));
        Check(await f.Update(Id(c),saved.Data!["version"]!.ToString(),Patch("/starred",JsonValue.Create(true))));
        var folder = Directory.GetDirectories(Path.Combine(f.Root,".contactmirror","editor-backups")).Single(d => File.Exists(Path.Combine(d,"before.photo")));
        File.Delete(Path.Combine(folder,"before.photo")); var backupId = "local:" + Guid.Parse(Path.GetFileName(folder));
        var read = await f.Call("history_get",new() { ["workspace"] = f.Workspace, ["backupId"] = backupId }); await Assert.That(read.Error!.Code).IsEqualTo("backup_invalid");
        var prepare = await f.Finish(await f.Call("history_prepare_restore", new() { ["workspace"] = f.Workspace, ["backupId"] = backupId, ["expectedAccountKey"] = DemoAccountConnector.Identity.Key })); await Assert.That(prepare.Error!.Code).IsEqualTo("backup_invalid");
        await Assert.That(Check(await f.Get(Id(c))).Data!["photo"]!["sha256"]!.ToString()).IsEqualTo(JsonSemantics.Hash(Png));
    }
    [Test]
    public async Task Descriptor_and_operation_authorization_follow_UI_account_and_allowed_root_changes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create(data:new() { ["biographies"] = new JsonArray(new JsonObject { ["value"] = new string('X',100000) }) });
        var descriptor = Check(await f.Get(Id(c))).Data!["document"]!.DeepClone(); var p = await Prepare(f);
        await f.Account.SignOutAsync(); await f.Runtime.NotifyUserChangeAsync(null,null);
        var old = await f.Call("values_read",new() { ["valueId"] = descriptor["valueId"]!.DeepClone(), ["version"] = descriptor["version"]!.DeepClone() }); await Assert.That(old.Error!.Code).IsEqualTo("value_stale");
        await f.Account.SignInAsync(); await f.Runtime.NotifyUserChangeAsync(null,DemoAccountConnector.Identity); await f.OpenAsync();
        f.Configuration = f.Configuration with { Roots = [], Generation = Guid.NewGuid() }; var bridge = f.Runtime.ConnectBridge();
        var rootRead = await f.Call("contacts_get",new() { ["workspace"] = f.Workspace, ["id"] = Id(c) },bridge); await Assert.That(rootRead.Error!.Code).IsEqualTo("workspace_not_allowed");
        f.Runtime.DisconnectBridge(bridge); _ = p;
    }
    [Test]
    public async Task GUI_prepare_does_not_evict_eight_MCP_plans_but_ninth_MCP_prepare_does()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var plans = new List<string>();
        for(var i=0;i<8;i++) plans.Add((await Prepare(f)).Data!["planId"]!.ToString());
        using(PlanOrigin.Enter("gui")) _ = await f.Coordinator.PrepareAsync(f.Root,DemoAccountConnector.Identity);
        foreach(var id in plans) Check(await f.Call("sync_get_plan",new() { ["planId"] = id, ["take"] = 1 }));
        _ = await Prepare(f); var expired = await f.Call("sync_get_plan",new() { ["planId"] = plans[0] }); await Assert.That(expired.Error!.Code).IsEqualTo("plan_expired");
    }
    [Test]
    public async Task Local_restore_keeps_live_trusted_Google_metadata_and_rejects_cross_kind_UUID_collision()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var id = Id(await f.Create("Связанный контакт")); await Apply(f,await Prepare(f));
        var linked = Check(await f.Get(id)); var google = linked.Data!["document"]!["google"]!.DeepClone();
        Check(await f.Call("contacts_delete",new() { ["workspace"] = f.Workspace, ["id"] = id, ["expectedVersion"] = Ver(linked), ["requestKey"] = Key(), ["confirmDelete"] = true }));
        var h = Check(await f.Call("history_list",new() { ["workspace"] = f.Workspace })); var backup = h.Data!["items"]!.AsArray().Single(n=>n?["id"]?.ToString()==id&&n["available"]?.GetValue<bool>()==true)!["backupId"]!.DeepClone();
        var groupPath = Path.Combine(f.Root,"groups",id+".group.json"); await File.WriteAllBytesAsync(groupPath,JsonSemantics.Serialize(DocumentCodec.ToJson(new GroupDocument { Id = Guid.Parse(id), Name = "Занятый UUID" })));
        var planned = Check(await f.Finish(await f.Call("history_prepare_restore",new() { ["workspace"] = f.Workspace, ["backupId"] = backup.DeepClone(), ["expectedAccountKey"] = DemoAccountConnector.Identity.Key })));
        var restoreArgs = new JsonObject { ["planId"] = planned.Data!["planId"]!.DeepClone(), ["selectionDigest"] = planned.Data["selectionDigest"]!.DeepClone(), ["choices"] = new JsonArray(new JsonObject { ["entryKey"] = id, ["resolution"] = "UseLocal" }), ["requestKey"] = Key() };
        var collision = await f.Finish(await f.Call("history_apply_local_restore",restoreArgs)); await Assert.That(collision.Error!.Code).IsEqualTo("identity_collision"); File.Delete(groupPath);
        var fresh = Check(await f.Finish(await f.Call("history_prepare_restore",new() { ["workspace"] = f.Workspace, ["backupId"] = backup.DeepClone(), ["expectedAccountKey"] = DemoAccountConnector.Identity.Key })));
        restoreArgs["planId"] = fresh.Data!["planId"]!.DeepClone(); restoreArgs["selectionDigest"] = fresh.Data["selectionDigest"]!.DeepClone(); restoreArgs["requestKey"] = Key(); Check(await f.Finish(await f.Call("history_apply_local_restore",restoreArgs)));
        await Assert.That(JsonNode.DeepEquals(Check(await f.Get(id)).Data!["document"]!["google"],google)).IsTrue();
    }
}
