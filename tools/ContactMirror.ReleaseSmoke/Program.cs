using System.Diagnostics;
using System.Text.Json;
using AppAutomation.Abstractions;
using AppAutomation.FlaUI.Automation;
using AppAutomation.FlaUI.Session;
using AppAutomation.Session.Contracts;
using ContactMirror.UiTests.Authoring.Pages;
using ContactMirror.UiTests.FlaUI.Tests;
using FlaUI.UIA3;
using FlaUI.Core.AutomationElements;

if (args.Length < 3) throw new ArgumentException("Usage: ReleaseSmoke <validationInstallRoot> <inspect|begin-connect|status|upgrade-existing|export|check> <evidenceDirectory>");
var root = Path.GetFullPath(args[0]); var action = args[1]; var evidence = Path.GetFullPath(args[2]);
if (action is not ("inspect" or "begin-connect" or "status" or "upgrade-existing" or "export" or "check" or "reject-corrupt" or "upgrade-race" or "upgrade-busy" or "recover" or "close"))
    throw new ArgumentException("Unknown action. Human consent must use begin-connect, which leaves the application open.");
if (Path.GetFileName(root) is not ("ContactMirror.Validation" or "ContactMirror.IntegrationValidation"))
    throw new InvalidOperationException("Only isolated validation installs are allowed.");
