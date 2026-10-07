using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Application.Updates;
using Avalonia.Threading;

namespace ContactMirror.Desktop;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private IAccountConnector _accountConnector;
    private ISyncCoordinator _sync;
    private CancellationTokenSource? _cancellation;
    private SyncPreview? _preview;
    private AccountIdentity? _account;
    private int _progressVersion;
    private readonly bool _persist;
    private readonly ApplicationActivity _activity;
    private readonly Action _activityChanged;
    private TaskCompletionSource? _idle;
    private bool _closeRequested;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public MainWindowViewModel(IAccountConnector account, ISyncCoordinator sync, bool demo = false, bool persist = true, ApplicationActivity? activity = null, IApplicationUpdateService? updates = null)
    {
        _accountConnector = account;
        _sync = sync;
        _persist = persist;
        _activity = activity ?? new();
        Updates = new(updates, _activity);
        _activityChanged = () => { if (Dispatcher.UIThread.CheckAccess()) RefreshCommands(); else Dispatcher.UIThread.Post(RefreshCommands); };
        _activity.Changed += _activityChanged;
        IsDemo = demo;
        if (persist) Folder = DesktopPreferences.Load(demo).Folder;
        ConfigurationHint = account.ConfigurationHint;
    }

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _accountLabel = "Google ещё не подключён";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showCapabilities;
    [ObservableProperty] private bool _showHistory;
    [ObservableProperty] private bool _showDeleteConfirmation;
    [ObservableProperty] private bool _deletesConfirmed;
    [ObservableProperty] private string _deletePhrase = "";
    [ObservableProperty] private string _deleteWarning = "";
    [ObservableProperty] private string _status = "Подключите Google и выберите папку для контактов.";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _configurationHint = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private string _applyCaption = "Применить";
    [ObservableProperty] private string _allFields = "Выберите изменение, чтобы увидеть полные значения поля.";
    [ObservableProperty] private string _selectedFile = "";
    [ObservableProperty] private string _lastRun = "Синхронизация ещё не выполнялась";
    [ObservableProperty] private EntryViewModel? _selectedEntry;
    [ObservableProperty] private RunViewModel? _selectedRun;
    [ObservableProperty] private bool _showBackupDeleteConfirmation;
    [ObservableProperty] private string _backupDeleteWarning = "";
    [ObservableProperty] private string _historyBackupVolume = "";
    public bool IsDemo { get; }
    public bool CanConfigure => !IsBusy && !_activity.IsActive && !_activity.IsApplying && !_closeRequested;
    public bool CanOverrideOAuth => CanConfigure && !ContactMirror.Infrastructure.Configuration.ApplicationPaths.IsValidation && !IsDemo;
    public UpdatesViewModel Updates { get; }
    public bool IsOnboarding => !IsConnected;
    public bool CanCheck => IsConnected && CanConfigure && !string.IsNullOrWhiteSpace(Folder);
    public bool CanDisconnect => IsConnected && CanConfigure;
    public bool CanApply => HasPreview && CanConfigure && Entries.Any(x => x.CanApply);
    public bool CanCleanupBackup => !IsBusy && IsConnected && SelectedRun?.CanCleanup == true;
    public bool CanRestoreBackup => !IsBusy && IsConnected && SelectedRun?.Summary.BackupAvailable == true;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasNotices => !string.IsNullOrEmpty(Notice);
    public bool HasResults => Results.Count > 0;
    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool HasSelectedConflict => SelectedEntry?.IsConflict == true;
    public ObservableCollection<EntryViewModel> Entries { get; } = [];
    public ObservableCollection<EntryViewModel> VisibleEntries { get; } = [];
    public ObservableCollection<RunViewModel> History { get; } = [];
    public ObservableCollection<OperationResult> Results { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["Все изменения", "В Google", "В папку", "Требуют внимания", "Удаления"];
    public IReadOnlyList<FieldCapability> Capabilities => CapabilityRegistry.Fields;

    partial void OnIsConnectedChanged(bool value) { OnPropertyChanged(nameof(IsOnboarding)); RefreshCommands(); }
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnHasPreviewChanged(bool value) => RefreshCommands();
    partial void OnFolderChanged(string value) { InvalidatePreview(); RefreshCommands(); }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotices));
    partial void OnSearchChanged(string value) => Filter();
    partial void OnFilterIndexChanged(int value) => Filter();
    partial void OnSelectedRunChanged(RunViewModel? value)
    {
        ShowBackupDeleteConfirmation = false;
        OnPropertyChanged(nameof(CanCleanupBackup));
        OnPropertyChanged(nameof(CanRestoreBackup)); RestoreCommand.NotifyCanExecuteChanged();
        CleanupBackupCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedEntryChanged(EntryViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedEntry)); OnPropertyChanged(nameof(HasSelectedConflict));
        AllFields = value is null ? "Выберите изменение." : $"{value.FieldLabel} · {value.Entry.Field}\n\nПАПКА: {CompactJson(value.Entry.Local)}\nGOOGLE: {CompactJson(value.Entry.Google)}\nБЫЛО: {CompactJson(value.Entry.Before)}\n\nПолные JSON-значения доступны для выделения и копирования. Кнопка «Все поля» откроет полный документ; google.person — сведения только для чтения.";
        SelectedFile = "";
        if (value is not null) _ = LocateFileAsync(value);
    }
    private async Task LocateFileAsync(EntryViewModel entry)
    {
        try
        {
            var path = await Task.Run(() => FindFile(entry.Entry));
            if (SelectedEntry == entry) SelectedFile = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Error = error.Message; }
    }
    public async Task InitializeAsync()
    {
        await RunAsync(ReloadConnectionAsync);
        _ = Updates.CheckForStartupAsync();
    }
    public async Task ReloadConnectionAsync(CancellationToken token)
    {
        _account = await _accountConnector.GetAccountAsync(token);
        SetAccount();
        if (IsConnected) Status = string.IsNullOrWhiteSpace(Folder) ? "Выберите папку и проверьте изменения." : "Проверьте изменения перед синхронизацией.";
        if (IsConnected && !string.IsNullOrEmpty(Folder)) await RefreshHistoryAsync(token);
    }
    public Task ConfigureAsync(Func<CancellationToken, Task> action) => RunAsync(action);

    [RelayCommand] private async Task ConnectAsync() => await RunAsync(async token =>
    {
        Status = "Завершите вход в открывшемся браузере. Пароль приложение не получает.";
        _account = await _accountConnector.SignInAsync(token);
        SetAccount();
        Status = "Google подключён. Выберите папку и проверьте изменения.";
    });

    [RelayCommand] private async Task DisconnectAsync() => await RunAsync(async token =>
    {
        await _accountConnector.SignOutAsync(false, token);
        _account = null;
        SetAccount();
        InvalidatePreview();
        Status = "Аккаунт отключён. Файлы контактов сохранены.";
    });

    [RelayCommand(CanExecute = nameof(CanCheck))] private async Task CheckAsync() => await RunAsync(async token =>
    {
        ShowDeleteConfirmation = false;
        var folder = Folder; var account = _account!; var progress = Progress();
        _preview = await Task.Run(() => _sync.PrepareAsync(folder, account, progress, token), token);
        LoadPreview(_preview);
        if (_persist) (DesktopPreferences.Load(IsDemo) with { Folder = Folder }).Save(IsDemo);
    });

    [RelayCommand(CanExecute = nameof(CanApply))] private async Task ApplyAsync()
    {
        var deleted = Entries.Count(x => x.CanApply && x.Entry.IsDestructiveFor(x.Resolution));
        if (deleted > 0 && !DeletesConfirmed)
        {
            ShowDeleteConfirmation = true;
            DeleteWarning = $"Будет удалено: {deleted}. Будет сохранена резервная копия. " +
                (_preview!.RequiresDeletionConfirmation(deleted) ? "Это значительная часть адресной книги. Введите УДАЛИТЬ для подтверждения." : "Подтвердите выбранные удаления.");
            return;
        }
        await ApplyConfirmedAsync();
    }

    [RelayCommand] private async Task ConfirmDeletesAsync()
    {
        if (_preview is null || IsBusy) return;
        var deleted = Entries.Count(x => x.CanApply && x.Entry.IsDestructiveFor(x.Resolution));
        if (_preview?.RequiresDeletionConfirmation(deleted) == true && DeletePhrase.Trim() != "УДАЛИТЬ")
        { Error = "Для подтверждения большого числа удалений введите УДАЛИТЬ."; return; }
        DeletesConfirmed = true;
        ShowDeleteConfirmation = false;
        await ApplyConfirmedAsync();
    }
    [RelayCommand] private void DismissDeletes() { ShowDeleteConfirmation = false; DeletesConfirmed = false; DeletePhrase = ""; }

    private async Task ApplyConfirmedAsync() => await RunAsync(async token =>
    {
        var choices = Entries.Where(x => x.CanApply).Select(x => new PlanChoice(x.Entry.Key, x.Resolution)).ToArray();
        if (_preview is null || choices.Length == 0) return;
        var preview = _preview; var progress = Progress();
        var result = await Task.Run(() => _sync.ApplyAsync(preview, choices, progress, token), token);
        _progressVersion++;
        Results.Clear();
        foreach (var operation in result.Operations) Results.Add(operation);
        OnPropertyChanged(nameof(HasResults));
        Status = result.IsComplete ? $"Синхронизировано: {result.Confirmed} изменений." : $"Выполнено частично: {result.Confirmed} подтверждено, {result.Failed} ошибок, {result.Unknown} с неизвестным результатом. Проверьте изменения для продолжения.";
        LastRun = $"Последняя синхронизация: {DateTime.Now:g}";
        InvalidatePreview();
        await RefreshHistoryAsync(CancellationToken.None);
    });

    [RelayCommand] private void Cancel() => _cancellation?.Cancel();
    [RelayCommand] private void ToggleSettings() => ShowSettings = !ShowSettings;
    [RelayCommand] private void ToggleCapabilities() => ShowCapabilities = !ShowCapabilities;
    [RelayCommand] private async Task ToggleHistoryAsync()
    {
        ShowHistory = !ShowHistory;
        if (ShowHistory && !string.IsNullOrWhiteSpace(Folder)) await RunAsync(RefreshHistoryAsync);
    }
    [RelayCommand(CanExecute = nameof(CanCheck))] private async Task NewContactAsync() => await RunAsync(async token =>
    {
        var folder = Folder;
        var path = await Task.Run(() => _sync.CreateContactFileAsync(folder, token), token);
        TryOpen(path);
        InvalidatePreview();
        Status = "Создан новый файл контакта. Заполните его и проверьте изменения.";
    });
    [RelayCommand(CanExecute = nameof(CanRestoreBackup))] private async Task RestoreAsync()
    {
        if (_account is null || SelectedRun is null || IsBusy) return;
        await RunAsync(async token =>
        {
            var folder = Folder; var account = _account; var runId = SelectedRun.Summary.Id; var progress = Progress();
            LoadPreview(await Task.Run(() => _sync.PrepareRestoreAsync(folder, account, runId, progress, token), token));
            ShowHistory = false;
        });
    }
    [RelayCommand(CanExecute = nameof(CanCleanupBackup))] private void CleanupBackup()
    {
        if (SelectedRun is null) return;
        BackupDeleteWarning = $"Удалить резервную копию от {SelectedRun.Summary.StartedAt.ToLocalTime():g}? Освободится {SelectedRun.BackupSize}. После удаления восстановление этой операции станет недоступно. Журнал операций сохранится.";
        ShowBackupDeleteConfirmation = true;
    }
    [RelayCommand] private void DismissBackupDelete() => ShowBackupDeleteConfirmation = false;
    [RelayCommand] private async Task ConfirmBackupDeleteAsync()
    {
        if (!ShowBackupDeleteConfirmation || !CanCleanupBackup || SelectedRun is null || _account is null) return;
        var run = SelectedRun.Summary;
        ShowBackupDeleteConfirmation = false;
        await RunAsync(async token =>
        {
            var folder = Folder; var account = _account;
            await Task.Run(() => _sync.CleanupBackupAsync(folder, account, run.Id, token), token);
            InvalidatePreview();
            await RefreshHistoryAsync(token);
            Status = "Резервная копия удалена. Журнал операций сохранён.";
        });
    }
    [RelayCommand] private void UseLocal() { SelectedEntry?.Choose(Resolution.UseLocal); UpdateSelection(); }
    [RelayCommand] private void UseGoogle() { SelectedEntry?.Choose(Resolution.UseGoogle); UpdateSelection(); }
    [RelayCommand] private void Skip() { SelectedEntry?.Choose(Resolution.Skip); UpdateSelection(); }
    [RelayCommand] private void NextConflict() => SelectedEntry = Entries.FirstOrDefault(x => x.IsConflict && !x.CanApply);
    [RelayCommand] private void OpenFolder() => TryOpen(Folder);
    [RelayCommand] private void OpenFile() => TryOpen(SelectedFile);
    [RelayCommand] private void ShowFile() { if (File.Exists(SelectedFile)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedFile}\"") { UseShellExecute = true }); }
    [RelayCommand] private async Task ShowAllFieldsAsync()
    {
        if (!File.Exists(SelectedFile)) return;
        try { AllFields = Json(JsonNode.Parse(await File.ReadAllTextAsync(SelectedFile))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Error = e.Message; }
    }
    [RelayCommand] private void SelectSafe()
    {
        foreach (var entry in Entries) entry.IsSelected = entry.Entry.DefaultSelected;
        UpdateSelection();
    }

    public void ReplaceServices(IAccountConnector account, ISyncCoordinator coordinator)
    {
        _accountConnector = account; _sync = coordinator; ConfigurationHint = account.ConfigurationHint;
        _account = null; SetAccount(); InvalidatePreview();
    }
    private void LoadPreview(SyncPreview preview)
    {
        _progressVersion++;
        _preview = preview;
        Results.Clear(); Entries.Clear();
        OnPropertyChanged(nameof(HasResults));
        foreach (var entry in preview.Entries)
        {
            var viewModel = new EntryViewModel(entry);
            viewModel.PropertyChanged += (_, _) => UpdateSelection();
            Entries.Add(viewModel);
        }
        HasPreview = true; DeletesConfirmed = false; DeletePhrase = "";
        Notice = string.Join("\n", preview.Notices);
        Status = preview.IsRecovery ? "Восстановление: проверьте план компенсации. Изменения в Google появятся после применения." : Entries.Count == 0 ? "Изменений нет. Google и папка согласованы." : $"Сравнение завершено. Контактов: {preview.ContactCount}, ярлыков: {preview.GroupCount}.";
        Filter(); SelectedEntry = VisibleEntries.FirstOrDefault(); UpdateSelection();
    }
    private void Filter()
    {
        VisibleEntries.Clear();
        foreach (var entry in Entries.Where(x => (FilterIndex switch { 1 => x.ToGoogle, 2 => x.ToFolder, 3 => x.IsConflict || !x.Entry.IsSelectable, 4 => x.Entry.IsDestructive, _ => true }) && (string.IsNullOrEmpty(Search) || (x.Entry.Name + x.FieldLabel + x.Entry.Field).Contains(Search, StringComparison.OrdinalIgnoreCase)))) VisibleEntries.Add(entry);
    }
    private void UpdateSelection()
    {
        Counts = $"В Google: {Entries.Count(x => x.ToGoogle)}    В папку: {Entries.Count(x => x.ToFolder)}    Конфликты: {Entries.Count(x => x.IsConflict)}    Удаления: {Entries.Count(x => x.Entry.IsDestructiveFor(x.Resolution))}";
        var count = Entries.Count(x => x.CanApply);
        SelectionSummary = $"Выбрано: {count} · Отложено конфликтов: {Entries.Count(x => x.IsConflict && !x.CanApply)}. Перед записью будет создана резервная копия.";
        ApplyCaption = $"Применить {count}"; DeletesConfirmed = false; RefreshCommands();
    }
    private void InvalidatePreview() { HasPreview = false; _preview = null; ShowDeleteConfirmation = false; }
    private void SetAccount() { IsConnected = _account is not null; AccountLabel = _account?.Email ?? "Google ещё не подключён"; }
    private async Task RefreshHistoryAsync(CancellationToken token)
    {
        SelectedRun = null;
        var folder = Folder;
        var runs = await Task.Run(() => _sync.GetHistoryAsync(folder, token), token);
        History.Clear(); foreach (var run in runs) History.Add(new(run));
        HistoryBackupVolume = $"Резервные копии занимают {RunViewModel.FormatBytes(History.Where(run => run.Summary.BackupAvailable).Sum(run => run.Summary.BackupBytes))}.";
        if (History.Count > 0) LastRun = $"Последняя синхронизация: {History[0].Summary.StartedAt.ToLocalTime():g}";
    }
    private IProgress<SyncProgress> Progress()
    {
        var version = _progressVersion;
        return new ThrottledProgress(new Progress<SyncProgress>(value =>
        {
            if (IsBusy && version == _progressVersion)
                Status = value.Total > 0 ? $"{value.Message} ({value.Completed}/{value.Total})" : value.Message;
        }));
    }
    // Filter reports before Progress<T> posts to the UI context; the operation itself sets the final status.
    private sealed class ThrottledProgress(IProgress<SyncProgress> target) : IProgress<SyncProgress>
    {
        private long _lastPublished;
        public void Report(SyncProgress value)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastPublished);
            if (previous != 0 && now - previous < Stopwatch.Frequency / 10) return;
            if (Interlocked.CompareExchange(ref _lastPublished, now, previous) == previous) target.Report(value);
        }
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _closeRequested) return;
        using var activity = _activity.TryEnter();
        if (activity is null) { Error = "Приложение занято перезапуском или другой операцией. Дождитесь её завершения."; return; }
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _progressVersion++;
        IsBusy = true; Error = "";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { _progressVersion++; Status = "Операция остановлена. Подтверждённые шаги сохранены; проверьте изменения перед продолжением."; InvalidatePreview(); }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException or JsonException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _progressVersion++;
            Error = e.Message;
            Status = "Операцию не удалось завершить. Исправьте причину и проверьте изменения снова.";
            if (e is SyncException { Code: "google-rate-limit" })
                Status = "Достигнут лимит Google. Подождите и повторите проверку изменений; переподключать аккаунт не нужно.";
            if (e is SyncException syncError && syncError.Code is "reauth-required" or "google-http-401" or "oauth-refresh" or "oauth-invalid-grant")
            { ShowSettings = true; Status = "Нужно войти снова. Откройте вход в настройках подключения."; }
            InvalidatePreview();
        }
        finally { _progressVersion++; _cancellation = null; activity.Dispose(); IsBusy = false; _idle.TrySetResult(); }
    }
    public async Task RequestCloseAsync()
    {
        _closeRequested = true;
        _cancellation?.Cancel();
        if (_idle is { } idle) await idle.Task;
    }
    public void Detach()
    {
        _activity.Changed -= _activityChanged;
        Updates.Detach();
    }
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanCheck)); OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanOverrideOAuth));
        OnPropertyChanged(nameof(CanCleanupBackup)); CleanupBackupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRestoreBackup)); RestoreCommand.NotifyCanExecuteChanged();
        CheckCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); NewContactCommand.NotifyCanExecuteChanged();
    }
    private string FindFile(SyncEntry entry)
    {
        var directory = Path.Combine(Folder, entry.Entity == EntityKind.Contact ? "contacts" : "groups");
        if (!Directory.Exists(directory)) return "";
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            try { if (JsonNode.Parse(File.ReadAllText(file))?["id"]?.GetValue<string>() == entry.EntityId.ToString()) return file; }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return "";
    }
    private void TryOpen(string path) { try { if (!string.IsNullOrEmpty(path)) OpenPath(path); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Error = e.Message; } }
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private static string Json(JsonNode? value) => value?.ToJsonString(JsonOptions) ?? "—";
    private static string CompactJson(JsonNode? value) => value?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "—";
}

