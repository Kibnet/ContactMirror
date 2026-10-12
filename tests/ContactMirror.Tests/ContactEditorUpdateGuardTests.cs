using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Application.Updates;
using ContactMirror.Core;
using Xunit;

namespace ContactMirror.Tests;

public sealed class ContactEditorUpdateGuardTests
{
    private sealed class Backend : IApplicationUpdateBackend
    {
        public bool IsSupported => true; public bool IsConfigured => true; public string CurrentVersion => "0.3.0";
        public bool HasAmbiguousApply { get; set; }
        public int Applies; public bool Hold, Fail;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public Task<string?> CheckAsync(CancellationToken token) => Task.FromResult<string?>("0.4.0");
        public Task DownloadAsync(IProgress<int> progress, CancellationToken token) { progress.Report(100); return Task.CompletedTask; }
        public void ApplyAndRestart() { Interlocked.Increment(ref Applies); Started.TrySetResult(); if (Hold) Release.Wait(); if (Fail) throw new IOException("synthetic pre-launch failure"); }
    }
    private static async Task<ApplicationUpdateService> ReadyAsync(Backend backend, ApplicationActivity activity, IUpdateRestartGuard guard)
    {
        var service = new ApplicationUpdateService(backend, activity, guard); await service.CheckAsync(); await service.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State); return service;
    }
    [Fact]
    public async Task DirectServiceRestartCannotBypassDirtyCardOrCancelledTransition()
    {
        foreach (var cancelled in new[] { false, true })
        {
            var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var backend = new Backend();
            guard.TryMutate(() => { }); guard.PrepareTransitionAsync = () => Task.FromResult(!cancelled);
            var service = await ReadyAsync(backend, activity, guard); await service.ApplyAndRestartAsync();
            Assert.Equal(0, backend.Applies); Assert.True(guard.IsDirty); Assert.False(activity.IsActive); Assert.False(activity.IsApplying);
            Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
        }
    }
    private sealed class AfterPrepareGuard(DraftRestartGuard inner, Action afterPrepare) : IUpdateRestartGuard
    {
        public async Task<long?> PrepareAsync() { var stamp = await inner.PrepareAsync(); afterPrepare(); return stamp; }
        public IDisposable? TryAuthorize(long generation) => inner.TryAuthorize(generation);
    }
    [Fact]
    public async Task MutationAfterReadinessBeforeLeaseRejectsStaleGenerationAndPreservesUpdate()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var backend = new Backend();
        guard.PrepareTransitionAsync = () => Task.FromResult(true);
        var value = "before"; var between = new AfterPrepareGuard(guard, () => Assert.True(guard.TryMutate(() => value = "new draft")));
        var service = await ReadyAsync(backend, activity, between); await service.ApplyAndRestartAsync();
        Assert.Equal("new draft", value); Assert.True(guard.IsDirty); Assert.Equal(0, backend.Applies);
        Assert.False(activity.IsActive); Assert.False(activity.IsApplying); Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
    }
    [Fact]
    public async Task ApplyingLeaseAndAuthorizationPreventAnyDraftMutationWhileBackendIsHeld()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var backend = new Backend { Hold = true };
        guard.PrepareTransitionAsync = () => Task.FromResult(true); var service = await ReadyAsync(backend, activity, guard);
        var value = "safe"; var generation = guard.Generation;
        var applying = service.ApplyAndRestartAsync();
        try
        {
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(applying.IsCompleted); Assert.True(activity.IsApplying);
            Assert.False(guard.TryMutate(() => value = "changed")); Assert.False(guard.Reset(true, () => value = "discarded"));
            Assert.Equal("safe", value); Assert.Equal(generation, guard.Generation); Assert.False(guard.IsDirty);
            await service.ApplyAndRestartAsync(); Assert.Equal(1, backend.Applies); Assert.Null(activity.TryEnter());
        }
        finally { backend.Release.Set(); }
        await applying; Assert.True(activity.IsApplying); Assert.False(guard.TryMutate(() => { }));
    }
    [Fact]
    public async Task DraftGuardReplacementBetweenReadinessAndAuthorizationNeverCallsBackend()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var replacement = new DraftRestartGuard(activity); var backend = new Backend();
        guard.PrepareTransitionAsync = () => Task.FromResult(true); var service = await ReadyAsync(backend, activity, guard);
        activity.Changed += () => { if (activity.IsApplying) service.SetRestartGuard(replacement); };
        await service.ApplyAndRestartAsync(); Assert.Equal(0, backend.Applies); Assert.False(activity.IsActive); Assert.False(activity.IsApplying);
        Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendFailureReleasesDraftGateButOnlyKnownFailureReleasesActivity(bool ambiguous)
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var backend = new Backend { Fail = true, HasAmbiguousApply = ambiguous };
        guard.PrepareTransitionAsync = () => Task.FromResult(true); var service = await ReadyAsync(backend, activity, guard);
        await service.ApplyAndRestartAsync(); Assert.Equal(1, backend.Applies);
        Assert.Equal(ambiguous, activity.IsApplying); Assert.Equal(!ambiguous, guard.TryMutate(() => { }));
    }
    private sealed class Store : IContactEditorStore
    {
        public int Saves; public bool Fail, Cancel;
        public Task<ContactCatalog> LoadAsync(string root, AccountIdentity? account = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ContactSnapshot> ReadAsync(string root, Guid id, AccountIdentity? account = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteAsync(ContactSnapshot original, AccountIdentity? account = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo, AccountIdentity? account = null, CancellationToken token = default)
        {
            Saves++; if (Fail) throw new SyncException("localDrift", "synthetic drift"); if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(original with { Document = (JsonObject)document.DeepClone(), Hash = "saved" });
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledSaveDuringPreparationKeepsDraftAndNeverInvokesRestart(bool cancel)
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var backend = new Backend(); var store = new Store { Fail = !cancel, Cancel = cancel };
        var editor = new ContactEditorService(store, activity); var id = Guid.NewGuid(); var doc = new JsonObject { ["schemaVersion"] = 1, ["id"] = id.ToString(), ["data"] = new JsonObject() };
        var snapshot = new ContactSnapshot("synthetic", Guid.NewGuid(), id, "synthetic.contact.json", doc, null, null, null, null);
        guard.TryMutate(() => { });
        guard.PrepareTransitionAsync = async () =>
        {
            try { await editor.SaveAsync(snapshot, doc, new()); return guard.Reset(); }
            catch (Exception error) when (error is SyncException or OperationCanceledException) { return false; }
        };
        var service = await ReadyAsync(backend, activity, guard); await service.ApplyAndRestartAsync();
        Assert.Equal(1, store.Saves); Assert.Equal(0, backend.Applies); Assert.True(guard.IsDirty); Assert.False(activity.IsActive);
        Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
    }
    [Fact]
    public async Task SuccessfulLocalSaveCanAuthorizeOneRestartWithoutNestedActivityLease()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var backend = new Backend(); var store = new Store();
        var editor = new ContactEditorService(store, activity); var id = Guid.NewGuid(); var doc = new JsonObject { ["schemaVersion"] = 1, ["id"] = id.ToString(), ["data"] = new JsonObject() };
        var snapshot = new ContactSnapshot("synthetic", Guid.NewGuid(), id, "synthetic.contact.json", doc, null, null, null, null);
        guard.TryMutate(() => { }); guard.PrepareTransitionAsync = async () => { await editor.SaveAsync(snapshot, doc, new()); return guard.Reset(); };
        var service = await ReadyAsync(backend, activity, guard); await service.ApplyAndRestartAsync();
        Assert.Equal(1, store.Saves); Assert.Equal(1, backend.Applies); Assert.False(guard.IsDirty); Assert.True(activity.IsApplying);
    }
    [Fact]
    public async Task DirectServiceWithoutRegisteredDraftGuardFailsClosedAndRetainsDownloadedUpdate()
    {
        var activity = new ApplicationActivity(); var backend = new Backend(); var service = new ApplicationUpdateService(backend, activity);
        await service.CheckAsync(); await service.DownloadAsync(); Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
        await service.ApplyAndRestartAsync();
        Assert.Equal(0, backend.Applies); Assert.False(activity.IsActive); Assert.False(activity.IsApplying);
        Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State); Assert.NotNull(service.Snapshot.Error);
        using var lease = activity.TryEnter(); Assert.NotNull(lease);
    }

}
