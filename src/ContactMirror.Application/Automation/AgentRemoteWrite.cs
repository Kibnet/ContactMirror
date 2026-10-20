namespace ContactMirror.Application.Automation;

public static class AgentRemoteWrite
{
    private static readonly AsyncLocal<Action?> check = new();
    public static void Verify() => check.Value?.Invoke();
    public static IDisposable Enter(Action verify) { var before = check.Value; check.Value = verify; return new Scope(() => check.Value = before); }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
