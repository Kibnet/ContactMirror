using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using Google.Apis.Auth;

namespace ContactMirror.Infrastructure.Google;

public sealed class GoogleAccountService(OAuthClientOptions options, WindowsCredentialVault? vault = null) : IAccountConnector, IAccessTokenProvider
{
    private readonly WindowsCredentialVault vault = vault ?? new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private StoredCredentials? credentials;
    private bool loaded;
    public bool IsConfigured => options.IsConfigured;
    public string ConfigurationHint => options.ConfigurationProblem ?? (!IsConfigured
        ? "Эта сборка пока не настроена для входа Google. Для разработки укажите Desktop OAuth client в appsettings/oauth-client.local.json; публичному выпуску нужна конфигурация издателя."
        : options.ConfigurationSource switch
        {
            "publisher" => "Вход Google настроен издателем приложения.",
            "managed" => "Используется импортированная конфигурация Desktop OAuth. Она сохранена в папке пользователя. При смене клиента войдите снова.",
            "environment" => "Для разработки используется OAuth-клиент из переменных окружения. Доступ зависит от тестовой аудитории проекта Google; публичная проверка не подтверждена.",
            _ => "Для разработки используется локальный OAuth JSON. Доступ зависит от тестовой аудитории проекта Google; публичная проверка не подтверждена."
        });

