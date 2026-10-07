using AppAutomation.Abstractions;
using AppAutomation.Avalonia.Headless.Automation;
using AppAutomation.Avalonia.Headless.Session;
using AppAutomation.TestHost.Avalonia;
using AppAutomation.TUnit;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.UiTests.Authoring.Pages;
using ContactMirror.UiTests.Authoring.Tests;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

[InheritsTests]
public sealed class IntegratedDemoHeadlessTests : RealPipelineScenariosBase<IntegratedDemoHeadlessTests.RuntimeSession>
{
    protected override string IntegrationRoot => Session.Directory.FullPath;
    protected override RuntimeSession LaunchSession()
    {
        var directory = TemporaryDirectory.Create("ContactMirror-integrated-headless");
        try
        {
            var inner = DesktopAppSession.Launch(ContactMirrorAppLaunchHost.CreateIntegratedHeadlessLaunchOptions(directory.FullPath));
            HeadlessRuntime.Dispatch(inner.MainWindow.Show);
            return new(inner, directory);
        }
        catch { directory.Dispose(); throw; }
    }
    protected override MainWindowPage CreatePage(RuntimeSession session) => new(new HeadlessControlResolver(session.Inner.MainWindow));
    protected override void CaptureCheckpoint(string state)
    {
        var path = Session.Inner.CaptureScreenshot(Path.Combine(RepositoryRoot(), "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), state + "-headless.png"));
        Console.WriteLine($"Integrated real pipeline screenshot: {path}");
    }
    protected override ValueTask<IReadOnlyList<UiFailureArtifact>> CollectFailureArtifactsAsync(CancellationToken cancellationToken = default)
    {
        var path = Session.Inner.CaptureScreenshot(Path.Combine(RepositoryRoot(), "chat-artifacts", "ui", Guid.NewGuid().ToString("N"), "integrated-test-failure.png"));
        return ValueTask.FromResult<IReadOnlyList<UiFailureArtifact>>([new("screenshot", "integrated-test-failure", Path.GetRelativePath(AppContext.BaseDirectory, path), "image/png", false, path)]);
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
        public void Dispose()
        {
            try { HeadlessRuntime.Dispatch(Inner.MainWindow.Close); }
            finally { Inner.Dispose(); Directory.Dispose(); }
        }
    }
}
