using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application.Contacts;

public enum ContactWriteOutcome { Unchanged, Committed, Uncertain }
public sealed class ContactEditorWriteException(ContactWriteOutcome outcome, ContactSnapshot? snapshot, string message, Exception inner)
    : Exception(message, inner)
{
    public ContactWriteOutcome Outcome { get; } = outcome;
    public ContactSnapshot? Snapshot { get; } = snapshot;
}

public sealed record ContactCatalogItem(Guid Id, string Path, JsonObject Document);
public sealed record ContactCatalog(string Root, Guid WorkspaceId, IReadOnlyList<ContactCatalogItem> Contacts,
    IReadOnlyDictionary<Guid, string> Labels, IReadOnlyList<WorkspaceIssue> Issues,
    string? ReadOnlyReason = null, IReadOnlyDictionary<Guid, string>? BlockedContacts = null,
    string? BindingHash = null, string? LabelsHash = null);
public sealed record ContactSnapshot(string Root, Guid WorkspaceId, Guid Id, string Path, JsonObject Document,
    byte[]? RawBytes, string? Hash, byte[]? PhotoBytes, string? PhotoHash, string? ReadOnlyReason = null,
    string? BindingHash = null, string? LabelsHash = null)
{
    public bool IsNew => Hash is null;
}
public sealed record ContactPhotoEdit(bool Changed = false, byte[]? Bytes = null);

public interface IContactEditorStore
{
    Task<ContactCatalog> LoadAsync(string root, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default);
    Task<ContactSnapshot> ReadAsync(string root, Guid id, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default);
    Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo,
        AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(ContactSnapshot original, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default);
}

public sealed class ContactEditorService(IContactEditorStore store, ApplicationActivity activity)
{
    public Task<ContactCatalog> LoadAsync(string root, AccountIdentity? account = null, CancellationToken token = default) =>
        store.LoadAsync(root, account, token);
    public Task<ContactSnapshot> ReadAsync(string root, Guid id, AccountIdentity? account = null, CancellationToken token = default) =>
        store.ReadAsync(root, id, account, token);
    public async Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo,
        AccountIdentity? account = null, CancellationToken token = default)
    {
        using var lease = activity.TryEnter() ?? throw new SyncException("editorBusy", "Завершите текущую операцию перед сохранением.");
        return await store.SaveAsync(original, document, photo, account, token);
    }
    public async Task DeleteAsync(ContactSnapshot original, AccountIdentity? account = null, CancellationToken token = default)
    {
        using var lease = activity.TryEnter() ?? throw new SyncException("editorBusy", "Завершите текущую операцию перед удалением.");
        await store.DeleteAsync(original, account, token);
    }
}
