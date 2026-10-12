using Avalonia;
using ContactMirror.Desktop;

namespace ContactMirror.AppAutomation.TestHost;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if(args.Length==5&&args[0]=="--editor-preferences-probe")
        {
            var preferences=new ContactMirror.Desktop.Contacts.ContactTablePreferences(false,true,args[1]).Load(Guid.Parse(args[2]),args[3]);
            File.WriteAllText(args[4],System.Text.Json.JsonSerializer.Serialize(preferences));return;
        }
        App.MainWindowFactory = args.Length == 2 && args[0] == "--integration-demo"
            ? () => IntegratedDemoBootstrap.CreateWindow(args[1])
            : () => new MainWindow(new MainWindowViewModel(new TestAccountConnector(), new TestSyncCoordinator(), demo: true, persist: false));
        ContactMirror.Desktop.Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
