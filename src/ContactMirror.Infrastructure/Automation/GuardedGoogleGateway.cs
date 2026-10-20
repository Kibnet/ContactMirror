using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Automation;

public sealed class GuardedGoogleGateway(IGoogleContactsGateway inner) : IGoogleContactsGateway
{
    private static void Verify(CancellationToken t) { t.ThrowIfCancellationRequested(); AgentRemoteWrite.Verify(); }
    public Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? p = null, CancellationToken t = default) => inner.ReadAllAsync(p,t);
    public Task<JsonObject?> GetPersonAsync(string r, CancellationToken t = default) => inner.GetPersonAsync(r,t);
    public Task<JsonObject?> GetGroupAsync(string r, CancellationToken t = default) => inner.GetGroupAsync(r,t);
    public Task<byte[]> DownloadPhotoAsync(string url, CancellationToken t = default) => inner.DownloadPhotoAsync(url,t);
    public Task<JsonObject> CreateContactAsync(JsonObject p, CancellationToken t = default) { Verify(t); return inner.CreateContactAsync(p,t); }
    public Task<JsonObject> UpdateContactAsync(string r, JsonObject p, IReadOnlyCollection<string> f, CancellationToken t = default) { Verify(t); return inner.UpdateContactAsync(r,p,f,t); }
    public Task DeleteContactAsync(string r, CancellationToken t = default) { Verify(t); return inner.DeleteContactAsync(r,t); }
    public Task<JsonObject> UpdatePhotoAsync(string r, byte[]? b, CancellationToken t = default) { Verify(t); return inner.UpdatePhotoAsync(r,b,t); }
    public Task<JsonObject> CreateGroupAsync(string n, JsonArray d, CancellationToken t = default) { Verify(t); return inner.CreateGroupAsync(n,d,t); }
    public Task<JsonObject> UpdateGroupAsync(string r, string n, JsonArray d, CancellationToken t = default) { Verify(t); return inner.UpdateGroupAsync(r,n,d,t); }
    public Task DeleteGroupAsync(string r, CancellationToken t = default) { Verify(t); return inner.DeleteGroupAsync(r,t); }
    public Task ModifyMembershipAsync(string g, string p, bool add, CancellationToken t = default) { Verify(t); return inner.ModifyMembershipAsync(g,p,add,t); }
}
