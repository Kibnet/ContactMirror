using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using Xunit;

namespace ContactMirror.Tests;

public sealed class ContactEditorPopupGuardTests
{
    [Fact]
    public void CheckedResultValidatesOwnershipInsideTheAuthorizationGate()
    {
        var guard = new DraftRestartGuard(new()); var value = "original";
        var stamp = guard.Generation;
        Assert.True(guard.TryMutateChecked(stamp, () => guard.TryAuthorize(stamp) is null,
            () => { value = "chosen"; return true; }));
        Assert.Equal("chosen",value); Assert.True(guard.IsDirty); Assert.True(guard.Generation > stamp);
    }
    [Fact]
    public void StaleGenerationAndRejectedIdentityNeverInvokePayload()
    {
        var guard = new DraftRestartGuard(new()); var stamp = guard.Generation; var calls = 0;
        Assert.True(guard.TryMutate(() => { }));
        Assert.False(guard.TryMutateChecked(stamp, () => true, () => { calls++; return true; }));
        Assert.False(guard.TryMutateChecked(guard.Generation, () => false, () => { calls++; return true; }));
        Assert.Equal(0,calls);
    }
    [Fact]
    public void CheckedCancelAndNoOpPreserveDirtyStateAndAdvanceOwnershipVersion()
    {
        var guard = new DraftRestartGuard(new()); var stamp = guard.Generation;
        Assert.True(guard.TryMutateChecked(stamp, () => true, () => false)); Assert.False(guard.IsDirty);
        Assert.Null(guard.TryAuthorize(stamp));
        Assert.True(guard.TryMutate(() => { }));
        Assert.True(guard.TryMutateChecked(guard.Generation, () => true, () => false)); Assert.True(guard.IsDirty);
    }
    [Fact]
    public async Task OpenPopupBlocksBothPreparationAndDirectRestartAuthorization()
    {
        var guard = new DraftRestartGuard(new()); var popup = true;
        guard.IsTransitionBlocked = () => popup;
        guard.PrepareTransitionAsync = () => Task.FromResult(!popup);
        Assert.Null(await guard.PrepareAsync()); Assert.Null(guard.TryAuthorize(guard.Generation));
        popup = false;
        using var lease = guard.TryAuthorize(guard.Generation); Assert.NotNull(lease);
    }
    [Fact]
    public void ActivityAndAuthorizedRestartFenceCheckedResults()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var calls = 0;
        using (var authorization = guard.TryAuthorize(guard.Generation))
        {
            Assert.NotNull(authorization);
            Assert.False(guard.TryMutateChecked(guard.Generation, () => true, () => { calls++; return true; }));
        }
        using (activity.TryEnter()) Assert.False(guard.TryMutateChecked(guard.Generation, () => true, () => { calls++; return true; }));
        Assert.Equal(0,calls); Assert.False(guard.IsDirty);
    }
    [Fact]
    public void ThrowingOwnershipCheckReleasesGateWithoutWriting()
    {
        var guard = new DraftRestartGuard(new()); var stamp = guard.Generation;
        Assert.Throws<InvalidOperationException>(() => guard.TryMutateChecked(stamp, () => throw new InvalidOperationException("retired owner"), () => true));
        Assert.Equal(stamp,guard.Generation); Assert.False(guard.IsDirty);
        using var lease = guard.TryAuthorize(stamp); Assert.NotNull(lease);
    }
    [Fact]
    public void ActivityStartingDuringOwnershipValidationRejectsThePayload()
    {
        var activity = new ApplicationActivity(); var guard = new DraftRestartGuard(activity); var called = false; IDisposable? activityLease = null;
        try
        {
            Assert.False(guard.TryMutateChecked(guard.Generation,
                () => { activityLease = activity.TryEnter(); return true; },
                () => { called = true; return true; }));
            Assert.NotNull(activityLease); Assert.False(called); Assert.False(guard.IsDirty); Assert.Equal(0,guard.Generation);
        }
        finally { activityLease?.Dispose(); }
    }
}
