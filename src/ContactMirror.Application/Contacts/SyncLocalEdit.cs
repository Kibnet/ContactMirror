using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Application.Contacts;

// Returned values are presentation copies. Only the coordinator's registered token authorizes a write.
public sealed class SyncLocalEdit
{
    internal SyncLocalEdit() { }
    public Guid Token { get; } = Guid.NewGuid();
    public required Guid EntityId { get; init; }
    public required EntityKind Kind { get; init; }
    public required string Field { get; init; }
    public required string Root { get; init; }
    public required string Name { get; init; }
    public string? ResourceName { get; init; }
    public string? SourceId { get; init; }
    public bool LocalExists { get; init; }
    public required JsonObject Document { get; init; }
    public JsonObject? GoogleDocument { get; init; }
    public byte[]? PhotoBytes { get; init; }
    public byte[]? GooglePhotoBytes { get; init; }
    public required IReadOnlyDictionary<Guid, string> Labels { get; init; }
    public required IReadOnlyDictionary<Guid, string> MissingLabels { get; init; }
    public required IReadOnlyDictionary<Guid, string> LabelResources { get; init; }
    public DateTimeOffset PreparedAt { get; init; }
    public bool CanReset => GoogleDocument is not null;
    public bool CanDelete => GoogleDocument is null;
}

public sealed record SyncLocalEditResult(JsonObject Document, byte[]? PhotoBytes);
