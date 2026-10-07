using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Google;

public static class GoogleErrors
{
    public static async Task<SyncException> ReadAsync(HttpResponseMessage response, bool tokenExchange, bool mutation, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[64 * 1024 + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (count == 0) break;
            length += count;
        }
        return Classify(response.StatusCode, length > 64 * 1024 ? null : Encoding.UTF8.GetString(bytes, 0, length), tokenExchange, mutation);
    }
    public static SyncException Classify(HttpStatusCode status, string? content, bool tokenExchange, bool mutation)
    {
        JsonNode? json = null;
        try { if (content is { Length: <= 65536 }) json = JsonNode.Parse(content); }
        catch (System.Text.Json.JsonException) { }
        if (json is not JsonObject) json = null;
        if (tokenExchange)
        {
            var reason = json?["error"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            return reason switch
            {
                "invalid_grant" => new("oauth-invalid-grant", "Google больше не подтверждает сохранённое разрешение. Войдите снова; в тестовой аудитории разрешение может истекать."),
                "invalid_client" => new("oauth-invalid-client", "Google не принимает конфигурацию приложения. Импортируйте корректный Desktop OAuth client или обратитесь к издателю сборки."),
                "access_denied" => new("oauth-denied", "Вход или доступ к контактам не разрешён. Повторите вход и подтвердите доступ; тестовый аккаунт должен входить в аудиторию проекта."),
                "unauthorized_client" => new("oauth-client-blocked", "Google заблокировал этот способ входа. Проверьте тип Desktop app и тестовую аудиторию проекта."),
                _ => new("oauth-token", "Google не подтвердил доступ. Проверьте сеть и подключите аккаунт заново.")
            };
        }
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (json?["error"] is JsonObject error)
        {
            foreach (var name in new[] { "details", "errors" })
                if (error[name] is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>())
                        if (item["reason"] is JsonValue reason && reason.TryGetValue<string>(out var parsed)) reasons.Add(parsed);
        }
        if (reasons.Contains("SERVICE_DISABLED") || reasons.Contains("accessNotConfigured"))
            return new("google-api-disabled", "People API не включён в проекте приложения. Издателю нужно включить Google People API; затем повторите проверку изменений.");
        if (reasons.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT") || reasons.Contains("insufficientPermissions"))
            return new("oauth-scope", "Google не предоставил доступ к контактам. Войдите снова и подтвердите разрешение на синхронизацию контактов.");
        if (status == HttpStatusCode.Unauthorized)
            return new("google-http-401", "Google больше не принимает сохранённый доступ. Войдите снова в настройках подключения.");
        if (status == HttpStatusCode.TooManyRequests || reasons.Overlaps(["rateLimitExceeded", "userRateLimitExceeded", "quotaExceeded", "RATE_LIMIT_EXCEEDED"]))
            return new("google-rate-limit", "Google ограничил частоту запросов. Подождите минуту и снова проверьте изменения. Повторный вход не требуется; уже сохранённые файлы останутся в папке.");
        if (status == HttpStatusCode.Forbidden)
            return new("google-http-403", "Google запретил доступ. Проверьте тестовую аудиторию приложения или ограничения аккаунта Workspace; затем повторите вход.");
        return new("google-http-" + (int)status, $"Google вернул HTTP {(int)status}. Проверьте доступ и повторно проверьте изменения.", mutation && (int)status >= 500);
    }
}
