using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ContactMirror.UiTests.FlaUI.Tests;

internal static class NativeWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    public static void Maximize(IntPtr window) => ShowWindow(window, 3);
    public static void Save(IntPtr window, string path)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (!GetWindowRect(window, out var rect))
                throw new InvalidOperationException("Не удалось получить физические границы тестового окна.");
            var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var context = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(window, context, 2))
                        throw new InvalidOperationException("Не удалось захватить содержимое тестового окна.");
                }
                finally { graphics.ReleaseHdc(context); }
            }
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"Native window capture: DPI={GetDpiForWindow(window)}, size={bounds.Width}x{bounds.Height}");
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
}
