using Avalonia.Threading;
using ContactMirror.Application.Automation;
using ContactMirror.Core;

namespace ContactMirror.Desktop;

internal sealed class DesktopAgentUi(Func<Task> show) : IAgentUi
{
    public MainWindowViewModel? Model { get; set; }
    public Task<IDisposable?> TryEnterAsync(ContactMirror.Application.ApplicationActivity activity) => Dispatcher.UIThread.InvokeAsync(() => activity.TryEnter()).GetTask();
    public Task<AgentUiState> GetStateAsync() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var m = Model; return m is null ? new AgentUiState() : new(m.Contacts.Root ?? m.Folder, m.Contacts.Original?.Id, m.Contacts.IsDirty,
            m.Contacts.ValidationErrors.Count > 0, m.LocalEditorOpen || m.Contacts.HasModal || m.ShowDeleteConfirmation || m.ShowBackupDeleteConfirmation,
            m.IsBusy || m.Contacts.IsBusy, m.Contacts.Guard.Generation, HasWindow: true);
    }).GetTask();
    public async Task ShowWindowAsync() => await Dispatcher.UIThread.InvokeAsync(show);
    public async Task OpenContactAsync(string root, Guid id)
    {
        await ShowWindowAsync();
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var m = Model ?? throw new SyncException("host_unavailable", "Окно не удалось открыть.");
            if (m.LocalEditorOpen || m.Contacts.IsDirty || m.Contacts.HasModal || m.IsBusy || m.Contacts.IsActivityBusy) throw new SyncException("draft_conflict", "Сначала завершите текущий черновик или операцию.");
            m.Folder = root; await m.ShowContactsCommand.ExecuteAsync(null); await m.Contacts.SelectAsync(id);
            if (m.Contacts.Original?.Id != id) throw new SyncException("not_found", "Карточку контакта не удалось открыть.");
        });
    }
    public Task ChangedAsync(string? root)
    {
        Model?.RequestAgentRefresh(root); return Task.CompletedTask;
    }
}
