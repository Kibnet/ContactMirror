namespace ContactMirror.Application;

/// <summary>One atomic lease covers account, workspace and restart operations in the composition.</summary>
public sealed class ApplicationActivity
{
    private readonly object gate = new();
    private bool active;
    private bool applying;
    public event Action? Changed;
    public bool IsActive { get { lock (gate) return active; } }
    public bool IsApplying { get { lock (gate) return applying; } }
    public IDisposable? TryEnter(bool restart = false)
    {
        lock (gate)
        {
            if (active || applying) return null;
            active = true;
            applying = restart;
        }
        Changed?.Invoke();
        return new Lease(this);
    }
    public void KeepApplying()
    {
        lock (gate) { active = true; applying = true; }
        Changed?.Invoke();
    }
    private void Exit()
    {
        lock (gate) { active = false; applying = false; }
        Changed?.Invoke();
    }
    private sealed class Lease(ApplicationActivity owner) : IDisposable
    {
        private ApplicationActivity? value = owner;
        public void Dispose() => Interlocked.Exchange(ref value, null)?.Exit();
    }
}
