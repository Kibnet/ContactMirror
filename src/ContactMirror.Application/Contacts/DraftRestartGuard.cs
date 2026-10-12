using ContactMirror.Application.Updates;

namespace ContactMirror.Application.Contacts;

public sealed class DraftRestartGuard(ApplicationActivity activity) : IUpdateRestartGuard
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long generation;
    private bool dirty;
    public bool IsDirty => Volatile.Read(ref dirty);
    public long Generation => Interlocked.Read(ref generation);
    public Func<Task<bool>>? PrepareTransitionAsync { get; set; }
    public event Action? Changed;

    public bool TryMutate(Action change, bool markDirty = true)
    {
        if (!gate.Wait(0)) return false;
        try
        {
            if (activity.IsActive || activity.IsApplying) return false;
            change(); Volatile.Write(ref dirty, markDirty); Interlocked.Increment(ref generation);
        }
        finally { gate.Release(); }
        Changed?.Invoke(); return true;
    }
    public bool Reset(bool isDirty = false, Action? change = null)
    {
        if (!gate.Wait(0)) return false;
        try
        {
            if (activity.IsApplying) return false;
            change?.Invoke(); Volatile.Write(ref dirty, isDirty); Interlocked.Increment(ref generation);
        }
        finally { gate.Release(); }
        Changed?.Invoke(); return true;
    }
    public async Task<long?> PrepareAsync()
    {
        var prepare = PrepareTransitionAsync;
        if (prepare is null || !await prepare()) return null;
        return IsDirty ? null : Generation;
    }
    public IDisposable? TryAuthorize(long expectedGeneration)
    {
        if (!gate.Wait(0)) return null;
        if (IsDirty || Generation != expectedGeneration) { gate.Release(); return null; }
        return new Authorization(gate);
    }
    private sealed class Authorization(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
    }
}
