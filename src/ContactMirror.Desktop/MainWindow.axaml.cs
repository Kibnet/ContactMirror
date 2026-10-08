using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ContactMirror.Application;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Google;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    public MainWindow() : this(App.CreateViewModel()) { }
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        SizeChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel current) return;
            current.SettingsPanelHeight = Math.Clamp(Bounds.Height - 650, 100, 240);
            current.WorkspaceHeaderHeight = Math.Clamp(Bounds.Height - 520, 120, 400);
        };
        Opened += async (_, _) => await viewModel.InitializeAsync();
        Closing += async (_, args) =>
        {
            if (!viewModel.IsBusy) return;
            args.Cancel = true; IsEnabled = false;
            await viewModel.RequestCloseAsync();
            Close();
        };
        Closed += (_, _) => { viewModel.CancelCommand.Execute(null); viewModel.Detach(); };
    }
    private async void ChooseFolder(object? sender, RoutedEventArgs args) => await _viewModel.ConfigureAsync(async token =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка контактов ContactMirror", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        token.ThrowIfCancellationRequested();
        if (path is null) return;
        try
        {
            WorkspacePathPolicy.Current.Validate(path);
            if (Directory.EnumerateFileSystemEntries(path).Any() && !File.Exists(Path.Combine(path, ".contactmirror", "workspace.json")))
            {
                var contacts = Path.Combine(path, "ContactMirror");
                var suffix = 2;
                while (Directory.Exists(contacts) && Directory.EnumerateFileSystemEntries(contacts).Any() && !File.Exists(Path.Combine(contacts, ".contactmirror", "workspace.json")))
                    contacts = Path.Combine(path, $"ContactMirror ({suffix++})");
                Directory.CreateDirectory(contacts);
                _viewModel.Folder = contacts;
                _viewModel.Status = $"В выбранной папке есть другие файлы. Для контактов подготовлена подпапка {contacts}.";
            }
            else _viewModel.Folder = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContactMirror.Core.SyncException)
        { _viewModel.Error = $"Не удалось подготовить папку контактов: {error.Message}"; }
    });
    private async void ChooseOAuth(object? sender, RoutedEventArgs args)
    {
        if (!_viewModel.CanOverrideOAuth)
        {
            _viewModel.Status = "Для настройки Google запустите ContactMirror без параметра --demo. Этот режим использует только вымышленные контакты.";
            return;
        }
        await _viewModel.ConfigureAsync(async token =>
        {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "OAuth-конфигурация приложения", AllowMultiple = false, FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        token.ThrowIfCancellationRequested();
        if (path is null) return;
        try
        {
            var managedPath = OAuthClientOptions.ImportManaged(path);
            var account = new GoogleAccountService(OAuthClientOptions.Load(managedPath) with { ConfigurationSource = "managed" }, new WindowsCredentialVault());
            _viewModel.ReplaceServices(account, new SyncCoordinator(new GoogleContactsGateway(account), new FileWorkspaceStore()));
            new DesktopPreferences(_viewModel.Folder, managedPath).Save();
            await _viewModel.ReloadConnectionAsync(token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ContactMirror.Core.SyncException)
        { _viewModel.Error = $"Конфигурацию не удалось открыть: {error.Message}"; }
        });
    }
    private async void UsePublisherOAuth(object? sender, RoutedEventArgs args)
    {
        if (!_viewModel.CanOverrideOAuth) return;
        await _viewModel.ConfigureAsync(async token =>
        {
        try
        {
            (DesktopPreferences.Load() with { OAuthPath = "" }).Save();
            var options = OAuthClientOptions.LoadForRuntime(true, null, AppContext.BaseDirectory, Environment.CurrentDirectory);
            var account = new GoogleAccountService(options, new WindowsCredentialVault());
            _viewModel.ReplaceServices(account, new SyncCoordinator(new GoogleContactsGateway(account), new FileWorkspaceStore()));
            await _viewModel.ReloadConnectionAsync(token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _viewModel.Error = "Не удалось сохранить настройки подключения. Проверьте доступ к папке пользователя."; }
        });
    }
    private void ThemeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox box && Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = box.SelectedIndex switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
}
