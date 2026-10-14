using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Contacts;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Demo;
using Xunit;

namespace ContactMirror.Tests;

public sealed partial class CoordinatorTests
{
    [Fact]
    public async Task First_contact_state_failure_without_groups_recovers_verified_initial_identity()
    {
        await using var h = new Harness();
        var gateway = new HookGateway(h.Remote) { BulkContacts = 1, NoGroups = true, RejectWrites = true };
        var failing = new SyncCoordinator(gateway, new FailStateStore(h.Store));
        var preview = await failing.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var edit = await failing.OpenLocalEditAsync(preview, Assert.Single(preview.Entries, e => e.Entity == EntityKind.Contact).Key);
        var remote = await File.ReadAllBytesAsync(Path.Combine(h.Temp, "remote.json"));
        var output = (JsonObject)edit.Document.DeepClone(); SetPhone(output, "После сбоя первой записи");
        await Assert.ThrowsAsync<ContactEditorWriteException>(() => failing.SaveLocalEditAsync(edit, output, new()));
        await using (var session = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity))
        { Assert.True(session.View.IsRecovery); Assert.False(session.HasRecoveryFlag); Assert.Empty(session.View.States); }
        var healthy = new SyncCoordinator(gateway, h.Store);
        var recovered = await healthy.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.False(recovered.IsRecovery);
        Assert.Contains(recovered.Entries, e => e.EntityId == edit.EntityId && e.Field == "phoneNumbers" && e.Kind == ChangeKind.Upload);
        Assert.DoesNotContain(recovered.Entries, e => e.Kind is ChangeKind.Blocked or ChangeKind.CreateLocal or ChangeKind.CreateRemote);
        await using (var session = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity))
        { Assert.Single(session.View.States); Assert.Equal(edit.EntityId, Assert.Single(session.View.Contacts).Key); Assert.Empty(session.View.Pending); }
        Assert.Equal(remote, await File.ReadAllBytesAsync(Path.Combine(h.Temp, "remote.json")));
        Assert.Null((await new ContactEditorStore().LoadAsync(h.Root, DemoAccountConnector.Identity)).ReadOnlyReason);
    }
    [Fact]
    public async Task Mutating_presentation_entry_cannot_broaden_registered_local_edit_scope()
    {
        await using var h = new Harness(); await h.InitialAsync(); await h.EditAsync(d => SetPhone(d, "registered field"));
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var original = preview.Entries.First(e => e.Field == "phoneNumbers"); var rows = (SyncEntry[])preview.Entries;
        rows[Array.IndexOf(rows, original)] = new() { Key = original.Key, EntityId = original.EntityId, Entity = EntityKind.Contact, Field = "$entity", Kind = ChangeKind.Upload, Name = "forged" };
        var edit = await h.Coordinator.OpenLocalEditAsync(preview, original.Key); Assert.Equal("phoneNumbers", edit.Field);
        var output = (JsonObject)edit.Document.DeepClone(); output["data"]!["names"]![0]!["givenName"] = "Другая область";
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.SaveLocalEditAsync(edit, output, new()));
    }
    [Fact]
    public async Task Remote_only_dependencies_are_matched_by_resource_and_materialize_without_duplicates()
    {
        await using var h = new Harness();
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        SyncLocalEdit? original = null;
        foreach (var row in preview.Entries.Where(e => e.Entity == EntityKind.Contact && e.Kind == ChangeKind.CreateLocal))
        { var candidate = await h.Coordinator.OpenLocalEditAsync(preview, row.Key); if (candidate.MissingLabels.Count > 0) { original = candidate; break; } }
        Assert.NotNull(original);
        var remoteBefore = await File.ReadAllBytesAsync(Path.Combine(h.Temp, "remote.json"));
        await h.Coordinator.SaveLocalLabelDependenciesAsync(original);
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.SaveLocalEditAsync(original, original.Document, new()));
        var fresh = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var rowAfter = fresh.Entries.Single(e => e.Entity == EntityKind.Contact && e.Google?["resourceName"]?.ToString() == original.ResourceName && e.Field == "$entity");
        var updated = await h.Coordinator.OpenLocalEditAsync(fresh, rowAfter.Key);
        Assert.Empty(updated.MissingLabels); Assert.Equal(original.SourceId, updated.SourceId);
        await h.Coordinator.SaveLocalEditAsync(updated, updated.Document, new());
        var after = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(after.Entries, e => e.EntityId == updated.EntityId && e.Kind is ChangeKind.CreateLocal or ChangeKind.CreateRemote or ChangeKind.Blocked);
        Assert.Equal(remoteBefore, await File.ReadAllBytesAsync(Path.Combine(h.Temp, "remote.json")));
        Assert.Null((await new ContactEditorStore().LoadAsync(h.Root, DemoAccountConnector.Identity)).ReadOnlyReason);
    }

    [Fact]
    public async Task Replaced_workspace_identity_invalidates_local_edit_before_backup_or_file_write()
    {
        await using var h = new Harness(); await h.InitialAsync(); await h.EditAsync(d => SetPhone(d, "manifest guard"));
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var edit = await h.Coordinator.OpenLocalEditAsync(preview, preview.Entries.First(e => e.Field == "phoneNumbers").Key);
        var path = Path.Combine(h.Root, ".contactmirror", "workspace.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!; manifest["workspaceId"] = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        var before = await File.ReadAllBytesAsync(h.FirstContact); var history = (await h.Coordinator.GetHistoryAsync(h.Root)).Count;
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.SaveLocalEditAsync(edit, edit.Document, new()));
        Assert.Equal(before, await File.ReadAllBytesAsync(h.FirstContact)); Assert.Equal(history, (await h.Coordinator.GetHistoryAsync(h.Root)).Count);
    }

    [Theory, InlineData(EntityKind.Contact), InlineData(EntityKind.Group)]
    public async Task Restoring_deleted_local_file_uses_trusted_google_envelope_without_advancing_baseline(EntityKind kind)
    {
        await using var h = new Harness(); await h.InitialAsync();
        var path = kind == EntityKind.Contact ? h.FirstContact : Directory.GetFiles(Path.Combine(h.Root, "groups"), "*.json").First();
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); var id = Guid.Parse(doc["id"]!.ToString()); var resource = doc["google"]!["resourceName"]!.ToString();
        string originalState;
        await using (var session = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity)) originalState = JsonSerializer.Serialize(session.View.States[id], JsonSemantics.Options);
        File.Delete(path);
        if (kind == EntityKind.Contact) await h.Remote.UpdateContactAsync(resource, new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Новая версия Google" }) }, ["names"]);
        else await h.Remote.UpdateGroupAsync(resource, "Новое имя Google", new JsonArray());
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var e = p.Entries.Single(e => e.Entity == kind && e.EntityId == id && e.Field == "$entity");
        var edit = await h.Coordinator.OpenLocalEditAsync(p, e.Key);
        await h.Coordinator.SaveLocalEditAsync(edit, edit.Document, new());
        var next = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(next.Entries, e => e.EntityId == id && e.Kind == ChangeKind.Blocked);
        await using var actual = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Equal(originalState, JsonSerializer.Serialize(actual.View.States[id], JsonSemantics.Options));
    }
    [Fact]
    public async Task Local_reset_covers_all_writable_categories_preserving_baselines_other_fields_and_Google()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var remoteBefore = await File.ReadAllBytesAsync(Path.Combine(h.Temp, "remote.json"));
        var original = await h.ReadAsync(); var id = Guid.Parse(original["id"]!.ToString());
        var states = await States();
        foreach (var field in CapabilityRegistry.WritableFields)
        {
            await h.EditAsync(doc => doc["data"]![field] = new JsonArray(new JsonObject()));
            var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            var entry = Assert.Single(preview.Entries, e => e.EntityId == id && e.Field == field);
            var edit = await h.Coordinator.OpenLocalEditAsync(preview, entry.Key);
            var candidate = (JsonObject)edit.Document.DeepClone();
            if (edit.GoogleDocument!["data"]!.AsObject().TryGetPropertyValue(field, out var value)) candidate["data"]![field] = value?.DeepClone();
            else candidate["data"]!.AsObject().Remove(field);
            var saved = await h.Coordinator.SaveLocalEditAsync(edit, candidate, new());
            Assert.True(JsonNode.DeepEquals(candidate, saved.Document));
            Assert.Equal(states, await States());
            Assert.DoesNotContain((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries, e => e.EntityId == id && e.Field == field);
            Assert.Equal(remoteBefore, await File.ReadAllBytesAsync(Path.Combine(h.Temp, "remote.json")));
        }
        Assert.True(JsonNode.DeepEquals(original, await h.ReadAsync()));
        async Task<string> States() { await using var session = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity); return JsonSerializer.Serialize(session.View.States.OrderBy(x => x.Key), JsonSemantics.Options); }
    }

    [Fact]
    public async Task Local_edit_rejects_other_fields_file_drift_and_old_plan_without_remote_mutation()
    {
        await using var h = new Harness(); await h.InitialAsync(); await h.EditAsync(d => SetPhone(d, "local edit"));
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var edit = await h.Coordinator.OpenLocalEditAsync(preview, preview.Entries.First(e => e.Field == "phoneNumbers").Key);
        var candidate = (JsonObject)edit.Document.DeepClone(); candidate["starred"] = !candidate["starred"]!.GetValue<bool>();
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.SaveLocalEditAsync(edit, candidate, new()));
        candidate = (JsonObject)edit.Document.DeepClone(); candidate["data"]!["phoneNumbers"]![0]!["value"] = "manual";
        await h.EditAsync(d => d["extensions"]!["external"] = "keep");
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.SaveLocalEditAsync(edit, candidate, new()));
        await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.SaveLocalEditAsync(edit, candidate, new()));
        Assert.Equal("keep", (await h.ReadAsync())["extensions"]!["external"]!.ToString());
    }

    [Fact]
    public async Task Remote_only_contact_and_group_materialize_with_remote_baseline_then_manual_delta_is_upload()
    {
        await using var h = new Harness(); var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var group = p.Entries.First(e => e.Entity == EntityKind.Group && e.Kind == ChangeKind.CreateLocal);
        var target = await h.Coordinator.OpenLocalEditAsync(p, group.Key);
        var candidate = (JsonObject)target.Document.DeepClone(); candidate["name"] = "Локальное название";
        await h.Coordinator.SaveLocalEditAsync(target, candidate, new());
        var next = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(next.Entries, e => e.EntityId == group.EntityId && e.Field == "name" && e.Kind == ChangeKind.Upload);
        var contactEntry = next.Entries.First(e => e.Entity == EntityKind.Contact && e.Kind == ChangeKind.CreateLocal);
        var contact = await h.Coordinator.OpenLocalEditAsync(next, contactEntry.Key);
        if (contact.MissingLabels.Count > 0)
        {
            await h.Coordinator.SaveLocalLabelDependenciesAsync(contact);
            next = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            contact = await h.Coordinator.OpenLocalEditAsync(next, next.Entries.First(e => e.EntityId == contactEntry.EntityId && e.Field == "$entity").Key);
        }
        candidate = (JsonObject)contact.Document.DeepClone(); candidate["data"]!["names"] = new JsonArray(new JsonObject { ["givenName"] = "Ручная правка до загрузки" });
        await h.Coordinator.SaveLocalEditAsync(contact, candidate, new());
        var final = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(final.Entries, e => e.EntityId == contactEntry.EntityId && e.Field == "names" && e.Kind == ChangeKind.Upload);
        Assert.DoesNotContain(final.Entries, e => e.EntityId == contactEntry.EntityId && e.Kind is ChangeKind.CreateLocal or ChangeKind.CreateRemote or ChangeKind.Blocked);
        var catalog = await new ContactEditorStore().LoadAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Null(catalog.ReadOnlyReason);
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task Interrupted_local_save_is_classified_and_recheck_resolves_only_verified_local_intent(bool newContact)
    {
        await using var h = new Harness();
        if (!newContact) { await h.InitialAsync(); await h.EditAsync(d => SetPhone(d, "before local fault")); }
        else
        {
            var initialPreview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            while (initialPreview.Entries.FirstOrDefault(e => e.Entity == EntityKind.Group && e.Kind == ChangeKind.CreateLocal) is { } group)
            {
                var groupEdit = await h.Coordinator.OpenLocalEditAsync(initialPreview, group.Key);
                await h.Coordinator.SaveLocalEditAsync(groupEdit, groupEdit.Document, new());
                initialPreview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            }
        }
        var failing = new SyncCoordinator(h.Remote, new FailStateStore(h.Store, failState: newContact, failWrite: newContact ? 0 : 2));
        var p = await failing.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var entry = p.Entries.First(e => e.Entity == EntityKind.Contact && (newContact ? e.Kind == ChangeKind.CreateLocal : e.Field == "phoneNumbers"));
        var edit = await failing.OpenLocalEditAsync(p, entry.Key);
        if (edit.MissingLabels.Count > 0)
        {
            await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            // Pick a remote contact without label dependencies to isolate the state-write fault.
            foreach (var e in p.Entries.Where(e => e.Entity == EntityKind.Contact && e.Kind == ChangeKind.CreateLocal))
            { edit = await failing.OpenLocalEditAsync(p, e.Key); if (edit.MissingLabels.Count == 0) break; }
        }
        var output = (JsonObject)edit.Document.DeepClone(); output["data"]!["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "after local fault" });
        await Assert.ThrowsAsync<ContactEditorWriteException>(() => failing.SaveLocalEditAsync(edit, output, new()));
        var recovered = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.False(recovered.IsRecovery);
        Assert.Contains(recovered.Entries, e => e.EntityId == edit.EntityId && e.Field == "phoneNumbers" && e.Kind == ChangeKind.Upload);
        Assert.Null((await new ContactEditorStore().LoadAsync(h.Root, DemoAccountConnector.Identity)).ReadOnlyReason);
    }
}
