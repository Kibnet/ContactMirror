using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Nodes;
using ContactMirror.Core;
using ContactMirror.Infrastructure;
using Xunit;

namespace ContactMirror.GoogleTests;

public sealed class FileWorkspaceIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ContactMirror-Index-" + Guid.NewGuid().ToString("N"));
    private static readonly AccountIdentity Account = new("index-test", "index@example.invalid");
    public FileWorkspaceIndexTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static byte[] Document(Guid id) => JsonSemantics.Serialize(DocumentCodec.ToJson(new ContactDocument { Id = id, Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Fixture" }) } }));
    private async Task<string> Seed(Guid id, string? filename = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "contacts"));
        var path = Path.Combine(root, "contacts", filename ?? $"{id}.contact.json");
        await File.WriteAllBytesAsync(path, Document(id));
        return path;
    }
    [Fact]
    public async Task CachedIdentityAllowsExclusiveEditorAndDetectsOpenWriterDuplicate()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await Seed(first); var secondPath = await Seed(second, "other.contact.json");
        await using var session = await new FileWorkspaceStore().OpenAsync(root, Account);
        using (var editor = new FileStream(secondPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { Assert.True(editor.CanWrite); }
        using (var writer = new FileStream(secondPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write(Document(first)); writer.Flush(flushToDisk: false); // Reach the OS cache, without disk flush or close.
            var error = await Assert.ThrowsAsync<SyncException>(() => session.ReadLocalAsync(EntityKind.Contact, first));
            Assert.Equal("invalidFile", error.Code);
        }
        Assert.Equal("invalidFile", (await Assert.ThrowsAsync<SyncException>(() => session.ReadLocalAsync(EntityKind.Contact, first))).Code);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WritableMappingBeforeOrAfterIndexNeverHidesDuplicate(bool before)
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await Seed(first); var secondPath = await Seed(second, "other.contact.json");
        using var mapping = before ? MemoryMappedFile.CreateFromFile(secondPath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite) : null;
        var reads = new List<string>();
        await using var session = await new FileWorkspaceStore(null, onDocumentRead: path => reads.Add(Path.GetFileName(path))).OpenAsync(root, Account);
        reads.Clear();
        using var later = before ? null : MemoryMappedFile.CreateFromFile(secondPath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        using var view = (mapping ?? later)!.CreateViewAccessor();
        view.WriteArray(0, Document(first), 0, Document(first).Length);
        using (var reader = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        { using var memory = new MemoryStream(); await reader.CopyToAsync(memory); Assert.Equal(Document(first), memory.ToArray()); }
        var error = await Record.ExceptionAsync(() => session.ReadLocalAsync(EntityKind.Contact, first));
        Assert.True(error is SyncException, "Duplicate was not blocked; read paths: " + string.Join(',', reads));
        Assert.Equal("invalidFile", ((SyncException)error).Code);
    }
    [Fact]
    public async Task NativeReadLeaseIsGrantedWithoutBlockingExclusiveOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); var path = await Seed(id);
        using var cached = FileReadCacheLease.TryCreate(path);
        Assert.NotNull(cached); Assert.False(cached.HasChanged());
        Assert.Equal(Document(id), await cached.ReadBytesAsync(16 * 1024 * 1024, default));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        writer.WriteByte((byte)'!');
        writer.Flush(flushToDisk: false);
        Assert.True(cached.HasChanged());
    }
    [Fact]
    public async Task RenameDirectoryReplacementAndOverflowRefreshIdentity()
    {
        var id = Guid.NewGuid(); var path = await Seed(id);
        await using var session = await new FileWorkspaceStore().OpenAsync(root, Account);
        var nested = Path.Combine(root, "contacts", "nested"); Directory.CreateDirectory(nested);
        var renamed = Path.Combine(nested, "renamed.contact.json"); File.Move(path, renamed);
        Assert.Equal(renamed, (await session.ReadLocalAsync(EntityKind.Contact, id))!.Path);
        for (var i = 0; i < 1200; i++) File.WriteAllText(Path.Combine(nested, new string('x', 90) + i + ".txt"), "ignored");
        await File.WriteAllBytesAsync(Path.Combine(nested, "copy.contact.json"), Document(id));
        Assert.Equal("invalidFile", (await Assert.ThrowsAsync<SyncException>(() => session.ReadLocalAsync(EntityKind.Contact, id))).Code);
    }
    [Fact]
    public async Task DirectoryAnchorRecognizesOrdinaryPathReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Seed(Guid.NewGuid());
        var contacts = Path.Combine(root, "contacts");
        using var journal = new DirectoryChangeJournal(contacts);
        Assert.True(journal.MatchesPath(contacts));
        Directory.Move(contacts, Path.Combine(root, "old-contacts"));
        Directory.CreateDirectory(contacts);
        Assert.False(journal.MatchesPath(contacts));
    }
    [Fact]
    public async Task NativeReadLeaseBreaksWhenWritableMappingIsCreated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = await Seed(Guid.NewGuid());
        using var cached = FileReadCacheLease.TryCreate(path);
        Assert.NotNull(cached); Assert.False(cached.HasChanged());
        _ = await cached.ReadBytesAsync(16 * 1024 * 1024, default);
        Assert.False(cached.HasChanged(), cached.BreakDiagnostic());
        using var mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        using var view = mapping.CreateViewAccessor(); view.Write(0, (byte)'!');
        Assert.True(cached.HasChanged());
        using var uncacheable = FileReadCacheLease.TryCreate(path);
        Assert.Null(uncacheable);
    }
    [Fact]
    public async Task NotificationsSurviveTheCallingThreadExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid(); var path = await Seed(id);
        FileReadCacheLease? cached = null; DirectoryChangeJournal? journal = null; Exception? failure = null;
        var caller = new Thread(() =>
        {
            try { cached = FileReadCacheLease.TryCreate(path); journal = new DirectoryChangeJournal(Path.GetDirectoryName(path)!); }
            catch (Exception error) { failure = error; }
        });
        caller.Start(); caller.Join();
        using (cached) using (journal)
        {
            Assert.Null(failure); Assert.NotNull(cached); Assert.NotNull(journal);
            Assert.False(cached.HasChanged()); Assert.Empty(journal.Poll()!);
            Assert.Equal(Document(id), await cached.ReadBytesAsync(16 * 1024 * 1024, default));
            Assert.False(cached.HasChanged());
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            writer.WriteByte((byte)'!'); writer.Flush(flushToDisk: false);
            Assert.True(cached.HasChanged());
        }
    }
    [Fact]
    public async Task UnchangedFolderReadsOnlyRequestedDocuments()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) await Seed(id);
        var reads = 0;
        await using var session = await new FileWorkspaceStore(null, onDocumentRead: _ => reads++).OpenAsync(root, Account);
        Assert.Equal(1000, reads);
        foreach (var id in ids) Assert.NotNull(await session.ReadLocalAsync(EntityKind.Contact, id));
        Assert.InRange(reads, 2000, 4000);
    }
}
