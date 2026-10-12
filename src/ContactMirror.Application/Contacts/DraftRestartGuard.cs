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
    public Func<bool>? IsTransitionBlocked { get; set; }
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
    /// <summary>Checks a captured editor version and ownership under the restart authorization gate.</summary>
    public bool TryMutateChecked(long expectedGeneration, Func<bool> condition, Func<bool> change)
    {
        if (!gate.Wait(0)) return false;
        try
        {
            if (Generation != expectedGeneration || activity.IsActive || activity.IsApplying || !condition() || activity.IsActive || activity.IsApplying) return false;
            var changed = change();
            Volatile.Write(ref dirty, IsDirty || changed); Interlocked.Increment(ref generation);
        }
        finally { gate.Release(); }
        Changed?.Invoke(); return true;
    }
    public bool TryMutateChecked(long expectedGeneration, Func<bool> condition, Action change, bool markDirty = true) =>
        TryMutateChecked(expectedGeneration, condition, () => { change(); return markDirty; });
    public async Task<long?> PrepareAsync()
    {
        var prepare = PrepareTransitionAsync;
        if (prepare is null || !await prepare()) return null;
        return IsDirty || IsTransitionBlocked?.Invoke() == true ? null : Generation;
    }
    public IDisposable? TryAuthorize(long expectedGeneration)
    {
        if (!gate.Wait(0)) return null;
        try
        {
            if (IsDirty || Generation != expectedGeneration || IsTransitionBlocked?.Invoke() == true) { gate.Release(); return null; }
            return new Authorization(gate);
        }
        catch { gate.Release(); throw; }
    }
    private sealed class Authorization(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
    }
}
