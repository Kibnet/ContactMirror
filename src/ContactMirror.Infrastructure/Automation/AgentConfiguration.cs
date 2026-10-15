using System.Text.Json;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Automation;

public sealed record AgentConfiguration(bool Enabled = false, bool LocalWrite = true, bool GoogleWrite = false,
    string[]? Roots = null, Guid Generation = default, int Version = 1)
{
    public string[] AllowedRoots => Roots ?? [];
    public static string PathName => Path.Combine(ApplicationPaths.DataRoot, "Automation", "configuration.json");
    public static AgentConfiguration Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            if (new FileInfo(PathName).Length > 64 * 1024) return new();
            var c = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(PathName), JsonSemantics.Options);
            return c is { Version: 1 } ? c : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public AgentConfiguration Save()
    {
        var roots = AllowedRoots.Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots) { WorkspacePathPolicy.Current.Validate(root); FileWorkspaceStore.ValidatePath(root, root); }
        var next = this with { Roots = roots, Generation = Guid.NewGuid() };
        ApplicationPaths.AtomicWrite(PathName, JsonSerializer.Serialize(next, JsonSemantics.Options)); return next;
    }
    public void Check(string root)
    {
        root = Canonical(root); WorkspacePathPolicy.Current.Validate(root); FileWorkspaceStore.ValidatePath(root, root);
        if (!AllowedRoots.Any(r => string.Equals(Canonical(r), root, StringComparison.OrdinalIgnoreCase)))
            throw new SyncException("workspace_not_allowed", "Папка не разрешена в настройках MCP.");
    }
    public static string Canonical(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}
