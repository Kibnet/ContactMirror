using System.Security.Cryptography;
using System.Text;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Google;
using Xunit;
using Google.Apis.Auth;

namespace ContactMirror.GoogleTests;

public sealed class OAuthTests
{
    [Theory]
    [InlineData("https://accounts.google.com", "subject", "account@example.test", true, "expected", "expected", true)]
    [InlineData("accounts.google.com", "subject", "account@example.test", true, "expected", "expected", true)]
    [InlineData("https://evil.test", "subject", "account@example.test", true, "expected", "expected", false)]
    [InlineData("https://accounts.google.com", "", "account@example.test", true, "expected", "expected", false)]
    [InlineData("https://accounts.google.com", "subject", "", true, "expected", "expected", false)]
    [InlineData("https://accounts.google.com", "subject", "account@example.test", false, "expected", "expected", false)]
    [InlineData("https://accounts.google.com", "subject", "account@example.test", true, "expected", "wrong", false)]
    [InlineData("https://accounts.google.com", "subject", "account@example.test", true, "expected", null, false)]
    [InlineData("https://accounts.google.com", "subject", "account@example.test", true, null, null, true)]
    public void VerifiedIdTokenClaimsRequireGoogleIssuerSubVerifiedEmailAndLoginNonce(string issuer, string subject, string email, bool emailVerified, string? expectedNonce, string? signedNonce, bool allowed)
    {
        var payload = new GoogleJsonWebSignature.Payload { Issuer = issuer, Subject = subject, Email = email, EmailVerified = emailVerified };
        if (allowed)
        {
            var account = OAuthIdentityClaims.ValidateVerifiedPayload(payload, expectedNonce, signedNonce);
            Assert.Equal("https://accounts.google.com|subject", account.Key);
        }
        else Assert.Throws<SyncException>(() => OAuthIdentityClaims.ValidateVerifiedPayload(payload, expectedNonce, signedNonce));
    }

    [Fact]
    public void PkceUsesStandardS256AndUnpredictableOneTimeValues()
    {
        var first = OAuthRequest.Create();
        var second = OAuthRequest.Create();
        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.Equal(43, first.Verifier.Length);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(first.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, first.Challenge);
        var url = Uri.UnescapeDataString(first.AuthorizationUrl("123.apps.googleusercontent.com", "http://127.0.0.1:10000/callback/"));
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("nonce=" + first.Nonce, url);
        Assert.Contains("scope=openid email https://www.googleapis.com/auth/contacts", url);
    }

    [Theory]
    [InlineData("state=wrong&code=abc", "oauth-state")]
    [InlineData("code=abc", "oauth-state")]
    [InlineData("state=expected&state=expected&code=abc", "oauth-callback")]
    [InlineData("state=expected&error=access_denied", "oauth-denied")]
    [InlineData("state=expected", "oauth-code")]
    public void CallbackRejectsSpoofedOrDeniedFlow(string query, string code)
    {
        var flow = new OAuthRequest("expected", "nonce", "verifier", "challenge");
        Assert.Equal(code, Assert.Throws<SyncException>(() => flow.ValidateCallback(new Uri("http://127.0.0.1:10000/callback/?" + query), "http://127.0.0.1:10000/callback/")).Code);
    }

    [Theory]
    [InlineData("http://localhost:10000/callback/?state=expected&code=abc")]
    [InlineData("http://127.0.0.1:10001/callback/?state=expected&code=abc")]
    [InlineData("http://127.0.0.1:10000/other/?state=expected&code=abc")]
    [InlineData("http://127.0.0.1:10000/callback/?state=expected&code=abc#x")]
    public void CallbackChecksBoundLoopbackOriginAndPath(string url)
    {
        var flow = new OAuthRequest("expected", "nonce", "verifier", "challenge");
        Assert.Throws<SyncException>(() => flow.ValidateCallback(new Uri(url), "http://127.0.0.1:10000/callback/"));
    }

    [Fact]
    public void CallbackDecodesCodeAfterStateCheck()
    {
        var flow = new OAuthRequest("expected", "nonce", "verifier", "challenge");
        Assert.Equal("abc/def+", flow.ValidateCallback(new Uri("http://127.0.0.1:10000/callback/?state=expected&code=abc%2Fdef%2B"), "http://127.0.0.1:10000/callback/"));
    }

    [Fact]
    public async Task MissingPublisherConfigExplainsBuildStateWithoutReadingCredentials()
    {
        var service = new GoogleAccountService(new());
        Assert.False(service.IsConfigured);
        Assert.Contains("сборка пока не настроена", service.ConfigurationHint);
        Assert.Null(await service.GetAccountAsync());
        Assert.Equal("oauth-not-configured", (await Assert.ThrowsAsync<SyncException>(() => service.SignInAsync())).Code);
    }

    [Fact]
    public void DeveloperGoogleDownloadedConfigIsSupportedAndNotPublisherReadiness()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, "{\"installed\":{\"client_id\":\"123.apps.googleusercontent.com\",\"client_secret\":\"synthetic-public-desktop-config\"}}");
            var options = OAuthClientOptions.Load(temp);
            Assert.True(options.IsConfigured);
            Assert.Equal("developer", options.ConfigurationSource);
            Assert.Contains("публичная проверка", new GoogleAccountService(options).ConfigurationHint);
        }
        finally { File.Delete(temp); }
    }
}
