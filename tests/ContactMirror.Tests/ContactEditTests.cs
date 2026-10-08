using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Core;
using ContactMirror.Application;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Demo;
using Xunit;

namespace ContactMirror.Tests;

public sealed partial class CoordinatorTests
{
    private static JsonArray Sample(string field) => JsonNode.Parse(field switch
    {
        "names" => "[{\"givenName\":\"Иван\",\"familyName\":\"Петренко\"}]",
        "addresses" => "[{\"streetAddress\":\"Тестовая 1\",\"city\":\"Город\"}]",
        "birthdays" or "events" => "[{\"date\":{\"year\":2000,\"month\":5,\"day\":12}}]",
        "clientData" or "userDefined" => "[{\"key\":\"test\",\"value\":\"changed\"}]",
        "locales" => "[{\"value\":\"ru\"}]",
        "organizations" => "[{\"name\":\"Тестовая организация\",\"title\":\"Инженер\"}]",
        "biographies" => "[{\"value\":\"Новая заметка\",\"contentType\":\"TEXT_PLAIN\"}]",
        "locations" => "[{\"value\":\"Офис\",\"type\":\"desk\"}]",
        "calendarUrls" => "[{\"url\":\"https://example.test/new\"}]",
        "urls" => "[{\"value\":\"https://example.test/new\"}]",
        "imClients" => "[{\"username\":\"changed\",\"protocol\":\"test\"}]",
        "relations" => "[{\"person\":\"changed\",\"type\":\"friend\"}]",
        "emailAddresses" => "[{\"value\":\"new@example.test\"}]",
        "phoneNumbers" => "[{\"value\":\"+1 202 555 0199\"}]",
        _ => "[{\"value\":\"changed\"}]"
    })!.AsArray();
    public static IEnumerable<object[]> WritableCategories => CapabilityRegistry.WritableFields.Select(f => new object[] { f });

