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

namespace ContactMirror.Desktop;

public sealed partial class App : Avalonia.Application
{
    public static Func<MainWindow>? MainWindowFactory { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    internal static WindowIcon LoadWindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://ContactMirror/Assets/contactmirror.ico"));
        return new WindowIcon(stream);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = !string.IsNullOrEmpty(Program.StartupProblem) ? new UpdateRecoveryWindow() : MainWindowFactory?.Invoke() ?? new MainWindow(CreateViewModel());
        base.OnFrameworkInitializationCompleted();
    }

    public static MainWindowViewModel CreateViewModel()
    {
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
        return new MainWindowViewModel(account,
                new SyncCoordinator(gateway, new FileWorkspaceStore()), Program.IsDemo, activity: Program.Activity, updates: Program.Updates);
    }
}
