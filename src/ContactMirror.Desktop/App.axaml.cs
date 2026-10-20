using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ContactMirror.Application;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Google;
using ContactMirror.Infrastructure.Demo;
using ContactMirror.Infrastructure.Configuration;
using ContactMirror.Infrastructure.Automation;
using ContactMirror.Infrastructure.Contacts;
using Avalonia.Threading;

namespace ContactMirror.Desktop;

public sealed partial class App : Avalonia.Application
{
    public static Func<MainWindow>? MainWindowFactory { get; set; }
    public static AgentRuntime? Automation { get; private set; }
    private static IAccountConnector? sharedAccount;
    private static SyncCoordinator? sharedSync;
    private static ContactEditorStore? sharedEditor;
    private MainWindow? window;
    private DesktopAgentUi? agentUi;
    private AgentPipeServer? pipe;
    private AgentActivation? activation;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    internal static WindowIcon LoadWindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://ContactMirror/Assets/contactmirror.ico"));
        return new WindowIcon(stream);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!string.IsNullOrEmpty(Program.StartupProblem)) desktop.MainWindow = new UpdateRecoveryWindow();
            else if (MainWindowFactory is not null) desktop.MainWindow = MainWindowFactory();
            else
            {
                var initial = CreateViewModel();
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                agentUi = new DesktopAgentUi(ShowWindowAsync); Automation!.Ui = agentUi;
                pipe = new(Automation); _ = pipe.StartAsync();
                activation = new(ShowWindowAsync); _ = activation.StartAsync();
                if (!Program.IsAgentHost) CreateWindow(initial); else initial.Detach();
                var idleSince = DateTimeOffset.UtcNow;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    if (window is not null || Automation.ConnectedClients > 0 || Automation.HasRunningJobs || Program.Activity.IsActive) idleSince = DateTimeOffset.UtcNow;
                    else if (DateTimeOffset.UtcNow - idleSince > TimeSpan.FromSeconds(30)) desktop.Shutdown();
                };
                timer.Start(); desktop.Exit += (_, _) => { timer.Stop(); activation.Dispose(); pipe.Dispose(); Automation.Dispose(); };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void CreateWindow(MainWindowViewModel model)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        window = new MainWindow(model); agentUi!.Model = model; desktop.MainWindow = window;
        window.Closed += (_, _) =>
        {
            window = null; agentUi.Model = null;
            if (Automation!.ConnectedClients == 0 && !Automation.HasRunningJobs && !Program.Activity.IsActive) desktop.Shutdown();
        };
    }
    private Task ShowWindowAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess()) return Dispatcher.UIThread.InvokeAsync(ShowWindowAsync);
        if (window is null) CreateWindow(CreateViewModel());
        window!.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); return Task.CompletedTask;
    }

    public static MainWindowViewModel CreateViewModel()
    {
        if (sharedAccount is not null) return ViewModel(sharedAccount, sharedSync!, sharedEditor!);
            IAccountConnector account;
            IGoogleContactsGateway gateway;
            if (Program.IsDemo)
            {
                account = new DemoAccountConnector();
                gateway = new DemoGoogleGateway();
            }
            else
            {
                var optionsPath = DesktopPreferences.Load().OAuthPath;
                OAuthClientOptions options;
                try
                {
                    var managed = !ApplicationPaths.IsValidation && !string.IsNullOrEmpty(optionsPath) && Path.GetFullPath(optionsPath).Equals(ApplicationPaths.ManagedOAuthPath, StringComparison.OrdinalIgnoreCase) ? ApplicationPaths.ManagedOAuthPath : null;
                    options = OAuthClientOptions.LoadForRuntime(Program.IsPackaged || ApplicationPaths.IsValidation, managed, AppContext.BaseDirectory, Environment.CurrentDirectory,
                        Environment.GetEnvironmentVariable("CONTACTMIRROR_GOOGLE_CLIENT_ID"), Environment.GetEnvironmentVariable("CONTACTMIRROR_GOOGLE_CLIENT_SECRET"));
                    if (!ApplicationPaths.IsValidation && !string.IsNullOrEmpty(optionsPath) && managed is null)
                        options = options with { ConfigurationProblem = "Ранее выбранный файл OAuth нужно импортировать заново в настройках; он не загружается автоматически." };
                }
                catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
                { options = new(ConfigurationSource: "invalid"); }
                var google = new GoogleAccountService(options, new WindowsCredentialVault());
                account = google;
                gateway = new GoogleContactsGateway(google);
            }
        var store = new FileWorkspaceStore(); var sync = new SyncCoordinator(new GuardedGoogleGateway(gateway), store); var editor = new ContactEditorStore();
        sharedAccount = account; sharedSync = sync; sharedEditor = editor;
        Automation = new AgentRuntime(account, sync, editor, store, Program.Activity);
        return ViewModel(account, sync, editor);
    }
    private static MainWindowViewModel ViewModel(IAccountConnector account, SyncCoordinator sync, ContactEditorStore editor)
    {
        var model = new MainWindowViewModel(account, sync, Program.IsDemo, activity: Program.Activity, updates: Program.Updates, editorStore: editor);
        model.AttachAutomation(Automation!); return model;
    }
    internal static void ReplaceComposition(IAccountConnector account, SyncCoordinator sync)
    {
        sharedAccount = account; sharedSync = sync; Automation?.Reconfigure(account, sync);
    }
}
