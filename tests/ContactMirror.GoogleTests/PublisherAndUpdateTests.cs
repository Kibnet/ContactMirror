using System.Net;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Configuration;
using ContactMirror.Infrastructure.Google;
using ContactMirror.Infrastructure.Updates;
using Xunit;

namespace ContactMirror.GoogleTests;

public sealed class PublisherAndUpdateTests
{
    [Fact]
    public void CompiledValidationProfilesEnforcePublisherOnlyOrDisableOAuth()
    {
        using var files = new Files();
        files.Write("exe/appsettings/oauth-client.json", Client("publisher"));
        files.Write("managed.json", Client("managed"));
        var options = OAuthClientOptions.LoadForRuntime(false, files.Path("managed.json"), files.Path("exe"), files.Path("cwd"), "env.apps.googleusercontent.com");
        if (ApplicationPaths.IsSyntheticValidation) Assert.False(options.IsConfigured);
        else Assert.Equal(ApplicationPaths.IsValidation ? "publisher" : "managed", options.ConfigurationSource);
    }
    [Theory]
    [InlineData("win-x64", "win", "fixture", true)]
    [InlineData("win-arm64", "win", "fixture", false)]
    [InlineData("win-x64", "beta", "fixture", false)]
    [InlineData("win-x64", "win", "other", false)]
    public void PackageVerificationRejectsWrongRidChannelInnerIdentityAndCorruption(string rid, string channel, string id, bool valid)
    {
        using var files = new Files(); var path = files.Path("fixture.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("fixture.nuspec").Open()))
                writer.Write($"<package><metadata><id>{id}</id><version>0.2.1</version><rid>{rid}</rid><os>win</os><channel>{channel}</channel><mainExe>ContactMirror.exe</mainExe><machineArchitecture>x64</machineArchitecture></metadata></package>");
            using var assembly = new StreamWriter(zip.CreateEntry("lib/app/ContactMirror.dll").Open()); assembly.Write("synthetic assembly");
        }
        var asset = new Velopack.VelopackAsset { PackageId = "fixture", Version = Velopack.SemanticVersion.Parse("0.2.1"), Type = Velopack.VelopackAssetType.Full, FileName = "fixture-full.nupkg", Size = new FileInfo(path).Length, SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) };
        if (valid) Assert.Equal(64, VelopackUpdateBackend.VerifyPackageFile(path, asset, "fixture").Length);
        else Assert.Throws<InvalidDataException>(() => VelopackUpdateBackend.VerifyPackageFile(path, asset, "fixture"));
        File.AppendAllText(path, "corrupt");
        Assert.Throws<InvalidDataException>(() => VelopackUpdateBackend.VerifyPackageFile(path, asset, "fixture"));
    }
    [Fact]
    public void ProductionPublisherCannotBeShadowedByEnvironmentOrWorkingDirectory()
    {
        using var files = new Files();
        files.Write("exe/appsettings/oauth-client.json", Client("publisher"));
        files.Write("exe/appsettings/oauth-client.local.json", Client("exe-local"));
        files.Write("cwd/appsettings/oauth-client.local.json", Client("cwd-local"));
        var options = OAuthClientOptions.LoadForRuntime(true, null, files.Path("exe"), files.Path("cwd"), "env.apps.googleusercontent.com", "other-source-secret");
        Assert.Equal("publisher.apps.googleusercontent.com", options.ClientId);
        Assert.Null(options.ClientSecret);
        Assert.Equal("publisher", options.ConfigurationSource);
        var raw = OAuthClientOptions.LoadForRuntime(false, null, files.Path("exe"), files.Path("cwd"), "env.apps.googleusercontent.com");
        Assert.Equal("env.apps.googleusercontent.com", raw.ClientId);
    }
    [Fact]
    public void MissingOrMalformedExplicitOverrideNeverFallsBackSilently()
    {
        using var files = new Files(); files.Write("exe/appsettings/oauth-client.json", Client("publisher"));
        foreach (var content in new[] { "{broken", "{\"installed\":{\"client_id\":\"invalid\"}}", "{\"web\":{\"client_id\":\"web.apps.googleusercontent.com\"}}" })
        {
            files.Write("managed.json", content);
            var options = OAuthClientOptions.LoadForRuntime(true, files.Path("managed.json"), files.Path("exe"), files.Path("cwd"));
            Assert.False(options.IsConfigured);
            Assert.Equal("managed", options.ConfigurationSource);
            Assert.NotNull(options.ConfigurationProblem);
        }
        File.Delete(files.Path("managed.json"));
        Assert.False(OAuthClientOptions.LoadForRuntime(true, files.Path("managed.json"), files.Path("exe"), files.Path("cwd")).IsConfigured);
    }
    [Fact]
    public void WorkspaceInsideInstallRootIsRejectedIncludingDirectoryAlias()
    {
        using var files = new Files(); Directory.CreateDirectory(files.Path("installed/current"));
        var policy = new WorkspacePathPolicy([files.Path("installed")]);
        Assert.Throws<SyncException>(() => policy.Validate(files.Path("installed/current/contacts")));
        policy.Validate(files.Path("installed-other/contacts"));
        var link = files.Path("alias");
        Directory.CreateSymbolicLink(link, files.Path("installed"));
        Assert.Throws<SyncException>(() => policy.Validate(Path.Combine(link, "current", "contacts")));
        Directory.Delete(link);
    }
    [Fact]
    public void RestartFenceRejectsShortcutWrongNonceOldVersionAndChangedSource()
    {
        using var files = new Files(); var handoff = new UpdateHandoff(files.Path("applying.json"));
        var marker = handoff.Prepare("fixture", "owner", "source", "0.2.1", new('B', 64), "0.2.0", new('A', 64));
        Assert.False(handoff.TryResume("fixture", "owner", "source", "0.2.1", new('B', 64), marker.Nonce, false));
        Assert.False(handoff.TryResume("fixture", "owner", "source", "0.2.0", new('A', 64), marker.Nonce, true));
        Assert.False(handoff.TryResume("fixture", "owner", "source", "0.2.1", new('B', 64), "wrong", true));
        Assert.False(handoff.TryResume("fixture", "owner", "other-source", "0.2.1", new('B', 64), marker.Nonce, true));
        Assert.True(handoff.IsPending);
        Assert.True(handoff.TryResume("fixture", "owner", "source", "0.2.1", new('B', 64), marker.Nonce, true));
        Assert.False(handoff.IsPending);
    }
    [Fact]
    public void CrashRecoveryRequiresStoppedProcessesAndMatchingCurrentBinary()
    {
        using var files = new Files(); var handoff = new UpdateHandoff(files.Path("applying.json"));
        handoff.Prepare("fixture", "owner", "source", "0.2.1", new('B', 64), "0.2.0", new('A', 64));
        Assert.False(handoff.TryRecover("fixture", "owner", "0.2.0", new('A', 64), _ => false));
        Assert.False(handoff.TryRecover("fixture", "owner", "0.2.0", new('C', 64), _ => true));
        Assert.True(handoff.TryRecover("fixture", "owner", "0.2.0", new('A', 64), _ => true));
    }
    [Theory]
    [InlineData("github", "https://github.com/owner/repo", "win", false, true)]
    [InlineData("https", "http://example.test/updates", "win", false, false)]
    [InlineData("https", "https://secret@example.test/updates", "win", false, false)]
    [InlineData("https", "https://example.test/updates?token=private", "win", false, false)]
    [InlineData("https", "https://example.test/updates", "linux", false, false)]
    [InlineData("file", "C:/fixtures/feed", "win", false, false)]
    [InlineData("file", "C:/fixtures/feed", "win", true, true)]
    public void UpdateSourceRejectsWrongChannelAndCredentials(string kind, string url, string channel, bool allowLocal, bool expected)
        => Assert.Equal(expected, UpdateSourceOptions.Validate(kind, url, channel, allowLocal));
    [Theory]
    [InlineData("{\"error\":\"invalid_grant\",\"error_description\":\"private-token\"}", true, "oauth-invalid-grant")]
    [InlineData("{\"error\":{\"details\":[{\"reason\":\"SERVICE_DISABLED\"}],\"message\":\"private-token\"}}", false, "google-api-disabled")]
    [InlineData("{\"error\":{\"details\":[{\"reason\":\"ACCESS_TOKEN_SCOPE_INSUFFICIENT\"}]}}", false, "oauth-scope")]
    public void GoogleErrorsExplainKnownFailureWithoutRawPrivatePayload(string json, bool exchange, string code)
    {
        var error = GoogleErrors.Classify(HttpStatusCode.Forbidden, json, exchange, false);
        Assert.Equal(code, error.Code); Assert.DoesNotContain("private-token", error.Message);
    }
    [Fact]
    public async Task EncryptedVaultSurvivesNewServiceInstanceInDisposableDirectory()
    {
        using var files = new Files(); var path = files.Path("Credentials/google.dpapi");
        await new WindowsCredentialVault(path).SaveAsync("synthetic-credential-данные");
        Assert.DoesNotContain("synthetic", File.ReadAllText(path));
        Assert.Equal("synthetic-credential-данные", await new WindowsCredentialVault(path).ReadAsync());
    }
    private static string Client(string id) => "{\"installed\":{\"client_id\":\"" + id + ".apps.googleusercontent.com\"}}";
    private sealed class Files : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ContactMirror-updates-" + Guid.NewGuid().ToString("N"));
        public string Path(string relative) => System.IO.Path.Combine(root, relative);
        public void Write(string relative, string content) { var path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
