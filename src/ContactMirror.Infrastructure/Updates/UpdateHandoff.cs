using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Infrastructure.Updates;

public sealed record UpdateApplyMarker(string AppId, string Owner, string SourceIdentity, string Version, string AssemblyHash, string PreviousVersion, string PreviousAssemblyHash, string Nonce, int PreviousPid, DateTimeOffset PreviousProcessStart, DateTimeOffset CreatedAt);

/// <summary>A durable fence survives the old process while Update.exe replaces current.</summary>
public sealed class UpdateHandoff(string path)
{
    public bool IsPending => File.Exists(path);
    public UpdateApplyMarker Prepare(string appId, string owner, string sourceIdentity, string targetVersion, string assemblyHash, string currentVersion, string currentAssemblyHash)
    {
        if (IsPending) throw new InvalidOperationException("Предыдущее обновление ещё не подтверждено.");
        using var process = Process.GetCurrentProcess();
        var marker = new UpdateApplyMarker(appId, owner, sourceIdentity, targetVersion, assemblyHash, currentVersion, currentAssemblyHash,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), process.Id, process.StartTime.ToUniversalTime(), DateTimeOffset.UtcNow);
        ApplicationPaths.AtomicWrite(path, JsonSerializer.Serialize(marker));
        return marker;
    }
    public UpdateApplyMarker Read()
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new IOException("Некорректное состояние обновления.");
        var marker = JsonSerializer.Deserialize<UpdateApplyMarker>(File.ReadAllText(path));
        if (marker is null || string.IsNullOrWhiteSpace(marker.AppId) || string.IsNullOrWhiteSpace(marker.Owner) || string.IsNullOrWhiteSpace(marker.SourceIdentity) || string.IsNullOrWhiteSpace(marker.Version)
            || marker.AssemblyHash?.Length != 64 || marker.PreviousAssemblyHash?.Length != 64 || marker.Nonce?.Length != 64 || !marker.Nonce.All(Uri.IsHexDigit) || marker.PreviousPid <= 0)
            throw new IOException("Некорректное состояние обновления.");
        return marker;
    }
    public bool TryResume(string appId, string owner, string sourceIdentity, string installedVersion, string assemblyHash, string? nonce, bool restartedByVelopack)
    {
        if (!IsPending) return true;
        var marker = Read();
        if (!restartedByVelopack || nonce is null || marker.AppId != appId || marker.Owner != owner || marker.SourceIdentity != sourceIdentity || marker.Version != installedVersion || marker.AssemblyHash != assemblyHash || !FixedEquals(marker.Nonce, nonce)) return false;
        File.Delete(path);
        return true;
    }
    public bool TryRecover(string appId, string owner, string version, string assemblyHash, Func<UpdateApplyMarker, bool> updaterAndPreviousProcessProvenStopped)
    {
        if (!IsPending) return true;
        var marker = Read();
        if (marker.AppId != appId || marker.Owner != owner || !updaterAndPreviousProcessProvenStopped(marker)) return false;
        if (!(marker.Version == version && marker.AssemblyHash == assemblyHash) && !(marker.PreviousVersion == version && marker.PreviousAssemblyHash == assemblyHash)) return false;
        File.Delete(path);
        return true;
    }
    public static string HashAssembly(string assemblyPath)
    {
        using var file = File.OpenRead(assemblyPath);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static bool FixedEquals(string expected, string value)
    {
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(value);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
