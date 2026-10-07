using Avalonia;
using ContactMirror.Desktop;

namespace ContactMirror.AppAutomation.TestHost;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.MainWindowFactory = args.Length == 2 && args[0] == "--integration-demo"
            ? () => IntegratedDemoBootstrap.CreateWindow(args[1])
            : () => new MainWindow(new MainWindowViewModel(new TestAccountConnector(), new TestSyncCoordinator(), demo: true, persist: false));
        ContactMirror.Desktop.Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
