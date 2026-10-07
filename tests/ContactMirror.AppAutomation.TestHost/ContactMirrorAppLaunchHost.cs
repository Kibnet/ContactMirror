using AppAutomation.Session.Contracts;
using AppAutomation.TestHost.Avalonia;
using ContactMirror.Desktop;

namespace ContactMirror.AppAutomation.TestHost;

public static class ContactMirrorAppLaunchHost
{
    public static Type AvaloniaAppType => typeof(App);

    private static readonly AvaloniaDesktopAppDescriptor DesktopApp = new(
        solutionFileNames: new[]
        {
            "ContactMirror.sln"
        },
        desktopProjectRelativePaths: new[]
        {
            "tests\\ContactMirror.AppAutomation.TestHost\\ContactMirror.AppAutomation.TestHost.csproj"
        },
        desktopTargetFramework: "net10.0",
        executableName: "ContactMirror.TestHost.exe");

    public static DesktopAppLaunchOptions CreateDesktopLaunchOptions(
        string? buildConfiguration = null,
        DesktopWindowPlacement? windowPlacement = null,
        string? integrationRoot = null)
    {
        return AvaloniaDesktopLaunchHost.CreateLaunchOptions(
            DesktopApp,
            new AvaloniaDesktopLaunchOptions
            {
                BuildConfiguration = buildConfiguration ?? BuildConfigurationDefaults.ForAssembly(typeof(ContactMirrorAppLaunchHost).Assembly),
                WindowPlacement = windowPlacement,
                Arguments = integrationRoot is null ? [] : ["--integration-demo", integrationRoot]
            });
    }

    public static HeadlessAppLaunchOptions CreateHeadlessLaunchOptions()
    {
        return AvaloniaHeadlessLaunchHost.Create(
            static () => new MainWindow(new MainWindowViewModel(new TestAccountConnector(), new TestSyncCoordinator(), demo: true, persist: false)));
    }
    public static HeadlessAppLaunchOptions CreateIntegratedHeadlessLaunchOptions(string root) =>
        AvaloniaHeadlessLaunchHost.Create(() => IntegratedDemoBootstrap.CreateWindow(root));
}
