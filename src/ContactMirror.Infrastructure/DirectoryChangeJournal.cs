using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContactMirror.Infrastructure;

/// <summary>Polls completed kernel notifications before trusting a session's file identity index.</summary>
internal sealed class DirectoryChangeJournal : IDisposable
{
    private const int Incomplete = 996;
    private readonly SafeFileHandle directory;
    private readonly EventWaitHandle completed = new(false, EventResetMode.ManualReset);
    private readonly IntPtr buffer = Marshal.AllocHGlobal(64 * 1024);
    private readonly IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
    private bool pending;
    private bool disposed;

    public DirectoryChangeJournal(string path)
    {
        directory = CreateFileW(path, 1, 7, IntPtr.Zero, 3, 0x42000000, IntPtr.Zero);
        try
        {
            if (directory.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Arm();
        }
        catch { Dispose(); throw; }
    }
    // null means lost notifications: the caller must rebuild the complete index.
    public IReadOnlyList<string>? Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!GetOverlappedResult(directory, overlapped, out var bytes, false))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == Incomplete) return [];
            pending = false;
            // ERROR_NOTIFY_ENUM_DIR also requires a complete rescan.
            if (error != 1022 && error != 995) throw new Win32Exception(error);
            Arm();
            return null;
        }
        pending = false;
        var changes = new List<string>();
        var overflow = bytes < 12;
        try
        {
            var offset = 0;
            while (!overflow && offset < bytes)
            {
                if (offset > bytes - 12) { overflow = true; break; }
                var next = Marshal.ReadInt32(buffer, offset);
                var nameBytes = Marshal.ReadInt32(buffer, offset + 8);
                if (nameBytes < 0 || nameBytes % 2 != 0 || nameBytes > bytes - offset - 12) { overflow = true; break; }
                changes.Add(Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 12), nameBytes / 2)!);
                if (next == 0) break;
                if (next < 12 || next > bytes - offset) { overflow = true; break; }
                offset += next;
            }
        }
        finally { Arm(); }
        return overflow ? null : changes;
    }
    public bool MatchesPath(string path) => NativeFileIdentity.Matches(directory, path);
    private void Arm()
    {
        completed.Reset();
        Marshal.StructureToPtr(new Overlap { Event = new IntPtr(completed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1) }, overlapped, false);
        var error = KernelNotificationThread.Invoke(() => ReadDirectoryChangesW(directory, buffer, 64 * 1024, true, 1 | 2 | 4 | 8 | 16 | 64, IntPtr.Zero, overlapped, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error());
        if (error != 0) throw new Win32Exception(error);
        pending = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (pending && !directory.IsInvalid)
        {
            CancelIoEx(directory, overlapped);
            // The buffer and OVERLAPPED must outlive cancellation completion.
            GetOverlappedResult(directory, overlapped, out _, true);
        }
        directory.Dispose();
        completed.Dispose();
        Marshal.FreeHGlobal(overlapped);
        Marshal.FreeHGlobal(buffer);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Overlap { public IntPtr Internal; public IntPtr InternalHigh; public uint Offset; public uint OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadDirectoryChangesW(SafeFileHandle handle, IntPtr buffer, uint size, [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter, IntPtr bytes, IntPtr overlap, IntPtr callback);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlap, out uint bytes, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlap);
}
