using ContactMirror.Application;
using ContactMirror.Application.Updates;
using Xunit;

namespace ContactMirror.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task SharedCheckCoalescesAndDoesNotDownloadOrRestart()
    {
        var backend = new Backend { HoldCheck = true };
        var service = new ApplicationUpdateService(backend, new());
        var first = service.CheckAsync();
        await backend.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = service.CheckAsync();
        Assert.Same(first, second);
        backend.CheckRelease.SetResult();
        await first;
        Assert.Equal(1, backend.Checks);
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(0, backend.Applies);
        Assert.Equal(UpdateState.Available, service.Snapshot.State);
    }
    [Fact]
    public async Task ActiveAccountOrWorkspacePreventsRestartWithoutDiscardingPreparedUpdate()
    {
        var backend = new Backend(); var activity = new ApplicationActivity();
        var service = new ApplicationUpdateService(backend, activity);
        await service.CheckAsync(); await service.DownloadAsync();
        using (activity.TryEnter()) await service.ApplyAndRestartAsync();
        Assert.Equal(0, backend.Applies);
        Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
        await service.ApplyAndRestartAsync();
        Assert.Equal(1, backend.Applies);
        Assert.Equal(UpdateState.Applying, service.Snapshot.State);
        Assert.Null(activity.TryEnter());
    }
    [Fact]
    public async Task CancelledDownloadKeepsCurrentVersionAndAllowsRetry()
    {
        var backend = new Backend { HoldDownload = true }; var service = new ApplicationUpdateService(backend, new());
        await service.CheckAsync();
        var download = service.DownloadAsync(); await backend.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.Cancel(); await download;
        Assert.Equal(UpdateState.Available, service.Snapshot.State);
        Assert.Equal("0.2.0", service.Snapshot.CurrentVersion);
        Assert.Equal(0, backend.Applies);
        backend.HoldDownload = false;
        await service.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToRestart, service.Snapshot.State);
    }
    [Fact]
    public async Task AmbiguousApplyRetainsFenceWhileKnownPrelaunchFailureAllowsWorkspaceWork()
    {
        foreach (var ambiguous in new[] { false, true })
        {
            var activity = new ApplicationActivity(); var backend = new Backend { ApplyError = true, HasAmbiguousApply = ambiguous };
            var service = new ApplicationUpdateService(backend, activity);
            await service.CheckAsync(); await service.DownloadAsync(); await service.ApplyAndRestartAsync();
            Assert.Equal(ambiguous ? UpdateState.Applying : UpdateState.ReadyToRestart, service.Snapshot.State);
            using var lease = activity.TryEnter();
            Assert.Equal(!ambiguous, lease is not null);
            Assert.DoesNotContain("private-token", service.Snapshot.Error!);
        }
    }
    [Fact]
    public async Task RawOrUnconfiguredRuntimeDoesNotCallBackend()
    {
        foreach (var supported in new[] { false, true })
        {
            var backend = new Backend { IsSupported = supported, IsConfigured = false };
            var service = new ApplicationUpdateService(backend, new());
            await service.CheckAsync(); await service.DownloadAsync(); await service.ApplyAndRestartAsync();
            Assert.Equal(supported ? UpdateState.NotConfigured : UpdateState.Unsupported, service.Snapshot.State);
            Assert.Equal(0, backend.Checks + backend.Downloads + backend.Applies);
        }
    }
    [Fact]
    public async Task SlowPackageRevalidationRunsOffCallerAndRetainsExclusiveLease()
    {
        var activity = new ApplicationActivity(); var backend = new Backend { HoldApply = true };
        var service = new ApplicationUpdateService(backend, activity);
        await service.CheckAsync(); await service.DownloadAsync();
        var apply = service.ApplyAndRestartAsync();
        await backend.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(apply.IsCompleted);
        Assert.Equal(UpdateState.Applying, service.Snapshot.State);
        Assert.Null(activity.TryEnter());
        backend.ApplyRelease.Set();
        await apply;
        Assert.Null(activity.TryEnter());
    }
    [Fact]
    public async Task RestartAndWorkspaceRaceHaveExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var activity = new ApplicationActivity();
            using var start = new ManualResetEventSlim(false);
            IDisposable? work = null, restart = null;
            var first = Task.Run(() => { start.Wait(); work = activity.TryEnter(); });
            var second = Task.Run(() => { start.Wait(); restart = activity.TryEnter(true); });
            start.Set(); await Task.WhenAll(first, second);
            Assert.NotEqual(work is null, restart is null);
            work?.Dispose(); restart?.Dispose();
        }
    }
    private sealed class Backend : IApplicationUpdateBackend
    {
        public bool IsSupported { get; set; } = true;
        public bool IsConfigured { get; set; } = true;
        public string CurrentVersion => "0.2.0";
        public bool HasAmbiguousApply { get; set; }
        public bool HoldCheck { get; set; }
        public bool HoldDownload { get; set; }
        public bool ApplyError { get; set; }
        public bool HoldApply { get; set; }
        public TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ApplyRelease { get; } = new(false);
        public int Checks, Downloads, Applies;
        public TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CheckRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> CheckAsync(CancellationToken token)
        {
            Checks++; CheckStarted.TrySetResult(); if (HoldCheck) await CheckRelease.Task.WaitAsync(token); return "0.2.1";
        }
        public async Task DownloadAsync(IProgress<int> progress, CancellationToken token)
        {
            Downloads++; DownloadStarted.TrySetResult(); progress.Report(64); if (HoldDownload) await Task.Delay(Timeout.Infinite, token);
        }
        public void ApplyAndRestart() { Applies++; ApplyStarted.TrySetResult(); if (HoldApply) ApplyRelease.Wait(); if (ApplyError) throw new InvalidOperationException("private-token"); }
    }
}
