using ContactMirror.Application;
using ContactMirror.Desktop;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Demo;

namespace ContactMirror.AppAutomation.TestHost;

public static class IntegratedDemoBootstrap
{
    public static MainWindow CreateWindow(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        var account = new DemoAccountConnector();
        var gateway = new DemoGoogleGateway(Path.Combine(root, "remote.json"));
        var coordinator = new SyncCoordinator(gateway, new FileWorkspaceStore());
        var viewModel = new MainWindowViewModel(account, coordinator, demo: true, persist: false)
        { Folder = Path.Combine(root, "workspace") };
        return new MainWindow(viewModel);
    }
}
