using System.Text.Json.Nodes;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;

namespace ContactMirror.Desktop;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool localEditorOpen;
    private SyncLocalEdit? activeLocalEdit;
    partial void OnLocalEditorOpenChanged(bool value) { RefreshCommands(); if(!value && Automation is not null) AutomationChanged(); }
    public bool CanEditEntry => HasPreview && CanConfigure && SelectedEntry is { Entry.Kind: not (ChangeKind.Blocked or ChangeKind.Reconcile or ChangeKind.Snapshot) } entry
        && (entry.Entry.Field == "$entity" || !entry.Entry.Field.StartsWith('$'));
    public async Task<SyncLocalEdit?> OpenSyncEditorAsync()
    {
        if (!CanEditEntry || !await Contacts.ResolveDraftAsync() || _preview is null || SelectedEntry is null) return null;
        if (!CanEditEntry) return null;
        var preview = _preview; var key = SelectedEntry.Entry.Key;
        LocalEditorOpen = true;
        try
        {
            var edit = await Task.Run(() => _sync.OpenLocalEditAsync(preview, key));
            if (_closeRequested) { LocalEditorOpen = false; return null; }
            activeLocalEdit = edit;
            LocalEditorOpen = true; RefreshCommands(); return edit;
        }
        catch (Exception error) { LocalEditorOpen = false; Error = error.Message; return null; }
    }
    public void CloseSyncEditor() { activeLocalEdit = null; LocalEditorOpen = false; RefreshCommands(); }
    private void RequireActiveEdit(SyncLocalEdit edit)
    {
        if (!LocalEditorOpen || !ReferenceEquals(activeLocalEdit, edit)) throw new InvalidOperationException("Форма уже закрыта или её версия устарела. Запись не выполнена.");
    }
    public async Task SaveSyncEditorAsync(SyncLocalEdit edit, JsonObject doc, ContactPhotoEdit photo)
    {
        if (_closeRequested) throw new InvalidOperationException("Приложение закрывается. Черновик не записан.");
        RequireActiveEdit(edit);
        using var lease = _activity.TryEnter() ?? throw new InvalidOperationException("Приложение занято.");
        var idle = _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await Task.Run(() => _sync.SaveLocalEditAsync(edit, doc, photo)); }
        catch { InvalidatePreview(); throw; }
        finally { idle.TrySetResult(); }
    }
    public async Task DeleteSyncEditorAsync(SyncLocalEdit edit)
    {
        if (_closeRequested) throw new InvalidOperationException("Приложение закрывается. Файл не удалён.");
        RequireActiveEdit(edit);
        using var lease = _activity.TryEnter() ?? throw new InvalidOperationException("Приложение занято.");
        var idle = _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await Task.Run(() => _sync.DeleteLocalEditAsync(edit)); }
        finally { InvalidatePreview(); idle.TrySetResult(); }
    }
    public async Task<SyncLocalEdit> SaveSyncDependenciesAsync(SyncLocalEdit edit)
    {
        if (_closeRequested) throw new InvalidOperationException("Приложение закрывается. Ярлыки не записаны.");
        RequireActiveEdit(edit);
        using var lease = _activity.TryEnter() ?? throw new InvalidOperationException("Приложение занято.");
        var idle = _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
        var old = Entries.ToArray();
        InvalidatePreview();
        await Task.Run(() => _sync.SaveLocalLabelDependenciesAsync(edit));
        _preview = await Task.Run(() => _sync.PrepareAsync(Folder, _account!)); LoadPreview(_preview);
        RestoreEditChoices(old);
        var key = _preview.Entries.FirstOrDefault(e => e.Entity == edit.Kind && e.Field == edit.Field && (edit.LocalExists ? e.EntityId == edit.EntityId
            : (e.Google?["resourceName"] ?? e.Google?["google"]?["resourceName"])?.ToString() == edit.ResourceName))?.Key
            ?? throw new SyncException("stalePlan", "Состояние изменилось. Закройте форму и проверьте сохранённые ярлыки; контакт ещё не записан.");
        var updated = await Task.Run(() => _sync.OpenLocalEditAsync(_preview, key)); activeLocalEdit = updated; return updated;
        } finally { idle.TrySetResult(); }
    }
    public async Task RefreshAfterSyncEditAsync(SyncLocalEdit edit)
    {
        if (_closeRequested) { InvalidatePreview(); CloseSyncEditor(); return; }
        using var lease = _activity.TryEnter() ?? throw new InvalidOperationException("Приложение занято.");
        var idle = _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var choices = Entries.ToArray(); var selected = SelectedEntry?.Entry.Key;
        InvalidatePreview(); CloseSyncEditor(); Status = "Сохранено в папку. Обновляем сравнение…";
        try
        {
            var preview = await Task.Run(() => _sync.PrepareAsync(Folder, _account!)); _preview = preview; LoadPreview(preview);
            AcknowledgeAgentRefresh();
            RestoreEditChoices(choices, edit);
            SelectedEntry = Entries.FirstOrDefault(e => e.Entry.Key == selected) ?? Entries.FirstOrDefault(e => e.Entry.EntityId == edit.EntityId) ?? Entries.FirstOrDefault();
            UpdateSelection(); Status = "Сохранено в папку. " + (Entries.Any(e => e.Entry.EntityId == edit.EntityId && (e.Entry.Field == edit.Field || edit.Field == "$entity" && e.CanEditLocal)) ? "Сравнение обновлено; отправка в Google — отдельно." : "Значение в папке совпадает с Google.");
        }
        catch (Exception error) { Error = error.Message; Status = "Сохранено в папку. Не удалось обновить сравнение; нажмите «Проверить изменения»."; }
        finally { idle.TrySetResult(); }
    }
    private void RestoreEditChoices(IReadOnlyList<EntryViewModel> choices, SyncLocalEdit? edit = null)
    {
        static string? Resource(SyncEntry entry) => entry.Google is JsonObject obj ? (obj["resourceName"] ?? (obj["google"] as JsonObject)?["resourceName"])?.ToString() : null;
        foreach (var entry in Entries)
        {
            var old = choices.FirstOrDefault(o => o.Entry.Key == entry.Entry.Key || o.Entry.Entity == entry.Entry.Entity && o.Entry.Field == entry.Entry.Field
                && Resource(o.Entry) is { } resource && resource == Resource(entry.Entry));
            if (old is null && edit?.Field == "$entity" && entry.Entry.EntityId == edit.EntityId && entry.Entry.Kind == ChangeKind.Upload)
                old = choices.FirstOrDefault(o => o.Entry.EntityId == edit.EntityId && o.Entry.Field == "$entity");
            var unchanged = old is not null && old.Entry.Kind == entry.Entry.Kind && JsonSemantics.Equal(old.Entry.Before, entry.Entry.Before)
                && JsonSemantics.Equal(old.Entry.Local, entry.Entry.Local) && JsonSemantics.Equal(old.Entry.Google, entry.Entry.Google)
                && old.Entry.PhotoComparison?.LocalHash == entry.Entry.PhotoComparison?.LocalHash && old.Entry.PhotoComparison?.GoogleHash == entry.Entry.PhotoComparison?.GoogleHash;
            if (unchanged) entry.Choose(old!.Resolution);
            entry.IsSelected = old is not null && old.IsSelected && (unchanged || entry.Entry.EntityId == edit?.EntityId && (entry.Entry.Field == edit.Field || edit.Field == "$entity" && entry.Entry.Kind == ChangeKind.Upload) && !entry.IsConflict && !entry.Entry.IsDestructive);
        }
        UpdateSelection();
    }
}
