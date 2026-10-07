using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application;

public interface IAccountConnector
{
    bool IsConfigured { get; }
    string ConfigurationHint { get; }
    Task<AccountIdentity?> GetAccountAsync(CancellationToken cancellationToken = default);
    Task<AccountIdentity> SignInAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(bool revoke = false, CancellationToken cancellationToken = default);
}

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public interface IGoogleContactsGateway
{
    Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<JsonObject?> GetPersonAsync(string resourceName, CancellationToken cancellationToken = default);
    Task<JsonObject> CreateContactAsync(JsonObject person, CancellationToken cancellationToken = default);
    Task<JsonObject> UpdateContactAsync(string resourceName, JsonObject person, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default);
    Task DeleteContactAsync(string resourceName, CancellationToken cancellationToken = default);
    Task<JsonObject> UpdatePhotoAsync(string resourceName, byte[]? bytes, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadPhotoAsync(string url, CancellationToken cancellationToken = default);
    Task<JsonObject?> GetGroupAsync(string resourceName, CancellationToken cancellationToken = default);
    Task<JsonObject> CreateGroupAsync(string name, JsonArray clientData, CancellationToken cancellationToken = default);
    Task<JsonObject> UpdateGroupAsync(string resourceName, string name, JsonArray clientData, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(string resourceName, CancellationToken cancellationToken = default);
    Task ModifyMembershipAsync(string groupResourceName, string personResourceName, bool add, CancellationToken cancellationToken = default);
}

public interface ISyncCoordinator
{
    Task<SyncPreview> PrepareAsync(string root, AccountIdentity account, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<SyncRunResult> ApplyAsync(SyncPreview preview, IReadOnlyList<PlanChoice> choices, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string root, CancellationToken cancellationToken = default);
    Task<string> CreateContactFileAsync(string root, CancellationToken cancellationToken = default);
    Task<SyncPreview> PrepareRestoreAsync(string root, AccountIdentity account, Guid runId, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default);
    Task CleanupBackupAsync(string root, AccountIdentity account, Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Очистка резервных копий недоступна в этом тестовом провайдере.");
}