if (action is "upgrade-race" or "reject-corrupt" && Path.GetFileName(root) != "ContactMirror.Validation") throw new InvalidOperationException("Fault injection is limited to Synthetic validation.");
Directory.CreateDirectory(evidence);
var executable = Path.Combine(root, "current", "ContactMirror.exe");
if (action is "begin-connect" or "status" or "upgrade-existing" or "export" or "check" or "reject-corrupt" or "upgrade-race" or "upgrade-busy" or "recover" or "close")
{
    var process = Process.GetProcessesByName("ContactMirror").FirstOrDefault(p => PathMatches(p, executable));
    if (process is null && action == "status") throw new InvalidOperationException("Installed application is not running.");
    process ??= Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = true })!;
    Console.WriteLine(JsonSerializer.Serialize(new { step = "attach", pid = process.Id, action }));
    using var application = FlaUI.Core.Application.Attach(process.Id);
    using var automation = new UIA3Automation();
    var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("No installed window.");
    Console.WriteLine(JsonSerializer.Serialize(new { step = "window", title = window.Title }));
    if (action == "close") { window.Close(); await WaitFor(() => process.HasExited, 30); return; }
    if (action == "recover")
    {
        var beforePid = process.Id;
        if (!window.Title.Contains("установка")) throw new InvalidOperationException("Recovery requires a guarded installation window.");
        var recover = window.FindFirstDescendant(cf => cf.ByName("Проверить восстановление"));
        if (recover?.IsEnabled != true) throw new InvalidOperationException("Recovery is unavailable.");
        recover.AsButton().Invoke();
        Process? recovered = null;
        await WaitFor(() => { recovered = Process.GetProcessesByName("ContactMirror").FirstOrDefault(p => p.Id != beforePid && PathMatches(p, executable) && p.MainWindowTitle == "ContactMirror"); return recovered is not null; }, 60);
        using var recoveredApplication = FlaUI.Core.Application.Attach(recovered!.Id);
        var recoveredWindow = recoveredApplication.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
        recoveredWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))!.AsButton().Invoke();
        Console.WriteLine(JsonSerializer.Serialize(new { action, oldPid = beforePid, newPid = recovered.Id, version = recoveredWindow.FindFirstDescendant(cf => cf.ByAutomationId("AppVersionText"))?.Name }));
        NativeWindowCapture.Save(recoveredWindow.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "recovered.png"));
        return;
    }
    if (action == "begin-connect")
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var button = window.FindFirstDescendant(cf => cf.ByAutomationId("ConnectGoogleButton"));
            if (button?.IsEnabled == true) { button.AsButton().Invoke(); break; }
            if (attempt == 99) throw new InvalidOperationException("Connect button is not available.");
            await Task.Delay(100);
        }
    }
    if (action is "upgrade-existing" or "upgrade-race" or "upgrade-busy" or "recover" or "close" or "reject-corrupt")
    {
        var settingsCheck = window.FindFirstDescendant(cf => cf.ByAutomationId("CheckUpdatesButton"));
        if (settingsCheck is null || settingsCheck.Properties.IsOffscreen.Value)
            window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))!.AsButton().Invoke();
        await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CheckUpdatesButton"))?.IsEnabled == true, 30);
        window.FindFirstDescendant(cf => cf.ByAutomationId("CheckUpdatesButton"))!.AsButton().Invoke();
        await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("DownloadUpdateButton"))?.IsEnabled == true, 45);
        window.FindFirstDescendant(cf => cf.ByAutomationId("DownloadUpdateButton"))!.AsButton().Invoke();
        await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("RestartUpdateButton"))?.IsEnabled == true
            || !string.IsNullOrEmpty(window.FindFirstDescendant(cf => cf.ByAutomationId("UpdateErrorText"))?.Name), 180);
        var updateError = window.FindFirstDescendant(cf => cf.ByAutomationId("UpdateErrorText"))?.Name;
        if (action == "reject-corrupt")
        {
            if (string.IsNullOrEmpty(updateError) || window.FindFirstDescendant(cf => cf.ByAutomationId("RestartUpdateButton"))?.IsEnabled == true)
                throw new InvalidOperationException("Corrupt package was not rejected.");
            NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "corrupt-package-rejected.png"));
            Console.WriteLine(JsonSerializer.Serialize(new { action, pid = process.Id, version = window.FindFirstDescendant(cf => cf.ByAutomationId("AppVersionText"))?.Name, updateError, restartEnabled = false }));
            return;
        }
        if (!string.IsNullOrEmpty(updateError)) throw new InvalidOperationException(updateError);
        if (action == "upgrade-busy")
        {
            window.FindFirstDescendant(cf => cf.ByAutomationId("CheckChangesButton"))!.AsButton().Invoke();
            await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CancelButton"))?.IsEnabled == true, 30);
            if (window.FindFirstDescendant(cf => cf.ByAutomationId("RestartUpdateButton"))?.IsEnabled != false) throw new InvalidOperationException("Restart remained enabled during sync.");
            NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "restart-blocked-during-sync.png"));
            await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CheckChangesButton"))?.IsEnabled == true, 600);
            await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("RestartUpdateButton"))?.IsEnabled == true, 30);
        }
        NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "ready-existing.png"));
        var previousPid = process.Id;
        var dataName = Path.GetFileName(root) == "ContactMirror.Validation" ? "ContactMirror.Validation.Data" : "ContactMirror.IntegrationValidation.Data";
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dataName);
        string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : null;
        var preferences = Path.Combine(dataRoot, Path.GetFileName(root) == "ContactMirror.Validation" ? "desktop-demo.json" : "desktop.json");
        var vault = Path.Combine(dataRoot, "Credentials", "google.dpapi");
        var preferencesHash = Hash(preferences); var vaultHash = Hash(vault);
        var workspace = File.Exists(preferences) ? JsonDocument.Parse(File.ReadAllText(preferences)).RootElement.GetProperty("Folder").GetString() : null;
        Dictionary<string,string> WorkspaceSnapshot() => string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace) ? new() : Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "workspace.lock" && !path.EndsWith("-shm") && !path.EndsWith("-wal"))
            .ToDictionary(path => Path.GetRelativePath(workspace, path), path => Hash(path)!);
        var workspaceBefore = WorkspaceSnapshot();
        await File.WriteAllTextAsync(Path.Combine(evidence, "workspace-before.json"), JsonSerializer.Serialize(workspaceBefore));
        await File.WriteAllTextAsync(Path.Combine(evidence, "preservation-before.json"), JsonSerializer.Serialize(new { preferencesHash, vaultHash }));
        Task<Process?>? racerTask = null;
        if (action == "upgrade-race")
        {
            var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactMirror.Validation.Data", "update-applying.json");
            racerTask = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(marker) && process.HasExited)
                        return Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = true });
                    await Task.Delay(1);
                }
                return null;
            });
        }
        window.FindFirstDescendant(cf => cf.ByAutomationId("RestartUpdateButton"))!.AsButton().Invoke();
        if (racerTask is not null)
        {
            using var racer = await racerTask ?? throw new InvalidOperationException("Did not observe the old-exit Applying gap.");
            using var racerApplication = FlaUI.Core.Application.Attach(racer.Id);
            var guarded = racerApplication.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("No guarded second-launch window.");
            if (!guarded.Title.Contains("установка") || guarded.FindFirstDescendant(cf => cf.ByAutomationId("CheckChangesButton")) is not null)
                throw new InvalidOperationException("Second launch entered the ordinary sync window.");
            NativeWindowCapture.Save(guarded.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "second-launch-guarded.png"));
            Console.WriteLine(JsonSerializer.Serialize(new { action = "second-launch-during-apply", pid = racer.Id, guarded = true, oldExited = true }));
            guarded.Close();
        }
        Process? resumed = null;
        await WaitFor(() =>
        {
            resumed = Process.GetProcessesByName("ContactMirror").FirstOrDefault(p => p.Id != previousPid && PathMatches(p, executable) && p.MainWindowHandle != IntPtr.Zero);
            return resumed is not null;
        }, 90);
        using var resumedApplication = FlaUI.Core.Application.Attach(resumed!.Id);
        var resumedWindow = resumedApplication.GetMainWindow(automation, TimeSpan.FromSeconds(30))!;
        await WaitFor(() => resumedWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))?.IsEnabled == true, 30);
        resumedWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))!.AsButton().Invoke();
        Console.WriteLine(JsonSerializer.Serialize(new { oldPid = previousPid, newPid = resumed.Id,
            version = resumedWindow.FindFirstDescendant(cf => cf.ByAutomationId("AppVersionText"))?.Name,
            connected = resumedWindow.FindFirstDescendant(cf => cf.ByAutomationId("AccountLabel"))?.Name.Contains('@') == true }));
        var samePreferences = preferencesHash == Hash(preferences); var sameVault = vaultHash == Hash(vault);
        var applyingMarkerExists = File.Exists(Path.Combine(dataRoot, "update-applying.json"));
        var workspaceAfter = WorkspaceSnapshot();
        var sameWorkspace = workspaceBefore.Count == workspaceAfter.Count && workspaceBefore.All(pair => workspaceAfter.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
        await File.WriteAllTextAsync(Path.Combine(evidence, "workspace-after.json"), JsonSerializer.Serialize(workspaceAfter));
        await File.WriteAllTextAsync(Path.Combine(evidence, "preservation-after.json"), JsonSerializer.Serialize(new { samePreferences, sameVault, sameWorkspace, workspaceFiles = workspaceAfter.Count, applyingMarkerExists }));
        if (!samePreferences || !sameVault || !sameWorkspace || applyingMarkerExists) throw new InvalidOperationException("Update persistence or handoff not confirmed.");
        NativeWindowCapture.Save(resumedWindow.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "after-upgrade-existing.png"));
        return;
    }
    if (action is "export" or "check")
    {
        await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CheckChangesButton"))?.IsEnabled == true, 30);
        window.FindFirstDescendant(cf => cf.ByAutomationId("CheckChangesButton"))!.AsButton().Invoke();
        Console.WriteLine("check invoked");
        await WaitFor(() => (window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))?.Name is string status && (status.Contains("Сравнение завершено") || status.Contains("Изменений нет")))
            || !string.IsNullOrEmpty(window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorText"))?.Name), 600);
        var error = window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorText"))?.Name;
        if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
        // Only the user's initial Google-to-folder export is allowed by this action.
        var summary = window.FindFirstDescendant(cf => cf.ByAutomationId("PlanSummaryText"))?.Name ?? "";
        if (action == "check")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { action, summary }));
            NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "manual-check.png"));
            return;
        }
        if (!summary.StartsWith("В Google: 0", StringComparison.Ordinal) || !summary.Contains("Конфликты: 0") || !summary.Contains("Удаления: 0"))
            throw new InvalidOperationException("Export requires a plan with zero Google writes, conflicts and deletions: " + summary);
        window.FindFirstDescendant(cf => cf.ByAutomationId("ApplyButton"))!.AsButton().Invoke();
        await Task.Delay(500);
        await WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CheckChangesButton"))?.IsEnabled == true, 900);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { pid = process.Id, status = window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))?.Name,
        connected = window.FindFirstDescendant(cf => cf.ByAutomationId("AccountLabel"))?.Name.Contains('@') == true,
        error = window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorText"))?.Name }));
    NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value, Path.Combine(evidence, action + ".png"));
    // Attaching disposes handles only. The user's ordinary application stays open.
    return;
}
using var session = DesktopAppSession.Launch(new DesktopAppLaunchOptions { ExecutablePath = executable, WorkingDirectory = Path.GetDirectoryName(executable)!, MainWindowTimeout = TimeSpan.FromSeconds(30) });
NativeWindowCapture.Maximize(session.MainWindow.Properties.NativeWindowHandle.Value);
var page = new MainWindowPage(new FlaUiControlResolver(session.MainWindow, session.ConditionFactory));
page.ClickButton(x => x.SettingsButton);
Console.WriteLine(JsonSerializer.Serialize(new { action, executable, initialVersion = page.AppVersionText.Text }));
NativeWindowCapture.Save(session.MainWindow.Properties.NativeWindowHandle.Value, Path.Combine(evidence, "before-" + action + ".png"));
static bool PathMatches(Process process, string path)
{
    try { return process.MainModule?.FileName.Equals(path, StringComparison.OrdinalIgnoreCase) == true; }
    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
}
static async Task WaitFor(Func<bool> condition, int seconds)
{
    for (var attempt = 0; attempt < seconds * 2; attempt++)
    {
        if (condition()) return;
        await Task.Delay(500);
    }
    throw new TimeoutException("Installed application did not reach the expected state.");
}
