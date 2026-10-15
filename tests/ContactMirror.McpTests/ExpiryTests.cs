using System.Text.Json.Nodes;
using ContactMirror.Core;
using TUnit.Assertions;
using TUnit.Core;
namespace ContactMirror.McpTests;
public sealed partial class RuntimeTests
{
    [Test]
    public async Task Retention_byte_counter_matches_canonical_UTF8_for_unicode_numbers_and_arrays()
    {
        using var counter=new ContactMirror.Application.Automation.JsonByteCounter();
        foreach(var node in new JsonNode[] { JsonValue.Create("Кириллица 🧙‍♂️ & <tag>")!,new JsonArray(1,true,"Я"),JsonNode.Parse("{\"large\":123456789012345678901234567890,\"precise\":1.234567890123456789e-30}")!,new JsonObject { ["text"] = new string('Я',100000) } }) await Assert.That(counter.Count(node)).IsEqualTo((long)JsonSemantics.Serialize(node).Length);
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan amount)=>now+=amount;
    }
    [Test]
    public async Task Cursor_upload_value_and_actual_prepared_plan_expire_but_saved_request_replays()
    {
        var clock=new ManualClock(); await using var f=new Fixture(clock); await f.InitializeAsync(); var c=await f.Create(data:new() { ["biographies"] = new JsonArray(new JsonObject { ["value"] = new string('X',100000) }) }); var id=Id(c); _=await f.Create("Другой");
        var page=Check(await f.Call("contacts_list",new() { ["workspace"] = f.Workspace,["take"] = 1 })); var cursor=page.Data!["nextCursor"]!.DeepClone(); clock.Advance(TimeSpan.FromSeconds(61));
        var next=await f.Call("contacts_list",new() { ["workspace"] = f.Workspace,["take"] = 1,["cursor"] = cursor }); await Assert.That(next.Error!.Code).IsEqualTo("cursor_stale");
        var upload=await Upload(f,Png); var key=Key(); var args=new JsonObject { ["workspace"] = f.Workspace,["id"] = id,["expectedVersion"] = c["version"]!.DeepClone(),["requestKey"] = key,["patches"] = new JsonArray(),["photo"] = new JsonObject { ["action"] = "set",["uploadId"] = upload } }; var saved=Check(await f.Call("contacts_update",args));
        var descriptor=Check(await f.Get(id)).Data!["document"]!.DeepClone(); var prepared=await Prepare(f); var unused=await Upload(f,Png); clock.Advance(TimeSpan.FromMinutes(11));
        var expired=await f.Call("contacts_update",new() { ["workspace"] = f.Workspace,["id"] = id,["expectedVersion"] = saved.Data!["version"]!.DeepClone(),["requestKey"] = Key(),["patches"] = new JsonArray(),["photo"] = new JsonObject { ["action"] = "set",["uploadId"] = unused } }); await Assert.That(expired.Error!.Code).IsEqualTo("upload_expired");
        clock.Advance(TimeSpan.FromMinutes(5)); var value=await f.Call("values_read",new() { ["valueId"] = descriptor["valueId"]!.DeepClone(),["version"] = descriptor["version"]!.DeepClone() }); await Assert.That(value.Error!.Code).IsEqualTo("value_stale");
        var plan=await f.Call("sync_get_plan",new() { ["planId"] = prepared.Data!["planId"]!.DeepClone() }); await Assert.That(plan.Error!.Code).IsEqualTo("plan_expired");
        await Assert.That(Check(await f.Call("contacts_update",args)).Data!.ToJsonString()).IsEqualTo(saved.Data.ToJsonString());
    }
    [Test]
    public async Task Photo_invalid_hash_invalid_content_and_size_limit_fail_without_contact_write()
    {
        await using var f=new Fixture(); await f.InitializeAsync();
        var over=await f.Call("photo_upload_begin",new() { ["workspace"] = f.Workspace,["uploadKey"] = Key(),["length"] = 20*1024*1024+1,["sha256"] = new string('A',64) }); await Assert.That(over.Error!.Code).IsEqualTo("invalid_arguments");
        var bytes=Enumerable.Repeat((byte)42,32).ToArray(); var upload=Check(await f.Call("photo_upload_begin",new() { ["workspace"] = f.Workspace,["uploadKey"] = Key(),["length"] = bytes.Length,["sha256"] = JsonSemantics.Hash(bytes) })).Data!["uploadId"]!.DeepClone();
        Check(await f.Call("photo_upload_chunk",new() { ["uploadId"] = upload.DeepClone(),["offset"] = 0,["base64"] = Convert.ToBase64String(bytes) })); var invalid=await f.Call("photo_upload_complete",new() { ["uploadId"] = upload.DeepClone() }); await Assert.That(invalid.Status).IsEqualTo("error");
        var wrong=Check(await f.Call("photo_upload_begin",new() { ["workspace"] = f.Workspace,["uploadKey"] = Key(),["length"] = Png.Length,["sha256"] = new string('B',64) })).Data!["uploadId"]!.DeepClone();
        Check(await f.Call("photo_upload_chunk",new() { ["uploadId"] = wrong.DeepClone(),["offset"] = 0,["base64"] = Convert.ToBase64String(Png) })); var mismatch=await f.Call("photo_upload_complete",new() { ["uploadId"] = wrong }); await Assert.That(mismatch.Status).IsEqualTo("error");
        await Assert.That(Directory.GetFiles(Path.Combine(f.Root,"contacts"),"*.contact.json").Length).IsEqualTo(0);
    }
    [Test]
    public async Task Large_plan_entry_is_chunked_exactly_and_value_is_invalidated_with_its_plan()
    {
        await using var f=new Fixture(); await f.InitializeAsync(); var c=await f.Create(data:new() { ["biographies"] = new JsonArray(new JsonObject { ["value"] = new string('Я',100000) }) }); var p=await Prepare(f);
        var read=Check(await f.Call("sync_get_plan",new() { ["planId"] = p.Data!["planId"]!.DeepClone(),["take"] = 200 })); await Assert.That(JsonSemantics.Serialize(read).Length < 4*1024*1024).IsTrue(); var row=read.Data!["items"]!.AsArray().First(n=>n!["id"]!.ToString()==Id(c)); var descriptor=row!["local"]!;
        using var output=new MemoryStream(); var offset=0; var total=descriptor["totalBytes"]!.GetValue<int>(); while(offset<total) { var part=Check(await f.Call("values_read",new() { ["valueId"] = descriptor["valueId"]!.DeepClone(),["version"] = descriptor["version"]!.DeepClone(),["offset"] = offset,["count"] = 8192 })); var bytes=Convert.FromBase64String(part.Data!["base64"]!.ToString()); output.Write(bytes); offset+=bytes.Length; }
        await Assert.That(JsonSemantics.Hash(output.ToArray())).IsEqualTo(descriptor["sha256"]!.ToString());
        Check(await f.Update(Id(c),c["version"]!.ToString(),Patch("/starred",JsonValue.Create(true)))); var stale=await f.Call("values_read",new() { ["valueId"] = descriptor["valueId"]!.DeepClone(),["version"] = descriptor["version"]!.DeepClone() }); await Assert.That(stale.Error!.Code).IsEqualTo("value_stale");
    }
}
