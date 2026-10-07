using System.Text.Json;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Updates;

public sealed record UpdateSourceOptions(string Kind = "", string Url = "", string Channel = "win", string? Problem = null)
{
    public bool IsConfigured => Problem is null && Validate(Kind, Url, Channel, ApplicationPaths.IsValidation);
    public static bool Validate(string kind, string url, string channel, bool allowLocal)
    {
        if (channel != "win") return false;
        if (kind == "file") return allowLocal && Path.IsPathFullyQualified(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (kind == "github") return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Trim('/').Split('/').Length == 2;
        return kind == "https";
    }
    public static UpdateSourceOptions Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings", "updates.json");
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return new(Problem: "Конфигурация обновлений слишком велика.");
            return JsonSerializer.Deserialize<UpdateSourceOptions>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(Problem: "Конфигурацию обновлений не удалось прочитать."); }
    }
}
