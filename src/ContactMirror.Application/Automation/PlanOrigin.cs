namespace ContactMirror.Application.Automation;

/// <summary>The UI and each bridge own independent prepared inputs, even in a shared coordinator.</summary>
public static class PlanOrigin
{
    private static readonly AsyncLocal<string?> origin = new();
    public static string Current => origin.Value ?? "gui";
    public static IDisposable Enter(string owner)
    {
        var previous = origin.Value; origin.Value = owner;
        return new Scope(() => origin.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
