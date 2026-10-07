using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using SkiaSharp;
using Xunit;

namespace ContactMirror.GoogleTests;

public sealed class FileWorkspaceStoreSafetyTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ContactMirror-StoreSafety-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> junctions = [];
    private string Root => Path.Combine(folder, "workspace");
    private string External => Path.Combine(folder, "external");
    private static readonly AccountIdentity Account = new("synthetic-store-test", "test@example.test");
    public FileWorkspaceStoreSafetyTests() { Directory.CreateDirectory(Root); Directory.CreateDirectory(External); }
    public void Dispose()
    {
        foreach (var junction in junctions) if (Directory.Exists(junction)) Directory.Delete(junction);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
    private void Junction(string path)
    {
        var processInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        processInfo.ArgumentList.Add("/c"); processInfo.ArgumentList.Add("mklink"); processInfo.ArgumentList.Add("/J"); processInfo.ArgumentList.Add(path); processInfo.ArgumentList.Add(External);
        using var process = Process.Start(processInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "Test junction could not be created: " + output);
        junctions.Add(path);
    }
    private static JsonObject Contact(Guid id, string name = "Синтетический контакт") => DocumentCodec.ToJson(new ContactDocument
    {
        Id = id, Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = name }) }
    });
    private static byte[] Image(SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    [Fact]
    public async Task WindowsReplaceWithPinnedReadHandleReturnsExactCommittedSnapshotAndRawBeforeImage()
    {
        var store = new FileWorkspaceStore();
        await using var session = await store.OpenAsync(Root, Account);
        var path = await store.CreateContactFileAsync(Root);
        var original = await File.ReadAllBytesAsync(path);
        var id = DocumentCodec.Contact(JsonSemantics.ParseObject(original, "test")).Id;
        var local = (await session.ReadLocalAsync(EntityKind.Contact, id))!;
        var newDoc = Contact(id, "Обновлённый");
        var committed = await session.WriteAsync(EntityKind.Contact, id, newDoc, local.Hash);
        Assert.Equal(JsonSemantics.Serialize(newDoc), committed.RawBytes);
        Assert.Equal(JsonSemantics.Hash(committed.RawBytes!), committed.Hash);
        Assert.Equal(committed.RawBytes, await File.ReadAllBytesAsync(path));
        var displaced = Directory.GetFiles(Path.Combine(Root, ".contactmirror", "recovery", "displaced"), "*.json");
        Assert.Single(displaced);
        Assert.Equal(original, await File.ReadAllBytesAsync(displaced[0]));
        await File.WriteAllBytesAsync(path, JsonSemantics.Serialize(Contact(id, "Поздняя правка")));
        Assert.Equal("Обновлённый", committed.Document["data"]!["names"]![0]!["givenName"]!.GetValue<string>());
        Assert.NotEqual(JsonSemantics.Hash(await File.ReadAllBytesAsync(path)), committed.Hash);
    }

    [Fact]
    public async Task CompetingAtomicRenameKeepsActualDisplacedEditAndFlagsRecovery()
    {
        var plain = new FileWorkspaceStore();
        Guid id;
        string target;
        await using (var setup = await plain.OpenAsync(Root, Account))
        {
            target = await plain.CreateContactFileAsync(Root);
            id = DocumentCodec.Contact(JsonSemantics.ParseObject(await File.ReadAllBytesAsync(target), "test")).Id;
        }
        var racedBytes = JsonSemantics.Serialize(Contact(id, "Конкурентная правка"));
        var store = new FileWorkspaceStore(path =>
        {
            var competing = path + ".competing";
            File.WriteAllBytes(competing, racedBytes);
            File.Replace(competing, path, path + ".previous", ignoreMetadataErrors: false);
        });
        await using (var session = await store.OpenAsync(Root, Account))
        {
            var local = (await session.ReadLocalAsync(EntityKind.Contact, id))!;
            var error = await Assert.ThrowsAsync<SyncException>(() => session.WriteAsync(EntityKind.Contact, id, Contact(id, "Планируемая правка"), local.Hash));
            Assert.Equal("localDrift", error.Code);
            var displacedFolder = Path.Combine(Root, ".contactmirror", "recovery", "displaced");
            var marker = Assert.Single(Directory.GetFiles(displacedFolder, "*.recovery.json"));
            var metadata = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(marker), "test");
            Assert.Equal(JsonSemantics.Hash(racedBytes), metadata["actualDisplacedHash"]!.GetValue<string>());
            Assert.Equal(racedBytes, await File.ReadAllBytesAsync(Path.Combine(displacedFolder, metadata["displacedFile"]!.GetValue<string>())));
        }
        await using var reopened = await plain.OpenAsync(Root, Account);
        Assert.True(reopened.View.IsRecovery);
        Assert.Contains(reopened.View.Issues, issue => issue.Id == id && issue.Path.EndsWith(".recovery.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureImmediatelyAfterReplaceLeavesDurableIntentAndBlocksHiddenDisplacedEdit(bool competingRename)
    {
        var plain = new FileWorkspaceStore();
        Guid id;
        string target;
        await using (var setup = await plain.OpenAsync(Root, Account))
        {
            target = await plain.CreateContactFileAsync(Root);
            id = DocumentCodec.Contact(JsonSemantics.ParseObject(await File.ReadAllBytesAsync(target), "test")).Id;
        }
        var original = await File.ReadAllBytesAsync(target);
        var competing = JsonSemantics.Serialize(Contact(id, "Конкурентная версия B"));
        var committed = JsonSemantics.Serialize(Contact(id, "Собственная версия A"));
        var store = new FileWorkspaceStore(path =>
        {
            if (!competingRename) return;
            File.WriteAllBytes(path + ".competing", competing);
            File.Replace(path + ".competing", path, path + ".previous", ignoreMetadataErrors: false);
        }, _ => throw new IOException("Synthetic interruption immediately after atomic replacement"));
        await using (var session = await store.OpenAsync(Root, Account))
        {
            var local = (await session.ReadLocalAsync(EntityKind.Contact, id))!;
            Assert.Equal("fileBusy", (await Assert.ThrowsAsync<SyncException>(() => session.WriteAsync(EntityKind.Contact, id, Contact(id, "Собственная версия A"), local.Hash))).Code);
            Assert.Equal(committed, await File.ReadAllBytesAsync(target));
            // In-process retries must also observe the incomplete intent, not only next startup.
            Assert.Equal("invalidFile", (await Assert.ThrowsAsync<SyncException>(() => session.ReadLocalAsync(EntityKind.Contact, id))).Code);
        }
        var displacedFolder = Path.Combine(Root, ".contactmirror", "recovery", "displaced");
        var intentPath = Assert.Single(Directory.GetFiles(displacedFolder, "*.intent.json"));
        var intent = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(intentPath), "test");
        Assert.Equal(JsonSemantics.Hash(original), intent["expectedHash"]!.GetValue<string>());
        Assert.Equal(JsonSemantics.Hash(committed), intent["committedHash"]!.GetValue<string>());
        Assert.Equal(id.ToString(), intent["entityId"]!.GetValue<string>());
        Assert.Equal(competingRename ? competing : original, await File.ReadAllBytesAsync(Path.Combine(displacedFolder, intent["displacedFile"]!.GetValue<string>())));
        Assert.Empty(Directory.GetFiles(displacedFolder, "*.recovery.json"));
        await using var reopened = await plain.OpenAsync(Root, Account);
        Assert.True(reopened.View.IsRecovery);
        Assert.False(reopened.View.Contacts.ContainsKey(id));
        var issue = Assert.Single(reopened.View.Issues, i => i.Id == id && i.Path.EndsWith(".intent.json"));
        Assert.Contains(competingRename ? "конкурентная правка сохранена" : "завершение операции не подтверждено", issue.Message);
    }

    [Fact]
    public async Task FailureBeforeReplaceAlsoLeavesVisibleIntentUntilExplicitRecovery()
    {
        var plain = new FileWorkspaceStore();
        Guid id;
        string target;
        await using (var setup = await plain.OpenAsync(Root, Account))
        {
            target = await plain.CreateContactFileAsync(Root);
            id = DocumentCodec.Contact(JsonSemantics.ParseObject(await File.ReadAllBytesAsync(target), "test")).Id;
        }
        var original = await File.ReadAllBytesAsync(target);
        var store = new FileWorkspaceStore(_ => throw new IOException("Synthetic interruption after intent flush before replacement"));
        await using (var session = await store.OpenAsync(Root, Account))
        {
            var local = (await session.ReadLocalAsync(EntityKind.Contact, id))!;
            await Assert.ThrowsAsync<SyncException>(() => session.WriteAsync(EntityKind.Contact, id, Contact(id, "Не отправленная версия"), local.Hash));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(target));
        await using var reopened = await plain.OpenAsync(Root, Account);
        Assert.True(reopened.View.IsRecovery);
        Assert.Contains(reopened.View.Issues, i => i.Id == id && i.Path.EndsWith(".intent.json") && i.Message.Contains("Вытесненный файл ещё не создан"));
    }

    [Fact]
    public async Task MissingManifestWithExistingStateDoesNotBindNewAccountOrModifyState()
    {
        var metadata = Path.Combine(Root, ".contactmirror"); Directory.CreateDirectory(metadata);
        var db = Path.Combine(metadata, "state.db"); var bytes = Encoding.UTF8.GetBytes("synthetic-existing-state"); await File.WriteAllBytesAsync(db, bytes);
        var error = await Assert.ThrowsAsync<SyncException>(() => new FileWorkspaceStore().OpenAsync(Root, Account));
        Assert.Equal("missingManifest", error.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(db));
        Assert.False(File.Exists(Path.Combine(metadata, "workspace.json")));
        Assert.False(File.Exists(Path.Combine(metadata, "workspace.lock")));
    }

    [Theory]
    [InlineData("workspace.json")]
    [InlineData("workspace.lock")]
    [InlineData("state.db")]
    [InlineData("state.db-wal")]
    [InlineData("state.db-shm")]
    [InlineData("state.db-journal")]
    public async Task EveryControlPathRejectsReparseBeforeAccess(string controlFile)
    {
        var metadata = Path.Combine(Root, ".contactmirror"); Directory.CreateDirectory(metadata);
        Junction(Path.Combine(metadata, controlFile));
        var error = await Assert.ThrowsAsync<SyncException>(() => new FileWorkspaceStore().OpenAsync(Root, Account));
        Assert.Equal("unsafePath", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(External));
    }

    [Fact]
    public async Task MetadataDirectoryJunctionIsRejectedBeforeCreatingExternalFiles()
    {
        Junction(Path.Combine(Root, ".contactmirror"));
        var error = await Assert.ThrowsAsync<SyncException>(() => new FileWorkspaceStore().OpenAsync(Root, Account));
        Assert.Equal("unsafePath", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(External));
    }

    [Fact]
    public async Task BackupPreservesOriginalUtf8BomWhitespaceAndValidatesHashes()
    {
        var store = new FileWorkspaceStore();
        await using var session = await store.OpenAsync(Root, Account);
        var id = Guid.NewGuid(); var path = Path.Combine(Root, "contacts", "renamed.contact.json");
        var text = Contact(id).ToJsonString(new() { WriteIndented = true }).Replace("\n", "\r\n") + " \r\n";
        var raw = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        await File.WriteAllBytesAsync(path, raw);
        var local = (await session.ReadLocalAsync(EntityKind.Contact, id))!;
        Assert.Equal(raw, local.RawBytes);
        var runId = await session.BeginRunAsync([new(EntityKind.Contact, id, local.Document, null, null, null, null, local.RawBytes, local.Path)]);
        var backup = Assert.Single(await store.GetBackupAsync(Root, runId));
        Assert.Equal(raw, backup.RawBytes);
        Assert.Equal(path, backup.LocalPath);
        var backupFile = Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N"), $"Contact-{id:N}.local.json");
        await File.AppendAllTextAsync(backupFile, " ");
        Assert.Equal("invalidBackup", (await Assert.ThrowsAsync<SyncException>(() => store.GetBackupAsync(Root, runId))).Code);
    }

    [Fact]
    public async Task MissingOrReparseBackupConstituentCannotPretendTheOriginalDidNotExist()
    {
        var store = new FileWorkspaceStore();
        await using var session = await store.OpenAsync(Root, Account);
        var id = Guid.NewGuid(); var document = Contact(id); var raw = JsonSemantics.Serialize(document);
        var runId = await session.BeginRunAsync([new(EntityKind.Contact, id, document, null, null, null, null, raw)]);
        var backupFile = Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N"), $"Contact-{id:N}.local.json");
        File.Delete(backupFile);
        Assert.Equal("invalidBackup", (await Assert.ThrowsAsync<SyncException>(() => store.GetBackupAsync(Root, runId))).Code);
        Junction(backupFile);
        Assert.Equal("unsafePath", (await Assert.ThrowsAsync<SyncException>(() => store.GetBackupAsync(Root, runId))).Code);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void TruncatedPngHeaderReturnsExplainedErrorWithoutOutOfRange(int length)
    {
        var bytes = new byte[length]; new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        Assert.Equal("invalidPhoto", Assert.Throws<SyncException>(() => FileWorkspaceStore.ValidateImage(bytes)).Code);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public void GenuinePngAndJpegDecodeButTruncatedPixelsAreRejected(SKEncodedImageFormat format)
    {
        var bytes = Image(format);
        FileWorkspaceStore.ValidateImage(bytes);
        Assert.Equal("invalidPhoto", Assert.Throws<SyncException>(() => FileWorkspaceStore.ValidateImage(bytes[..(bytes.Length / 2)])).Code);
    }

    [Fact]
    public void PngIncompleteEndAndResourceBombAreRejectedBeforeAllocation()
    {
        var bytes = Image();
        Assert.Throws<SyncException>(() => FileWorkspaceStore.ValidateImage(bytes[..^12]));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 8000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 8000);
        Assert.Throws<SyncException>(() => FileWorkspaceStore.ValidateImage(bytes));
    }

    [Fact]
    public async Task PhotoWriteReturnsExactCommittedPhotoAndNeverOverwritesExistingReferencedImage()
    {
        var store = new FileWorkspaceStore();
        await using var session = await store.OpenAsync(Root, Account);
        var id = Guid.NewGuid(); var photo = Image();
        var committed = await session.WriteAsync(EntityKind.Contact, id, Contact(id), null, photo, writePhoto: true);
        Assert.Equal(photo, committed.PhotoBytes);
        Assert.Equal(JsonSemantics.Hash(photo), committed.PhotoHash);
        var path = FileWorkspaceStore.SafePhotoPath(Root, committed.Document["photo"]!.GetValue<string>());
        Assert.Equal(photo, await File.ReadAllBytesAsync(path));
        var removed = await session.WriteAsync(EntityKind.Contact, id, committed.Document, committed.Hash, null, committed.PhotoHash, writePhoto: true);
        Assert.Null(removed.PhotoBytes);
        Assert.Null(removed.Document["photo"]);
        Assert.Equal(photo, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ReadOnlyCacheKeepsRawGifOrWebpBytesAndUsesUrlAndContentIdentity()
    {
        await using var session = await new FileWorkspaceStore().OpenAsync(Root, Account);
        var gif = Encoding.ASCII.GetBytes("GIF89a-synthetic-readonly-descriptor-image");
        var firstUrl = "https://lh3.googleusercontent.com/profile/photo1";
        var first = await session.CacheImageAsync(gif, firstUrl);
        Assert.Equal($".contactmirror/cache/{JsonSemantics.Hash(firstUrl)}-{JsonSemantics.Hash(gif)}.image", first);
        Assert.Equal(gif, await File.ReadAllBytesAsync(Path.Combine(Root, first)));
        Assert.Equal(first, await session.CacheImageAsync(gif, firstUrl));
        Assert.NotEqual(first, await session.CacheImageAsync(gif, "https://lh3.googleusercontent.com/profile/photo2"));
        var webp = Encoding.ASCII.GetBytes("RIFF-synthetic-WEBP-readonly-image");
        Assert.NotEqual(first, await session.CacheImageAsync(webp, firstUrl));
    }

    [Fact]
    public async Task ReadOnlyCacheRejectsTamperedExistingFileWithoutOverwritingIt()
    {
        await using var session = await new FileWorkspaceStore().OpenAsync(Root, Account);
        var original = Encoding.UTF8.GetBytes("synthetic readonly raw image");
        var relative = await session.CacheImageAsync(original, "https://lh3.googleusercontent.com/photo");
        var changed = Encoding.UTF8.GetBytes("changed cache contents");
        await File.WriteAllBytesAsync(Path.Combine(Root, relative), changed);
        Assert.Equal("cacheCollision", (await Assert.ThrowsAsync<SyncException>(() => session.CacheImageAsync(original, "https://lh3.googleusercontent.com/photo"))).Code);
        Assert.Equal(changed, await File.ReadAllBytesAsync(Path.Combine(Root, relative)));
    }

    [Fact]
    public async Task ReadOnlyCacheRejectsForeignUrlOversizeAndReparseWithoutExternalWrite()
    {
        await using var session = await new FileWorkspaceStore().OpenAsync(Root, Account);
        var raw = Encoding.UTF8.GetBytes("synthetic bytes");
        Assert.Equal("photo-host", (await Assert.ThrowsAsync<SyncException>(() => session.CacheImageAsync(raw, "https://evil.test/photo"))).Code);
        Assert.Equal("cacheImageSize", (await Assert.ThrowsAsync<SyncException>(() => session.CacheImageAsync(new byte[20 * 1024 * 1024 + 1], "https://lh3.googleusercontent.com/photo"))).Code);
        var url = "https://lh3.googleusercontent.com/photo";
        var filename = $"{JsonSemantics.Hash(url)}-{JsonSemantics.Hash(raw)}.image";
        Junction(Path.Combine(Root, ".contactmirror", "cache", filename));
        Assert.Equal("unsafePath", (await Assert.ThrowsAsync<SyncException>(() => session.CacheImageAsync(raw, url))).Code);
        Assert.Empty(Directory.GetFileSystemEntries(External));
    }

    [Fact]
    public async Task DeleteCompletedBackupPreservesHistoryCountsAndReportsNoAvailableBytes()
    {
        var store = new FileWorkspaceStore();
        Guid runId;
        await using (var session = await store.OpenAsync(Root, Account))
        {
            var id = Guid.NewGuid(); var document = Contact(id); var raw = JsonSemantics.Serialize(document);
            runId = await session.BeginRunAsync([new(EntityKind.Contact, id, document, null, null, null, null, raw)]);
            await session.CompleteRunAsync(runId, new SyncRunResult(runId, 3, 0, 0, []));
            var before = Assert.Single(await store.GetHistoryAsync(Root));
            Assert.True(before.BackupAvailable);
            var backupFolder = Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N"));
            var expectedBytes = Directory.GetFiles(backupFolder).Sum(path => new FileInfo(path).Length);
            Assert.Equal(expectedBytes, before.BackupBytes);
            Assert.True(before.BackupBytes > raw.Length);
            await session.DeleteBackupAsync(runId);
            Assert.False(Directory.Exists(backupFolder));
            await session.DeleteBackupAsync(runId); // Repeating a completed cleanup is harmless.
        }
        var after = Assert.Single(await store.GetHistoryAsync(Root));
        Assert.Equal(runId, after.Id);
        Assert.Equal("complete", after.Status);
        Assert.Equal(3, after.Confirmed);
        Assert.False(after.BackupAvailable);
        Assert.Equal(0, after.BackupBytes);
        Assert.Equal("missingBackup", (await Assert.ThrowsAsync<SyncException>(() => store.GetBackupAsync(Root, runId))).Code);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("partial")]
    [InlineData("started")]
    [InlineData("unknown")]
    [InlineData("confirmed")]
    public async Task ActivePartialOrUnresolvedBackupCannotBeDeleted(string reason)
    {
        await using var session = await new FileWorkspaceStore().OpenAsync(Root, Account);
        var id = Guid.NewGuid(); var document = Contact(id); var raw = JsonSemantics.Serialize(document);
        var runId = await session.BeginRunAsync([new(EntityKind.Contact, id, document, null, null, null, null, raw)]);
        if (reason == "partial") await session.CompleteRunAsync(runId, new SyncRunResult(runId, 0, 1, 0, []));
        else if (reason != "running")
        {
            await session.RecordAsync(runId, new JournalOperation("phone", id, EntityKind.Contact, "phoneNumbers", reason));
            await session.CompleteRunAsync(runId, new SyncRunResult(runId, 1, 0, 0, []));
        }
        Assert.Equal("backupInUse", (await Assert.ThrowsAsync<SyncException>(() => session.DeleteBackupAsync(runId))).Code);
        Assert.True(File.Exists(Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N"), $"Contact-{id:N}.local.json")));
    }

    [Fact]
    public async Task RecoveredBackupWithResolvedOperationsMayBeDeleted()
    {
        await using var session = await new FileWorkspaceStore().OpenAsync(Root, Account);
        var id = Guid.NewGuid(); var runId = await session.BeginRunAsync([]);
        await session.RecordAsync(runId, new JournalOperation("phone", id, EntityKind.Contact, "phoneNumbers", "unknown"));
        await session.ResolvePendingAsync(id, "phoneNumbers");
        await session.AcknowledgeRecoveryAsync();
        await session.DeleteBackupAsync(runId);
        Assert.False(Directory.Exists(Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N"))));
    }

    [Fact]
    public async Task BackupCleanupPrevalidatesAllChildrenBeforeDeletingAnything()
    {
        await using var session = await new FileWorkspaceStore().OpenAsync(Root, Account);
        var id = Guid.NewGuid(); var document = Contact(id); var raw = JsonSemantics.Serialize(document);
        var runId = await session.BeginRunAsync([new(EntityKind.Contact, id, document, null, null, null, null, raw)]);
        await session.CompleteRunAsync(runId, new SyncRunResult(runId, 1, 0, 0, []));
        var folder = Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N"));
        Junction(Path.Combine(folder, "external-child"));
        var outside = Path.Combine(External, "preserve.txt"); await File.WriteAllTextAsync(outside, "keep");
        Assert.Equal("unsafePath", (await Assert.ThrowsAsync<SyncException>(() => session.DeleteBackupAsync(runId))).Code);
        Assert.Equal(raw, await File.ReadAllBytesAsync(Path.Combine(folder, $"Contact-{id:N}.local.json")));
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task ConfirmedOperationPreventsRecoveryAcknowledgementAndStaysVisibleAfterRestart()
    {
        var store = new FileWorkspaceStore();
        Guid runId;
        await using (var session = await store.OpenAsync(Root, Account))
        {
            runId = await session.BeginRunAsync([]);
            await session.RecordAsync(runId, new JournalOperation("phone", Guid.NewGuid(), EntityKind.Contact, "phoneNumbers", "confirmed"));
            await session.CompleteRunAsync(runId, new SyncRunResult(runId, 1, 1, 0, []));
            await session.AcknowledgeRecoveryAsync();
            Assert.Equal("partial", Assert.Single(await store.GetHistoryAsync(Root)).Status);
        }
        await using var reopened = await store.OpenAsync(Root, Account);
        Assert.Equal("confirmed", Assert.Single(reopened.View.Pending).Status);
        Assert.Equal("backupInUse", (await Assert.ThrowsAsync<SyncException>(() => reopened.DeleteBackupAsync(runId))).Code);
    }

    [Fact]
    public async Task FieldScopedResolutionLeavesOtherUnresolvedOperationVisibleEvenForCompleteRun()
    {
        var store = new FileWorkspaceStore();
        var id = Guid.NewGuid();
        Guid runId;
        await using (var session = await store.OpenAsync(Root, Account))
        {
            runId = await session.BeginRunAsync([]);
            await session.RecordAsync(runId, new JournalOperation("name", id, EntityKind.Contact, "names", "confirmed"));
            await session.RecordAsync(runId, new JournalOperation("phone", id, EntityKind.Contact, "phoneNumbers", "unknown"));
            await session.CompleteRunAsync(runId, new SyncRunResult(runId, 2, 0, 0, []));
            await session.ResolvePendingAsync(id, "names");
        }
        await using var reopened = await store.OpenAsync(Root, Account);
        var unresolved = Assert.Single(reopened.View.Pending);
        Assert.Equal("phoneNumbers", unresolved.Field);
        Assert.Equal("unknown", unresolved.Status);
        Assert.Equal("backupInUse", (await Assert.ThrowsAsync<SyncException>(() => reopened.DeleteBackupAsync(runId))).Code);
        await reopened.ResolvePendingAsync(id, "phoneNumbers");
        await reopened.DeleteBackupAsync(runId);
    }
}