public sealed partial class EntryViewModel(SyncEntry entry) : ObservableObject
{
    public SyncEntry Entry { get; } = entry;
    [ObservableProperty] private bool _isSelected = entry.DefaultSelected;
    [ObservableProperty] private Resolution _resolution = Resolution.Automatic;
    public bool IsConflict => Entry.Kind == ChangeKind.Conflict;
    public bool CanApply => IsSelected && Entry.IsSelectable && (!IsConflict || Resolution is Resolution.UseLocal or Resolution.UseGoogle);
    public bool ToGoogle => Entry.Kind is ChangeKind.Upload or ChangeKind.CreateRemote || Entry.DeletesRemote(Resolution) || IsConflict && Resolution == Resolution.UseLocal;
    public bool ToFolder => Entry.Kind is ChangeKind.Download or ChangeKind.CreateLocal or ChangeKind.Snapshot || Entry.DeletesLocal(Resolution) || IsConflict && Resolution == Resolution.UseGoogle;
    public string Direction => Entry.DeletesRemote(Resolution) ? "Удалить в Google" : Entry.DeletesLocal(Resolution) ? "Удалить файл" : Entry.Kind switch { ChangeKind.Upload or ChangeKind.CreateRemote => "В Google", ChangeKind.Download or ChangeKind.CreateLocal or ChangeKind.Snapshot => "В папку", ChangeKind.Conflict => "Разные правки — выберите версию", ChangeKind.Blocked => "Требует исправления", ChangeKind.Reconcile => "Проверить неизвестный результат", _ => Entry.Kind.ToString() };
    public string FieldLabel => Entry.Entity == EntityKind.Group && Entry.Field == "$entity" ? "Ярлык" : CapabilityRegistry.Title(Entry.Field);
    public string ResolutionLabel => Resolution switch { Resolution.UseLocal => "Выбрана версия из папки", Resolution.UseGoogle => "Выбрана версия Google", Resolution.Skip => "Отложено", _ => "" };
    partial void OnResolutionChanged(Resolution value) { OnPropertyChanged(nameof(ResolutionLabel)); OnPropertyChanged(nameof(Direction)); OnPropertyChanged(nameof(ToGoogle)); OnPropertyChanged(nameof(ToFolder)); }
    public void Choose(Resolution resolution) { Resolution = resolution; IsSelected = resolution is Resolution.UseLocal or Resolution.UseGoogle; }
}
public sealed record RunViewModel(RunSummary Summary)
{
    public string BackupSize => FormatBytes(Summary.BackupBytes);
    public static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} МБ" : bytes >= 1024 ? $"{bytes / 1024d:0.##} КБ" : $"{bytes} Б";
    public bool CanCleanup => Summary.BackupAvailable && Summary.Status is "complete" or "recovered";
    public string Caption => $"{Summary.StartedAt.ToLocalTime():g} · {StatusTitle} · {Summary.Confirmed} подтверждено, {Summary.Failed} ошибок, {Summary.Unknown} неизвестно\nРезервная копия: {(Summary.BackupAvailable ? BackupSize : "недоступна")}";
    private string StatusTitle => Summary.Status switch { "complete" => "Завершено", "partial" => "Выполнено частично", "running" => "Незавершённая операция", "recovered" => "Восстановлено", _ => Summary.Status };
}
