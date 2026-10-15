using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application.Automation;
using ContactMirror.Core;
using Microsoft.Data.Sqlite;

namespace ContactMirror.Infrastructure.Automation;

/// <summary>Persist the key before issuing writes. Expired keys become tombstones, never new writes.</summary>
public sealed class AgentReceipts
{
    private readonly string path;
    private readonly Action? beforeComplete;
    public AgentReceipts(string directory, Action? beforeComplete = null)
    {
        this.beforeComplete = beforeComplete;
        Directory.CreateDirectory(directory); path = Path.Combine(directory, "receipts.db");
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "CREATE TABLE IF NOT EXISTS receipts(key TEXT PRIMARY KEY, hash TEXT NOT NULL, operation TEXT NOT NULL, created TEXT NOT NULL, state TEXT NOT NULL, result TEXT, before_account TEXT, after_account TEXT, workspace TEXT, intent TEXT); UPDATE receipts SET state='unknown' WHERE state='running';";
        c.ExecuteNonQuery();
        using var columns = db.CreateCommand(); columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('receipts') WHERE name='intent'";
        if (Convert.ToInt32(columns.ExecuteScalar()) == 0) { using var alter = db.CreateCommand(); alter.CommandText = "ALTER TABLE receipts ADD COLUMN intent TEXT"; alter.ExecuteNonQuery(); }
        using var expire = db.CreateCommand(); expire.CommandText = "UPDATE receipts SET state='expired',result=NULL,intent=NULL WHERE state='completed' AND julianday(created) < julianday('now','-30 days')"; expire.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
    }
    public sealed record Receipt(string Key, string Hash, string Operation, string State, AgentResult? Result, string? BeforeAccount, string? AfterAccount, string? Workspace, JsonObject? Intent);
    public Receipt? Get(string key)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT hash,operation,state,result,before_account,after_account,workspace,created,intent FROM receipts WHERE key=$key"; c.Parameters.AddWithValue("$key", key);
        using var r = c.ExecuteReader(); if (!r.Read()) return null;
        var expired = r.GetString(2) is "completed" or "expired" && DateTimeOffset.Parse(r.GetString(7)) < DateTimeOffset.UtcNow.AddDays(-30);
        return new(key, r.GetString(0), r.GetString(1), expired ? "expired" : r.GetString(2), expired || r.IsDBNull(3) ? null : JsonSerializer.Deserialize<AgentResult>(r.GetString(3), JsonSemantics.Options),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(8) ? null : JsonNode.Parse(r.GetString(8))!.AsObject());
    }
    public void Begin(string key, string hash, string operation, string? account, string? workspace, JsonObject intent)
    {
        if (new FileInfo(path).Length > 1024L * 1024 * 1024) throw new SyncException("receipt_store_full", "Хранилище результатов переполнено. Запись не начата.");
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO receipts(key,hash,operation,created,state,before_account,workspace,intent) VALUES($key,$hash,$op,$created,'running',$account,$workspace,$intent)";
        c.Parameters.AddWithValue("$key", key); c.Parameters.AddWithValue("$hash", hash); c.Parameters.AddWithValue("$op", operation);
        c.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); c.Parameters.AddWithValue("$account", (object?)account ?? DBNull.Value); c.Parameters.AddWithValue("$workspace", (object?)workspace ?? DBNull.Value); c.Parameters.AddWithValue("$intent", intent.ToJsonString()); c.ExecuteNonQuery();
    }
    public void Complete(string key, AgentResult result, string? account)
    {
        beforeComplete?.Invoke();
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "UPDATE receipts SET state=$state,result=$result,after_account=$account WHERE key=$key";
        c.Parameters.AddWithValue("$key", key); c.Parameters.AddWithValue("$state", result.Status == "unknown" ? "unknown" : "completed");
        c.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, JsonSemantics.Options)); c.Parameters.AddWithValue("$account", (object?)account ?? DBNull.Value); c.ExecuteNonQuery();
    }
    public bool HasUnknown(string root)
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM receipts WHERE workspace=$root AND state='unknown'"; c.Parameters.AddWithValue("$root", root); return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }
    public void AddIntent(string key, JsonObject additions)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var read = db.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT intent FROM receipts WHERE key=$key"; read.Parameters.AddWithValue("$key", key);
        var intent = JsonNode.Parse(Convert.ToString(read.ExecuteScalar()) ?? "{}")!.AsObject();
        foreach (var p in additions) intent[p.Key] = p.Value?.DeepClone();
        using var update = db.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE receipts SET intent=$intent WHERE key=$key";
        update.Parameters.AddWithValue("$intent", intent.ToJsonString()); update.Parameters.AddWithValue("$key", key); update.ExecuteNonQuery(); tx.Commit();
    }
    public bool BackupIsReferenced(string root, string backupId)
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM receipts WHERE workspace=$root AND state IN ('running','unknown') AND (json_extract(intent,'$.backupId')=$backup OR ('sync:'||json_extract(intent,'$.runId'))=$backup)";
        c.Parameters.AddWithValue("$root", root); c.Parameters.AddWithValue("$backup", backupId); return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }
}
