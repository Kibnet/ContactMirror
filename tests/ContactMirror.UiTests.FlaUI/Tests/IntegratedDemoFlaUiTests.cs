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
        var directory = Path.Combine(RepositoryRoot(), "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, state + "-native.png");
        NativeWindowCapture.Save(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, path);
        Console.WriteLine($"Native integrated real pipeline screenshot: {path}");
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
