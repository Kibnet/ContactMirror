using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using Xunit;

namespace ContactMirror.Tests;

public sealed class DraftRestartGuardTests
{
    [Fact]
    public void MutationAndDiscardChangeGenerationAndDirtyButAuthorizationPinsBoth()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var changes = 0; var value = "before";
        guard.Changed += () => changes++;
        var initial = guard.Generation;
        Assert.True(guard.TryMutate(() => value = "draft")); Assert.True(guard.IsDirty); Assert.True(guard.Generation > initial);
        Assert.Null(guard.TryAuthorize(guard.Generation));
        Assert.True(guard.Reset(change: () => value = "before")); var cleanGeneration = guard.Generation;
        var authorization = guard.TryAuthorize(cleanGeneration); Assert.NotNull(authorization);
        Assert.False(guard.TryMutate(() => value = "lost")); Assert.False(guard.Reset(true, () => value = "lost"));
        Assert.Null(guard.TryAuthorize(cleanGeneration)); Assert.Equal("before", value); Assert.Equal(cleanGeneration, guard.Generation); Assert.False(guard.IsDirty);
        authorization.Dispose(); authorization.Dispose();
        Assert.True(guard.TryMutate(() => value = "after")); Assert.Equal("after", value); Assert.True(guard.IsDirty); Assert.Equal(3, changes);
    }
    [Fact]
    public void OldCleanGenerationCannotAuthorizeAfterEditThenDiscard()
    {
        var guard = new DraftRestartGuard(new()); var stamp = guard.Generation;
        Assert.True(guard.TryMutate(() => { })); Assert.True(guard.Reset()); Assert.False(guard.IsDirty);
        Assert.Null(guard.TryAuthorize(stamp)); using var current = guard.TryAuthorize(guard.Generation); Assert.NotNull(current);
    }
    [Fact]
    public void WorkspaceAndRestartActivityFenceUserMutations()
    {
        foreach (var restart in new[] { false, true })
        {
            var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var invoked = false;
            using (var lease = activity.TryEnter(restart))
            {
                Assert.NotNull(lease); Assert.False(guard.TryMutate(() => invoked = true)); Assert.False(invoked); Assert.False(guard.IsDirty); Assert.Equal(0, guard.Generation);
                if (restart) Assert.False(guard.Reset(change: () => invoked = true));
            }
            Assert.True(guard.TryMutate(() => invoked = true)); Assert.True(invoked);
        }
    }
    [Fact]
    public async Task PreparationRequiresARealTransitionAndACleanDraft()
    {
        var guard = new DraftRestartGuard(new()); Assert.Null(await guard.PrepareAsync());
        guard.PrepareTransitionAsync = () => Task.FromResult(false); Assert.Null(await guard.PrepareAsync());
        guard.TryMutate(() => { }); guard.PrepareTransitionAsync = () => Task.FromResult(true); Assert.Null(await guard.PrepareAsync());
        guard.PrepareTransitionAsync = () => Task.FromResult(guard.Reset()); Assert.Equal(guard.Generation + 1, await guard.PrepareAsync()); Assert.False(guard.IsDirty);
    }
    [Fact]
    public async Task EditDuringAsyncTransitionCannotBeAuthorizedAsClean()
    {
        var guard = new DraftRestartGuard(new()); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.PrepareTransitionAsync = () => { started.TrySetResult(); return release.Task; };
        var preparing = guard.PrepareAsync(); await started.Task;
        Assert.True(guard.TryMutate(() => { })); release.SetResult(true);
        Assert.Null(await preparing); Assert.True(guard.IsDirty);
    }
}
