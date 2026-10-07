using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ContactMirror.Infrastructure;

/// <summary>A Windows RWH lease detects ordinary and mapped writers; cached bytes use its own handle.</summary>
internal sealed class FileReadCacheLease : IDisposable
{
    private readonly SafeFileHandle file;
    private readonly string path;
    private readonly EventWaitHandle completed = new(false, EventResetMode.ManualReset);
    private readonly IntPtr overlap = Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
    private readonly IntPtr input = Marshal.AllocHGlobal(12);
    private readonly IntPtr output = Marshal.AllocHGlobal(24);
    private bool pending;
    private bool disposed;
    private readonly object gate = new();
    private RegisteredWaitHandle? breakWatch;
    private bool changed;
    private FileReadCacheLease(string path)
    {
        this.path = path;
        file = CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        try
        {
            if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(new byte[24], 0, output, 24);
            Marshal.StructureToPtr(new Overlap { Event = new IntPtr(completed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1) }, overlap, false);
            Marshal.WriteInt16(input, 0, 1); Marshal.WriteInt16(input, 2, 12);
            Marshal.WriteInt32(input, 4, 1 | 2 | 4); // Read/write/handle caching detects writable mappings and acknowledges sharing conflicts.
            Marshal.WriteInt32(input, 8, 1); // REQUEST_OPLOCK_INPUT_FLAG_REQUEST.
            var error = KernelNotificationThread.Invoke(() => DeviceIoControl(file, 0x00090240, input, 12, output, 24, out _, overlap) ? 0 : Marshal.GetLastWin32Error());
            if (error != 997) throw new Win32Exception(error);
            pending = true;
            breakWatch = ThreadPool.RegisterWaitForSingleObject(completed, (_, _) =>
            {
                lock (gate)
                {
                    if (disposed) return;
                    changed = true; pending = false;
                    file.Dispose(); // Acknowledge a sharing-conflict break before the editor opens.
                }
            }, null, Timeout.Infinite, true);
        }
        catch { Dispose(); throw; }
    }
    public static FileReadCacheLease? TryCreate(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return new(path); }
        catch (Win32Exception) { return null; } // Unsupported filesystem / existing writer: reread, never cache.
    }
    public bool HasChanged()
    {
        lock (gate)
        {
            if (changed || !pending) return true;
            if (!GetOverlappedResult(file, overlap, out _, false) && Marshal.GetLastWin32Error() == 996) return false;
            changed = true; pending = false; file.Dispose();
            return true;
        }
    }
    public bool MatchesPath(string path) { lock (gate) return !changed && !file.IsClosed && NativeFileIdentity.Matches(file, path); }
    internal string BreakDiagnostic() { lock (gate) return $"status={Marshal.ReadInt64(overlap):X},old={Marshal.ReadInt32(output, 4)},new={Marshal.ReadInt32(output, 8)},flags={Marshal.ReadInt32(output, 12)},access={Marshal.ReadInt32(output, 16):X},share={Marshal.ReadInt32(output, 20):X}"; }
    public async Task<byte[]> ReadBytesAsync(int maxBytes, CancellationToken token)
    {
        var cached = await Task.Run(() =>
        {
            lock (gate)
            {
                if (changed || file.IsClosed) return null;
                using var readEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
                var readOverlap = Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
                var buffer = Marshal.AllocHGlobal(64 * 1024);
                try
                {
                    using var memory = new MemoryStream(); var managed = new byte[64 * 1024];
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        Marshal.StructureToPtr(new Overlap { Offset = (uint)memory.Length, Event = new IntPtr(readEvent.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1) }, readOverlap, false);
                        using var cancel = token.Register(() => CancelIoEx(file, readOverlap));
                        token.ThrowIfCancellationRequested();
                        var started = ReadFile(file, buffer, 64 * 1024, IntPtr.Zero, readOverlap);
                        var error = started ? 0 : Marshal.GetLastWin32Error();
                        if (error == 38) break;
                        if (!started && error != 997) throw new IOException("Не удалось прочитать файл.", new Win32Exception(error));
                        if (token.IsCancellationRequested) CancelIoEx(file, readOverlap);
                        if (!GetOverlappedResult(file, readOverlap, out var bytes, true))
                        {
                            error = Marshal.GetLastWin32Error(); if (error == 38) break;
                            token.ThrowIfCancellationRequested();
                            throw new IOException("Не удалось завершить чтение файла.", new Win32Exception(error));
                        }
                        if (bytes == 0) break;
                        if (memory.Length + bytes > maxBytes) throw TooLarge();
                        Marshal.Copy(buffer, managed, 0, (int)bytes); memory.Write(managed, 0, (int)bytes);
                    }
                    token.ThrowIfCancellationRequested(); return memory.ToArray();
                }
                finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(readOverlap); }
            }
        }, token);
        if (cached is not null) return cached;
        // A break before reading has already invalidated the index. Caller performs its guarded reread.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > maxBytes) throw TooLarge();
        using var fallback = new MemoryStream((int)stream.Length);
        var chunk = new byte[64 * 1024]; int read;
        while ((read = await stream.ReadAsync(chunk, token)) != 0)
        { if (fallback.Length + read > maxBytes) throw TooLarge(); fallback.Write(chunk, 0, read); }
        return fallback.ToArray();
    }
    private static ContactMirror.Core.SyncException TooLarge() => new("fileTooLarge", "Файл превышает допустимый размер; чтение и синхронизация остановлены.");
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            breakWatch?.Unregister(null);
            if (pending && !file.IsInvalid && !file.IsClosed)
            {
                CancelIoEx(file, overlap);
                GetOverlappedResult(file, overlap, out _, true);
            }
            file.Dispose(); completed.Dispose(); Marshal.FreeHGlobal(overlap); Marshal.FreeHGlobal(input); Marshal.FreeHGlobal(output);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Overlap { public IntPtr Internal; public IntPtr InternalHigh; public uint Offset; public uint OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytes, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlap, out uint bytes, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint count, IntPtr bytes, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlap);
}

internal static class NativeFileIdentity
{
    public static bool Matches(SafeFileHandle handle, string path)
    {
        using var current = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        return !current.IsInvalid && MatchesHandles(handle, current);
    }
    public static bool MatchesHandles(SafeFileHandle expectedHandle, SafeFileHandle actualHandle) => GetFileInformationByHandleEx(expectedHandle, 18, out var expected, 24)
        && GetFileInformationByHandleEx(actualHandle, 18, out var actual, 24) && expected == actual;
    [StructLayout(LayoutKind.Sequential)] private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity info, uint size);
}