    [Theory, MemberData(nameof(WritableCategories))]
    public async Task EveryWritableCategoryHasReadablePreviewExactMaskAndConverges(string field)
    {
        await using var h = new Harness(); await h.InitialAsync();
        var target = Sample(field); Assert.Empty(CapabilityRegistry.ValidateEditable(field, target));
        await h.EditAsync(d => d["data"]![field] = target.DeepClone());
        var original = await h.ReadAsync(); var resource = original["google"]!["resourceName"]!.ToString();
        var before = await h.Remote.GetPersonAsync(resource);
        var gateway = new HookGateway(h.Remote); var coordinator = new SyncCoordinator(gateway, h.Store);
        var preview = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var entry = Assert.Single(preview.Entries, e => e.Field == field);
        Assert.Equal(ChangeKind.Upload, entry.Kind); Assert.True(entry.IsSelectable); Assert.NotEmpty(PreviewDiff.Build(entry));
        var run = await coordinator.ApplyAsync(preview, Defaults(preview)); Assert.True(run.IsComplete, string.Join(";", run.Operations.Select(o => o.Message)));
        Assert.Equal(new[] { field }, gateway.WrittenFields);
        var after = (await h.Remote.GetPersonAsync(resource))!;
        Assert.True(JsonSemantics.Equal(target, PersonCodec.Data(after)[field]));
        foreach (var other in CapabilityRegistry.WritableFields.Where(x => x != field)) Assert.True(JsonSemantics.Equal(PersonCodec.ContactValues(before!, other), PersonCodec.ContactValues(after, other)), other);
        Assert.Equal(PersonCodec.Memberships(before!), PersonCodec.Memberships(after));
        Assert.Empty((await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    private static async Task<(string Path, JsonObject Doc)> PhotoContact(Harness h) => (await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts"), "*.json").Select(async p => (Path: p, Doc: await h.ReadAsync(p))))).First(x => x.Doc["photo"] is not null);
    private static async Task EditSnapshot(Harness h, string path, bool data = true)
    {
        await h.EditAsync(d =>
        {
            if (data) { d["data"]!["names"]![0]!["familyName"] = "Петренко"; d["data"]!["names"]![0]!["unstructuredName"] = "Иван Петренко"; }
            foreach (var f in new[] { "familyName", "unstructuredName", "displayName", "displayNameLastFirst" }) d["google"]!["person"]!["names"]![0]![f] = "Петренко";
        }, path);
    }

    [Fact]
    public async Task RepairPreservesWholeContactPhotoBaselinesAndExactBackupThenUploadsEverything()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var (path, doc) = await PhotoContact(h); var id = Guid.Parse(doc["id"]!.ToString());
        EntityState baseline;
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) baseline = s.View.States[id];
        var stateJson = JsonSerializer.Serialize(baseline, JsonSemantics.Options);
        var photoPath = FileWorkspaceStore.SafePhotoPath(h.Root, doc["photo"]!.ToString());
        var image = (await File.ReadAllBytesAsync(photoPath)).Concat(new byte[] { 0 }).ToArray(); await File.WriteAllBytesAsync(photoPath, image);
        await EditSnapshot(h, path);
        await h.EditAsync(d =>
        {
            foreach (var field in new[] { "phoneNumbers", "emailAddresses", "addresses", "organizations", "biographies", "birthdays", "userDefined" }) d["data"]![field] = Sample(field);
            d["labels"] = new JsonArray(); d["starred"] = !(d["starred"]!.GetValue<bool>()); d["extensions"]!["preserve"] = "extension";
        }, path);
        var raw = await File.ReadAllBytesAsync(path); var edited = await h.ReadAsync(path);
        var gateway = new HookGateway(h.Remote); var coordinator = new SyncCoordinator(gateway, h.Store);
        var preview = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); var blocked = Assert.Single(preview.Entries, e => e.EntityId == id);
        Assert.Equal(ChangeKind.Blocked, blocked.Kind); Assert.True(blocked.CanRepairGoogleSnapshot); Assert.False(blocked.IsSelectable);
        var rows = PreviewDiff.Build(blocked);
        foreach (var field in new[] { "familyName", "unstructuredName" }) Assert.Contains(rows, r => r.Path == "$.data.names[0]." + field);
        foreach (var field in new[] { "familyName", "unstructuredName", "displayName", "displayNameLastFirst" }) Assert.Contains(rows, r => r.Path == "$.google.person.names[0]." + field);
        Assert.Equal(image, blocked.PhotoComparison!.LocalBytes); Assert.Contains("заменено", blocked.PhotoComparison.Status);
        gateway.RejectReads = true; gateway.RejectWrites = true;
        var repaired = await coordinator.RepairGoogleSnapshotAsync(preview, blocked.Key); Assert.True(repaired.IsComplete);
        var backup = Assert.Single(await h.Store.GetBackupAsync(h.Root, repaired.RunId)); Assert.Equal(raw, backup.RawBytes); Assert.Equal(image, backup.LocalPhoto);
        var final = await h.ReadAsync(path);
        foreach (var field in new[] { "data", "labels", "starred", "photo", "extensions", "id" }) Assert.True(JsonSemantics.Equal(edited[field], final[field]), field);
        Assert.True(JsonSemantics.Equal(baseline.Baseline["google"], final["google"])); Assert.Equal(image, await File.ReadAllBytesAsync(photoPath));
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) { Assert.Equal(stateJson, JsonSerializer.Serialize(s.View.States[id], JsonSemantics.Options)); Assert.Empty(s.View.Pending); }
        gateway.RejectReads = false; gateway.RejectWrites = false;
        preview = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        foreach (var field in new[] { "names", "phoneNumbers", "emailAddresses", "addresses", "organizations", "biographies", "birthdays", "userDefined", "labels", "starred", "photo" }) Assert.Contains(preview.Entries, e => e.EntityId == id && e.Field == field && e.Kind == ChangeKind.Upload);
        var applied = await coordinator.ApplyAsync(preview, Defaults(preview)); Assert.True(applied.IsComplete, string.Join(";", applied.Operations.Select(o => o.Message)));
        var next = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(next.Entries, e => e.Entity == EntityKind.Contact);
        // Computed label memberCount snapshots can change after a membership write; no contact is resent.
        if (next.Entries.Count > 0) { Assert.All(next.Entries, e => Assert.Equal(ChangeKind.Snapshot, e.Kind)); Assert.True((await coordinator.ApplyAsync(next, Defaults(next))).IsComplete); }
        Assert.Empty((await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task MetadataOnlyRepairNeverCreatesEditableUpload()
    {
        await using var h = new Harness(); await h.InitialAsync(); await EditSnapshot(h, h.FirstContact, false);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); var e = Assert.Single(p.Entries); Assert.True(e.CanRepairGoogleSnapshot);
        Assert.True((await h.Coordinator.RepairGoogleSnapshotAsync(p, e.Key)).IsComplete);
        Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Theory, InlineData("file"), InlineData("photo"), InlineData("state"), InlineData("root"), InlineData("account"), InlineData("key")]
    public async Task RepairRejectsUnreviewedInputsWithoutOverwrite(string drift)
    {
        await using var h = new Harness(); await h.InitialAsync(); var (path, doc) = await PhotoContact(h); await EditSnapshot(h, path);
        var id = Guid.Parse(doc["id"]!.ToString()); var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); var e = Assert.Single(p.Entries, e => e.EntityId == id);
        if (drift == "file") await h.EditAsync(d => d["extensions"]!["new"] = "keep", path);
        if (drift == "photo") { var pp = FileWorkspaceStore.SafePhotoPath(h.Root, doc["photo"]!.ToString()); await File.WriteAllBytesAsync(pp, (await File.ReadAllBytesAsync(pp)).Concat(new byte[] { 0 }).ToArray()); }
        if (drift == "state") { await using var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity); var state = s.View.States[id]; state.Baseline["starred"] = !state.Baseline["starred"]!.GetValue<bool>(); await s.SaveStateAsync(state); }
        if (drift is "root" or "account") p = new() { PlanId = p.PlanId, Root = drift == "root" ? h.Temp : p.Root, Account = drift == "account" ? new("other", "other@example.test") : p.Account, Entries = p.Entries };
        var bytes = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.RepairGoogleSnapshotAsync(p, drift == "key" ? "forged" : e.Key));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory, InlineData(1, false), InlineData(2, false), InlineData(1, true), InlineData(2, true)]
    public async Task RestartRepairIsExplicitLocalOnlyAndRejectsDrift(int failWrite, bool drift)
    {
        await using var h = new Harness(); await h.InitialAsync(); await EditSnapshot(h, h.FirstContact);
        var faulted = new SyncCoordinator(h.Remote, new FailStateStore(h.Store, failState: false, failWrite: failWrite));
        var p = await faulted.PrepareAsync(h.Root, DemoAccountConnector.Identity); var e = Assert.Single(p.Entries);
        Assert.Equal(1, (await faulted.RepairGoogleSnapshotAsync(p, e.Key)).Unknown);
        if (drift) await h.EditAsync(d => d["data"]!["phoneNumbers"] = Sample("phoneNumbers"));
        var gateway = new HookGateway(h.Remote); var restarted = new SyncCoordinator(gateway, h.Store);
        gateway.RejectReads = true;
        var original = await File.ReadAllBytesAsync(h.FirstContact); JournalOperation[] pending;
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) pending = s.View.Pending.ToArray();
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity));
        Assert.Equal(original, await File.ReadAllBytesAsync(h.FirstContact));
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) Assert.Equal(JsonSerializer.Serialize(pending), JsonSerializer.Serialize(s.View.Pending));
        gateway.RejectReads = false; p = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity); Assert.Equal(1, gateway.CatalogReads);
        e = Assert.Single(p.Entries, e => e.Field == "$repairGoogleSnapshot"); Assert.DoesNotContain(p.Entries, e => e.Field.StartsWith("$journal:"));
        Assert.Equal(!drift, e.CanRepairGoogleSnapshot); Assert.Equal(!drift && failWrite == 2, e.CompletesLocalRepair);
        gateway.RejectReads = true; gateway.RejectWrites = true;
        if (drift) { await Assert.ThrowsAsync<SyncException>(() => restarted.RepairGoogleSnapshotAsync(p, e.Key)); Assert.Equal(original, await File.ReadAllBytesAsync(h.FirstContact)); }
        else
        {
            Assert.True((await restarted.RepairGoogleSnapshotAsync(p, e.Key)).IsComplete);
            await using var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity); Assert.Empty(s.View.Pending);
            Assert.Equal("Петренко", s.View.Contacts[e.EntityId].Document["data"]!["names"]![0]!["familyName"]!.ToString());
        }
    }

    [Fact] public async Task PhotoFailureKeepsTextCheckpointAndReencodingDoesNotRepeatUpload()
    {
        await using var h = new Harness(); await h.InitialAsync(); var (path, doc) = await PhotoContact(h);
        var photoPath = FileWorkspaceStore.SafePhotoPath(h.Root, doc["photo"]!.ToString()); var bytes = (await File.ReadAllBytesAsync(photoPath)).Concat(new byte[] { 0 }).ToArray(); await File.WriteAllBytesAsync(photoPath, bytes);
        await h.EditAsync(d => d["data"]!["names"] = Sample("names"), path);
        var gateway = new HookGateway(h.Remote) { FailPhoto = true }; var c = new SyncCoordinator(gateway, h.Store);
        var p = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity); var result = await c.ApplyAsync(p, Defaults(p)); Assert.False(result.IsComplete); Assert.Equal(new[] { "names" }, gateway.WrittenFields);
        p = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity); Assert.DoesNotContain(p.Entries, e => e.Field == "names"); Assert.Contains(p.Entries, e => e.Field == "photo" && e.Kind == ChangeKind.Upload);
        gateway.FailPhoto = false; gateway.ReencodePhoto = true; Assert.True((await c.ApplyAsync(p, Defaults(p))).IsComplete);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(photoPath)); Assert.Empty((await c.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
        await using var session = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity); var state = session.View.States[Guid.Parse(doc["id"]!.ToString())]; Assert.NotEqual(state.PhotoLocalHash, state.PhotoRemoteHash);
    }

    [Theory, InlineData("baseline"), InlineData("duplicate"), InlineData("remote"), InlineData("pending"), InlineData("degraded"), InlineData("missingPhoto"), InlineData("outsidePhoto")]
    public async Task UnsafeContactsCannotUseSnapshotRepair(string issue)
    {
        await using var h = new Harness(); await h.InitialAsync(); var (path, doc) = await PhotoContact(h); await EditSnapshot(h, path); var id = Guid.Parse(doc["id"]!.ToString());
        if (issue == "baseline") { await using var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity); await s.RemoveStateAsync(id); }
        if (issue == "duplicate") File.Copy(path, Path.Combine(h.Root, "contacts", "duplicate.contact.json"));
        if (issue == "remote") await h.Remote.DeleteContactAsync(doc["google"]!["resourceName"]!.ToString());
        if (issue == "pending")
        { await using var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity); var run = await s.BeginRunAsync([]); await s.RecordAsync(run, new("pending", id, EntityKind.Contact, "names", "started", ResourceName: doc["google"]!["resourceName"]!.ToString())); }
        if (issue == "degraded")
        { await using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(h.Root, ".contactmirror", "state.db") + ";Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = "INSERT OR REPLACE INTO flags(name,value) VALUES('recovery','1')"; await command.ExecuteNonQueryAsync(); }
        if (issue == "missingPhoto") File.Delete(FileWorkspaceStore.SafePhotoPath(h.Root, doc["photo"]!.ToString()));
        if (issue == "outsidePhoto") await h.EditAsync(d => d["photo"] = "../outside.png", path);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(p.Entries, e => e.EntityId == id && e.CanRepairGoogleSnapshot);
    }

    [Theory, InlineData(1), InlineData(2)]
    public async Task RepeatedRepairFailuresRecoverOneLogicalIntent(int firstFailure)
    {
        await using var h = new Harness(); await h.InitialAsync(); await EditSnapshot(h, h.FirstContact);
        var failing = new SyncCoordinator(h.Remote, new FailStateStore(h.Store, failState: false, failWrite: firstFailure));
        var p = await failing.PrepareAsync(h.Root, DemoAccountConnector.Identity); var e = Assert.Single(p.Entries);
        Assert.Equal(1, (await failing.RepairGoogleSnapshotAsync(p, e.Key)).Unknown);
        failing = new SyncCoordinator(h.Remote, new FailStateStore(h.Store, failState: false, failResolve: true, failWrite: firstFailure == 1 ? 1 : 0));
        p = await failing.PrepareAsync(h.Root, DemoAccountConnector.Identity); e = Assert.Single(p.Entries, e => e.Field == "$repairGoogleSnapshot");
        Assert.True(e.CanRepairGoogleSnapshot); Assert.Equal(1, (await failing.RepairGoogleSnapshotAsync(p, e.Key)).Unknown);
        var gateway = new HookGateway(h.Remote); var restarted = new SyncCoordinator(gateway, h.Store);
        p = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity); e = Assert.Single(p.Entries, e => e.Field == "$repairGoogleSnapshot"); Assert.True(e.CanRepairGoogleSnapshot);
        gateway.RejectReads = true; gateway.RejectWrites = true; Assert.True((await restarted.RepairGoogleSnapshotAsync(p, e.Key)).IsComplete);
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) Assert.Empty(s.View.Pending);
        gateway.RejectReads = false; gateway.RejectWrites = false; p = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(p.Entries, e => e.Field == "names" && e.Kind == ChangeKind.Upload); Assert.True((await restarted.ApplyAsync(p, Defaults(p))).IsComplete);
        Assert.Empty((await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }
    [Fact] public async Task CompletedMetadataOnlyRepairRecoversOldHistoryAndSupportsLegacyJournalReader()
    {
        await using var h = new Harness(); await h.InitialAsync(); await EditSnapshot(h, h.FirstContact, false);
        var failing = new SyncCoordinator(h.Remote, new FailStateStore(h.Store, failState: false, failWrite: 2));
        var p = await failing.PrepareAsync(h.Root, DemoAccountConnector.Identity); var e = Assert.Single(p.Entries); var failed = await failing.RepairGoogleSnapshotAsync(p, e.Key); Assert.Equal(1, failed.Unknown);
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity))
        {
            var operation = Assert.Single(s.View.Pending); var legacy = System.Text.Json.JsonSerializer.Deserialize<LegacyOperation>(JsonSerializer.Serialize(operation, JsonSemantics.Options), JsonSemantics.Options)!;
            Assert.Equal("$repairGoogleSnapshot", legacy.Field); Assert.Equal("unknown", legacy.Status);
            var older = JsonSerializer.Deserialize<JournalOperation>("{\"key\":\"old\",\"entityId\":\"" + e.EntityId + "\",\"kind\":0,\"field\":\"names\",\"status\":\"started\"}", JsonSemantics.Options)!; Assert.Null(older.SnapshotRepair);
        }
        p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); e = Assert.Single(p.Entries, e => e.Field == "$repairGoogleSnapshot"); Assert.True(e.CompletesLocalRepair);
        Assert.True((await h.Coordinator.RepairGoogleSnapshotAsync(p, e.Key)).IsComplete); Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
        var history = await h.Store.GetHistoryAsync(h.Root); var oldRun = Assert.Single(history, r => r.Id == failed.RunId); Assert.Equal("recovered", oldRun.Status); Assert.Equal(0, oldRun.Unknown);
        await using (var s = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) { Assert.Empty(s.View.Pending); await s.DeleteBackupAsync(failed.RunId); }
        Assert.False(Assert.Single(await h.Store.GetHistoryAsync(h.Root), r => r.Id == failed.RunId).BackupAvailable);
    }
    private sealed record LegacyOperation(string Key, Guid EntityId, EntityKind Kind, string Field, string Status);
}
