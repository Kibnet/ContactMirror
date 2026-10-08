using AppAutomation.FlaUI.Automation;
using AppAutomation.Abstractions;
using AppAutomation.TestHost.Avalonia;
using AppAutomation.FlaUI.Session;
using AppAutomation.TUnit;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.UiTests.Authoring.Pages;
using ContactMirror.UiTests.Authoring.Tests;
using TUnit.Core;
using TUnit.Assertions;

namespace ContactMirror.UiTests.FlaUI.Tests;

[InheritsTests]
public sealed class MainWindowFlaUiTests
    : MainWindowScenariosBase<MainWindowFlaUiTests.FlaUiRuntimeSession>
{
    protected override void CaptureCheckpoint(string state)
    {
        if (state == "conflict")
        {
            Page.WaitUntilNameContains(static p => p.DiffSummary, "Изменённых значений");
            Thread.Sleep(150);
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ContactMirror.sln"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), state + "-native.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, path);
        Console.WriteLine($"Native workspace screenshot: {path}");
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Native_keyboard_modal_keeps_focus_and_escape_preserves_plan()
    {
        PreparePreview();
        Page.EnterText(static p => p.SearchInput, "Глеб").SetChecked(static p => p.DeleteGlebCheckbox, true).ClickButton(static p => p.ApplyButton);
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
        await Task.Delay(150);
        var focused = Session.Inner.MainWindow.FindAllDescendants().FirstOrDefault(e => e.Properties.HasKeyboardFocus.ValueOrDefault);
        await TUnit.Assertions.Assert.That(focused?.AutomationId is "DeletePhraseInput" or "DismissDeletesButton" or "ConfirmDeletesButton").IsTrue();
        CaptureCheckpoint("ux-keyboard-focus");
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        await Task.Delay(150); await Assert.That(Page.ApplyButton.IsEnabled).IsTrue();
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Ordinary_desktop_demo_opens_and_prepares_in_separate_temporary_workspace()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ContactMirror.sln"))) root = root.Parent;
        var repository = root?.FullName ?? throw new InvalidOperationException("Repository not found.");
        using var folder = TemporaryDirectory.Create("ContactMirror-ordinary-demo-smoke");
        var preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContactMirror", "desktop-demo.json");
        var original = File.Exists(preferences) ? File.ReadAllBytes(preferences) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(preferences)!);
        try
        {
            File.WriteAllText(preferences, System.Text.Json.JsonSerializer.Serialize(new { Folder = folder.FullPath, OAuthPath = "" }));
            using var application = global::FlaUI.Core.Application.Launch(new System.Diagnostics.ProcessStartInfo(Path.Combine(repository, "src", "ContactMirror.Desktop", "bin", "Release", "net10.0", "ContactMirror.exe"), "--demo") { UseShellExecute = false, WorkingDirectory = repository });
            using var automation = new global::FlaUI.UIA3.UIA3Automation();
            try
            {
                var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(10)) ?? throw new InvalidOperationException("Demo window did not open.");
                var page = new MainWindowPage(new FlaUiControlResolver(window, automation.ConditionFactory));
                page.WaitUntilNameContains(static p => p.AccountLabel, "Демонстрационный аккаунт")
                    .ClickButton(static p => p.CheckChangesButton)
                    .WaitUntilNameContains(static p => p.StatusText, "Сравнение завершено");
                var screenshot = Path.Combine(repository, "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), "ordinary-demo-native.png");
                Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
                NativeWindowCapture.Save(window.Properties.NativeWindowHandle.Value, screenshot);
                Console.WriteLine($"Ordinary Desktop --demo: prepared synthetic preview in temporary workspace; {screenshot}");
            }
            finally { application.Close(); }
        }
        finally
        {
            if (original is null) File.Delete(preferences); else File.WriteAllBytes(preferences, original);
        }
    }
    protected override FlaUiRuntimeSession LaunchSession()
    {
        return new FlaUiRuntimeSession(
            DesktopAppSession.Launch(ContactMirrorAppLaunchHost.CreateDesktopLaunchOptions()));
    }

    protected override MainWindowPage CreatePage(FlaUiRuntimeSession session)
    {
        return new MainWindowPage(
            new FlaUiControlResolver(session.Inner.MainWindow, session.Inner.ConditionFactory));
    }

    public sealed class FlaUiRuntimeSession : IUiTestSession
    {
        public FlaUiRuntimeSession(DesktopAppSession inner)
        {
            Inner = inner;
        }

        public DesktopAppSession Inner { get; }

        public void Dispose()
        {
            Inner.Dispose();
        }
    }
}
