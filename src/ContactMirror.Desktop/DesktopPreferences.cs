using System.Text.Json;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Desktop;

public sealed record DesktopPreferences(string Folder = "", string OAuthPath = "")
{
    private static string GetPath(bool demo) => System.IO.Path.Combine(ApplicationPaths.DataRoot, demo ? "desktop-demo.json" : "desktop.json");
    public static DesktopPreferences Load(bool demo = false)
    {
        var path = GetPath(demo);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(bool demo = false)
    {
        var path = GetPath(demo);
        ApplicationPaths.AtomicWrite(path, JsonSerializer.Serialize(this));
    }
}
