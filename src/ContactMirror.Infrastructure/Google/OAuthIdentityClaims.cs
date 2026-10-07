using ContactMirror.Core;
using Google.Apis.Auth;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ContactMirror.GoogleTests")]

namespace ContactMirror.Infrastructure.Google;

internal static class OAuthIdentityClaims
{
    // Called only after the Google library has validated signature, audience and token lifetime.
    internal static AccountIdentity ValidateVerifiedPayload(GoogleJsonWebSignature.Payload payload, string? expectedNonce, string? signedNonce)
    {
        if (payload.Issuer is not ("accounts.google.com" or "https://accounts.google.com") || string.IsNullOrWhiteSpace(payload.Subject) || !payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Email))
            throw new SyncException("oauth-identity", "Google не вернул подтверждённую личность аккаунта.");
        if (expectedNonce is not null && signedNonce != expectedNonce)
            throw new SyncException("oauth-nonce", "Ответ Google не относится к этому подключению. Повторите вход.");
        return new(payload.Subject, payload.Email);
    }
}
