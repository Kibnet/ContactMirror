using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ContactMirror.Infrastructure.Updates;

public static class WindowsUpdateProcesses
{
    public static string CurrentOwner()
    {
        if (!OperatingSystem.IsWindows()) return Environment.UserName;
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("Не определён пользователь Windows.");
    }
    public static bool ProvenStopped(UpdateApplyMarker marker, string updaterPath)
    {
        if (!OperatingSystem.IsWindows() || marker.Owner != CurrentOwner()) return false;
        try
        {
            try
            {
                using var previous = Process.GetProcessById(marker.PreviousPid);
                // A reused PID is deliberately not treated as proof of the previous process exiting.
                if (!previous.HasExited) return false;
            }
            catch (ArgumentException) { }
            foreach (var process in Process.GetProcessesByName("Update"))
            using (process)
            {
                if (process.HasExited) continue;
                if (!OpenProcessToken(process.Handle, 8, out var token)) return false;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                {
                    if (identity.User?.Value != marker.Owner) continue;
                    var executable = process.MainModule?.FileName;
                    if (executable is null || Path.GetFullPath(executable).Equals(Path.GetFullPath(updaterPath), StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        { return false; }
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle token);
}
