using Avalonia;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ContactMirror.Application;
using ContactMirror.Application.Updates;
using ContactMirror.Infrastructure.Configuration;
using ContactMirror.Infrastructure.Updates;
using Velopack;
using Velopack.Locators;

namespace ContactMirror.Desktop;

public static class Program
{
    public static bool IsDemo { get; private set; }
    public static bool IsPackaged { get; private set; }
    public static bool WasRestarted { get; private set; }
    public static string StartupProblem { get; private set; } = "";
    public static bool CanRecoverUpdate { get; private set; }
    public static ApplicationActivity Activity { get; } = new();
    public static IApplicationUpdateService? Updates { get; private set; }
    public static bool RestartAfterRecovery { get; set; }
    private static string InstanceName(string owner) => "Local\\" + ApplicationPaths.AppId + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)));
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).OnRestarted(_ => WasRestarted = true).Run();
        if (args.SequenceEqual(new[] { "--inspect-package-profile" }))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { appId = ApplicationPaths.AppId, dataRoot = ApplicationPaths.DataDirectoryName,
                profile = ApplicationPaths.IsSyntheticValidation ? "Synthetic" : ApplicationPaths.IsValidation ? "Integration" : "Production" }));
            return;
        }
        IsDemo = ApplicationPaths.IsSyntheticValidation || args.Contains("--demo");
        var locator = VelopackLocator.Current;
        IsPackaged = locator.CurrentlyInstalledVersion is not null;
        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationPaths.AppId) };
        if (locator.RootAppDir is { } installRoot) roots.Add(installRoot);
        WorkspacePathPolicy.Current = new(roots);
        Mutex? instance = null;
        var ownsInstance = false;
        try
        {
            var owner = WindowsUpdateProcesses.CurrentOwner();
            if (IsPackaged)
            {
                if (locator.AppId != ApplicationPaths.AppId) StartupProblem = "Идентификатор установленного приложения не соответствует этой сборке. Повторите установку ContactMirror.";
                instance = new Mutex(false, InstanceName(owner));
                try { ownsInstance = instance.WaitOne(0); } catch (AbandonedMutexException) { ownsInstance = true; }
                if (!ownsInstance) StartupProblem = "ContactMirror уже открыт или обновляется. Вернитесь в первое окно и дождитесь завершения операции.";
                var handoff = new UpdateHandoff(ApplicationPaths.ApplyingPath);
                if (ownsInstance && handoff.IsPending)
                {
                    var nonceIndex = Array.IndexOf(args, "--resume-update");
                    var nonce = nonceIndex >= 0 && nonceIndex + 1 < args.Length ? args[nonceIndex + 1] : null;
                    var version = locator.CurrentlyInstalledVersion!.ToString();
                    var sourceIdentity = VelopackUpdateBackend.GetSourceIdentity(UpdateSourceOptions.Load());
                    if (!handoff.TryResume(ApplicationPaths.AppId, owner, sourceIdentity, version, UpdateHandoff.HashAssembly(Path.Combine(AppContext.BaseDirectory, "ContactMirror.dll")), nonce, WasRestarted))
                    {
                        StartupProblem = "Обновление ещё не подтверждено. Вход Google и синхронизация остановлены. Дождитесь завершения установки; если она прервалась, проверьте восстановление.";
                        CanRecoverUpdate = true;
                    }
                }
            }
            if (string.IsNullOrEmpty(StartupProblem)) Updates = new ApplicationUpdateService(new VelopackUpdateBackend(UpdateSourceOptions.Load(), owner, IsDemo), Activity);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { StartupProblem = "Не удалось проверить состояние установки. Вход Google и синхронизация не запущены. Повторите установку ContactMirror."; }
        // A blocked shortcut must not retain the instance gate against the SDK restart.
        if (ownsInstance && !string.IsNullOrEmpty(StartupProblem)) { instance!.ReleaseMutex(); ownsInstance = false; }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { if (ownsInstance) instance?.ReleaseMutex(); instance?.Dispose(); }
        if (RestartAfterRecovery)
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Arguments = IsDemo ? "--demo" : "" });
    }

    public static bool RecoverUpdate()
    {
        var locator = VelopackLocator.Current;
        if (!CanRecoverUpdate || locator.CurrentlyInstalledVersion is null || locator.UpdateExePath is null) return false;
        var owner = WindowsUpdateProcesses.CurrentOwner();
        using var gate = new Mutex(false, InstanceName(owner));
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return false;
        try
        {
            return new UpdateHandoff(ApplicationPaths.ApplyingPath).TryRecover(ApplicationPaths.AppId, owner, locator.CurrentlyInstalledVersion.ToString(),
                UpdateHandoff.HashAssembly(Path.Combine(AppContext.BaseDirectory, "ContactMirror.dll")), marker => WindowsUpdateProcesses.ProvenStopped(marker, locator.UpdateExePath));
        }
        finally { gate.ReleaseMutex(); }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().WithInterFont().LogToTrace();
}
