using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application;

public sealed class EntityState
{
    public Guid Id { get; set; }
    public EntityKind Kind { get; set; }
    public required string ResourceName { get; set; }
    public string? SourceId { get; set; }
    public JsonObject Baseline { get; set; } = new();
    public string? PhotoLocalHash { get; set; }
    public string? PhotoRemoteHash { get; set; }
}
public sealed record LocalEntity(Guid Id, EntityKind Kind, string Path, JsonObject Document, string Hash, byte[]? PhotoBytes, string? PhotoHash, byte[]? RawBytes = null);
public sealed record WorkspaceIssue(string Path, string Message, Guid? Id = null);
public sealed record JournalOperation(string Key, Guid EntityId, EntityKind Kind, string Field, string Status, JsonObject? Payload = null, string? ResourceName = null, string? Message = null, bool CreateIntent = false, byte[]? IntentPhoto = null, string? IntentInputPhotoHash = null, bool HasIntentInput = false);
public sealed record BackupItem(EntityKind Kind, Guid Id, JsonObject? LocalDocument, byte[]? LocalPhoto, JsonObject? Remote, byte[]? RemotePhoto, EntityState? State, byte[]? RawBytes = null, string? LocalPath = null);

public sealed class WorkspaceView
{
    public required IReadOnlyDictionary<Guid, LocalEntity> Contacts { get; init; }
    public required IReadOnlyDictionary<Guid, LocalEntity> Groups { get; init; }
    public required IReadOnlyDictionary<Guid, EntityState> States { get; init; }
    public required IReadOnlyList<WorkspaceIssue> Issues { get; init; }
    public required IReadOnlyList<JournalOperation> Pending { get; init; }
    public bool IsRecovery { get; init; }
}

public interface IWorkspaceStore
{
    Task<IWorkspaceSession> OpenAsync(string root, AccountIdentity account, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string root, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupItem>> GetBackupAsync(string root, Guid runId, CancellationToken cancellationToken = default);
    Task<string> CreateContactFileAsync(string root, CancellationToken cancellationToken = default);
}
public interface IWorkspaceSession : IAsyncDisposable
{
    string Root { get; }
    WorkspaceView View { get; }
    Task<LocalEntity?> ReadLocalAsync(EntityKind kind, Guid id, CancellationToken cancellationToken = default);
    Task<Guid> BeginRunAsync(IReadOnlyList<BackupItem> items, CancellationToken cancellationToken = default);
    Task RecordAsync(Guid runId, JournalOperation operation, CancellationToken cancellationToken = default);
    Task SaveStateAsync(EntityState state, CancellationToken cancellationToken = default);
    Task RemoveStateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<LocalEntity> WriteAsync(EntityKind kind, Guid id, JsonObject document, string? expectedHash, byte[]? photoBytes = null, string? expectedPhotoHash = null, bool writePhoto = false, CancellationToken cancellationToken = default);
    Task TrashAsync(EntityKind kind, Guid id, string expectedHash, Guid runId, CancellationToken cancellationToken = default);
    Task CompleteRunAsync(Guid runId, SyncRunResult result, CancellationToken cancellationToken = default);
    Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken = default);
    Task ResolvePendingAsync(Guid entityId, string? field = null, CancellationToken cancellationToken = default);
    Task<string> CacheImageAsync(byte[] bytes, string sourceUrl, CancellationToken cancellationToken = default);
    Task DeleteBackupAsync(Guid runId, CancellationToken cancellationToken = default);
}
