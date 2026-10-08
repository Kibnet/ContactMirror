using AppAutomation.Abstractions;
using AppAutomation.FlaUI.Automation;
using AppAutomation.FlaUI.Session;
using AppAutomation.TestHost.Avalonia;
using AppAutomation.TUnit;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.UiTests.Authoring.Pages;
using ContactMirror.UiTests.Authoring.Tests;
using TUnit.Core;

namespace ContactMirror.UiTests.FlaUI.Tests;

[InheritsTests]
public sealed class IntegratedDemoFlaUiTests : RealPipelineScenariosBase<IntegratedDemoFlaUiTests.RuntimeSession>
{
    private System.Diagnostics.Process? _video;
    private bool _compactValidation;
    private void ConfigureCompactValidation() => _compactValidation = true;
    [Test, NotInParallel("DesktopUi")]
    public async Task Contact_edit_details_remain_usable_with_settings_in_compact_native_window()
    {
        ConfigureCompactValidation();
        await Whole_contact_edits_and_same_path_photo_survive_snapshot_repair_and_upload();
    }
    protected override async Task StartContactEditVideoAsync()
    {
        var phase = Environment.GetEnvironmentVariable("CONTACTMIRROR_CONTACT_EDIT_VIDEO");
        if (_compactValidation) return;
        if (phase is not ("before" or "after")) return;
        NativeWindowCapture.PositionForRecording(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value);
        var output = Path.Combine(Environment.GetEnvironmentVariable("CONTACTMIRROR_CONTACT_EDIT_VIDEO_DIRECTORY") ?? Path.Combine(RepositoryRoot(), "chat-artifacts", "contact-edit"), phase + ".mp4");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        var recorder = Environment.GetEnvironmentVariable("CONTACTMIRROR_CONTACT_EDIT_RECORDER") ?? @"C:\Users\Kibnet\.codex\skills\record-app-screen\scripts\record_app_window.ps1";
        foreach (var argument in new[] { "-NoProfile", "-File", recorder, "-ProcessName", "ContactMirror.TestHost.exe", "-Output", output, "-DurationSeconds", "30", "-Fps", "15" }) start.ArgumentList.Add(argument);
        _video = System.Diagnostics.Process.Start(start);
        await Task.Delay(2500);
    }
    protected override async Task FinishContactEditVideoAsync()
    {
        if (_video is null) return;
        var stderr = _video.StandardError.ReadToEndAsync(); var stdout = _video.StandardOutput.ReadToEndAsync();
        await _video.WaitForExitAsync();
        if (_video.ExitCode != 0) throw new InvalidOperationException("Contact edit video recording failed: " + await stderr + await stdout);
        _video.Dispose(); _video = null;
    }
    protected override string IntegrationRoot => Session.Directory.FullPath;
    protected override RuntimeSession LaunchSession()
    {
        var directory = TemporaryDirectory.Create("ContactMirror-integrated-native");
        try
        {
            var inner = DesktopAppSession.Launch(ContactMirrorAppLaunchHost.CreateDesktopLaunchOptions(integrationRoot: directory.FullPath));
            NativeWindowCapture.Maximize(inner.MainWindow.Properties.NativeWindowHandle.Value);
            return new(inner, directory);
        }
        catch { directory.Dispose(); throw; }
    }
    protected override MainWindowPage CreatePage(RuntimeSession session) => new(new FlaUiControlResolver(session.Inner.MainWindow, session.Inner.ConditionFactory));
    protected override void CaptureCheckpoint(string state)
    {
        if (_compactValidation && state == "contact-edit-blocked")
        {
            NativeWindowCapture.Compact(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value);
            Page.ClickButton(static p => p.SettingsButton);
            Thread.Sleep(350);
            var panelShot = Path.Combine(RepositoryRoot(), "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), "ux-settings-native.png");
            Directory.CreateDirectory(Path.GetDirectoryName(panelShot)!);
            NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, panelShot);
            Page.ClickButton(static p => p.ClosePanelButton);
            Thread.Sleep(200);
            state += "-settings-compact";
        }
        var directory = Path.Combine(RepositoryRoot(), "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, state + "-native.png");
        NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, path);
        Console.WriteLine($"Native integrated real pipeline screenshot: {path}");
        if (state.StartsWith("contact-edit-blocked", StringComparison.Ordinal) && Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("ContactDetailsScroll")) is { } details)
        {
            Console.WriteLine($"UIA contact details bounds: {details.BoundingRectangle}");
            if (_compactValidation && details.BoundingRectangle.Height < 100) throw new InvalidOperationException("Settings displaced the contact details in compact layout.");
            var scroll = details.Patterns.Scroll.Pattern;
            scroll.SetScrollPercent(-1, 50);
            var photo = Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByName("Фотография")) ?? throw new InvalidOperationException("Photo comparison heading not found.");
            AlignWithDetails(photo);
            Thread.Sleep(650);
            var values = Path.Combine(directory, state + "-photo-values-native.png");
            NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, values);
            Console.WriteLine($"Native compact scrolled details screenshot: {values}");
            scroll.SetScrollPercent(-1, 100);
            Thread.Sleep(250);
            var list = Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("DiffRows")) ?? throw new InvalidOperationException("Diff list not found.");
            AlignWithDetails(list);
            Thread.Sleep(650);
            var fields = Path.Combine(directory, state + "-field-values-native.png");
            NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, fields);
            Console.WriteLine($"Native compact scrolled fields screenshot: {fields}");
            if (!_compactValidation)
            {
                var surname = list.FindFirstDescendant(Session.Inner.ConditionFactory.ByName("Фамилия"));
                if (surname is null && list.Patterns.Scroll.IsSupported)
                {
                    for (var percent = 0; percent <= 100 && surname is null; percent += 10)
                    {
                        list.Patterns.Scroll.Pattern.SetScrollPercent(-1, percent);
                        Thread.Sleep(150);
                        surname = list.FindFirstDescendant(Session.Inner.ConditionFactory.ByName("Фамилия"));
                    }
                }
                if (surname is null) throw new InvalidOperationException("Surname difference not found in virtualized fields.");
                AlignWithDetails(surname);
                Thread.Sleep(650);
                var names = Path.Combine(directory, state + "-name-values-native.png");
                NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, names);
                Console.WriteLine($"Native exact surname values screenshot: {names}");
            }
            scroll.SetScrollPercent(-1, 0);
            void AlignWithDetails(global::FlaUI.Core.AutomationElements.AutomationElement element)
            {
                var range = details.BoundingRectangle.Height * (100 / scroll.VerticalViewSize.Value - 1);
                if (range > 0) scroll.SetScrollPercent(-1, Math.Clamp(scroll.VerticalScrollPercent.Value + (element.BoundingRectangle.Top - details.BoundingRectangle.Top) / range * 100, 0, 100));
            }
        }
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
    public sealed class RuntimeSession(DesktopAppSession inner, TemporaryDirectory directory) : IUiTestSession
    {
        public DesktopAppSession Inner { get; } = inner;
        public TemporaryDirectory Directory { get; } = directory;
        public void Dispose() { try { Inner.Dispose(); } finally { Directory.Dispose(); } }
    }
}
