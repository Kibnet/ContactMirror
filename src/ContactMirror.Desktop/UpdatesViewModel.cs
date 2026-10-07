using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContactMirror.Application;
using ContactMirror.Application.Updates;

namespace ContactMirror.Desktop;

public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IApplicationUpdateService? service;
    private readonly ApplicationActivity activity;
    private readonly Action<UpdateSnapshot> updateChanged;
    private readonly Action activityChanged;
    private UpdateSnapshot snapshot;
    private bool startupChecked;
    public UpdatesViewModel(IApplicationUpdateService? service, ApplicationActivity activity)
    {
        this.service = service; this.activity = activity;
        snapshot = service?.Snapshot ?? new(UpdateState.Unsupported, Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.2.0");
        updateChanged = value => Dispatch(() => { snapshot = value; Refresh(); });
        activityChanged = () => Dispatch(Refresh);
        if (service is not null) service.Changed += updateChanged;
        activity.Changed += activityChanged;
    }
    public string CurrentVersion => snapshot.CurrentVersion;
    public string TargetVersion => snapshot.TargetVersion ?? "";
    public int Progress => snapshot.Progress;
    public string Error => snapshot.Error ?? "";
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsDownloading => snapshot.State == UpdateState.Downloading;
    public bool IsChecking => snapshot.State == UpdateState.Checking;
    public bool CanCheck => service is not null && snapshot.State is UpdateState.Idle or UpdateState.Available or UpdateState.Error;
    public bool CanDownload => service is not null && snapshot.TargetVersion is not null && snapshot.State is UpdateState.Available or UpdateState.Error;
    public bool CanRestart => service is not null && snapshot.State == UpdateState.ReadyToRestart && !activity.IsActive && !activity.IsApplying;
    public bool IsReady => snapshot.State == UpdateState.ReadyToRestart;
    public string Status => snapshot.State switch
    {
        UpdateState.Unsupported => "Обновления доступны в копии, установленной через Setup или собранной Velopack portable.",
        UpdateState.NotConfigured => "В этой сборке источник обновлений ещё не настроен.",
        UpdateState.Checking => "Проверяем обновления…",
        UpdateState.Available => $"Доступна версия {TargetVersion}.",
        UpdateState.Downloading => $"Скачиваем обновление: {Progress}%.",
        UpdateState.ReadyToRestart => activity.IsActive ? "Обновление готово. Завершите синхронизацию или вход Google перед перезапуском." : "Обновление готово. Контактные файлы и подключение сохранятся после перезапуска.",
        UpdateState.Applying => "Устанавливаем обновление. Приложение перезапустится.",
        UpdateState.Error => "Не удалось проверить или скачать обновление. Текущая версия продолжает работать.",
        _ => snapshot.CheckedAt is null ? "Канал: стабильный. Можно проверить наличие новой версии." : "Установлена последняя доступная версия."
    };
    [RelayCommand(CanExecute = nameof(CanCheck))] private Task CheckAsync() => service?.CheckAsync() ?? Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanDownload))] private Task DownloadAsync() => service?.DownloadAsync() ?? Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanRestart))] private Task RestartAsync() => service?.ApplyAndRestartAsync() ?? Task.CompletedTask;
    [RelayCommand] private void Cancel() => service?.Cancel();
    public Task CheckForStartupAsync()
    {
        if (startupChecked || !CanCheck) return Task.CompletedTask;
        startupChecked = true;
        return service!.CheckAsync();
    }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(CurrentVersion), nameof(TargetVersion), nameof(Progress), nameof(Error), nameof(HasError), nameof(IsDownloading), nameof(IsChecking), nameof(CanCheck), nameof(CanDownload), nameof(CanRestart), nameof(IsReady), nameof(Status) }) OnPropertyChanged(name);
        CheckCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged();
    }
    private static void Dispatch(Action action) { if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action); }
    public void Detach() { if (service is not null) service.Changed -= updateChanged; activity.Changed -= activityChanged; }
}
