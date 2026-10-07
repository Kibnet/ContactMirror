using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Google;
using ContactMirror.Infrastructure;

if (args.FirstOrDefault() == "--disk")
{
    var diskOutput = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "chat-artifacts/disk-performance");
    Directory.CreateDirectory(diskOutput);
    var results = new List<object>();
    foreach (var size in new[] { 100, 1000 })
    {
        var workspace = Path.Combine(diskOutput, $"workspace-{size}-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
        using var diskHandler = new SyntheticPages(Enumerable.Range(1, size).Select(Person).ToArray());
        using var diskHttp = new HttpClient(diskHandler);
        var diskCoordinator = new SyncCoordinator(new GoogleContactsGateway(new SyntheticToken(), diskHttp), new FileWorkspaceStore());
        var diskAccount = new AccountIdentity("disk-performance", "disk@example.invalid", "contactmirror://synthetic");
        var preview = await diskCoordinator.PrepareAsync(workspace, diskAccount);
        if (preview.Entries.Count != size || preview.Entries.Any(e => e.Kind != ChangeKind.CreateLocal)) throw new InvalidOperationException("Unexpected disk fixture preview.");
        var requestsBefore = diskHandler.Reads;
        var start = Stopwatch.GetTimestamp();
        var result = await diskCoordinator.ApplyAsync(preview, preview.Entries.Select(e => new PlanChoice(e.Key)).ToArray());
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var readsDuringApply = diskHandler.Reads - requestsBefore;
        var writes = Directory.GetFiles(Path.Combine(workspace, "contacts"), "*.contact.json").Length;
        if (!result.IsComplete || result.Confirmed != size || writes != size || diskHandler.Reads != requestsBefore || diskHandler.Mutations != 0) throw new InvalidOperationException("Disk export incomplete or queried Google during Apply.");
        var next = await diskCoordinator.PrepareAsync(workspace, diskAccount);
        if (next.Entries.Count != 0) throw new InvalidOperationException("Disk export did not reach no-op.");
        results.Add(new { contacts = size, applyMilliseconds = milliseconds, googleRequestsDuringApply = readsDuringApply, googleMutations = diskHandler.Mutations, savedFiles = writes, nextChanges = next.Entries.Count });
        Console.WriteLine($"disk-export {size}: {milliseconds:F0} ms, {writes} files, Apply Google reads=0, next no-op");
    }
    await File.WriteAllTextAsync(Path.Combine(diskOutput, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

const int count = 10_000;
var output = Path.GetFullPath(args.FirstOrDefault() ?? "chat-artifacts/performance");
Directory.CreateDirectory(output);
var account = new AccountIdentity("performance-synthetic", "performance@example.test", "contactmirror://synthetic");
var people = Enumerable.Range(1, count).Select(Person).ToArray();
using var handler = new SyntheticPages(people);
using var http = new HttpClient(handler);
var gateway = new MeasuredGateway(new GoogleContactsGateway(new SyntheticToken(), http));
var reports = new List<Scenario>();

// Small unreported run initializes JIT/static capability definitions before measurements.
using (var warmHandler = new SyntheticPages(people.Take(10).ToArray()))
using (var warmHttp = new HttpClient(warmHandler))
    await new SyncCoordinator(new GoogleContactsGateway(new SyntheticToken(), warmHttp), new MemoryStore()).PrepareAsync("synthetic://warmup", account);

var importStore = new MemoryStore();
await Measure("first-import-preview", importStore, false, expectedEntries: count);
var baselineStore = new MemoryStore();
foreach (var person in people)
{
    var number = int.Parse(PersonCodec.SourceId(person));
    var id = DeterministicId(number);
    var document = DocumentCodec.ToJson(new ContactDocument { Id = id, Data = PersonCodec.Data(person), Google = PersonCodec.Snapshot(person) });
    var bytes = JsonSemantics.Serialize(document);
    baselineStore.Contacts[id] = new(id, EntityKind.Contact, $"synthetic://contacts/{id}.contact.json", document, JsonSemantics.Hash(bytes), null, null, bytes);
    baselineStore.States[id] = new() { Id = id, Kind = EntityKind.Contact, ResourceName = PersonCodec.Resource(person), SourceId = PersonCodec.SourceId(person), Baseline = (JsonObject)document.DeepClone() };
}
await Measure("agreed-baseline-no-op", baselineStore, true, expectedEntries: 0);
var changed = baselineStore.Contacts[DeterministicId(5000)];
var edit = (JsonObject)changed.Document.DeepClone();
edit["data"]!["names"] = new JsonArray(new JsonObject { ["givenName"] = "Одна синтетическая правка", ["familyName"] = "Тест" });
var editBytes = JsonSemantics.Serialize(edit);
baselineStore.Contacts[changed.Id] = changed with { Document = edit, Hash = JsonSemantics.Hash(editBytes), RawBytes = editBytes };
await Measure("one-local-file-edit-preview", baselineStore, false, expectedEntries: 1);

// Cancellation requested exactly after all paginated data has been read, before local planning.
using var cancel = new CancellationTokenSource();
gateway.AfterRead = cancel.Cancel;
var cancelStarted = Stopwatch.GetTimestamp();
bool observedCancellation = false;
try { await new SyncCoordinator(gateway, baselineStore).PrepareAsync("synthetic://workspace", account, cancellationToken: cancel.Token); }
catch (OperationCanceledException) { observedCancellation = true; }
gateway.AfterRead = null;
var cancellation = new
{
    RequestedAfterAllPages = true,
    Observed = observedCancellation,
    TotalMilliseconds = Stopwatch.GetElapsedTime(cancelStarted).TotalMilliseconds,
    PlanningCancellationLatencyMilliseconds = Stopwatch.GetElapsedTime(gateway.LastReadFinished).TotalMilliseconds,
    LocalContactWrites = baselineStore.ContactWrites,
    RemoteMutations = handler.Mutations,
};
if (!observedCancellation || handler.Mutations != 0 || baselineStore.ContactWrites != 0) throw new InvalidOperationException("Cancellation or zero-mutation contract failed.");
var report = new
{
    TimestampUtc = DateTimeOffset.UtcNow,
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    ProcessorCount = Environment.ProcessorCount,
    ApplicationAssemblySha256 = JsonSemantics.Hash(await File.ReadAllBytesAsync(typeof(SyncCoordinator).Assembly.Location)),
    Contacts = count,
    PageSize = SyntheticPages.PageSize,
    Method = "Real GoogleContactsGateway reads fake HTTP pages; production SyncCoordinator; in-memory preseeded workspace; no network, filesystem scan, SQLite, photos, or native UI measured. Release process warmed with 10 contacts. Wall time after ReadAllAsync return is local planning target; single run per scenario, not a statistical benchmark.",
    Scenarios = reports,
    Cancellation = cancellation,
    PeakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d,
    PlanningTargetMilliseconds = 5000,
    PlanningTargetPassed = reports.All(x => x.PlanningMilliseconds <= 5000),
    TotalRemoteMutations = handler.Mutations,
    TotalContactWrites = baselineStore.ContactWrites + importStore.ContactWrites,
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), json, new UTF8Encoding(false));
Console.WriteLine(json);
var md = new StringBuilder("# Синтетическая проверка производительности ContactMirror\n\n");
md.AppendLine($"Дата UTC: {report.TimestampUtc:O}. {report.Runtime}; {report.OS}; логических CPU: {report.ProcessorCount}.");
md.AppendLine($"Application.dll SHA-256: `{report.ApplicationAssemblySha256}`.");
md.AppendLine("\n10 000 детерминированных синтетических контактов, 20 HTTP-страниц по 500 записей + одна страница ярлыков. Настоящий GoogleContactsGateway с fake HTTP handler и настоящий SyncCoordinator. Store — заранее подготовленный in-memory; этот замер не включает чтение 10 000 файлов/SQLite, фотографии, сеть, UI и не доказывает полную файловую производительность. Измерение в Release, JIT прогрет 10 записями, по одному запуску на сценарий.");
md.AppendLine("\n| Сценарий | Чтение fake API, мс | Planning после чтения, мс | Строк плана | Allocated, MiB | Managed после, MiB |\n|---|---:|---:|---:|---:|---:|");
foreach (var item in reports) md.AppendLine($"| {item.Name} | {item.ReadMilliseconds:F1} | {item.PlanningMilliseconds:F1} | {item.Entries} | {item.AllocatedMiB:F1} | {item.ManagedAfterMiB:F1} |");
md.AppendLine($"\nЦель planning ≤5 с: {(report.PlanningTargetPassed ? "PASS" : "FAIL")}. Peak working set процесса: {report.PeakWorkingSetMiB:F1} MiB (весь процесс, включая генерацию, все сценарии и GC; не дельта одного planning).");
md.AppendLine($"\nОтмена после полного чтения данных: observed={cancellation.Observed}, latency={cancellation.PlanningCancellationLatencyMilliseconds:F1} мс. Zero remote mutations={handler.Mutations}; zero contact writes={report.TotalContactWrites}. No-op Apply записывает {baselineStore.RunStarts} запись истории запуска и {baselineStore.RunCompletions} завершение в fake store; контактные файлы и EntityState не записываются (state writes={baselineStore.StateWrites}).");
md.AppendLine("\nКоманда: `dotnet run --project tools/ContactMirror.Performance/ContactMirror.Performance.csproj -c Release -- chat-artifacts/performance`. JSON содержит исходные числа; target FAIL отражается в отчёте и exit code 2.");
await File.WriteAllTextAsync(Path.Combine(output, "README.md"), md.ToString(), new UTF8Encoding(false));
return report.PlanningTargetPassed ? 0 : 2;

async Task Measure(string name, MemoryStore store, bool applyNoOp, int expectedEntries)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var managedBefore = GC.GetTotalMemory(false);
    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    var requestsBefore = handler.Reads;
    var start = Stopwatch.GetTimestamp();
    var coordinator = new SyncCoordinator(gateway, store);
    var preview = await coordinator.PrepareAsync("synthetic://workspace", account);
    var finish = Stopwatch.GetTimestamp();
    if (handler.Reads - requestsBefore != 21) throw new InvalidOperationException("The paginated fixture did not read all 20 contact pages and one group page.");
    if (preview.ContactCount != count || preview.Entries.Count != expectedEntries) throw new InvalidOperationException($"{name}: incorrect preview count {preview.Entries.Count}.");
    if (name == "one-local-file-edit-preview" && (preview.Entries[0].Kind != ChangeKind.Upload || preview.Entries[0].Field != "names")) throw new InvalidOperationException("Single names edit wasn't exactly one upload.");
    var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
    var managedAfter = GC.GetTotalMemory(false);
    reports.Add(new(name, Stopwatch.GetElapsedTime(start, gateway.LastReadFinished).TotalMilliseconds,
        Stopwatch.GetElapsedTime(gateway.LastReadFinished, finish).TotalMilliseconds, preview.Entries.Count,
        handler.Reads - requestsBefore, allocated / 1048576d, managedBefore / 1048576d, managedAfter / 1048576d));
    if (applyNoOp)
    {
        var applied = await coordinator.ApplyAsync(preview, []);
        if (!applied.IsComplete || applied.Confirmed != 0 || store.ContactWrites != 0 || store.StateWrites != 0 || handler.Mutations != 0) throw new InvalidOperationException("No-op modified contact data.");
    }
    Console.WriteLine($"{name}: {reports[^1].PlanningMilliseconds:F1} ms planning, {preview.Entries.Count} entries");
}

static Guid DeterministicId(int number) => new(number, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
static JsonObject Person(int number) => new()
{
    ["resourceName"] = $"people/c{number:D5}", ["etag"] = $"etag-{number}-v1",
    ["metadata"] = new JsonObject { ["sources"] = new JsonArray(new JsonObject { ["type"] = "CONTACT", ["id"] = number.ToString(), ["etag"] = $"source-{number}-v1" }) },
    ["names"] = new JsonArray(new JsonObject { ["givenName"] = $"Синтетический {number:D5}", ["familyName"] = "Контакт" }),
    ["emailAddresses"] = new JsonArray(new JsonObject { ["value"] = $"contact-{number:D5}@example.test", ["type"] = "work" }),
    ["birthdays"] = new JsonArray(new JsonObject { ["date"] = new JsonObject { ["year"] = 0, ["month"] = 1 + number % 12, ["day"] = 1 + number % 28 } }),
    ["userDefined"] = new JsonArray(new JsonObject { ["key"] = "SyntheticId", ["value"] = number.ToString() }),
};

sealed record Scenario(string Name, double ReadMilliseconds, double PlanningMilliseconds, int Entries, int HttpReads, double AllocatedMiB, double ManagedBeforeMiB, double ManagedAfterMiB);
sealed class SyntheticToken : IAccessTokenProvider { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-not-a-real-token"); }
sealed class SyntheticPages(JsonObject[] people) : HttpMessageHandler
{
    public const int PageSize = 500;
    public int Reads { get; private set; }
    public int Mutations { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get) { Mutations++; throw new InvalidOperationException("Synthetic preview must never mutate remote contacts."); }
        Reads++;
        var uri = request.RequestUri!;
        JsonObject body;
        if (uri.AbsolutePath.EndsWith("/contactGroups", StringComparison.Ordinal)) body = new() { ["contactGroups"] = new JsonArray(), ["totalItems"] = 0 };
        else if (uri.AbsolutePath.EndsWith("/people/me/connections", StringComparison.Ordinal))
        {
            var token = uri.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith("pageToken=", StringComparison.Ordinal));
            var offset = token is null ? 0 : int.Parse(Uri.UnescapeDataString(token[10..]));
            body = new() { ["connections"] = new JsonArray(people.Skip(offset).Take(PageSize).Select(x => x.DeepClone()).ToArray()), ["totalItems"] = people.Length };
            if (offset + PageSize < people.Length) body["nextPageToken"] = (offset + PageSize).ToString();
        }
        else throw new InvalidOperationException($"Unexpected synthetic HTTP read {uri.AbsolutePath}.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
    }
}

sealed class MeasuredGateway(IGoogleContactsGateway inner) : IGoogleContactsGateway
{
    public long LastReadFinished { get; private set; }
    public Action? AfterRead { get; set; }
    public async Task<RemoteSnapshot> ReadAllAsync(IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    { var result = await inner.ReadAllAsync(progress, cancellationToken); LastReadFinished = Stopwatch.GetTimestamp(); AfterRead?.Invoke(); return result; }
    public Task<JsonObject?> GetPersonAsync(string resourceName, CancellationToken cancellationToken = default) => inner.GetPersonAsync(resourceName, cancellationToken);
    public Task<JsonObject> CreateContactAsync(JsonObject person, CancellationToken cancellationToken = default) => inner.CreateContactAsync(person, cancellationToken);
    public Task<JsonObject> UpdateContactAsync(string resourceName, JsonObject person, IReadOnlyCollection<string> fields, CancellationToken cancellationToken = default) => inner.UpdateContactAsync(resourceName, person, fields, cancellationToken);
    public Task DeleteContactAsync(string resourceName, CancellationToken cancellationToken = default) => inner.DeleteContactAsync(resourceName, cancellationToken);
    public Task<JsonObject> UpdatePhotoAsync(string resourceName, byte[]? bytes, CancellationToken cancellationToken = default) => inner.UpdatePhotoAsync(resourceName, bytes, cancellationToken);
    public Task<byte[]> DownloadPhotoAsync(string url, CancellationToken cancellationToken = default) => inner.DownloadPhotoAsync(url, cancellationToken);
    public Task<JsonObject?> GetGroupAsync(string resourceName, CancellationToken cancellationToken = default) => inner.GetGroupAsync(resourceName, cancellationToken);
    public Task<JsonObject> CreateGroupAsync(string name, JsonArray clientData, CancellationToken cancellationToken = default) => inner.CreateGroupAsync(name, clientData, cancellationToken);
    public Task<JsonObject> UpdateGroupAsync(string resourceName, string name, JsonArray clientData, CancellationToken cancellationToken = default) => inner.UpdateGroupAsync(resourceName, name, clientData, cancellationToken);
    public Task DeleteGroupAsync(string resourceName, CancellationToken cancellationToken = default) => inner.DeleteGroupAsync(resourceName, cancellationToken);
    public Task ModifyMembershipAsync(string groupResourceName, string personResourceName, bool add, CancellationToken cancellationToken = default) => inner.ModifyMembershipAsync(groupResourceName, personResourceName, add, cancellationToken);
}

sealed class MemoryStore : IWorkspaceStore
{
    public Dictionary<Guid, LocalEntity> Contacts { get; } = [];
    public Dictionary<Guid, EntityState> States { get; } = [];
    public int ContactWrites { get; private set; }
    public int StateWrites { get; private set; }
    public int RunStarts { get; private set; }
    public int RunCompletions { get; private set; }
    public Task<IWorkspaceSession> OpenAsync(string root, AccountIdentity account, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IWorkspaceSession>(new Session(this, root)); }
    public Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string root, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunSummary>>([]);
    public Task<IReadOnlyList<BackupItem>> GetBackupAsync(string root, Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string> CreateContactFileAsync(string root, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    sealed class Session(MemoryStore owner, string root) : IWorkspaceSession
    {
        public string Root => root;
        public WorkspaceView View { get; } = new() { Contacts = owner.Contacts, Groups = new Dictionary<Guid, LocalEntity>(), States = owner.States, Issues = [], Pending = [] };
        public Task<LocalEntity?> ReadLocalAsync(EntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult(owner.Contacts.GetValueOrDefault(id));
        public Task<Guid> BeginRunAsync(IReadOnlyList<BackupItem> items, CancellationToken cancellationToken = default) { owner.RunStarts++; return Task.FromResult(Guid.NewGuid()); }
        public Task RecordAsync(Guid runId, JournalOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveStateAsync(EntityState state, CancellationToken cancellationToken = default) { owner.StateWrites++; throw new NotSupportedException(); }
        public Task RemoveStateAsync(Guid id, CancellationToken cancellationToken = default) { owner.StateWrites++; throw new NotSupportedException(); }
        public Task<LocalEntity> WriteAsync(EntityKind kind, Guid id, JsonObject document, string? expectedHash, byte[]? photoBytes = null, string? expectedPhotoHash = null, bool writePhoto = false, CancellationToken cancellationToken = default) { owner.ContactWrites++; throw new NotSupportedException(); }
        public Task TrashAsync(EntityKind kind, Guid id, string expectedHash, Guid runId, CancellationToken cancellationToken = default) { owner.ContactWrites++; throw new NotSupportedException(); }
        public Task CompleteRunAsync(Guid runId, SyncRunResult result, CancellationToken cancellationToken = default) { owner.RunCompletions++; return Task.CompletedTask; }
        public Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolvePendingAsync(Guid entityId, string? field = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> CacheImageAsync(byte[] bytes, string sourceUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteBackupAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
