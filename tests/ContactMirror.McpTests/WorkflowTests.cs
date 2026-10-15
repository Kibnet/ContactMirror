using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Demo;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.McpTests;

public sealed partial class RuntimeTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static async Task<string> Upload(Fixture f, byte[] data)
    {
        var begin = Check(await f.Call("photo_upload_begin", new() { ["workspace"] = f.Workspace, ["uploadKey"] = Key(), ["length"] = data.Length, ["sha256"] = JsonSemantics.Hash(data) }));
        var id = begin.Data!["uploadId"]!.ToString(); var chunk = new JsonObject { ["uploadId"] = id, ["offset"] = 0, ["base64"] = Convert.ToBase64String(data) };
        Check(await f.Call("photo_upload_chunk", chunk)); Check(await f.Call("photo_upload_chunk", chunk)); Check(await f.Call("photo_upload_complete", new() { ["uploadId"] = id })); return id;
    }
    private static async Task<AgentResult> Prepare(Fixture f, string? bridge = null) => Check(await f.Finish(await f.Call("sync_prepare", new() { ["workspace"] = f.Workspace, ["expectedAccountKey"] = DemoAccountConnector.Identity.Key }, bridge)));
    private static async Task<AgentResult> Apply(Fixture f, AgentResult prepared)
    {
        var id = prepared.Data!["planId"]!.ToString(); var reviewed = Check(await f.Call("sync_get_plan", new() { ["planId"] = id, ["take"] = 200 }));
        var choices = new JsonArray(reviewed.Data!["items"]!.AsArray().Where(e => e!["defaultSelected"]!.GetValue<bool>()).Select(e => (JsonNode)new JsonObject { ["entryKey"] = e!["entryKey"]!.DeepClone(), ["resolution"] = "Automatic" }).ToArray());
        if (choices.Count == 0) return reviewed;
        var selected = Check(await f.Call("sync_get_plan", new() { ["planId"] = id, ["choices"] = choices.DeepClone() }));
        return Check(await f.Finish(await f.Call("sync_apply", new() { ["planId"] = id, ["choices"] = choices, ["selectionDigest"] = selected.Data!["selectionDigest"]!.DeepClone(), ["requestKey"] = Key() })));
    }
    [Test]
    public async Task Photo_upload_duplicate_chunk_set_read_and_remove_use_actual_bytes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create(); var upload = await Upload(f, Png);
        var saved = Check(await f.Call("contacts_update", new() { ["workspace"] = f.Workspace, ["id"] = Id(c), ["expectedVersion"] = c["version"]!.DeepClone(), ["requestKey"] = Key(), ["patches"] = new JsonArray(), ["photo"] = new JsonObject { ["action"] = "set", ["uploadId"] = upload } }));
        var read = Check(await f.Call("photo_get", new() { ["workspace"] = f.Workspace, ["id"] = Id(c), ["expectedVersion"] = saved.Data!["version"]!.DeepClone(), ["count"] = 1024 }));
        await Assert.That(read.Data!["sha256"]!.ToString()).IsEqualTo(JsonSemantics.Hash(Png));
        await Assert.That(Convert.FromBase64String(read.Data["base64"]!.ToString()).SequenceEqual(Png)).IsTrue();
        Check(await f.Call("photo_upload_abort",new(){["uploadId"]=upload}));
        var pngPath=Check(await f.Get(Id(c))).Data!["document"]!["photo"]!.ToString();
        using var bitmap=new SkiaSharp.SKBitmap(32,32);bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);
        using(var samePathImage=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,90))
        {
            var changed=samePathImage.ToArray();await File.WriteAllBytesAsync(Path.Combine(f.Root,pngPath.Replace('/',Path.DirectorySeparatorChar)),changed);
            var refreshed=Check(await f.Get(Id(c)));await Assert.That(refreshed.Data!["document"]!["photo"]!.ToString()).IsEqualTo(pngPath);
            var observed=Check(await f.Call("photo_get",new(){["workspace"]=f.Workspace,["id"]=Id(c),["expectedVersion"]=refreshed.Data["version"]!.DeepClone(),["count"]=1048576}));await Assert.That(observed.Data!["sha256"]!.ToString()).IsEqualTo(JsonSemantics.Hash(changed));
            saved=saved with{Data=new JsonObject{["version"]=refreshed.Data["version"]!.DeepClone()}};
        }
        foreach(var format in new[]{SkiaSharp.SKEncodedImageFormat.Png,SkiaSharp.SKEncodedImageFormat.Jpeg})
        {
            using var encoded=image.Encode(format,90);var bytes=encoded.ToArray();var nextUpload=await Upload(f,bytes);
            saved=Check(await f.Call("contacts_update",new(){["workspace"]=f.Workspace,["id"]=Id(c),["expectedVersion"]=saved.Data!["version"]!.DeepClone(),["requestKey"]=Key(),["patches"]=new JsonArray(),["photo"]=new JsonObject{["action"]="set",["uploadId"]=nextUpload}}));
            var actual=Check(await f.Call("photo_get",new(){["workspace"]=f.Workspace,["id"]=Id(c),["expectedVersion"]=saved.Data!["version"]!.DeepClone(),["count"]=1048576}));await Assert.That(actual.Data!["sha256"]!.ToString()).IsEqualTo(JsonSemantics.Hash(bytes));await Assert.That(Convert.FromBase64String(actual.Data["base64"]!.ToString()).SequenceEqual(bytes)).IsTrue();
            Check(await f.Call("photo_upload_abort",new(){["uploadId"]=nextUpload}));
        }
        Check(await f.Call("contacts_update", new() { ["workspace"] = f.Workspace, ["id"] = Id(c), ["expectedVersion"] = saved.Data!["version"]!.DeepClone(), ["requestKey"] = Key(), ["patches"] = new JsonArray(), ["photo"] = new JsonObject { ["action"] = "remove" } }));
        await Assert.That(Check(await f.Get(Id(c))).Data!["photo"]).IsNull();
    }
    [Test]
    public async Task Group_CRUD_membership_restricted_delete_and_versions_use_real_files()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var group = Check(await f.Call("groups_create", new() { ["workspace"] = f.Workspace, ["name"] = "Команда", ["requestKey"] = Key() }));
        var id = group.Data!["id"]!.ToString(); var contact = await f.Create();
        Check(await f.Update(Id(contact), contact["version"]!.ToString(), Patch("/labels", new JsonArray(id), "replace")));
        var result = await f.Call("groups_delete", new() { ["workspace"] = f.Workspace, ["id"] = id, ["expectedVersion"] = group.Data!["version"]!.DeepClone(), ["requestKey"] = Key(), ["confirmDelete"] = true });
        await Assert.That(result.Error!.Code).IsEqualTo("group_in_use");
        var current = Check(await f.Get(Id(contact))); Check(await f.Update(Id(contact), Ver(current), Patch("/labels", new JsonArray(), "replace")));
        var g = Check(await f.Call("groups_get", new() { ["workspace"] = f.Workspace, ["id"] = id }));
        Check(await f.Call("groups_delete", new() { ["workspace"] = f.Workspace, ["id"] = id, ["expectedVersion"] = g.Data!["version"]!.DeepClone(), ["requestKey"] = Key(), ["confirmDelete"] = true }));
        await Assert.That(Check(await f.Call("groups_list", new() { ["workspace"] = f.Workspace })).Data!["total"]!.GetValue<int>()).IsEqualTo(0);
    }
    [Test]
    public async Task Sync_prepare_review_apply_covers_demo_Google_and_history()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await f.Create("Агентская запись"); var prepared = await Prepare(f); var result = await Apply(f, prepared);
        await Assert.That(result.Data!["unknown"]!.GetValue<int>()).IsEqualTo(0); await Assert.That(result.Data["failed"]!.GetValue<int>()).IsEqualTo(0);
        var remote = await f.Gateway.ReadAllAsync(); await Assert.That(remote.People.Any(p => p["names"]?[0]?["givenName"]?.ToString() == "Агентская запись")).IsTrue();
        var history = Check(await f.Call("history_list", new() { ["workspace"] = f.Workspace })); await Assert.That(history.Data!["items"]!.AsArray().Any(e => e!["source"]!.ToString() == "sync")).IsTrue();
    }
    [Test]
    public async Task Two_agents_and_GUI_retain_independent_actual_plans_and_wrong_owner_is_rejected()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var p1 = await Prepare(f); var b2 = f.Runtime.ConnectBridge(); var p2 = await Prepare(f, b2);
        Check(await f.Call("sync_get_plan", new() { ["planId"] = p1.Data!["planId"]!.DeepClone() }));
        var other = await f.Call("sync_get_plan", new() { ["planId"] = p1.Data!["planId"]!.DeepClone() }, b2); await Assert.That(other.Error!.Code).IsEqualTo("plan_not_owned");
        using (PlanOrigin.Enter("gui")) _ = await f.Coordinator.PrepareAsync(f.Root, DemoAccountConnector.Identity);
        Check(await f.Call("sync_get_plan", new() { ["planId"] = p1.Data!["planId"]!.DeepClone() }));
        Check(await f.Call("sync_get_plan", new() { ["planId"] = p2.Data!["planId"]!.DeepClone() }, b2)); f.Runtime.DisconnectBridge(b2);
    }
    [Test]
    public async Task Invalid_selection_keeps_plan_and_local_mutation_invalidates_it()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var contact = await f.Create(); var p = await Prepare(f); var id = p.Data!["planId"]!.ToString();
        var bad = await f.Finish(await f.Call("sync_apply", new() { ["planId"] = id, ["choices"] = new JsonArray(), ["selectionDigest"] = "wrong", ["requestKey"] = Key() }));
        await Assert.That(bad.Status).IsEqualTo("error");
        Check(await f.Call("sync_get_plan", new() { ["planId"] = id }));
        var fresh = Check(await f.Get(Id(contact))); Check(await f.Update(Id(contact), Ver(fresh), Patch("/starred", JsonValue.Create(true))));
        var stale = await f.Call("sync_get_plan", new() { ["planId"] = id }); await Assert.That(stale.Error!.Code).IsEqualTo("plan_stale");
    }
    [Test]
    public async Task Large_contact_descriptor_roundtrips_chunks_and_becomes_stale_after_edit()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create(data: new JsonObject { ["biographies"] = new JsonArray(new JsonObject { ["value"] = new string('Я', 100000), ["contentType"] = "TEXT_PLAIN" }) });
        var read = Check(await f.Get(Id(c))); var descriptor = read.Data!["document"]!.AsObject(); await Assert.That(descriptor["externalValue"]!.GetValue<bool>()).IsTrue();
        var chunk = Check(await f.Call("values_read", new() { ["valueId"] = descriptor["valueId"]!.DeepClone(), ["version"] = descriptor["version"]!.DeepClone(), ["count"] = 1024 * 1024 }));
        var bytes = Convert.FromBase64String(chunk.Data!["base64"]!.ToString()); await Assert.That(JsonSemantics.Hash(bytes)).IsEqualTo(descriptor["sha256"]!.ToString());
        Check(await f.Update(Id(c), Ver(read), Patch("/starred", JsonValue.Create(true))));
        var stale = await f.Call("values_read", new() { ["valueId"] = descriptor["valueId"]!.DeepClone(), ["version"] = descriptor["version"]!.DeepClone() }); await Assert.That(stale.Error!.Code).IsEqualTo("value_stale");
    }
    [Test]
    public async Task Deleted_local_contact_restores_same_UUID_and_photo_from_editor_history()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var c = await f.Create(); var id = Id(c);
        Check(await f.Call("contacts_delete", new() { ["workspace"] = f.Workspace, ["id"] = id, ["expectedVersion"] = c["version"]!.DeepClone(), ["requestKey"] = Key(), ["confirmDelete"] = true }));
        var history = Check(await f.Call("history_list", new() { ["workspace"] = f.Workspace })); var backup = history.Data!["items"]!.AsArray().Last(e => e!["source"]!.ToString() == "localEditor" && e["available"]!.GetValue<bool>())!;
        var restoredPlan = Check(await f.Finish(await f.Call("history_prepare_restore", new() { ["workspace"] = f.Workspace, ["backupId"] = backup["backupId"]!.DeepClone(), ["expectedAccountKey"] = DemoAccountConnector.Identity.Key })));
        Check(await f.Finish(await f.Call("history_apply_local_restore", new() { ["planId"] = restoredPlan.Data!["planId"]!.DeepClone(), ["selectionDigest"] = restoredPlan.Data["selectionDigest"]!.DeepClone(), ["choices"] = new JsonArray(new JsonObject { ["entryKey"] = id, ["resolution"] = "UseLocal" }), ["requestKey"] = Key() })));
        await Assert.That(Check(await f.Get(id)).Data!["id"]!.ToString()).IsEqualTo(id);
    }
    [Test]
    public async Task Account_disconnect_and_connect_exact_replay_survive_identity_transition()
    {
        await using var f = new Fixture(); var disconnect = new JsonObject { ["requestKey"] = Key(), ["expectedAccountKey"] = DemoAccountConnector.Identity.Key };
        Check(await f.Call("account_disconnect", disconnect)); Check(await f.Call("account_disconnect", disconnect));
        var connect = new JsonObject { ["requestKey"] = Key(), ["expectedAccountKey"] = null, ["interactive"] = true };
        Check(await f.Finish(await f.Call("account_connect", connect))); var replay = Check(await f.Call("account_connect", connect)); await Assert.That(replay.Data!["connected"]!.GetValue<bool>()).IsTrue();
    }
}
