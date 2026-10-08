using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Demo;
using Xunit;

namespace ContactMirror.Tests;

public sealed partial class CoordinatorTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public string Temp { get; } = Path.Combine(Path.GetTempPath(), "ContactMirrorTests", Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public DemoGoogleGateway Remote { get; }
        public FileWorkspaceStore Store { get; } = new();
        public SyncCoordinator Coordinator { get; }
        public Harness()
        {
            Root = Path.Combine(Temp, "workspace"); Directory.CreateDirectory(Root);
            Remote = new(Path.Combine(Temp, "remote.json")); Coordinator = new(Remote, Store, e => Console.Error.WriteLine(e));
        }
        public async Task<SyncRunResult> InitialAsync()
        {
            var preview = await Coordinator.PrepareAsync(Root, DemoAccountConnector.Identity);
            var result = await Coordinator.ApplyAsync(preview, Defaults(preview));
            Assert.True(result.IsComplete, string.Join("; ", result.Operations.Where(o => o.Status != "confirmed").Select(o => o.Message)));
            return result;
        }
        public string FirstContact => Directory.GetFiles(Path.Combine(Root, "contacts"), "*.contact.json").Order().First();
        public async Task<JsonObject> ReadAsync(string? path = null) => JsonSemantics.ParseObject(await File.ReadAllBytesAsync(path ?? FirstContact), "contact.json");
        public async Task EditAsync(Action<JsonObject> edit, string? path = null)
        { var doc = await ReadAsync(path); edit(doc); await File.WriteAllBytesAsync(path ?? FirstContact, JsonSemantics.Serialize(doc)); }
        public ValueTask DisposeAsync()
        {
            // Test paths are fixed beneath this instance's GUID directory.
            if (Directory.Exists(Temp)) Directory.Delete(Temp, true);
            return ValueTask.CompletedTask;
        }
    }
    private static IReadOnlyList<PlanChoice> Defaults(SyncPreview p) => p.Entries.Where(e => e.DefaultSelected).Select(e => new PlanChoice(e.Key)).ToArray();
    private static void SetPhone(JsonObject doc, string value) => doc["data"]!["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = value, ["type"] = "Приёмная" });

    [Fact] public async Task InitialDownloadAndRepeatAreIdempotent()
    {
        await using var h = new Harness(); await h.InitialAsync();
        Assert.Equal(6, Directory.GetFiles(Path.Combine(h.Root, "contacts"), "*.contact.json").Length);
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Empty(preview.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportUsesReviewedSnapshotAndManualCheckFindsLaterChanges(bool drift)
    {
        await using var h = new Harness();
        var gateway = new HookGateway(h.Remote) { BulkContacts = 30, DriftBulkRead = drift };
        var coordinator = new SyncCoordinator(gateway, h.Store);
        var preview = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var result = await coordinator.ApplyAsync(preview, Defaults(preview));
        Assert.True(result.IsComplete, string.Join("; ", result.Operations.Select(o => o.Message)));
        Assert.Equal(30, Directory.GetFiles(Path.Combine(h.Root, "contacts"), "*.contact.json").Length);
        Assert.Equal(1, gateway.CatalogReads);
        Assert.Equal(0, gateway.PersonReads);
        var next = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        if (drift)
        {
            Assert.Contains(next.Entries, e => e.Field == "names" && e.Kind == ChangeKind.Download);
            gateway.RejectReads = true;
            Assert.True((await coordinator.ApplyAsync(next, Defaults(next))).IsComplete);
            gateway.RejectReads = false;
            Assert.Empty((await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
        }
        else Assert.Empty(next.Entries);
    }

    [Fact] public async Task InitialExportUsesAlreadyLoadedPhotosWithoutReadingGoogleAgain()
    {
        await using var h = new Harness();
        var gateway = new HookGateway(h.Remote);
        var coordinator = new SyncCoordinator(gateway, h.Store);
        var preview = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        gateway.RejectReads = true;
        var result = await coordinator.ApplyAsync(preview, Defaults(preview));
        Assert.True(result.IsComplete, string.Join("; ", result.Operations.Select(o => o.Message)));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(h.Root, "photos")));
        Assert.Equal(1, gateway.CatalogReads);
        Assert.Equal(0, gateway.PersonReads);
    }

    [Fact] public async Task UploadStillRejectsGoogleChangesAfterPreview()
    {
        await using var h = new Harness(); await h.InitialAsync();
        await h.EditAsync(d => SetPhone(d, "+7 000 000-00-55"));
        var document = await h.ReadAsync();
        var resource = document["google"]!["resourceName"]!.GetValue<string>();
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var before = (await h.Remote.GetPersonAsync(resource))!;
        await h.Remote.UpdateContactAsync(resource, new JsonObject
        {
            ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Server changed after preview" }),
            ["metadata"] = new JsonObject { ["sources"] = new JsonArray(PersonCodec.Source(before).DeepClone()) }
        }, ["names"]);
        var error = await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.ApplyAsync(preview, Defaults(preview)));
        Assert.Equal("remoteDrift", error.Code);
        Assert.True(JsonSemantics.Equal(PersonCodec.Data(before)["phoneNumbers"], PersonCodec.Data((await h.Remote.GetPersonAsync(resource))!)["phoneNumbers"]));
        Assert.Equal("+7 000 000-00-55", (await h.ReadAsync())["data"]!["phoneNumbers"]![0]!["value"]!.GetValue<string>());
    }

    [Fact] public async Task TwoFieldCommitsPreserveAllBaselinesAndLaterReversion()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var before = await h.ReadAsync(); var oldName = before["data"]!["names"]!.DeepClone();
        await h.EditAsync(d => { d["data"]!["names"] = new JsonArray(new JsonObject { ["givenName"] = "Изменённое имя" }); SetPhone(d, "+7 000 000-01-01"); });
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); var run = await h.Coordinator.ApplyAsync(p, Defaults(p));
        Assert.True(run.IsComplete, string.Join("; ", run.Operations.Select(o => o.Key + ": " + o.Message)));
        var repeat = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Empty(repeat.Entries);
        await h.EditAsync(d => d["data"]!["names"] = oldName.DeepClone());
        var reverted = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(reverted.Entries, e => e.Field == "names" && e.Kind == ChangeKind.Upload);
    }

    [Fact] public async Task RestoringInitialDownloadNeverDeletesPreExistingGoogleContacts()
    {
        await using var h = new Harness(); var first = await h.InitialAsync();
        var before = await h.Remote.ReadAllAsync();
        var restore = await h.Coordinator.PrepareRestoreAsync(h.Root, DemoAccountConnector.Identity, first.RunId);
        Assert.DoesNotContain(restore.Entries, e => e.Kind == ChangeKind.DeleteRemote);
        Assert.Equal(before.People.Count, (await h.Remote.ReadAllAsync()).People.Count);
    }

    [Fact] public async Task RestoreUploadOffersActualRemoteBeforeImage()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var original = (await h.ReadAsync())["data"]!["phoneNumbers"]?.DeepClone() ?? new JsonArray();
        await h.EditAsync(d => SetPhone(d, "+7 000 000-09-01"));
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var uploaded = await h.Coordinator.ApplyAsync(p, Defaults(p)); Assert.True(uploaded.IsComplete);
        var restore = await h.Coordinator.PrepareRestoreAsync(h.Root, DemoAccountConnector.Identity, uploaded.RunId);
        var entry = Assert.Single(restore.Entries, e => e.Field == "$restore:phoneNumbers");
        Assert.Equal(ChangeKind.Conflict, entry.Kind); Assert.False(entry.DefaultSelected);
        Assert.True(JsonSemantics.Equal(original, entry.Google));
        var run = await h.Coordinator.ApplyAsync(restore, [new(entry.Key, Resolution.UseGoogle)]); Assert.True(run.IsComplete);
        var doc = await h.ReadAsync(); var remote = await h.Remote.GetPersonAsync(doc["google"]!["resourceName"]!.GetValue<string>());
        Assert.True(JsonSemantics.Equal(original, PersonCodec.Data(remote!)["phoneNumbers"] ?? new JsonArray()));
        Assert.True(JsonSemantics.Equal(original, doc["data"]!["phoneNumbers"]));
        Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnknownRestoreCreateWithoutLocalFileRequiresRelinkInsteadOfDuplicate(bool editPhotoAfterLink, bool crashBeforeResolve)
    {
        await using var h = new Harness(); await h.InitialAsync();
        var target = (await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts")).Select(async path => (path, doc: await h.ReadAsync(path))))).First(x => x.doc["photo"] is not null);
        await h.EditAsync(d => SetPhone(d, "+7 000 000-10-01"), target.path);
        var upload = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var uploaded = await h.Coordinator.ApplyAsync(upload, Defaults(upload)); Assert.True(uploaded.IsComplete);
        var doc = await h.ReadAsync(target.path); var id = Guid.Parse(doc["id"]!.GetValue<string>());
        await h.Remote.DeleteContactAsync(doc["google"]!["resourceName"]!.GetValue<string>()); File.Delete(target.path);
        var hook = new HookGateway(h.Remote) { LoseCreateResponse = true };
        var c = new SyncCoordinator(hook, h.Store);
        var restore = await c.PrepareRestoreAsync(h.Root, DemoAccountConnector.Identity, uploaded.RunId);
        var creation = Assert.Single(restore.Entries, e => e.Field == "$restoreCreate");
        var run = await c.ApplyAsync(restore, [new(creation.Key, Resolution.UseGoogle)]); Assert.Equal(1, run.Unknown);
        Assert.Equal(6, (await h.Remote.ReadAllAsync()).People.Count);
        if (!editPhotoAfterLink && !crashBeforeResolve)
        {
            // Server portrait B is downloaded during relink; queued restore portrait A remains the upload target.
            var created = (await h.Remote.ReadAllAsync()).People.Last();
            var imageA = await File.ReadAllBytesAsync(FileWorkspaceStore.SafePhotoPath(h.Root, target.doc["photo"]!.GetValue<string>()));
            await h.Remote.UpdatePhotoAsync(PersonCodec.Resource(created), [.. imageA, 8]);
        }
        var restarted = new SyncCoordinator(h.Remote, h.Store);
        var next = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(next.Entries, e => e.EntityId == id && e.Field == "$retry");
        Assert.DoesNotContain(next.Entries, e => e.EntityId == id && e.Kind == ChangeKind.CreateRemote);
        var restoredAgain = await restarted.PrepareRestoreAsync(h.Root, DemoAccountConnector.Identity, uploaded.RunId);
        Assert.DoesNotContain(restoredAgain.Entries, e => e.EntityId == id && e.Field == "$restoreCreate");
        var link = Assert.Single(restoredAgain.Entries, e => e.EntityId == id && e.Field.StartsWith("$relink:"));
        var linked = await restarted.ApplyAsync(restoredAgain, [new(link.Key, Resolution.UseGoogle)]); Assert.True(linked.IsComplete, string.Join("; ", linked.Operations.Select(o => o.Message)));
        Assert.Equal(6, (await h.Remote.ReadAllAsync()).People.Count);
        byte[]? editedPhoto = null;
        if (editPhotoAfterLink)
        {
            var originalPhoto = await File.ReadAllBytesAsync(FileWorkspaceStore.SafePhotoPath(h.Root, target.doc["photo"]!.GetValue<string>()));
            editedPhoto = [.. originalPhoto, 9];
            await File.WriteAllBytesAsync(Path.Combine(h.Root, "photos", "user-edited.png"), editedPhoto);
            await h.EditAsync(d => d["photo"] = "photos/user-edited.png", target.path);
        }
        var photoRecovery = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(photoRecovery.Entries, e => e.EntityId == id && e.Field == "photo" && e.Kind == ChangeKind.Upload);
        if (!editPhotoAfterLink && !crashBeforeResolve)
        {
            var previewPhoto = Assert.Single(photoRecovery.Entries, e => e.EntityId == id && e.Field == "photo").PhotoComparison!;
            Assert.True(previewPhoto.HasPlannedPhoto); Assert.Equal(previewPhoto.LocalHash, previewPhoto.GoogleHash);
            Assert.NotEqual(previewPhoto.PlannedHash, previewPhoto.LocalHash); Assert.Contains("из журнала", previewPhoto.Status); Assert.NotNull(previewPhoto.PlannedBytes);
        }
        if (crashBeforeResolve)
        {
            var failing = new SyncCoordinator(h.Remote, new FailStateStore(h.Store, failState: false, failResolve: true));
            var faultPlan = await failing.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            Assert.False((await failing.ApplyAsync(faultPlan, Defaults(faultPlan))).IsComplete);
            var afterCrash = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity);
            Assert.DoesNotContain(afterCrash.Entries, e => e.EntityId == id && e.Field == "photo" && e.Kind == ChangeKind.Upload);
            Assert.True((await restarted.ApplyAsync(afterCrash, Defaults(afterCrash))).IsComplete);
        }
        else Assert.True((await restarted.ApplyAsync(photoRecovery, Defaults(photoRecovery))).IsComplete);
        var finalDoc = await h.ReadAsync(target.path); Assert.NotNull(finalDoc["photo"]);
        if (editedPhoto is not null)
        {
            Assert.Equal("photos/user-edited.png", finalDoc["photo"]!.GetValue<string>());
            Assert.Equal(editedPhoto, await File.ReadAllBytesAsync(FileWorkspaceStore.SafePhotoPath(h.Root, finalDoc["photo"]!.GetValue<string>())));
            var person = await h.Remote.GetPersonAsync(finalDoc["google"]!["resourceName"]!.GetValue<string>());
            Assert.Equal(editedPhoto, await h.Remote.DownloadPhotoAsync(PersonCodec.PhotoUrl(person!)!));
        }
        Assert.Empty((await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task ReadOnlyProfileImagesAreCachedAndNeverBecomeEditablePhoto()
    {
        await using var h = new Harness(); var hooked = new HookGateway(h.Remote) { InjectReadOnlyImage = true };
        var c = new SyncCoordinator(hooked, h.Store);
        var p = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.True((await c.ApplyAsync(p, Defaults(p))).IsComplete);
        var contacts = await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts")).Select(h.ReadAsync));
        Assert.All(contacts, doc =>
        {
            var images = Assert.IsType<JsonArray>(doc["google"]!["readOnlyImages"]);
            Assert.Single(images);
            var path = Path.GetFullPath(images[0]!["path"]!.GetValue<string>(), h.Root); Assert.True(File.Exists(path));
            Assert.Equal(images[0]!["sha256"]!.GetValue<string>(), JsonSemantics.Hash(File.ReadAllBytes(path)));
        });
        Assert.Equal(1, contacts.Count(d => d["photo"] is not null));
        Assert.Empty((await c.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task NamelessContactWithExplicitEmptyArraysCanBePreviewed()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var path = await h.Coordinator.CreateContactFileAsync(h.Root);
        await h.EditAsync(d => { d["data"]!["names"] = new JsonArray(); d["data"]!["emailAddresses"] = new JsonArray(); SetPhone(d, "+7 000 000-11-01"); }, path);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(p.Entries, e => e.Kind == ChangeKind.CreateRemote && e.Name == "Контакт без имени");
    }

    [Fact] public async Task PendingNoOpGetsVisibleReconciliationAndClosesOnlyItsField()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var doc = await h.ReadAsync(); var id = Guid.Parse(doc["id"]!.GetValue<string>());
        await using (var session = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity))
        {
            var run = await session.BeginRunAsync([]);
            await session.RecordAsync(run, new("interrupted-snapshot", id, EntityKind.Contact, "$snapshot", "started", doc, doc["google"]!["resourceName"]!.GetValue<string>()));
            await session.RecordAsync(run, new("unresolved-photo", id, EntityKind.Contact, "photo", "unknown", doc, doc["google"]!["resourceName"]!.GetValue<string>()));
        }
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var journal = Assert.Single(p.Entries, e => e.EntityId == id && e.Field == "$journal:$snapshot");
        var result = await h.Coordinator.ApplyAsync(p, [new(journal.Key)]); Assert.True(result.IsComplete);
        await using var reopened = await h.Store.OpenAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(reopened.View.Pending, o => o.Field == "$snapshot");
        Assert.Contains(reopened.View.Pending, o => o.Field == "photo");
    }

    [Fact] public async Task PostPhotoWriteDriftNeverAcknowledgesDifferentImages()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var contact = (await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts")).Select(async path => (path, doc: await h.ReadAsync(path))))).First(x => x.doc["photo"] is not null);
        var photoPath = FileWorkspaceStore.SafePhotoPath(h.Root, contact.doc["photo"]!.GetValue<string>());
        var original = await File.ReadAllBytesAsync(photoPath); await File.WriteAllBytesAsync(photoPath, [.. original, 1]);
        var hook = new HookGateway(h.Remote) { AfterPhotoUpdate = () => h.Remote.UpdatePhotoAsync(contact.doc["google"]!["resourceName"]!.GetValue<string>(), [.. original, 2]) };
        var c = new SyncCoordinator(hook, h.Store);
        var p = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var run = await c.ApplyAsync(p, Defaults(p)); Assert.False(run.IsComplete);
        var next = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(next.Entries, e => e.Field == "photo" && e.Kind == ChangeKind.Conflict);
    }

    [Fact] public async Task LocalEditDuringRemoteWriteIsPreservedAndBecomesConflict()
    {
        await using var h = new Harness(); await h.InitialAsync();
        await h.EditAsync(d => SetPhone(d, "+7 000 000-02-01"));
        var remote = new HookGateway(h.Remote) { AfterUpdate = () => h.EditAsync(d => SetPhone(d, "+7 000 000-02-02")) };
        var coordinator = new SyncCoordinator(remote, h.Store);
        var p = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var run = await coordinator.ApplyAsync(p, Defaults(p));
        Assert.False(run.IsComplete);
        Assert.Equal("+7 000 000-02-02", (await h.ReadAsync())["data"]!["phoneNumbers"]![0]!["value"]!.GetValue<string>());
        var after = await coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(after.Entries, e => e.Field == "phoneNumbers" && e.Kind == ChangeKind.Conflict);
    }

    [Fact] public async Task ConfirmedCreateSurvivesLocalStateFailureWithoutAutomaticDuplicate()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var path = await h.Coordinator.CreateContactFileAsync(h.Root);
        await h.EditAsync(d => d["data"]!["names"] = new JsonArray(new JsonObject { ["givenName"] = "Новая тестовая запись" }), path);
        var failing = new FailStateStore(h.Store);
        var c = new SyncCoordinator(h.Remote, failing);
        var p = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var run = await c.ApplyAsync(p, Defaults(p)); Assert.False(run.IsComplete);
        Assert.Equal(7, (await h.Remote.ReadAllAsync()).People.Count);
        var restarted = new SyncCoordinator(h.Remote, h.Store);
        var next = await restarted.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var id = Guid.Parse((await h.ReadAsync(path))["id"]!.GetValue<string>());
        Assert.DoesNotContain(next.Entries, e => e.EntityId == id && e.Kind == ChangeKind.CreateRemote);
        Assert.Equal(7, (await h.Remote.ReadAllAsync()).People.Count);
    }

    [Fact] public async Task InvalidFileAndMissingFolderCannotBecomeRemoteDeletions()
    {
        await using var h = new Harness(); await h.InitialAsync();
        await File.WriteAllTextAsync(h.FirstContact, "{ broken");
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(p.Entries, e => e.Kind == ChangeKind.Blocked);
        Assert.DoesNotContain(p.Entries, e => e.Kind == ChangeKind.DeleteRemote);
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.PrepareAsync(Path.Combine(h.Temp, "missing"), DemoAccountConnector.Identity));
    }

    [Fact] public async Task DeletedFileIsNotSelectedByDefaultAndRequiresExplicitApply()
    {
        await using var h = new Harness(); await h.InitialAsync(); File.Delete(h.FirstContact);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var deletion = Assert.Single(p.Entries, e => e.Kind == ChangeKind.DeleteRemote);
        Assert.False(deletion.DefaultSelected); Assert.Equal(6, (await h.Remote.ReadAllAsync()).People.Count);
        var run = await h.Coordinator.ApplyAsync(p, [new(deletion.Key)]); Assert.True(run.IsComplete);
        Assert.Equal(5, (await h.Remote.ReadAllAsync()).People.Count);
    }

    [Fact] public async Task EmptyContactsFolderNeverProposesMassRemoteDeletion()
    {
        await using var h = new Harness(); await h.InitialAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(h.Root, "contacts"))) File.Delete(file);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(p.Entries, e => e.Kind == ChangeKind.DeleteRemote);
        Assert.Contains(p.Entries, e => e.Kind == ChangeKind.Blocked);
    }

    [Fact] public async Task AmbiguousSourcesCannotBeMistakenForRemoteDeletion()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var hook = new HookGateway(h.Remote) { AmbiguousSource = true };
        var p = await new SyncCoordinator(hook, h.Store).PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.DoesNotContain(p.Entries, e => e.Kind is ChangeKind.DeleteLocal or ChangeKind.DeleteRemote);
        Assert.Contains(p.Entries, e => e.Kind == ChangeKind.Blocked);
    }

    [Fact] public async Task RestoreRecreatesDeletedContactWithNewIdentityAndPhoto()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var photoContact = (await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts")).Select(async path => (path, doc: await h.ReadAsync(path))))).First(x => x.doc["photo"] is not null);
        var oldResource = photoContact.doc["google"]!["resourceName"]!.GetValue<string>();
        File.Delete(photoContact.path);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var deletion = Assert.Single(p.Entries, e => e.Kind == ChangeKind.DeleteRemote);
        var deleted = await h.Coordinator.ApplyAsync(p, [new(deletion.Key)]); Assert.True(deleted.IsComplete);
        File.Delete(FileWorkspaceStore.SafePhotoPath(h.Root, photoContact.doc["photo"]!.GetValue<string>()));
        var restore = await h.Coordinator.PrepareRestoreAsync(h.Root, DemoAccountConnector.Identity, deleted.RunId);
        Assert.Contains(restore.Entries, e => e.Kind == ChangeKind.CreateRemote);
        var restored = await h.Coordinator.ApplyAsync(restore, Defaults(restore)); Assert.True(restored.IsComplete, string.Join("; ", restored.Operations.Select(o => o.Message)));
        Assert.Equal(6, (await h.Remote.ReadAllAsync()).People.Count);
        var doc = await h.ReadAsync(photoContact.path);
        Assert.NotEqual(oldResource, doc["google"]!["resourceName"]!.GetValue<string>());
        Assert.NotNull(doc["photo"]);
        Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task SelectiveRecoveryDoesNotAcknowledgeSkippedConflict()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var local = await h.ReadAsync(); var resource = local["google"]!["resourceName"]!.GetValue<string>();
        await h.EditAsync(d => d["data"]!["names"] = new JsonArray(new JsonObject { ["givenName"] = "Локальный вариант" }));
        var person = await h.Remote.GetPersonAsync(resource);
        await h.Remote.UpdateContactAsync(resource, new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Версия Google" }), ["metadata"] = new JsonObject { ["sources"] = new JsonArray(PersonCodec.Source(person!).DeepClone()) } }, ["names"]);
        File.Delete(Path.Combine(h.Root, ".contactmirror", "state.db"));
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(p.Entries, e => e.Field == "names" && e.Kind == ChangeKind.Conflict);
        var snapshots = p.Entries.Where(e => e.Field == "$snapshot").Select(e => new PlanChoice(e.Key)).ToArray();
        Assert.NotEmpty(snapshots);
        Assert.True((await h.Coordinator.ApplyAsync(p, snapshots)).IsComplete);
        var next = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(next.Entries, e => e.Field == "names" && e.Kind == ChangeKind.Conflict);
    }

    [Fact] public async Task RestoreCreatedRemotePreservesOriginalLocalFile()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var path = await h.Coordinator.CreateContactFileAsync(h.Root);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var created = await h.Coordinator.ApplyAsync(p, Defaults(p)); Assert.True(created.IsComplete);
        var restore = await h.Coordinator.PrepareRestoreAsync(h.Root, DemoAccountConnector.Identity, created.RunId);
        var deletion = Assert.Single(restore.Entries, e => e.Kind == ChangeKind.DeleteRemote);
        Assert.False(deletion.DefaultSelected);
        var run = await h.Coordinator.ApplyAsync(restore, [new(deletion.Key)]); Assert.True(run.IsComplete);
        Assert.True(File.Exists(path)); Assert.Null((await h.ReadAsync(path))["google"]);
        Assert.Equal(6, (await h.Remote.ReadAllAsync()).People.Count);
    }

    [Fact] public async Task WrongAccountAndFutureSchemaAreProtected()
    {
        await using var h = new Harness(); await h.InitialAsync();
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.PrepareAsync(h.Root, new("different", "different@example.test")));
        await h.EditAsync(d => d["schemaVersion"] = 2);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(p.Entries, e => e.Kind == ChangeKind.Blocked);
        Assert.Equal(2, (await h.ReadAsync())["schemaVersion"]!.GetValue<int>());
    }

    [Fact] public async Task UnknownNestedServerFieldBlocksKnownSiblingWrite()
    {
        await using var h = new Harness();
        var hooked = new HookGateway(h.Remote) { InjectUnknown = true };
        var c = new SyncCoordinator(hooked, h.Store);
        var p = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity); Assert.True((await c.ApplyAsync(p, Defaults(p))).IsComplete);
        await h.EditAsync(d => SetPhone(d, "+7 000 000-03-01"));
        var next = await c.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(next.Entries, e => e.Field == "phoneNumbers" && e.Kind == ChangeKind.Blocked);
    }

    [Fact] public async Task GoogleOnlyChangePreservesLocalExtensions()
    {
        await using var h = new Harness(); await h.InitialAsync();
        await h.EditAsync(d => d["extensions"]!["localNote"] = "Только у меня");
        var doc = await h.ReadAsync(); var person = await h.Remote.GetPersonAsync(doc["google"]!["resourceName"]!.GetValue<string>());
        await h.Remote.UpdateContactAsync(PersonCodec.Resource(person!), new JsonObject { ["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "+7 000 000-04-01" }), ["metadata"] = new JsonObject { ["sources"] = new JsonArray(PersonCodec.Source(person!).DeepClone()) } }, ["phoneNumbers"]);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); var applied = await h.Coordinator.ApplyAsync(p, Defaults(p)); Assert.True(applied.IsComplete, string.Join("; ", applied.Operations.Select(o => o.Key + ": " + o.Message)));
        Assert.Equal("Только у меня", (await h.ReadAsync())["extensions"]!["localNote"]!.GetValue<string>());
        Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task RemoteOnlyMembershipAdditionDownloadsUuidStringsAndConverges()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var group = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(h.Root, "groups"), "*.group.json").First()), "group.json");
        var groupId = Guid.Parse(group["id"]!.GetValue<string>());
        var documents = await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts"), "*.contact.json").Select(async path => (path, document: await h.ReadAsync(path))));
        var contact = documents.First(item => !DocumentCodec.Contact(item.document).Labels.Contains(groupId));
        await h.Remote.ModifyMembershipAsync(group["google"]!["resourceName"]!.GetValue<string>(), contact.document["google"]!["resourceName"]!.GetValue<string>(), true);
        var preview = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(preview.Entries, entry => entry.Field == "labels" && entry.Kind == ChangeKind.Download);
        var applied = await h.Coordinator.ApplyAsync(preview, Defaults(preview));
        Assert.True(applied.IsComplete, string.Join("; ", applied.Operations.Select(operation => operation.Message)));
        Assert.Contains(groupId, DocumentCodec.Contact(await h.ReadAsync(contact.path)).Labels);
        Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task GroupDeletionRemovesLabelsWithoutDeletingContacts()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var groupFile = Directory.GetFiles(Path.Combine(h.Root, "groups"), "*.group.json").First();
        var group = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(groupFile), "group.json");
        var id = Guid.Parse(group["id"]!.GetValue<string>()); File.Delete(groupFile);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        var delete = Assert.Single(p.Entries, e => e.Entity == EntityKind.Group && e.Kind == ChangeKind.DeleteRemote);
        var run = await h.Coordinator.ApplyAsync(p, [new(delete.Key)]); Assert.True(run.IsComplete, string.Join("; ", run.Operations.Select(o => o.Message)));
        Assert.Equal(6, (await h.Remote.ReadAllAsync()).People.Count);
        foreach (var path in Directory.GetFiles(Path.Combine(h.Root, "contacts"), "*.contact.json"))
            Assert.DoesNotContain(id, DocumentCodec.Contact(await h.ReadAsync(path)).Labels);
    }

    [Fact] public async Task PhotoReplacementAtSamePathIsDetectedAndReadbackIsNoOp()
    {
        await using var h = new Harness(); await h.InitialAsync();
        var photoDoc = (await Task.WhenAll(Directory.GetFiles(Path.Combine(h.Root, "contacts"), "*.contact.json").Select(async path => (path, doc: await h.ReadAsync(path))))).First(x => x.doc["photo"] is not null);
        var path = FileWorkspaceStore.SafePhotoPath(h.Root, photoDoc.doc["photo"]!.GetValue<string>());
        var bytes = await File.ReadAllBytesAsync(path); var changed = bytes.Concat(new byte[] { 0 }).ToArray(); await File.WriteAllBytesAsync(path, changed);
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity); Assert.Contains(p.Entries, e => e.Field == "photo" && e.Kind == ChangeKind.Upload);
        var applied = await h.Coordinator.ApplyAsync(p, Defaults(p)); Assert.True(applied.IsComplete, string.Join("; ", applied.Operations.Select(o => o.Key + ": " + o.Message)));
        Assert.Empty((await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity)).Entries);
    }

    [Fact] public async Task StalePreviewAndDuplicateIdentityAreRejected()
    {
        await using var h = new Harness(); await h.InitialAsync(); await h.EditAsync(d => SetPhone(d, "+7 000 000-05-01"));
        var p = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        await h.EditAsync(d => SetPhone(d, "+7 000 000-05-02"));
        await Assert.ThrowsAsync<SyncException>(() => h.Coordinator.ApplyAsync(p, Defaults(p)));
        var copy = Path.Combine(h.Root, "contacts", "copy.contact.json"); File.Copy(h.FirstContact, copy);
        var duplicate = await h.Coordinator.PrepareAsync(h.Root, DemoAccountConnector.Identity);
        Assert.Contains(duplicate.Entries, e => e.Kind == ChangeKind.Blocked);
        Assert.DoesNotContain(duplicate.Entries, e => e.Kind == ChangeKind.DeleteRemote);
    }

    private sealed class HookGateway(IGoogleContactsGateway inner) : IGoogleContactsGateway
    {
        public int BulkContacts { get; set; }
        public bool DriftBulkRead { get; set; }
        public bool RejectReads { get; set; }
        public bool RejectWrites { get; set; }
        public bool FailPhoto { get; set; }
        public bool ReencodePhoto { get; set; }
        public List<string> WrittenFields { get; } = [];
        public int PhotoWrites { get; private set; }
        public int CatalogReads { get; private set; }
        public int PersonReads { get; private set; }
        public Func<Task>? AfterUpdate { get; set; }
        public Func<Task>? AfterPhotoUpdate { get; set; }
        public bool InjectUnknown { get; set; }
        public bool AmbiguousSource { get; set; }
        public bool InjectReadOnlyImage { get; set; }
        public bool LoseCreateResponse { get; set; }
        private JsonObject? Patch(JsonObject? p)
        {
            if (InjectUnknown && p is not null && p["phoneNumbers"] is JsonArray { Count: > 0 } a) a[0]!["futureField"] = "Keep me";
            if (AmbiguousSource && p?["metadata"]?["sources"] is JsonArray sources) sources.Add(new JsonObject { ["type"] = "CONTACT", ["id"] = "ambiguous", ["etag"] = "ambiguous" });
            if (InjectReadOnlyImage && p is not null)
            {
                p["metadata"]!["sources"]!.AsArray().Add(new JsonObject { ["type"] = "PROFILE", ["id"] = "cached-profile" });
                p["photos"] ??= new JsonArray();
                p["photos"]!.AsArray().Add(new JsonObject { ["url"] = "https://lh3.googleusercontent.com/contactmirror-demo/sample.png", ["default"] = false, ["metadata"] = new JsonObject { ["source"] = new JsonObject { ["type"] = "PROFILE", ["id"] = "cached-profile" } } });
            }
            return p;
        }
        public async Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? p = null, CancellationToken c = default)
        {
            if (RejectReads) throw new Xunit.Sdk.XunitException("Export must use the reviewed snapshot.");
            CatalogReads++;
            var r = await inner.ReadAllAsync(p, c);
            if (BulkContacts > 0)
                return new(Enumerable.Range(0, BulkContacts).Select(i => new JsonObject
                {
                    ["resourceName"] = "people/bulk" + i,
                    ["etag"] = "one",
                    ["metadata"] = new JsonObject { ["sources"] = new JsonArray(new JsonObject { ["type"] = "CONTACT", ["id"] = "bulk" + i, ["etag"] = "one" }) },
                    ["names"] = new JsonArray(new JsonObject { ["givenName"] = DriftBulkRead && CatalogReads > 1 && i == 0 ? "Changed" : "Contact " + i })
                }).ToArray(), r.Groups);
            return new(r.People.Select(x => Patch(x)!).ToArray(), r.Groups);
        }
        public async Task<JsonObject?> GetPersonAsync(string r, CancellationToken c = default) { if (RejectReads) throw new Xunit.Sdk.XunitException("Export must not reread Google."); PersonReads++; return Patch(await inner.GetPersonAsync(r, c)); }
        public async Task<JsonObject> CreateContactAsync(JsonObject p, CancellationToken c = default) { var result = await inner.CreateContactAsync(p, c); if (LoseCreateResponse) { LoseCreateResponse = false; throw new SyncException("lost-create-response", "Ответ создания потерян", true); } return result; }
        public async Task<JsonObject> UpdateContactAsync(string r, JsonObject p, IReadOnlyCollection<string> f, CancellationToken c = default) { if (RejectWrites) throw new Xunit.Sdk.XunitException("Local repair must not write Google."); WrittenFields.AddRange(f); var result = await inner.UpdateContactAsync(r, p, f, c); if (AfterUpdate is not null) { var hook = AfterUpdate; AfterUpdate = null; await hook(); } return result; }
        public Task DeleteContactAsync(string r, CancellationToken c = default) => inner.DeleteContactAsync(r, c);
        public async Task<JsonObject> UpdatePhotoAsync(string r, byte[]? b, CancellationToken c = default) { if (RejectWrites) throw new Xunit.Sdk.XunitException("Local repair must not write Google."); PhotoWrites++; if (FailPhoto) throw new SyncException("testPhotoFailure", "Synthetic photo failure"); if (ReencodePhoto && b is not null) b = b.Concat(new byte[] { 0 }).ToArray(); var result = await inner.UpdatePhotoAsync(r, b, c); if (AfterPhotoUpdate is not null) { var hook = AfterPhotoUpdate; AfterPhotoUpdate = null; await hook(); } return result; }
        public Task<byte[]> DownloadPhotoAsync(string u, CancellationToken c = default) { if (RejectReads) throw new Xunit.Sdk.XunitException("Export must use already loaded photos."); return inner.DownloadPhotoAsync(u, c); }
        public Task<JsonObject?> GetGroupAsync(string r, CancellationToken c = default) { if (RejectReads) throw new Xunit.Sdk.XunitException("Export must not reread Google."); return inner.GetGroupAsync(r, c); }
        public Task<JsonObject> CreateGroupAsync(string n, JsonArray d, CancellationToken c = default) => inner.CreateGroupAsync(n, d, c);
        public Task<JsonObject> UpdateGroupAsync(string r, string n, JsonArray d, CancellationToken c = default) => inner.UpdateGroupAsync(r, n, d, c);
        public Task DeleteGroupAsync(string r, CancellationToken c = default) => inner.DeleteGroupAsync(r, c);
        public Task ModifyMembershipAsync(string g, string p, bool a, CancellationToken c = default) => inner.ModifyMembershipAsync(g, p, a, c);
    }

    private sealed class FailStateStore(IWorkspaceStore inner, bool failState = true, bool failResolve = false, int failWrite = 0) : IWorkspaceStore
    {
        public async Task<IWorkspaceSession> OpenAsync(string r, AccountIdentity a, CancellationToken c = default) => new FailSession(await inner.OpenAsync(r, a, c), failState, failResolve, failWrite);
        public Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string r, CancellationToken c = default) => inner.GetHistoryAsync(r, c);
        public Task<IReadOnlyList<BackupItem>> GetBackupAsync(string r, Guid id, CancellationToken c = default) => inner.GetBackupAsync(r, id, c);
        public Task<string> CreateContactFileAsync(string r, CancellationToken c = default) => inner.CreateContactFileAsync(r, c);
        private sealed class FailSession(IWorkspaceSession inner, bool failState, bool failResolve, int failWrite) : IWorkspaceSession
        {
            public string Root => inner.Root; public WorkspaceView View => inner.View;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
            public Task<LocalEntity?> ReadLocalAsync(EntityKind k, Guid id, CancellationToken c = default) => inner.ReadLocalAsync(k, id, c);
            public Task<Guid> BeginRunAsync(IReadOnlyList<BackupItem> b, CancellationToken c = default) => inner.BeginRunAsync(b, c);
            public Task RecordAsync(Guid id, JournalOperation o, CancellationToken c = default) => inner.RecordAsync(id, o, c);
            public Task SaveStateAsync(EntityState s, CancellationToken c = default) => failState ? throw new IOException("Injected state failure") : inner.SaveStateAsync(s, c);
            public Task RemoveStateAsync(Guid id, CancellationToken c = default) => inner.RemoveStateAsync(id, c);
            public async Task<LocalEntity> WriteAsync(EntityKind k, Guid id, JsonObject d, string? h, byte[]? b = null, string? ph = null, bool wp = false, CancellationToken c = default)
            { if (failWrite == 1) throw new IOException("Before write"); var written = await inner.WriteAsync(k, id, d, h, b, ph, wp, c); if (failWrite == 2) throw new IOException("After atomic write"); return written; }
            public Task TrashAsync(EntityKind k, Guid id, string h, Guid r, CancellationToken c = default) => inner.TrashAsync(k, id, h, r, c);
            public Task CompleteRunAsync(Guid id, SyncRunResult r, CancellationToken c = default) => inner.CompleteRunAsync(id, r, c);
            public Task AcknowledgeRecoveryAsync(CancellationToken c = default) => inner.AcknowledgeRecoveryAsync(c);
            public Task ResolvePendingAsync(Guid id, string? field = null, CancellationToken c = default) => failResolve ? throw new IOException("Injected failure after saved state before resolve") : inner.ResolvePendingAsync(id, field, c);
            public Task CommitLocalRepairAsync(Guid runId, JournalOperation operation, SyncRunResult result, CancellationToken c = default) => failResolve ? throw new IOException("Injected repair commit failure") : inner.CommitLocalRepairAsync(runId, operation, result, c);
            public Task<string> CacheImageAsync(byte[] b, string url, CancellationToken c = default) => inner.CacheImageAsync(b, url, c);
            public Task DeleteBackupAsync(Guid id, CancellationToken c = default) => inner.DeleteBackupAsync(id, c);
        }
    }
}
