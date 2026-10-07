using System.Security.Cryptography;
using System.Text;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Google;

public sealed record OAuthRequest(string State, string Nonce, string Verifier, string Challenge)
{
    public static OAuthRequest Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new(Base64Url(RandomNumberGenerator.GetBytes(32)), Base64Url(RandomNumberGenerator.GetBytes(32)), verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public string AuthorizationUrl(string clientId, string redirectUri)
        => "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code", ["scope"] = "openid email https://www.googleapis.com/auth/contacts",
            ["state"] = State, ["nonce"] = Nonce, ["code_challenge"] = Challenge, ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent select_account"
        }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public string ValidateCallback(Uri uri, string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var expected) || uri.Scheme != expected.Scheme || uri.Host != expected.Host || uri.Port != expected.Port || uri.AbsolutePath != expected.AbsolutePath || uri.Fragment.Length != 0)
            throw new SyncException("oauth-callback", "Получен неверный адрес ответа Google.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!query.TryAdd(key, value)) throw new SyncException("oauth-callback", "В ответе Google есть повторяющиеся параметры.");
        }
        if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(State)))
            throw new SyncException("oauth-state", "Не удалось подтвердить ответ входа. Повторите подключение.");
        if (query.ContainsKey("error")) throw new SyncException("oauth-denied", "Подключение Google отменено или доступ не предоставлен.");
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new SyncException("oauth-code", "Google не вернул код подключения.");
        return code;
    }
}
