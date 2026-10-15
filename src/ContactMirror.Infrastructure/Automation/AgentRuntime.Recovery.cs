using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Application.Automation;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Automation;

public sealed partial class AgentRuntime
{
    /// <summary>Only observed hashes/journal receipts can refine unknown. This path never repeats writes.</summary>
    private async Task<AgentReceipts.Receipt> ReconcileAsync(AgentReceipts.Receipt receipt, CancellationToken token)
    {
        if (receipt.State != "unknown" || receipt.Workspace is null || receipt.Intent is not JsonObject intent || activity.IsActive || receipt.BeforeAccount != cachedAccount?.Key) return receipt;
        configuration().Check(receipt.Workspace);
        using var lease = await Ui.TryEnterAsync(activity); if (lease is null) return receipt;
        try
        {
            var root = receipt.Workspace; var catalog = await editor.LoadAsync(root, cachedAccount, token);
            if (Optional(intent, "workspaceId") is { } expected && expected != catalog.WorkspaceId.ToString()) return receipt;
            AgentResult? outcome = null;
            if (Optional(intent, "runId") is { } runText)
            {
                var run = Guid.Parse(runText); var operations = await workspaces.GetRunOperationsAsync(root, run, token);
                var expectedKeys = intent["entryKeys"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                if (operations.Count > 0 && expectedKeys is { Length: > 0 } && expectedKeys.All(key => operations.Any(o => o.Key == key)) && operations.All(o => o.Status is "committed" or "reconciled" or "failed"))
                {
                    var failed = operations.Count(o => o.Status == "failed");
                    outcome = Ok(new { runId = run, reconciled = true, confirmed = operations.Count(o => o.Status == "committed"), resolved = operations.Count(o => o.Status == "reconciled"), failed, unknown = 0 });
                    if (failed > 0) outcome = outcome with { Status = "error", Error = new("partial_result", "Часть шагов не выполнена; неопределённых шагов нет.") };
                }
            }
            if (Optional(intent, "entityId") is { } entityText && Optional(intent, "intendedHash") is { } intended)
            {
                var id = Guid.Parse(entityText); var kind = Optional(intent, "entityKind");
                if (Optional(intent, "action") == "save")
                {
                    if (kind == "Contact" && catalog.Contacts.Any(c => c.Id == id))
                    {
                        var current = await editor.ReadAsync(root, id, cachedAccount, token);
                        if (ContentHash(current.Document, current.PhotoHash) == intended) outcome = Saved(current) with { Data = new JsonObject { ["id"] = id.ToString(), ["version"] = Version(current), ["outcome"] = "committed", ["reconciled"] = true } };
                    }
                    else if (kind == "Group" && catalog.Labels.ContainsKey(id))
                    {
                        var current = await ReadGroupAsync(root, id, token);
                        if (ContentHash(current.Document, null) == intended) outcome = Ok(new { id, version = GroupVersion(current, catalog), outcome = "committed", reconciled = true });
                    }
                }
                else if (kind == "Contact" && Optional(intent, "action") == "delete" && !catalog.Contacts.Any(c => c.Id == id) && Optional(intent, "backupId") is { } backup)
                {
                    var directory = Path.Combine(root, ".contactmirror", "editor-trash", Guid.Parse(backup[6..]).ToString("N")); var path = Path.Combine(directory, "contact.json");
                    if (File.Exists(path) && JsonSemantics.Hash(await FileWorkspaceStore.ReadBoundedBytesAsync(root, path, 16 * 1024 * 1024, token)) == Optional(intent, "expectedHash")) outcome = Ok(new { id, deleted = true, outcome = "committed", reconciled = true });
                }
            }
            if (outcome is null) return receipt;
            var completed = CompleteReceiptSafe(receipt.Key, outcome);
            return completed.Status == "unknown" ? receipt with { State = "unknown", Result = completed } : receipts.Value.Get(receipt.Key)!;
        }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException) { return receipt; }
    }
}
