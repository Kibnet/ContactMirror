using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public sealed record AccountIdentity(string Subject, string Email, string Issuer = "https://accounts.google.com")
{
    public string Key => $"{Issuer}|{Subject}";
}

public sealed record RemoteSnapshot(IReadOnlyList<JsonObject> People, IReadOnlyList<JsonObject> Groups);
public sealed record SyncProgress(string Message, int Completed = 0, int Total = 0);
public enum ChangeKind { Download, Upload, CreateRemote, CreateLocal, DeleteRemote, DeleteLocal, Conflict, Blocked, Reconcile, Snapshot }
public enum EntityKind { Contact, Group }
public enum Resolution { Automatic, UseLocal, UseGoogle, Skip }

public sealed class SyncEntry
{
    public required string Key { get; init; }
    public required Guid EntityId { get; init; }
    public EntityKind Entity { get; init; }
    public required string Name { get; init; }
    public required string Field { get; init; }
    public required ChangeKind Kind { get; init; }
    public JsonNode? Before { get; init; }
    public JsonNode? Local { get; init; }
    public JsonNode? Google { get; init; }
    public string? Explanation { get; init; }
    public bool IsDestructive => Kind is ChangeKind.DeleteLocal or ChangeKind.DeleteRemote;
    public bool DeletesRemote(Resolution resolution) => Kind == ChangeKind.DeleteRemote || Kind == ChangeKind.Conflict && Field == "$entity" && Local is null && resolution == Resolution.UseLocal;
    public bool DeletesLocal(Resolution resolution) => Kind == ChangeKind.DeleteLocal || Kind == ChangeKind.Conflict && Field == "$entity" && Google is null && resolution == Resolution.UseGoogle;
    public bool IsDestructiveFor(Resolution resolution) => DeletesRemote(resolution) || DeletesLocal(resolution);
    public bool IsSelectable => Kind != ChangeKind.Blocked;
    public bool DefaultSelected => Kind is not (ChangeKind.Conflict or ChangeKind.Blocked or ChangeKind.DeleteLocal or ChangeKind.DeleteRemote);
}

public sealed record PlanChoice(string Key, Resolution Resolution = Resolution.Automatic);
public sealed class SyncPreview
{
    public Guid PlanId { get; init; } = Guid.NewGuid();
    public required string Root { get; init; }
    public required AccountIdentity Account { get; init; }
    public DateTimeOffset PreparedAt { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<SyncEntry> Entries { get; init; }
    public IReadOnlyList<string> Notices { get; init; } = [];
    public int ContactCount { get; init; }
    public int GroupCount { get; init; }
    public bool IsRecovery { get; init; }
    public bool RequiresDeletionConfirmation(int selectedDeletes) => selectedDeletes >= 10 || (ContactCount > 0 && selectedDeletes >= ContactCount * .2);
}

public sealed record OperationResult(string Key, string Status, string Message);
public sealed record SyncRunResult(Guid RunId, int Confirmed, int Failed, int Unknown, IReadOnlyList<OperationResult> Operations)
{
    public bool IsComplete => Failed == 0 && Unknown == 0;
}
public sealed record RunSummary(Guid Id, DateTimeOffset StartedAt, string Status, int Confirmed, int Failed, int Unknown, long BackupBytes = 0, bool BackupAvailable = true);

public sealed class ContactDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public JsonObject Data { get; set; } = new();
    public List<Guid> Labels { get; set; } = [];
    public bool Starred { get; set; }
    public string? Photo { get; set; }
    public JsonObject Extensions { get; set; } = new();
    public JsonObject? Google { get; set; }
}

public sealed class GroupDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public JsonArray ClientData { get; set; } = [];
    public JsonObject Extensions { get; set; } = new();
    public JsonObject? Google { get; set; }
}

public sealed class SyncException(string code, string message, bool ambiguous = false, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool IsAmbiguous { get; } = ambiguous;
}
