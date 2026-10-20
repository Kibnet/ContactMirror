using System.Security.Cryptography;
using System.Text;
using ContactMirror.Infrastructure.Configuration;
using ContactMirror.Infrastructure.Updates;

namespace ContactMirror.Infrastructure.Automation;

/// <summary>Synchronous startup/restart fence. Hold only while checking bridges and writing the update marker.</summary>
public static class AgentUpdateGate
{
    public static IDisposable Enter()
    {
        var mutex = new Mutex(false, "Local\\" + ApplicationPaths.AppId + ".AgentUpdate." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsUpdateProcesses.CurrentOwner()))));
        try { try { mutex.WaitOne(); } catch (AbandonedMutexException) { } return new Lease(mutex); }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class Lease(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
}
