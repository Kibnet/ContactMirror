using System.Collections.Concurrent;

namespace ContactMirror.Infrastructure;

/// <summary>Windows cancels outstanding notification I/O when its issuing thread exits.</summary>
internal static class KernelNotificationThread
{
    private static readonly BlockingCollection<Action> requests = new();
    static KernelNotificationThread()
    {
        var thread = new Thread(() =>
        {
            foreach (var request in requests.GetConsumingEnumerable()) request();
        }) { IsBackground = true, Name = "ContactMirror file notifications" };
        thread.Start();
    }
    public static T Invoke<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }
}
