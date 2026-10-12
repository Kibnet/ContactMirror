using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ContactMirror.UiTests.FlaUI.Tests;

/// <summary>Records only the supplied HWND. No desktop pixels, foreground lookup or audio capture.</summary>
internal sealed class NativeWindowVideo : IAsyncDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    private sealed record Frame(string File, double Seconds);

    private readonly IntPtr window;
    private readonly uint processId;
    private readonly int fps;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch elapsed = new();
    private readonly List<Frame> frames = [];
    private readonly Task capture;
    private readonly object finishGate = new();
    private Task<string>? finishing;
    private double endSeconds;
    public string OutputPath { get; }
    public string FrameDirectory { get; }
    public string FirstFramePath => Path.Combine(FrameDirectory, "first-frame.png");
    public int Width { get; private set; }
    public int Height { get; private set; }

    private NativeWindowVideo(IntPtr window, string output, int fps)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || GetWindowThreadProcessId(window, out processId) == 0)
            throw new InvalidOperationException("The test HWND is unavailable for recording.");
        if (fps is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(fps));
        this.window = window; this.fps = fps;
        OutputPath = Path.GetFullPath(output);
        if (File.Exists(OutputPath)) throw new IOException("A native window video already exists at " + OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        FrameDirectory = Directory.CreateDirectory(OutputPath + ".frames-" + Guid.NewGuid().ToString("N")).FullName;
        capture = Task.Run(CaptureAsync);
    }

    public static async Task<NativeWindowVideo> StartAsync(IntPtr window, string output, int fps = 5, CancellationToken cancellationToken = default)
    {
        var recorder = new NativeWindowVideo(window, output, fps);
        try
        {
            await recorder.firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return recorder;
        }
        catch
        {
            try { await recorder.FinishAsync().ConfigureAwait(false); } catch { /* Keep the original startup failure and saved diagnostic frames. */ }
            throw;
        }
    }

    private async Task CaptureAsync()
    {
        elapsed.Start();
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var started = elapsed.Elapsed.TotalSeconds;
                var path = Path.Combine(FrameDirectory, $"frame-{frames.Count:D6}.jpg");
                CaptureFrame(path, frames.Count == 0);
                frames.Add(new(Path.GetFileName(path), started));
                if (frames.Count == 1) firstFrame.TrySetResult();
                // Do not burst to catch up after a slow PrintWindow call. Actual timestamps govern playback.
                var remaining = started + 1d / fps - elapsed.Elapsed.TotalSeconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) { firstFrame.TrySetException(error); throw; }
        finally
        {
            if (frames.Count == 0) firstFrame.TrySetCanceled();
        }
    }

    private void CaptureFrame(string path, bool first)
    {
        if (!IsWindow(window) || IsIconic(window) || GetWindowThreadProcessId(window, out var currentProcess) == 0 || currentProcess != processId)
            throw new InvalidOperationException("The exact test window was closed, replaced or minimized during recording.");
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException("Could not read physical test-window bounds.");
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) throw new InvalidOperationException("Invalid test-window recording dimensions.");
            if (first) { Width = width; Height = height; }
            else if (Width != width || Height != height) throw new InvalidOperationException("The test window changed recording dimensions; paired evidence requires constant geometry.");
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                var context = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(window, context, 2)) throw new InvalidOperationException("PrintWindow could not capture the exact test HWND.");
                }
                finally { graphics.ReleaseHdc(context); }
            }
            if (first) bitmap.Save(FirstFramePath, ImageFormat.Png);
            using var encoding = new EncoderParameters(1);
            encoding.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            bitmap.Save(path, ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid), encoding);
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    public Task<string> FinishAsync()
    {
        lock (finishGate) return finishing ??= FinishCoreAsync();
    }

    private async Task<string> FinishCoreAsync()
    {
        endSeconds = elapsed.Elapsed.TotalSeconds; stopping.Cancel();
        Exception? captureFailure = null;
        try
        {
            try { await capture.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException("PrintWindow did not finish within 20 seconds; saved frames are retained for diagnosis."); }
            catch (Exception error) { captureFailure = error; }
            elapsed.Stop();
            if (frames.Count == 0) throw new InvalidOperationException("No test-window frame was captured.", captureFailure);
            var duration = Math.Max(1d / fps, endSeconds - frames[0].Seconds);
            var manifest = new StringBuilder("ffconcat version 1.0\n");
            for (var index = 0; index < frames.Count; index++)
            {
                manifest.Append("file '").Append(frames[index].File).Append("'\n");
                var next = index + 1 < frames.Count ? frames[index + 1].Seconds : endSeconds;
                manifest.Append("duration ").Append(Math.Max(.001, next - frames[index].Seconds).ToString("0.#########", CultureInfo.InvariantCulture)).Append('\n');
            }
            // concat requires a repeated last image to honour its final duration.
            manifest.Append("file '").Append(frames[^1].File).Append("'\n");
            var concat = Path.Combine(FrameDirectory, "frames.ffconcat");
            await File.WriteAllTextAsync(concat, manifest.ToString(), new UTF8Encoding(false)).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(FrameDirectory, "capture.json"), JsonSerializer.Serialize(new
            {
                hwnd = "0x" + window.ToInt64().ToString("X", CultureInfo.InvariantCulture), processId, method = "PrintWindow(flags=2)",
                width = Width, height = Height, encodedWidth = Width + Width % 2, encodedHeight = Height + Height % 2,
                fps, elapsedSeconds = duration, audio = false, captureFailure = captureFailure?.ToString(), frames
            }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            await EncodeAsync(concat, duration).ConfigureAwait(false);
            if (captureFailure is not null) throw new InvalidOperationException("Native window recording failed after its first frame; the partial video and source frames are retained.", captureFailure);
            return OutputPath;
        }
        finally
        {
            if (capture.IsCompleted) stopping.Dispose();
            else _ = capture.ContinueWith(task => { _ = task.Exception; stopping.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task EncodeAsync(string concat, double duration)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-f", "concat", "-safe", "0", "-i", concat,
            "-an", "-vf", $"pad={Width + Width % 2}:{Height + Height % 2}:0:0:black,fps={fps}",
            "-t", duration.ToString("0.#########", CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "veryfast",
            "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", OutputPath
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native window video encoder did not start.");
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(stderr, stdout).ConfigureAwait(false); throw;
        }
        var error = await stderr.ConfigureAwait(false); var output = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(OutputPath) || new FileInfo(OutputPath).Length == 0)
            throw new InvalidOperationException("Native window video encoding failed: " + error + output);
    }

    public ValueTask DisposeAsync() => new(FinishAsync());
}
