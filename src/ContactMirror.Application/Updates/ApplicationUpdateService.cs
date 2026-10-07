namespace ContactMirror.Application.Updates;

public enum UpdateState { NotConfigured, Unsupported, Idle, Checking, Available, Downloading, ReadyToRestart, Applying, Error }
public sealed record UpdateSnapshot(UpdateState State, string CurrentVersion, string? TargetVersion = null, int Progress = 0, DateTimeOffset? CheckedAt = null, string? Error = null);
public interface IApplicationUpdateBackend
{
    bool IsSupported { get; }
    bool IsConfigured { get; }
    string CurrentVersion { get; }
    Task<string?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken);
    void ApplyAndRestart();
    bool HasAmbiguousApply { get; }
}
public interface IApplicationUpdateService
{
    UpdateSnapshot Snapshot { get; }
    event Action<UpdateSnapshot>? Changed;
    Task CheckAsync();
    Task DownloadAsync();
    void Cancel();
    Task ApplyAndRestartAsync();
}

public sealed class ApplicationUpdateService(IApplicationUpdateBackend backend, ApplicationActivity activity) : IApplicationUpdateService
{
    private readonly object gate = new();
    private Task? pending;
    private CancellationTokenSource? cancellation;
    private UpdateSnapshot snapshot = new(!backend.IsSupported ? UpdateState.Unsupported : !backend.IsConfigured ? UpdateState.NotConfigured : UpdateState.Idle, backend.CurrentVersion);
    public UpdateSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public event Action<UpdateSnapshot>? Changed;

    public Task CheckAsync() => Run(async token =>
    {
        Publish(Snapshot with { State = UpdateState.Checking, Error = null });
        var target = await backend.CheckAsync(token);
        Publish(Snapshot with { State = target is null ? UpdateState.Idle : UpdateState.Available, TargetVersion = target, Progress = 0, CheckedAt = DateTimeOffset.UtcNow });
    }, check: true);

    public Task DownloadAsync() => Run(async token =>
    {
        if (Snapshot.TargetVersion is null) return;
        Publish(Snapshot with { State = UpdateState.Downloading, Error = null, Progress = 0 });
        await backend.DownloadAsync(new InlineProgress(value => Publish(Snapshot with { Progress = Math.Clamp(value, 0, 100) })), token);
        Publish(Snapshot with { State = UpdateState.ReadyToRestart, Progress = 100 });
    });

    private Task Run(Func<CancellationToken, Task> action, bool check = false)
    {
        lock (gate)
        {
            if (pending is { IsCompleted: false }) return pending;
            if (snapshot.State is UpdateState.Unsupported or UpdateState.NotConfigured or UpdateState.Applying) return Task.CompletedTask;
            if (check && snapshot.State == UpdateState.ReadyToRestart) return Task.CompletedTask;
            if (!check && (snapshot.TargetVersion is null || snapshot.State == UpdateState.ReadyToRestart)) return Task.CompletedTask;
            cancellation?.Dispose();
            cancellation = new();
            var token = cancellation.Token;
            pending = Task.Run(async () =>
            {
                try { await action(token); }
                catch (OperationCanceledException) { Publish(Snapshot with { State = Snapshot.TargetVersion is null ? UpdateState.Idle : UpdateState.Available, Error = "Загрузка остановлена. Можно повторить позже." }); }
                catch (Exception error) { Publish(Snapshot with { State = UpdateState.Error, Error = SafeMessage(error) }); }
            });
            return pending;
        }
    }
    public void Cancel() { lock (gate) cancellation?.Cancel(); }

    public async Task ApplyAndRestartAsync()
    {
        lock (gate)
        {
            if (snapshot.State != UpdateState.ReadyToRestart || pending is { IsCompleted: false }) return;
        }
        var lease = activity.TryEnter(restart: true);
        if (lease is null) { Publish(Snapshot with { Error = "Завершите синхронизацию или вход Google перед перезапуском." }); return; }
        try
        {
            Publish(Snapshot with { State = UpdateState.Applying, Error = null });
            await Task.Run(backend.ApplyAndRestart);
            // A successful SDK apply exits this process. A returning backend cannot prove completion.
            activity.KeepApplying();
            Publish(Snapshot with { Error = "Ожидается перезапуск. Новые операции остановлены до завершения обновления." });
        }
        catch (Exception error)
        {
            if (backend.HasAmbiguousApply) activity.KeepApplying();
            else lease.Dispose();
            Publish(Snapshot with { State = backend.HasAmbiguousApply ? UpdateState.Applying : UpdateState.ReadyToRestart, Error = SafeMessage(error) });
        }
    }
    private void Publish(UpdateSnapshot value)
    {
        lock (gate) snapshot = value;
        Changed?.Invoke(value);
    }
    private static string SafeMessage(Exception error) => error is IOException ? "Не удалось прочитать или сохранить пакет обновления. Проверьте доступ к диску и повторите загрузку." : error is HttpRequestException ? "Сервер обновлений недоступен. Проверьте сеть и повторите позже." : "Обновление не удалось завершить. Проверьте источник обновлений и повторите загрузку.";
    private sealed class InlineProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
}
