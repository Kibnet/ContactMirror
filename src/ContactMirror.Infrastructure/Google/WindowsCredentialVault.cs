using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Google;

/// <summary>Only encrypted credentials are stored here, outside every contact workspace.</summary>
public sealed class WindowsCredentialVault
{
    private readonly string path;
    public WindowsCredentialVault(string? path = null) => this.path = path ?? ApplicationPaths.CredentialPath;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ContactMirror.Credentials.v1");

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWindows) throw Unsupported();
        if (!File.Exists(path)) return null;
        var cipher = await File.ReadAllBytesAsync(path, cancellationToken);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
    }

    public async Task SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!IsWindows) throw Unsupported();
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        directory.Create();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Не определён пользователь Windows.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, encrypted, cancellationToken); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWindows) throw Unsupported();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool IsWindows => OperatingSystem.IsWindows();
    private static PlatformNotSupportedException Unsupported() => new("Хранилище credentials этой сборки требует Windows.");
}