    public async Task<AccountIdentity?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;
        await gate.WaitAsync(cancellationToken);
        try { await LoadAsync(cancellationToken); return credentials?.Account; }
        finally { gate.Release(); }
    }

    public async Task<AccountIdentity> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new SyncException("oauth-not-configured", ConfigurationHint);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var flow = OAuthRequest.Create();
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var redirect = $"http://127.0.0.1:{port}/callback/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var registration = timeout.Token.Register(listener.Close);
            Process.Start(new ProcessStartInfo(flow.AuthorizationUrl(options.ClientId, redirect)) { UseShellExecute = true });
            HttpListenerContext callback;
            try { callback = await listener.GetContextAsync().WaitAsync(timeout.Token); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                    throw new SyncException("oauth-timeout", "Время ожидания входа истекло. Нажмите «Подключить Google» снова и завершите вход в новой вкладке браузера; прежний callback уже недействителен.", false, ex);
                throw new OperationCanceledException("Вход отменён.", ex, cancellationToken);
            }
            string code;
            try
            {
                if (callback.Request.HttpMethod != "GET" || callback.Request.Url is null) throw new SyncException("oauth-callback", "Неверный ответ подключения.");
                code = flow.ValidateCallback(callback.Request.Url, redirect);
                await RespondAsync(callback.Response, "Ответ Google получен. Вернитесь в ContactMirror, чтобы проверить результат подключения.");
            }
            catch
            {
                await RespondAsync(callback.Response, "Подключение не подтверждено. Вернитесь в ContactMirror и повторите вход.");
                throw;
            }
            var token = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = flow.Verifier }, timeout.Token);
            var idToken = token["id_token"]?.GetValue<string>() ?? throw new SyncException("oauth-identity", "Google не вернул подтверждённую личность аккаунта.");
            var account = await ValidateIdentityAsync(idToken, flow.Nonce, timeout.Token);
            var refresh = token["refresh_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(refresh)) throw new SyncException("oauth-refresh", "Google не предоставил постоянный доступ. Повторите подключение с подтверждением разрешений.");
            ValidateScopes(token);
            var saved = new StoredCredentials(options.ClientId, account, refresh, AccessToken(token), Expires(token));
            await vault.SaveAsync(JsonSerializer.Serialize(saved), timeout.Token);
            credentials = saved;
            loaded = true;
            return account;
        }
        finally { gate.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new SyncException("oauth-not-configured", ConfigurationHint);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var current = credentials ?? throw new SyncException("oauth-sign-in", "Подключите аккаунт Google.");
            if (current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return current.AccessToken;
            var token = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken }, cancellationToken);
            ValidateScopes(token);
            if (token["id_token"] is JsonValue idToken)
            {
                var identity = await ValidateIdentityAsync(idToken.GetValue<string>(), null, cancellationToken);
                if (identity.Key != current.Account.Key) throw new SyncException("oauth-account-changed", "Google вернул другой аккаунт. Переподключите аккаунт перед синхронизацией.");
            }
            var updated = current with { AccessToken = AccessToken(token), ExpiresAt = Expires(token), RefreshToken = token["refresh_token"]?.GetValue<string>() ?? current.RefreshToken };
            await vault.SaveAsync(JsonSerializer.Serialize(updated), cancellationToken);
            credentials = updated;
            return updated.AccessToken;
        }
        finally { gate.Release(); }
    }

    public async Task SignOutAsync(bool revoke = false, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (revoke) await LoadAsync(cancellationToken);
            if (revoke && credentials is not null)
            {
                using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = credentials.RefreshToken }), cancellationToken);
                if (!response.IsSuccessStatusCode) throw new SyncException("oauth-revoke", $"Google не подтвердил отзыв доступа (HTTP {(int)response.StatusCode}).");
            }
            await vault.DeleteAsync(cancellationToken);
            credentials = null;
            loaded = true;
        }
        finally { gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (loaded) return;
        try
        {
            var json = await vault.ReadAsync(ct);
            if (json is not null)
            {
                var saved = JsonSerializer.Deserialize<StoredCredentials>(json);
                if (saved is not null && saved.ClientId == options.ClientId && !string.IsNullOrWhiteSpace(saved.Account.Subject) && saved.Account.Issuer == "https://accounts.google.com") credentials = saved;
            }
            loaded = true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { throw new SyncException("oauth-vault", "Не удалось прочитать защищённое подключение. Отключите аккаунт и подключите его заново.", false, ex); }
    }

    private async Task<JsonObject> ExchangeAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        values["client_id"] = options.ClientId;
        if (!string.IsNullOrWhiteSpace(options.ClientSecret)) values["client_secret"] = options.ClientSecret;
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), ct);
        if (!response.IsSuccessStatusCode) throw await GoogleErrors.ReadAsync(response, true, false, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new SyncException("oauth-token", "Не удалось прочитать ответ подключения Google.");
    }

    private async Task<AccountIdentity> ValidateIdentityAsync(string token, string? nonce, CancellationToken ct)
    {
        GoogleJsonWebSignature.Payload payload;
        try { payload = await GoogleJsonWebSignature.ValidateAsync(token, new GoogleJsonWebSignature.ValidationSettings { Audience = [options.ClientId] }).WaitAsync(ct); }
        catch (InvalidJwtException ex) { throw new SyncException("oauth-identity", "Не удалось подтвердить личность Google-аккаунта.", false, ex); }
        string? signedNonce = null;
        if (nonce is not null)
        {
            var encoded = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var signedClaims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            signedNonce = signedClaims?["nonce"]?.GetValue<string>();
        }
        return OAuthIdentityClaims.ValidateVerifiedPayload(payload, nonce, signedNonce);
    }
    private static void ValidateScopes(JsonObject token)
    {
        if (token["scope"] is JsonValue scope && !scope.GetValue<string>().Split(' ').Contains("https://www.googleapis.com/auth/contacts", StringComparer.Ordinal))
            throw new SyncException("oauth-scope", "Разрешение на синхронизацию контактов не предоставлено. Повторите подключение.");
    }
    private static string AccessToken(JsonObject token) => token["access_token"]?.GetValue<string>() is string value && value.Length > 0 ? value : throw new SyncException("oauth-token", "Google не вернул доступ к API.");
    private static DateTimeOffset Expires(JsonObject token) => DateTimeOffset.UtcNow.AddSeconds(token["expires_in"]?.GetValue<int>() ?? 3600);
    private static async Task RespondAsync(HttpListenerResponse response, string message)
    {
        var bytes = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>ContactMirror</title><body><h1>ContactMirror</h1><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
    private sealed record StoredCredentials(string ClientId, AccountIdentity Account, string RefreshToken, string AccessToken, DateTimeOffset ExpiresAt);
}
