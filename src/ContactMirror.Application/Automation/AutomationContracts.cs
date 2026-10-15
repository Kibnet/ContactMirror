using System.Text.Json.Nodes;

namespace ContactMirror.Application.Automation;

public sealed record AgentCall(string Tool, JsonObject Arguments, string BridgeId);
public sealed record AgentResult(string Status, JsonNode? Data = null, string? OperationId = null,
    AgentError? Error = null, int ApiVersion = 1)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public static AgentResult Failure(string code, string message, bool retryable = false) => new("error", Error: new(code, message, retryable));
}
public sealed record AgentError(string Code, string Message, bool Retryable = false);
public sealed record AgentUiState(string? Root = null, Guid? ContactId = null, bool Dirty = false,
    bool InvalidInput = false, bool Modal = false, bool Busy = false, long Generation = 0, bool HasWindow = false);
public interface IAgentUi
{
    Task<AgentUiState> GetStateAsync();
    Task<IDisposable?> TryEnterAsync(ApplicationActivity activity) => Task.FromResult(activity.TryEnter());
    Task ShowWindowAsync();
    Task OpenContactAsync(string root, Guid id);
    Task ChangedAsync(string? root);
}
public sealed class BackgroundAgentUi : IAgentUi
{
    public Task<AgentUiState> GetStateAsync() => Task.FromResult(new AgentUiState());
    public Task ShowWindowAsync() => Task.CompletedTask;
    public Task OpenContactAsync(string root, Guid id) => Task.CompletedTask;
    public Task ChangedAsync(string? root) => Task.CompletedTask;
}
public static class AgentTools
{
    public static readonly string[] Names = ["app_get_state", "app_show_window", "app_open_contact", "account_get_status", "account_connect", "account_disconnect",
        "workspace_list", "workspace_open", "workspace_get_status", "workspace_initialize", "contacts_get_schema", "contacts_list", "contacts_get", "contacts_create", "contacts_update", "contacts_delete",
        "photo_get", "photo_upload_begin", "photo_upload_chunk", "photo_upload_complete", "photo_upload_abort", "values_read", "groups_list", "groups_get", "groups_create", "groups_update", "groups_delete",
        "sync_prepare", "sync_get_plan", "sync_apply", "sync_repair_snapshot", "history_list", "history_get", "history_prepare_restore", "history_cleanup_backup", "history_apply_local_restore", "operations_get", "operations_cancel"];
    public static bool Durable(string tool) => tool is "contacts_create" or "contacts_update" or "contacts_delete" or "groups_create" or "groups_update" or "groups_delete"
        or "account_connect" or "account_disconnect" or "workspace_initialize" or "sync_apply" or "sync_repair_snapshot" or "history_cleanup_backup" or "history_apply_local_restore";
    public static bool LongRunning(string tool) => tool is "account_connect" or "sync_prepare" or "sync_apply" or "history_prepare_restore" or "history_cleanup_backup" or "history_apply_local_restore";
}
