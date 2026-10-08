using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Threading;
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
            current.IsNarrow = Bounds.Width < 900;
            UpdateComparisonLayout(current);
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(viewModel.ShowNarrowDetails) or nameof(viewModel.IsNarrow)) UpdateComparisonLayout(viewModel);
            if (args.PropertyName == nameof(viewModel.ShowDeleteConfirmation) && viewModel.ShowDeleteConfirmation)
                Dispatcher.UIThread.Post(() => FocusById(viewModel.RequiresDeletePhrase ? "DeletePhraseInput" : "DismissDeletesButton"));
            if (args.PropertyName == nameof(viewModel.ShowBackupDeleteConfirmation) && viewModel.ShowBackupDeleteConfirmation)
                Dispatcher.UIThread.Post(() => FocusById("DismissBackupDeleteButton"));
            if (args.PropertyName == nameof(viewModel.ShowDeleteConfirmation) && !viewModel.ShowDeleteConfirmation)
                Dispatcher.UIThread.Post(() => FocusById("ApplyButton"));
            if (args.PropertyName == nameof(viewModel.ShowBackupDeleteConfirmation) && !viewModel.ShowBackupDeleteConfirmation)
                Dispatcher.UIThread.Post(() => FocusById("CleanupBackupButton"));
            if (args.PropertyName == nameof(viewModel.HasPanel) && !viewModel.HasPanel)
                Dispatcher.UIThread.Post(() => FocusById("SettingsButton"));
        };
        AddHandler(KeyDownEvent, HandleWorkspaceKey, RoutingStrategies.Tunnel);
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
    private void UpdateComparisonLayout(MainWindowViewModel model)
    {
        var grid = this.FindControl<Grid>("ComparisonGrid")!;
        if (model.IsNarrow) grid.ColumnDefinitions = new ColumnDefinitions(model.ShowNarrowDetails ? "0,0,*" : "*,0,0");
        else if (grid.ColumnDefinitions[1].Width.Value == 0) grid.ColumnDefinitions = new ColumnDefinitions("0.32*,10,0.68*");
    }
    private void FocusById(string id) => this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id)?.Focus();
    private void HandleWorkspaceKey(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            if (_viewModel.ShowDeleteConfirmation) _viewModel.DismissDeletesCommand.Execute(null);
            else if (_viewModel.ShowBackupDeleteConfirmation) _viewModel.DismissBackupDeleteCommand.Execute(null);
            else if (_viewModel.HasPanel) _viewModel.ClosePanelCommand.Execute(null);
            else if (_viewModel.IsNarrow && _viewModel.ShowNarrowDetails) _viewModel.BackToListCommand.Execute(null);
            else return;
            args.Handled = true;
        }
        if (args.Key == Key.Tab && (_viewModel.ShowDeleteConfirmation || _viewModel.ShowBackupDeleteConfirmation))
        {
            var ids = _viewModel.ShowBackupDeleteConfirmation ? new[] { "DismissBackupDeleteButton", "ConfirmCleanupBackupButton" } : _viewModel.RequiresDeletePhrase ? new[] { "DeletePhraseInput", "DismissDeletesButton", "ConfirmDeletesButton" } : new[] { "DismissDeletesButton", "ConfirmDeletesButton" };
            var controls = ids.Select(id => this.GetVisualDescendants().OfType<Control>().First(c => AutomationProperties.GetAutomationId(c) == id)).ToArray();
            var index = Array.FindIndex(controls, c => c.IsKeyboardFocusWithin);
            controls[(index + (args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? controls.Length - 1 : 1)) % controls.Length].Focus();
            args.Handled = true;
        }
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
