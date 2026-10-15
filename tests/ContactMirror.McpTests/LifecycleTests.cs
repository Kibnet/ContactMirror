using System.Text.Json.Nodes;
using ContactMirror.Core;
using ContactMirror.Application.Automation;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.McpTests;
public sealed partial class RuntimeTests
{
    private static async Task<AgentResult> ApplyExplicit(Fixture f,string planId)
    {
        var read=Check(await f.Call("sync_get_plan",new() { ["planId"] = planId,["take"] = 200 }));
        var choices=new JsonArray(read.Data!["items"]!.AsArray().Where(n=>n!["kind"]!.ToString()!="Blocked").Select(n=>(JsonNode)new JsonObject { ["entryKey"] = n!["entryKey"]!.DeepClone(),["resolution"] = n["kind"]!.ToString()=="Conflict" ? "UseLocal" : "Automatic" }).ToArray());
        var reviewed=Check(await f.Call("sync_get_plan",new() { ["planId"] = planId,["choices"] = choices.DeepClone() }));
        return Check(await f.Finish(await f.Call("sync_apply",new() { ["planId"] = planId,["choices"] = choices,["selectionDigest"] = reviewed.Data!["selectionDigest"]!.DeepClone(),["requestKey"] = Key(),["confirmDeletions"] = true,["deletePhrase"] = "УДАЛИТЬ" })));
    }
    [Test]
    public async Task MCP_full_Demo_photo_membership_conflict_delete_sync_restore_and_backup_cleanup()
    {
        await using var f=new Fixture(); await f.InitializeAsync();
        var group=Check(await f.Call("groups_create",new() { ["workspace"] = f.Workspace,["requestKey"] = Key(),["name"] = "Метка агента" })); var groupId=group.Data!["id"]!.ToString();
        var c=await f.Create("Полный сценарий"); var id=Id(c); var upload=await Upload(f,Png);
        Check(await f.Call("contacts_update",new() { ["workspace"] = f.Workspace,["id"] = id,["expectedVersion"] = c["version"]!.DeepClone(),["requestKey"] = Key(),["patches"] = Patch("/labels",new JsonArray(groupId)),["photo"] = new JsonObject { ["action"] = "set",["uploadId"] = upload } }));
        await ApplyExplicit(f,(await Prepare(f)).Data!["planId"]!.ToString());
        var saved=Check(await f.Get(id)); var resource=saved.Data!["document"]!["google"]!["resourceName"]!.ToString(); var groupRemote=Check(await f.Call("groups_get",new() { ["workspace"] = f.Workspace,["id"] = groupId })).Data!["document"]!["google"]!["resourceName"]!.ToString();
        await Assert.That(PersonCodec.Memberships((await f.Gateway.GetPersonAsync(resource))!).Contains(groupRemote)).IsTrue();
        Check(await f.Update(id,Ver(saved),Patch("/data/names/0/givenName",JsonValue.Create("Локальный выбор"))));
        var remote=(await f.Gateway.GetPersonAsync(resource))!; remote["names"]![0]!["givenName"]="Другая правка Google"; await f.Gateway.UpdateContactAsync(resource,remote,["names"]);
        var conflict=await Prepare(f); var preview=Check(await f.Call("sync_get_plan",new() { ["planId"] = conflict.Data!["planId"]!.DeepClone(),["take"] = 200 })); await Assert.That(preview.Data!["items"]!.AsArray().Any(n=>n!["kind"]!.ToString()=="Conflict")).IsTrue(); await ApplyExplicit(f,conflict.Data["planId"]!.ToString());
        saved=Check(await f.Get(id)); Check(await f.Call("contacts_delete",new() { ["workspace"] = f.Workspace,["id"] = id,["expectedVersion"] = saved.Data!["version"]!.DeepClone(),["requestKey"] = Key(),["confirmDelete"] = true }));
        var deleted=await ApplyExplicit(f,(await Prepare(f)).Data!["planId"]!.ToString()); await Assert.That(await f.Gateway.GetPersonAsync(resource)).IsNull();
        var backupId="sync:"+deleted.Data!["runId"]!.ToString(); Check(await f.Call("history_get",new() { ["workspace"] = f.Workspace,["backupId"] = backupId,["take"] = 1 }));
        var restore=Check(await f.Finish(await f.Call("history_prepare_restore",new() { ["workspace"] = f.Workspace,["backupId"] = backupId,["expectedAccountKey"] = DemoAccountConnector.Identity.Key }))); await ApplyExplicit(f,restore.Data!["planId"]!.ToString());
        var restored=Check(await f.Get(id)); await Assert.That(restored.Data!["document"]!["data"]!["names"]![0]!["givenName"]!.ToString()).IsEqualTo("Локальный выбор"); await Assert.That(restored.Data!["photo"]!["sha256"]!.ToString()).IsEqualTo(JsonSemantics.Hash(Png));
        var backup=Check(await f.Call("history_get",new() { ["workspace"] = f.Workspace,["backupId"] = backupId,["take"] = 1 }));
        Check(await f.Finish(await f.Call("history_cleanup_backup",new() { ["workspace"] = f.Workspace,["backupId"] = backupId,["expectedVersion"] = backup.Data!["version"]!.DeepClone(),["requestKey"] = Key(),["confirmDelete"] = true })));
        var history=Check(await f.Call("history_list",new() { ["workspace"] = f.Workspace })); await Assert.That(history.Data!["items"]!.AsArray().Single(n=>n!["backupId"]!.ToString()==backupId)!["available"]!.GetValue<bool>()).IsFalse();
    }
    [Test]
    public async Task MCP_repair_snapshot_has_durable_journal_intent_and_preserves_editable_fields()
    {
        await using var f=new Fixture(); await f.InitializeAsync(); var id=Id(await f.Create("Исправление копии")); await Apply(f,await Prepare(f));
        var path=Path.Combine(f.Root,"contacts",id+".contact.json"); var doc=JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); var before=doc["data"]!.DeepClone(); doc["google"]!["etag"]="tampered"; await File.WriteAllBytesAsync(path,JsonSemantics.Serialize(doc));
        var prepared=await Prepare(f); var plan=Check(await f.Call("sync_get_plan",new() { ["planId"] = prepared.Data!["planId"]!.DeepClone(),["take"] = 200 })); var entry=plan.Data!["items"]!.AsArray().First(n=>n!["canRepairGoogleSnapshot"]!.GetValue<bool>());
        var key=Key(); var repair=Check(await f.Finish(await f.Call("sync_repair_snapshot",new() { ["planId"] = prepared.Data!["planId"]!.DeepClone(),["planDigest"] = plan.Data!["planDigest"]!.DeepClone(),["entryKey"] = entry!["entryKey"]!.DeepClone(),["requestKey"] = key })));
        await Assert.That(JsonNode.DeepEquals(Check(await f.Get(id)).Data!["document"]!["data"],before)).IsTrue(); var receipt=new ContactMirror.Infrastructure.Automation.AgentReceipts(Path.Combine(f.Temp,"agent-state")).Get(key)!;
        await Assert.That(receipt.Intent!["runId"]!.ToString()).IsEqualTo(repair.Data!["runId"]!.ToString()); await Assert.That(receipt.Intent["entryKeys"]!.AsArray().Count).IsEqualTo(1);
    }
}
