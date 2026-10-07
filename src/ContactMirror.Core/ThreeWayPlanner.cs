using System.Text.Json.Nodes;

namespace ContactMirror.Core;

public static class ThreeWayPlanner
{
    public static ChangeKind? Compare(JsonNode? baseline, JsonNode? local, JsonNode? remote)
    {
        if (JsonSemantics.Equal(local, remote)) return JsonSemantics.Equal(local, baseline) ? null : ChangeKind.Reconcile;
        if (JsonSemantics.Equal(local, baseline)) return ChangeKind.Download;
        if (JsonSemantics.Equal(remote, baseline)) return ChangeKind.Upload;
        return ChangeKind.Conflict;
    }
    public static SyncEntry? Field(Guid id, EntityKind entity, string name, string field, JsonNode? baseline, JsonNode? local, JsonNode? remote, string? block = null)
    {
        var kind = block is null ? Compare(baseline, local, remote) : ChangeKind.Blocked;
        return kind is null ? null : new() { Key = $"{entity}:{id}:{field}", EntityId = id, Entity = entity, Name = name, Field = field, Kind = kind.Value, Before = JsonSemantics.Clone(baseline), Local = JsonSemantics.Clone(local), Google = JsonSemantics.Clone(remote), Explanation = block };
    }
}
