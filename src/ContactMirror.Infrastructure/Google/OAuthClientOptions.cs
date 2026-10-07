using System.Text.Json.Nodes;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Google;

public sealed record OAuthClientOptions(string ClientId = "", string? ClientSecret = null, string ConfigurationSource = "missing", string? ConfigurationProblem = null)
{
    public bool IsConfigured => ConfigurationProblem is null && ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) && ClientId.Length > ".apps.googleusercontent.com".Length && !ClientId.Any(char.IsWhiteSpace);
    public static OAuthClientOptions Load(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Файл OAuth-конфигурации слишком велик.");
        var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new System.Text.Json.JsonException("OAuth-конфигурация должна быть объектом JSON.");
        if (json?["web"] is not null) throw new SyncException("oauth-client-type", "Нужен OAuth-клиент типа Desktop app, а не Web application.");
        var options = json?["installed"] ?? json;
        return new(options?["client_id"]?.GetValue<string>() ?? options?["clientId"]?.GetValue<string>() ?? "", options?["client_secret"]?.GetValue<string>() ?? options?["clientSecret"]?.GetValue<string>(), "developer");
    }
    public static OAuthClientOptions Load() => LoadForRuntime(false, null, AppContext.BaseDirectory, Environment.CurrentDirectory,
        Environment.GetEnvironmentVariable("CONTACTMIRROR_GOOGLE_CLIENT_ID"), Environment.GetEnvironmentVariable("CONTACTMIRROR_GOOGLE_CLIENT_SECRET"));

    public static OAuthClientOptions LoadForRuntime(bool packaged, string? managedOverride, string executableDirectory, string currentDirectory, string? environmentId = null, string? environmentSecret = null)
    {
        if (ApplicationPaths.IsSyntheticValidation) return new(ConfigurationSource: "disabled", ConfigurationProblem: "В validation demo вход Google отключён.");
        if (ApplicationPaths.IsValidation) { packaged = true; managedOverride = null; }
        if (managedOverride is not null)
            return ReadSource(managedOverride, "managed");
        if (!packaged && environmentId is not null)
        {
            var options = new OAuthClientOptions(environmentId.Trim(), environmentSecret, "environment");
            return options.IsConfigured ? options : options with { ConfigurationProblem = "В переменной CONTACTMIRROR_GOOGLE_CLIENT_ID задан неверный Client ID Google." };
        }
        var sources = new List<(string Path, string Source)>();
        if (!packaged)
        {
            sources.Add((Path.Combine(executableDirectory, "appsettings", "oauth-client.local.json"), "developer"));
            sources.Add((Path.Combine(currentDirectory, "appsettings", "oauth-client.local.json"), "developer"));
        }
        sources.Add((Path.Combine(executableDirectory, "appsettings", "oauth-client.json"), "publisher"));
        foreach (var (path, source) in sources.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            if (File.Exists(path)) return ReadSource(path, source);
        return new();
    }

    private static OAuthClientOptions ReadSource(string path, string source)
    {
        try
        {
            var options = Load(path) with { ConfigurationSource = source };
            return options.IsConfigured ? options : options with { ConfigurationProblem = "OAuth-конфигурация содержит неверный Client ID. Импортируйте файл Desktop app или вернитесь к конфигурации издателя." };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or SyncException)
        { return new(ConfigurationSource: source, ConfigurationProblem: "OAuth-конфигурацию не удалось прочитать. Импортируйте файл Desktop app или вернитесь к конфигурации издателя."); }
    }

    public static string ImportManaged(string sourcePath)
    {
        var options = Load(sourcePath);
        if (!options.IsConfigured) throw new SyncException("oauth-client-invalid", "В файле нет корректного Desktop Client ID Google.");
        var json = new JsonObject { ["installed"] = new JsonObject { ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret } };
        ApplicationPaths.AtomicWrite(ApplicationPaths.ManagedOAuthPath, json.ToJsonString());
        return ApplicationPaths.ManagedOAuthPath;
    }
}
