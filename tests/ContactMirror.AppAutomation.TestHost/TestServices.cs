using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;

namespace ContactMirror.AppAutomation.TestHost;

public sealed class TestAccountConnector : IAccountConnector
{
    public static AccountIdentity Identity { get; } = new("ui-test", "example@example.test");
    public bool IsConfigured => true;
    public string ConfigurationHint => "Тестовый провайдер. Настоящие контакты не используются.";
    public Task<AccountIdentity?> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult<AccountIdentity?>(null);
    public Task<AccountIdentity> SignInAsync(CancellationToken cancellationToken = default) => Task.FromResult(Identity);
    public Task SignOutAsync(bool revoke = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public sealed class TestSyncCoordinator : ISyncCoordinator
{
    public int AppliedCount { get; private set; }
    public bool Partial { get; set; } = true;
    public bool Empty { get; set; }
    public bool BlockPrepare { get; set; }
    public bool GroupDeletionConflict { get; set; }
    public bool HistoryComplete { get; set; }
    public IReadOnlyList<SyncEntry>? OverrideEntries { get; set; }
    public Func<IReadOnlyList<PlanChoice>, SyncRunResult>? OverrideResult { get; set; }
    public int LargeEntryCount { get; set; }
    public bool SynchronousCpuPrepare { get; set; }
    public int PrepareThreadId { get; private set; }
    public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim CpuPrepareRelease { get; } = new(false);
    public int CleanupCalls { get; private set; }
    private readonly Guid _historyId = Guid.NewGuid();
    public IReadOnlyList<PlanChoice> AppliedChoices { get; private set; } = [];
    public async Task<SyncPreview> PrepareAsync(string root, AccountIdentity account, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        PrepareThreadId = Environment.CurrentManagedThreadId;
        if (SynchronousCpuPrepare)
        {
            for (var index = 0; index < 10000; index++) progress?.Report(new("Большая адресная книга", index, 10000));
            PrepareStarted.TrySetResult();
            if (!CpuPrepareRelease.Wait(TimeSpan.FromSeconds(3), cancellationToken))
                throw new TimeoutException("Тестовый CPU-планировщик ожидал отмену или освобождение.");
        }
        if (BlockPrepare) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (LargeEntryCount > 0)
            return new() { Root = root, Account = account, ContactCount = LargeEntryCount, Entries = Enumerable.Range(0, LargeEntryCount)
                .Select(index => Entry($"large-{index}", $"Контакт {index:D5}", ChangeKind.Upload, "phoneNumbers", "[{\"value\":\"+1 202 555 0100\"}]" )).ToArray() };
        return Preview(root, account);
    }
    private SyncPreview Preview(string root, AccountIdentity account) => new()
    {
        Root = root, Account = account, ContactCount = 4, GroupCount = 1,
        Entries = OverrideEntries ?? (Empty ? [] : GroupDeletionConflict ?
        [new SyncEntry { Key="group-delete-conflict", EntityId=Guid.NewGuid(), Entity=EntityKind.Group, Name="Коллеги", Field="$entity", Kind=ChangeKind.Conflict, Local=null, Before=JsonNode.Parse("{\"name\":\"Коллеги\"}"), Google=JsonNode.Parse("{\"name\":\"Коллеги: новое имя\"}"), Explanation="Ярлык удалён в папке и переименован в Google." }] :
        [
            Entry("anna", "Анна Примерова", ChangeKind.Upload, "phoneNumbers", "[ { \"value\": \"+7 000 000-00-01\" } ]"),
            Entry("boris", "Борис Примеров", ChangeKind.Download, "emailAddresses", "[ { \"value\": \"example@example.test\" } ]"),
            Entry("vera", "Вера Примерова", ChangeKind.Conflict, "phoneNumbers", "[ { \"value\": \"+7 000 000-00-02\" } ]"),
            Entry("gleb", "Глеб Примеров", ChangeKind.DeleteRemote, "$entity", "{}"),
            new SyncEntry { Key="blocked", EntityId=Guid.NewGuid(), Name="Даша Примерова", Field="userDefined", Kind=ChangeKind.Blocked, Explanation="Неизвестное вложенное поле. Остальные категории можно синхронизировать." }
        ])
    };
    private static SyncEntry Entry(string key, string name, ChangeKind kind, string field, string value) => new()
    {
        Key = key, EntityId = Guid.NewGuid(), Name = name, Kind = kind, Field = field,
        Before = kind == ChangeKind.Conflict ? JsonNode.Parse("[{\"value\":\"+7 000 000-00-00\"}]") : JsonNode.Parse("[]"),
        Local = kind == ChangeKind.Download ? JsonNode.Parse("[]") : JsonNode.Parse(value),
        Google = kind == ChangeKind.Conflict ? JsonNode.Parse("[{\"value\":\"+7 000 000-00-03\"}]") : kind == ChangeKind.Download ? JsonNode.Parse(value) : JsonNode.Parse("[]")
    };
    public Task<SyncRunResult> ApplyAsync(SyncPreview preview, IReadOnlyList<PlanChoice> choices, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        AppliedCount++; AppliedChoices = choices;
        if (OverrideResult is not null) return Task.FromResult(OverrideResult(choices));
        return Task.FromResult(new SyncRunResult(Guid.NewGuid(), Math.Max(0, choices.Count - (Partial ? 1 : 0)), Partial ? 1 : 0, 0, choices.Select((c, index) => new OperationResult(c.Key, Partial && index == choices.Count - 1 ? "failed" : "confirmed", Partial && index == choices.Count - 1 ? "Запрос не завершён. Проверьте изменения для продолжения." : "Операция подтверждена")).ToArray()));
    }
    public Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string root, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunSummary>>([new(_historyId, DateTimeOffset.UtcNow, HistoryComplete ? "complete" : "partial", 2, HistoryComplete ? 0 : 1, 0, CleanupCalls == 0 ? 8192 : 0, CleanupCalls == 0)]);
    public Task CleanupBackupAsync(string root, AccountIdentity account, Guid runId, CancellationToken cancellationToken = default)
    {
        if (!HistoryComplete || runId != _historyId) throw new SyncException("backup-required", "Резервная копия нужна для восстановления незавершённой операции.");
        CleanupCalls++;
        return Task.CompletedTask;
    }
    public Task<string> CreateContactFileAsync(string root, CancellationToken cancellationToken = default) => Task.FromResult(Path.Combine(root, "contacts", "example.json"));
    public Task<SyncPreview> PrepareRestoreAsync(string root, AccountIdentity account, Guid runId, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = Preview(root, account);
        return Task.FromResult(new SyncPreview { Root = root, Account = account, Entries = source.Entries, IsRecovery = true });
    }
}
