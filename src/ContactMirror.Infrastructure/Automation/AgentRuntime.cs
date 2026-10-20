using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Automation;

/// <summary>All bridges and the window share these services and the same atomic application lease.</summary>
public sealed partial class AgentRuntime : IDisposable
{
    private IAccountConnector account;
    private SyncCoordinator sync;
    private readonly IContactEditorStore editor;
    private readonly IWorkspaceStore workspaces;
    private readonly ApplicationActivity activity;
    private readonly Func<AgentConfiguration> configuration;
    private readonly Lazy<AgentReceipts> receipts;
    private sealed record UndurableResult(AgentResult Result,string? AfterAccount,bool AccountTransition);
    private readonly ConcurrentDictionary<string, UndurableResult> undurableResults = new();
    private readonly AsyncLocal<string?> activeReceipt = new();
    private readonly AgentValues values;
    private readonly AgentUploads uploads;
    private readonly TimeProvider clock;
    private DateTimeOffset Now => clock.GetUtcNow();
    private readonly ConcurrentDictionary<string, Guid> bridges = new();
    private readonly ConcurrentDictionary<string, WorkspaceHandle> handles = new();
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly ConcurrentDictionary<string, PlanHandle> plans = new();
    private sealed record PlanHandle(string Root, string AccountKey, DateTimeOffset PreparedAt, string BindingHash);
    private volatile AccountIdentity? cachedAccount;
    private bool disposed;
    private sealed record WorkspaceHandle(string Root, Guid WorkspaceId, string? AccountKey, string? BindingHash);
    private sealed class Job(string id, string bridge, string tool, string? key, string? root, string? accountKey, bool local, bool google)
    {
        public string Id { get; } = id;
        public string Bridge { get; } = bridge;
        public string Tool { get; } = tool;
        public string? Key { get; } = key;
        public string? Root { get; } = root;
        public string? BeforeAccount { get; } = accountKey;
        private volatile string? afterAccount;
        public string? AfterAccount { get => afterAccount; set => afterAccount = value; }
        public bool RequiresLocal { get; } = local;
        public bool RequiresGoogle { get; } = google;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        private readonly AgentJobLifecycle lifecycle = new();
        public string State { get => lifecycle.State; set => lifecycle.Complete(value); }
        public void RequestCancel() { if(lifecycle.RequestCancel()) { try { Cancellation.Cancel(); } catch(ObjectDisposedException) { } } }
        public SyncProgress Progress { get; set; } = new("Начинаем операцию…");
        public AgentResult? Result { get; set; }
    }
    public IAgentUi Ui { get; set; } = new BackgroundAgentUi();
    public int ConnectedClients => bridges.Count;
    public bool HasRunningJobs => jobs.Values.Any(j => j.State is "running" or "cancelRequested");
    public event Action? Changed;
    public AgentRuntime(IAccountConnector account, SyncCoordinator sync, IContactEditorStore editor,
        IWorkspaceStore workspaces, ApplicationActivity activity, Func<AgentConfiguration>? configuration = null, string? stateDirectory = null, Action? beforeReceiptComplete = null, TimeProvider? timeProvider = null)
    {
        this.account = account; this.sync = sync; this.editor = editor; this.workspaces = workspaces; this.activity = activity;
        clock = timeProvider ?? TimeProvider.System; values = new(clock); uploads = new(clock);
        this.configuration = configuration ?? AgentConfiguration.Load;
        receipts = new(() => new AgentReceipts(stateDirectory ?? Path.Combine(ApplicationPaths.DataRoot, "Automation"), beforeReceiptComplete));
        sync.PlanInvalidated += PlanInvalidated;
        values.Budget = sync.RetentionBudget;
        sync.RunStarted += RunStarted;
        if (editor is ContactMirror.Infrastructure.Contacts.ContactEditorStore fileEditor) fileEditor.BackupCreated += BackupCreated;
    }
    private void RunStarted(Guid id) { if (activeReceipt.Value is { } key) receipts.Value.AddIntent(key, new() { ["runId"] = id.ToString() }); }
    private void BackupCreated(string path) { if (activeReceipt.Value is { } key) receipts.Value.AddIntent(key, new() { ["backupId"] = "local:" + Guid.Parse(Path.GetFileName(path)) }); }
    private void WriteIntent(JsonObject additions) { if (activeReceipt.Value is { } key) receipts.Value.AddIntent(key, additions); }
    private void PlanInvalidated(Guid id, string reason) { plans.TryRemove(id.ToString(), out _); values.RemoveSource("plan:" + id); }
    public void Reconfigure(IAccountConnector connector, SyncCoordinator coordinator)
    {
        Revoke(); sync.PlanInvalidated -= PlanInvalidated; sync.RunStarted -= RunStarted; account = connector; sync = coordinator; values.Budget = sync.RetentionBudget; cachedAccount = null; sync.PlanInvalidated += PlanInvalidated; sync.RunStarted += RunStarted;
    }
    public async Task NotifyUserChangeAsync(string? root, AccountIdentity? identity)
    {
        cachedAccount = identity; values.Clear(); ClearLocalRestores(); cursors.Clear();
        if (root is null) handles.Clear();
        await sync.InvalidatePlansAsync(root, reason: root is null ? "account_changed" : "plan_stale");
    }
    public string ConnectBridge()
    {
        if (activity.IsApplying || File.Exists(ApplicationPaths.ApplyingPath)) throw new SyncException("update_in_progress", "Программа обновляется. Дождитесь завершения.");
        var c = configuration(); if (!c.Enabled) throw new SyncException("mcp_disabled", "Включите доступ MCP в настройках ContactMirror.");
        var id = Guid.NewGuid().ToString("N"); bridges[id] = c.Generation; Changed?.Invoke(); return id;
    }
    public void DisconnectBridge(string id)
    {
        bridges.TryRemove(id, out _); uploads.Disconnected(id);
        foreach (var j in jobs.Values.Where(j => j.Bridge == id)) j.RequestCancel();
        Changed?.Invoke();
    }
    public void Revoke()
    {
        foreach (var id in bridges.Keys.ToArray()) DisconnectBridge(id);
        handles.Clear(); plans.Clear(); ClearLocalRestores(); cursors.Clear(); values.Clear(); Changed?.Invoke();
    }
    private AgentConfiguration Permission(AgentCall call)
    {
        var c = configuration();
        if (!c.Enabled || !bridges.TryGetValue(call.BridgeId, out var generation) || generation != c.Generation)
            throw new SyncException("access_revoked", "Доступ MCP отключён или изменён. Подключитесь заново.");
        var local = call.Tool is "app_open_contact" or "workspace_initialize" or "contacts_create" or "contacts_update" or "contacts_delete" or "groups_create" or "groups_update" or "groups_delete"
            or "photo_upload_begin" or "photo_upload_chunk" or "photo_upload_complete" or "photo_upload_abort" or "sync_repair_snapshot" or "history_cleanup_backup" or "history_apply_local_restore" or "sync_apply";
        if (local && !c.LocalWrite) throw new SyncException("permission_denied", "В настройках MCP не разрешены локальные изменения.");
        if (call.Tool is "account_connect" or "account_disconnect" && !c.GoogleWrite) throw new SyncException("permission_denied", "В настройках MCP не разрешено управление Google.");
        return c;
    }
    public async Task<AgentResult> CallAsync(AgentCall call, CancellationToken cancellationToken = default)
    {
        string? begunReceipt = null;
        var backgroundStarted = false;
        try
        {
            call = call with { Arguments = (JsonObject)call.Arguments.DeepClone() };
            if (disposed) return AgentResult.Failure("host_unavailable", "Приложение завершает работу.");
            ValidateArguments(call);
            var config = Permission(call);
            using var callOrigin = PlanOrigin.Enter(call.BridgeId);
            if (call.Tool is "operations_get" or "operations_cancel") { if (!activity.IsActive) cachedAccount = await account.GetAccountAsync(cancellationToken); return await OperationAsync(call, cancellationToken); }
            if (call.Tool == "app_get_state") return Ok(new { version = typeof(AgentRuntime).Assembly.GetName().Version?.ToString(), appId = ApplicationPaths.AppId,
                processId = Environment.ProcessId, permissions = new { read = true, config.LocalWrite, config.GoogleWrite }, connectedClients = ConnectedClients, busy = activity.IsActive, applying = activity.IsApplying, ui = await Ui.GetStateAsync() });
            if (call.Tool == "app_show_window") { await Ui.ShowWindowAsync(); return Ok(new { shown = true }); }
            if (call.Tool == "account_get_status")
            {
                if (!activity.IsActive) cachedAccount = await account.GetAccountAsync(cancellationToken);
                return Ok(new { connected = cachedAccount is not null, identity = cachedAccount, configured = account.IsConfigured, hint = account.ConfigurationHint });
            }
            if (call.Tool == "app_open_contact")
            {
                if (activity.IsActive || activity.IsApplying) throw new SyncException("busy", "Приложение занято.");
                cachedAccount = await account.GetAccountAsync(cancellationToken); using var uiPin = PinHandle(call); var target = await RootForAsync(call, config, cancellationToken);
                await GuardAsync(call, target); await Ui.OpenContactAsync(target!, Uuid(call.Arguments, "id")); return Ok(new { opened = true });
            }
            var key = AgentTools.Durable(call.Tool) ? Uuid(call.Arguments, "requestKey").ToString() : null;
            var requestHash = JsonSemantics.Hash(Canonical(JsonSerializer.SerializeToNode(new { call.Tool, call.Arguments }, JsonSemantics.Options))!.ToJsonString());
            if (key is not null && receipts.Value.Get(key) is { } existingReceipt)
            {
                if (existingReceipt.Hash != requestHash) throw new SyncException("idempotency_conflict", "requestKey уже использован для другого запроса.");
                if (!activity.IsActive) cachedAccount = await account.GetAccountAsync(cancellationToken);
                existingReceipt = OverlayReceipt(existingReceipt);
                var existing = await ReconcileAsync(existingReceipt, cancellationToken);
                if (existing.Workspace is not null) config.Check(existing.Workspace);
                if (!activity.IsActive) cachedAccount = await account.GetAccountAsync(cancellationToken);
                if (existing.State == "completed" && call.Tool is "account_connect" or "account_disconnect")
                {
                    if (cachedAccount?.Key != existing.AfterAccount) throw new SyncException("account_changed", "Текущий аккаунт отличается от результата прежней операции.");
                }
                else CheckReceiptAccess(existing,call);
                if (existing.State == "expired") return AgentResult.Failure("request_key_expired", "Срок результата истёк; ключ не будет выполнен повторно.");
                return existing.Result ?? new(existing.State == "unknown" ? "unknown" : "accepted", OperationId: existing.Operation,
                    Error: existing.State == "unknown" ? new("recovery_required", "Исход записи неизвестен. Проверьте историю и данные.") : null);
            }
            using var lease = await Ui.TryEnterAsync(activity) ?? throw new SyncException("busy", "Приложение выполняет другую операцию. Проверьте её статус.");
            Permission(call);
            cachedAccount = await account.GetAccountAsync(cancellationToken);
            using var handlePin = PinHandle(call);
            var root = await RootForAsync(call, config, cancellationToken);
            if (AgentTools.Durable(call.Tool) || call.Tool is "sync_prepare" or "history_prepare_restore" or "app_open_contact") await GuardAsync(call, root);
            if (call.Tool is "account_connect" or "account_disconnect")
            {
                if (!call.Arguments.ContainsKey("expectedAccountKey") || Optional(call.Arguments, "expectedAccountKey") != cachedAccount?.Key)
                    throw new SyncException("account_changed", "Укажите актуальный expectedAccountKey (null при первом входе).");
            }
            var id = Guid.NewGuid().ToString("N");
            if (call.Tool is "contacts_create" or "groups_create") call.Arguments["_allocatedId"] = Guid.NewGuid().ToString();
            if (key is not null)
            {
                receipts.Value.Begin(key, requestHash, id, cachedAccount?.Key, root, new JsonObject { ["tool"] = call.Tool, ["entityId"] = call.Arguments["_allocatedId"]?.DeepClone() ?? call.Arguments["id"]?.DeepClone(), ["expectedVersion"] = call.Arguments["expectedVersion"]?.DeepClone(), ["planId"] = call.Arguments["planId"]?.DeepClone() }); begunReceipt = key;
            }
            if (AgentTools.LongRunning(call.Tool))
            {
                // Transfer the lease, not a second lease, into the accepted background operation.
                var held = new HeldLease(lease);
                var needsGoogle = call.Tool is "account_connect" or "account_disconnect";
                if (call.Tool == "sync_apply")
                {
                    var plan = await GetPlanAsync(call.Arguments); var selected = Choices(call.Arguments, true);
                    needsGoogle = selected.Any(c => c.Resolution != Resolution.Skip && plan.Entries.Any(e => e.Key == c.Key && WritesRemote(e, c.Resolution)));
                }
                var job = new Job(id, call.BridgeId, call.Tool, key, root, cachedAccount?.Key, AgentTools.Durable(call.Tool) && call.Tool is not ("account_connect" or "account_disconnect"), needsGoogle); jobs[id] = job;
                foreach (var old in jobs.Values.Where(j => j.State is not ("running" or "cancelRequested")).OrderBy(j => j.Created).Take(Math.Max(0, jobs.Count - 256))) { if (jobs.TryRemove(old.Id, out _)) old.Cancellation.Dispose(); }
                await held.StartAsync(() => RunJobAsync(job, call, root), handlePin);
                backgroundStarted = true;
                return new("accepted", OperationId: id);
            }
            try
            {
                using var origin = PlanOrigin.Enter(call.BridgeId);
                activeReceipt.Value = key;
                var result = await DispatchAsync(call, root, null, cancellationToken);
                if (key is not null) result = CompleteReceiptSafe(key, result, call.Tool);
                return result;
            }
            catch (Exception e) when (Recoverable(e))
            {
                var result = Failure(e);
                if (key is not null) result = CompleteReceiptSafe(key, result, call.Tool);
                return result;
            }
        }
        catch (Exception e) when (Recoverable(e))
        {
            var result = Failure(e);
            if (begunReceipt is not null && !backgroundStarted) result = CompleteReceiptSafe(begunReceipt, result, call.Tool);
            return result;
        }
    }
    // A detachable lease prevents the lexical using block from releasing a running job's lease.
    private sealed class HeldLease(IDisposable lease)
    {
        public Task StartAsync(Func<Task> run, HandlePin pin)
        {
            if (lease is not ApplicationActivity.TransferableLease transferable) throw new InvalidOperationException("A transferable lease is required.");
            var owned = transferable.Transfer();
            var ownedPin = pin.Transfer();
            _ = Task.Run(async () => { try { await run(); } finally { ownedPin?.Dispose(); owned.Dispose(); } });
            return Task.CompletedTask;
        }
    }
    private sealed class HandlePin(FileStream? stream) : IDisposable
    {
        private FileStream? held = stream;
        public FileStream? Transfer() { var result = held; held = null; return result; }
        public void Dispose() { held?.Dispose(); held = null; }
    }
    private HandlePin PinHandle(AgentCall call)
    {
        var root = Optional(call.Arguments, "workspace") is { } id && handles.TryGetValue(id, out var handle) && handle.BindingHash is not null ? handle.Root : null;
        if (root is null && Optional(call.Arguments, "planId") is { } planId)
        {
            if (plans.TryGetValue(planId, out var plan)) root = plan.Root;
            else if (localRestores.TryGetValue(planId, out var local)) root = local.Root;
        }
        if (root is null) return new(null);
        configuration().Check(root); var path = Path.Combine(root, ".contactmirror", "workspace.json"); FileWorkspaceStore.ValidatePath(root, path);
        try { return new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
        catch (IOException) { throw new SyncException("workspace_stale", "Папка изменяется; откройте её заново после завершения записи."); }
    }
    private void AccountTransitioned(AgentCall call)
    {
        foreach (var job in jobs.Values.Where(j => j.Bridge == call.BridgeId && j.Tool == call.Tool && j.Key == Optional(call.Arguments, "requestKey"))) job.AfterAccount = cachedAccount?.Key;
    }
    private async Task RunJobAsync(Job job, AgentCall call, string? root)
    {
        try
        {
            using var origin = PlanOrigin.Enter(call.BridgeId);
            activeReceipt.Value = job.Key;
            var progress = new DirectProgress(p => { job.Progress = p; Changed?.Invoke(); });
            job.Result = await DispatchAsync(call, root, progress, job.Cancellation.Token);
        }
        catch (Exception e) when (Recoverable(e)) { job.Result = Failure(e); }
        finally
        {
            job.Result ??= AgentResult.Failure("operation_failed", "Операцию не удалось завершить.");
            job.AfterAccount = cachedAccount?.Key;
            if (job.Key is not null) job.Result = CompleteReceiptSafe(job.Key, job.Result, job.Tool);
            job.State = job.Result.Status is "error" ? "failed" : job.Result.Status is "unknown" ? "unknown" : "completed";
            Changed?.Invoke();
        }
    }
    private AgentResult CompleteReceiptSafe(string key, AgentResult result,string? tool=null)
    {
        try { receipts.Value.Complete(key, ReceiptSafe(result), cachedAccount?.Key); undurableResults.TryRemove(key, out _); return result; }
        catch (Exception e) when (Recoverable(e))
        {
            var unknown = new AgentResult("unknown", Error: new("receipt_persistence_failed", "Запись могла завершиться, но её результат не удалось надёжно сохранить. Проверьте requestKey и историю; не повторяйте запись новым ключом."));
            undurableResults[key] = new(unknown,cachedAccount?.Key,tool is "account_connect" or "account_disconnect");
            foreach (var old in undurableResults.Keys.Take(Math.Max(0, undurableResults.Count - 256))) undurableResults.TryRemove(old, out _);
            return unknown;
        }
    }
    private AgentReceipts.Receipt OverlayReceipt(AgentReceipts.Receipt receipt)
    {
        if (receipt.State is "completed" or "expired") return receipt;
        if (undurableResults.TryGetValue(receipt.Key, out var pending)) return receipt with { State = "unknown", Result = pending.Result, AfterAccount = pending.AccountTransition ? pending.AfterAccount : receipt.AfterAccount };
        if (receipt.State == "running" && !activity.IsActive && !jobs.Values.Any(j => j.Id == receipt.Operation && j.State is "running" or "cancelRequested")) return receipt with { State = "unknown" };
        return receipt;
    }
    private sealed class DirectProgress(Action<SyncProgress> action) : IProgress<SyncProgress> { public void Report(SyncProgress value) => action(value); }
    private async Task GuardAsync(AgentCall call, string? root)
    {
        var ui = await Ui.GetStateAsync();
        if (ui.Modal) throw new SyncException("ui_modal_busy", "Завершите диалог в окне ContactMirror.");
        var related = root is null || ui.Root is null || string.Equals(root, ui.Root, StringComparison.OrdinalIgnoreCase);
        var onlyContact = call.Tool is "contacts_update" or "contacts_delete";
        var sameContact = !onlyContact || ui.ContactId == Uuid(call.Arguments, "id");
        if (related && sameContact && (ui.Dirty || ui.InvalidInput)) throw new SyncException("draft_conflict", "В окне есть несохранённые изменения. Сохраните или отмените их вручную.");
    }
    private async Task<string?> RootForAsync(AgentCall call, AgentConfiguration config, CancellationToken token)
    {
        if (call.Arguments["workspace"] is JsonValue handle)
        {
            if (!handles.TryGetValue(handle.GetValue<string>(), out var h) || h.AccountKey != cachedAccount?.Key) throw new SyncException("workspace_stale", "Откройте рабочую папку заново.");
            config.Check(h.Root);
            var manifest = Path.Combine(h.Root, ".contactmirror", "workspace.json");
            var binding = File.Exists(manifest) ? JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(h.Root, manifest, 16 * 1024 * 1024, token)) : null;
            if (binding != h.BindingHash) { handles.TryRemove(handle.GetValue<string>(), out _); throw new SyncException("workspace_stale", "Привязка папки изменилась; откройте её заново."); }
            return h.Root;
        }
        if (call.Arguments["root"] is JsonValue path) { var root = AgentConfiguration.Canonical(path.GetValue<string>()); config.Check(root); return root; }
        if (call.Arguments["planId"] is JsonValue id && plans.TryGetValue(id.GetValue<string>(), out var plan)) { config.Check(plan.Root); return plan.Root; }
        if (call.Arguments["planId"] is JsonValue localId && localRestores.TryGetValue(localId.GetValue<string>(), out var local)) { config.Check(local.Root); return local.Root; }
        return null;
    }
    private async Task<AgentResult> OperationAsync(AgentCall call, CancellationToken token)
    {
        var args = call.Arguments; var id = Optional(args, "operationId"); var key = Optional(args, "requestKey");
        if ((id is null) == (key is null)) throw new SyncException("invalid_arguments", "Укажите ровно одно: operationId или requestKey.");
        if(key is not null) key=Guid.Parse(key).ToString();
        if (key is not null)
        {
            var stored = receipts.Value.Get(Guid.Parse(key).ToString()) ?? throw new SyncException("operation_not_found", "Результат не найден.");
            stored = OverlayReceipt(stored);
            CheckReceiptAccess(stored, call);
            var r = await ReconcileAsync(stored, token);
            CheckReceiptAccess(r, call);
            if (call.Tool == "operations_get") return r.Result ?? new(r.State, OperationId: r.Operation);
            id = r.Operation;
        }
        if (!jobs.TryGetValue(id!, out var job)) throw new SyncException("operation_not_found", "Операция не найдена.");
        if (job.Root is not null) configuration().Check(job.Root);
        var ownTransition = job.Tool is "account_connect" or "account_disconnect" && job.State is "running" or "cancelRequested" && (job.Bridge == call.BridgeId || key is not null && key == job.Key);
        if (!ownTransition && job.BeforeAccount != cachedAccount?.Key && !(job.Tool is "account_connect" or "account_disconnect" && job.AfterAccount == cachedAccount?.Key)) throw new SyncException("account_changed", "Операция принадлежит прежнему аккаунту.");
        if (call.Tool == "operations_cancel")
        {
            if (job.Bridge != call.BridgeId && (key is null || key != job.Key)) throw new SyncException("operation_not_owned", "Нельзя остановить операцию другого клиента.");
            var c = configuration();
            if (job.RequiresGoogle && !c.GoogleWrite || job.RequiresLocal && !c.LocalWrite)
                throw new SyncException("permission_denied", "Недостаточно прав для остановки этой операции.");
            job.RequestCancel();
        }
        return Ok(new { operationId = job.Id, state = job.State, progress = job.Progress, result = job.Result });
    }
    private void CheckReceiptAccess(AgentReceipts.Receipt receipt, AgentCall call)
    {
        if (receipt.Workspace is not null) configuration().Check(receipt.Workspace);
        var ownTransition = jobs.TryGetValue(receipt.Operation, out var job) && job.Tool is "account_connect" or "account_disconnect" && job.State is "running" or "cancelRequested" && (job.Bridge == call.BridgeId || receipt.Key == job.Key);
        var completedAfter = receipt.State is "completed" or "expired" && receipt.AfterAccount == cachedAccount?.Key;
        var pendingTransition = receipt.State=="unknown" && undurableResults.TryGetValue(receipt.Key,out var pending) && pending.AccountTransition && pending.AfterAccount==cachedAccount?.Key;
        if (!ownTransition && receipt.BeforeAccount != cachedAccount?.Key && !completedAfter && !pendingTransition) throw new SyncException("account_changed", "Результат принадлежит прежнему аккаунту.");
    }
    private static JsonNode? Canonical(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()), _ => n?.DeepClone()
    };
    private static AgentResult ReceiptSafe(AgentResult r) => r.Error is null ? r : r with { Error = r.Error with { Message = "Проверьте статус и данные операции." } };
    private static bool Recoverable(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static AgentResult Failure(Exception e) => e switch
    {
        ContactEditorWriteException w when w.Outcome == ContactWriteOutcome.Uncertain => new("unknown", Error: new("recovery_required", "Исход локальной записи неизвестен; проверьте контакт и историю.")),
        ContactEditorWriteException w when w.Outcome == ContactWriteOutcome.Committed => Ok(new { outcome = "committed", id = w.Snapshot?.Id, version = w.Snapshot is null ? null : Version(w.Snapshot) }),
        SyncException s when s.IsAmbiguous => new("unknown", Error: new(s.Code, "Исход записи неизвестен; проверьте журнал перед повтором.")),
        SyncException s => AgentResult.Failure(s.Code, s.Message, s.Code == "busy"),
        OperationCanceledException => AgentResult.Failure("cancelled", "Операция остановлена; уже подтверждённые шаги сохранены."),
        JsonException or FormatException or InvalidOperationException or ArgumentException => AgentResult.Failure("invalid_arguments", "Проверьте типы и значения аргументов."),
        _ => AgentResult.Failure("operation_failed", "Операцию не удалось завершить; данные и журнал сохранены.")
    };
    private static AgentResult Ok(object? value) => new("completed", value is JsonNode node ? node : JsonSerializer.SerializeToNode(value, JsonSemantics.Options));
    private static string Required(JsonObject args, string key) => args[key]?.GetValue<string>() is { Length: > 0 } value ? value : throw new SyncException("invalid_arguments", $"Укажите {key}.");
    private static string? Optional(JsonObject args, string key) => args[key]?.GetValue<string>();
    private static Guid Uuid(JsonObject args, string key) => Guid.TryParse(Required(args, key), out var id) && id != Guid.Empty ? id : throw new SyncException("invalid_arguments", $"{key} должен быть UUID.");
    private static bool Flag(JsonObject args, string key) => args[key]?.GetValue<bool>() == true;
    private static int Number(JsonObject args, string key, int fallback = 0) => args[key]?.GetValue<int>() ?? fallback;
    private static string Version(ContactSnapshot s) => JsonSemantics.Hash(string.Join("|", s.WorkspaceId, s.Id, s.Hash, s.PhotoHash, s.BindingHash, s.LabelsHash, s.StateHash));
    private AccountIdentity Connected() => cachedAccount ?? throw new SyncException("authRequired", "Подключите Google-аккаунт.");
    public void Dispose() { disposed = true; Revoke(); sync.PlanInvalidated -= PlanInvalidated; sync.RunStarted -= RunStarted; if (editor is ContactMirror.Infrastructure.Contacts.ContactEditorStore e) e.BackupCreated -= BackupCreated; uploads.Dispose(); foreach (var j in jobs.Values) j.Cancellation.Dispose(); }
}
