using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContactMirror.Core;

namespace ContactMirror.Desktop;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<ContactGroupViewModel> _groups = [];
    private bool _selectionBatch;
    private bool _syncingSelectedEntry;
    [ObservableProperty] private bool _folderReady;
    [ObservableProperty] private string _folderValidation = "";
    [ObservableProperty] private IReadOnlyList<ContactGroupViewModel> _visibleGroups = [];
    [ObservableProperty] private ContactGroupViewModel? _selectedGroup;
    [ObservableProperty] private IReadOnlyList<EntryViewModel> _fieldEntries = [];
    [ObservableProperty] private IReadOnlyList<DisplayDiffRow> _displayRows = [];
    [ObservableProperty] private IReadOnlyList<ResultRow> _resultRows = [];
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _deleteValidation = "";
    [ObservableProperty] private bool _requiresDeletePhrase;
    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private bool _showNarrowDetails;

    public bool IsWorkspace => !IsOnboarding;
    public bool HasPanel => ShowSettings || ShowHistory || ShowCapabilities;
    public bool ShowPlan => IsWorkspace && !ShowResult;
    public bool ShowList => !IsNarrow || !ShowNarrowDetails;
    public bool ShowDetails => !IsNarrow || ShowNarrowDetails;
    public bool ShowPlanTools => !IsNarrow || !ShowNarrowDetails;
    public bool IsEmptyPlan => HasPreview && Entries.Count == 0;
    public bool HasNoMatches => HasPreview && Entries.Count > 0 && VisibleGroups.Count == 0;
    public string EmptyTitle => IsEmptyPlan ? "✓ Контакты согласованы" : HasNoMatches ? "Ничего не найдено" : "Готово к проверке";
    public string EmptyExplanation => IsEmptyPlan ? "В Google и папке нет изменений. Новая проверка запускается вручную." : HasNoMatches ? "Измените поиск или фильтр. Общий выбор сохранён." : "Проверьте изменения, чтобы увидеть план перед записью.";
    public string AllFilterCaption => $"Все · {Entries.Count}";
    public string GoogleFilterCaption => $"В Google · {Entries.Count(e => e.ToGoogle)}";
    public string FolderFilterCaption => $"В папку · {Entries.Count(e => e.ToFolder)}";
    public string AttentionFilterCaption => $"Требуют решения · {Entries.Count(e => e.IsConflict && !e.CanApply || !e.Entry.IsSelectable)}";
    public string DeleteFilterCaption => $"Удаления · {Entries.Count(e => e.Entry.IsDestructiveFor(e.Resolution))}";
    public bool AllFilterActive => FilterIndex == 0;
    public bool GoogleFilterActive => FilterIndex == 1;
    public bool FolderFilterActive => FilterIndex == 2;
    public bool AttentionFilterActive => FilterIndex == 3;
    public bool DeleteFilterActive => FilterIndex == 4;
    public string NextConflictCaption => $"Следующий конфликт · {Entries.Count(e => e.IsConflict && !e.CanApply)}";
    public bool HasUnresolvedConflicts => Entries.Any(e => e.IsConflict && !e.CanApply);
    public bool CanChooseLocal => CanConfigure && HasPreview && SelectedEntry?.AllowsLocal == true;
    public bool CanChooseGoogle => CanConfigure && HasPreview && SelectedEntry?.AllowsGoogle == true;
    public bool CanChooseAny => CanConfigure && HasPreview && SelectedEntry?.IsConflict == true;
    public string LocalChoiceCaption => SelectedEntry?.Entry.Field == "$retry" ? "Создать ещё одну запись" : "Оставить из папки";
    public string GoogleChoiceCaption => SelectedEntry?.Entry.Field.StartsWith("$relink:", StringComparison.Ordinal) == true ? "Связать с найденной записью" : "Оставить из Google";
    public string LeftColumnTitle => HasSelectedConflict || SelectedEntry?.Entry.Kind == ChangeKind.Blocked ? "В папке" : SelectedEntry?.ToGoogle == true ? "Сейчас в Google" : "Сейчас в папке";
    public string RightColumnTitle => HasSelectedConflict || SelectedEntry?.Entry.Kind == ChangeKind.Blocked ? "В Google" : "После применения";
    public string PlannedOutcome => !HasPreview ? "Это прежний просмотр. Для записи выполните новую проверку." : SelectedEntry is not { } e ? "Выберите поле контакта." : !e.CanApply ? e.IsConflict ? "Выберите версию или отложите это изменение." : "Изменение не выбрано; запись не запланирована." : e.PlannedOutcome;
    public string ApplyHint => !HasPreview ? "Сначала проверьте изменения." : Entries.Any(e => e.CanApply) ? "Перед записью будет создана резервная копия." : "Выберите изменения; конфликты требуют выбора версии.";

    partial void OnIsNarrowChanged(bool value) { if (!value) ShowNarrowDetails = false; NotifyLayout(); }
    partial void OnShowNarrowDetailsChanged(bool value) => NotifyLayout();
    partial void OnShowResultChanged(bool value) { OnPropertyChanged(nameof(ShowPlan)); }
    partial void OnDeletePhraseChanged(string value) => DeleteValidation = "";
    partial void OnShowSettingsChanged(bool value) { if (value) { ShowHistory = false; ShowCapabilities = false; } NotifyPanel(); }
    partial void OnShowHistoryChanged(bool value) { if (value) { ShowSettings = false; ShowCapabilities = false; } NotifyPanel(); }
    partial void OnShowCapabilitiesChanged(bool value) { if (value) { ShowSettings = false; ShowHistory = false; } NotifyPanel(); }
    partial void OnSelectedGroupChanged(ContactGroupViewModel? value)
    {
        FieldEntries = value?.VisibleMembers ?? [];
        if (!_syncingSelectedEntry && (SelectedEntry is null || !FieldEntries.Contains(SelectedEntry))) SelectedEntry = FieldEntries.FirstOrDefault();
        if (value is not null && IsNarrow) ShowNarrowDetails = true;
    }
    private void NotifyPanel() => OnPropertyChanged(nameof(HasPanel));
    private void NotifyLayout() { OnPropertyChanged(nameof(ShowList)); OnPropertyChanged(nameof(ShowDetails)); OnPropertyChanged(nameof(ShowPlanTools)); }
    private void ValidateFolder()
    {
        FolderReady = false; FolderValidation = "";
        if (string.IsNullOrWhiteSpace(Folder)) return;
        try
        {
            ContactMirror.Infrastructure.Configuration.WorkspacePathPolicy.Current.Validate(Folder);
            if (!Directory.Exists(Folder)) { FolderValidation = "Папка не найдена или недоступна. Выберите существующую папку."; return; }
            FolderReady = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SyncException)
        { FolderValidation = error.Message; }
    }
    private void NotifySetup() { OnPropertyChanged(nameof(IsOnboarding)); OnPropertyChanged(nameof(IsWorkspace)); OnPropertyChanged(nameof(ShowPlan)); }
    private void NotifyDetails()
    {
        foreach (var name in new[] { nameof(CanChooseLocal), nameof(CanChooseGoogle), nameof(LocalChoiceCaption), nameof(GoogleChoiceCaption), nameof(LeftColumnTitle), nameof(RightColumnTitle), nameof(PlannedOutcome) }) OnPropertyChanged(name);
        UseLocalCommand.NotifyCanExecuteChanged(); UseGoogleCommand.NotifyCanExecuteChanged();
    }
    private void NotifyPlan()
    {
        foreach (var name in new[] { nameof(AllFilterCaption), nameof(GoogleFilterCaption), nameof(FolderFilterCaption), nameof(AttentionFilterCaption), nameof(DeleteFilterCaption), nameof(AllFilterActive), nameof(GoogleFilterActive), nameof(FolderFilterActive), nameof(AttentionFilterActive), nameof(DeleteFilterActive), nameof(NextConflictCaption), nameof(HasUnresolvedConflicts), nameof(ApplyHint), nameof(IsEmptyPlan), nameof(HasNoMatches), nameof(EmptyTitle), nameof(EmptyExplanation) }) OnPropertyChanged(name);
        NotifyDetails();
    }
    [RelayCommand] private void SetFilter(string index) { if (int.TryParse(index, out var value)) FilterIndex = value; }
    [RelayCommand] private void BackToList() => ShowNarrowDetails = false;
    [RelayCommand] private void ClosePanel() { ShowSettings = false; ShowHistory = false; ShowCapabilities = false; }

    private void BuildGroups()
    {
        _groups = Entries.GroupBy(e => (e.Entry.Entity, e.Entry.EntityId)).OrderBy(g => g.Key.Entity).Select(g => new ContactGroupViewModel(g.ToArray(), SelectGroup)).ToArray();
    }
    private void SelectGroup(ContactGroupViewModel group, bool select)
    {
        if (!HasPreview || !CanConfigure || ShowDeleteConfirmation) return;
        _selectionBatch = true;
        try
        {
            foreach (var entry in group.VisibleMembers)
                if (!select || entry.Entry.IsSelectable && !entry.IsRecoveryAlternative && !entry.Entry.IsDestructiveFor(entry.Resolution) && (!entry.IsConflict || entry.CanApply)) entry.IsSelected = select;
        }
        finally { _selectionBatch = false; }
        UpdateSelection();
    }
    private void ChooseVersion(Resolution resolution)
    {
        if (SelectedEntry is not { } current || resolution == Resolution.UseLocal && !CanChooseLocal || resolution == Resolution.UseGoogle && !CanChooseGoogle) return;
        if (current.Entry.Field.StartsWith("$relink:", StringComparison.Ordinal))
        {
            var other = Entries.FirstOrDefault(e => e != current && e.CanApply && e.Entry.EntityId != current.Entry.EntityId && e.Entry.Field == current.Entry.Field);
            if (other is not null) { Error = $"Эта запись Google уже выбрана для «{other.Entry.Name}». Сначала отложите тот вариант связи."; return; }
        }
        _selectionBatch = true;
        try
        {
            if (current.IsRecoveryAlternative)
                foreach (var alternative in Entries.Where(e => e != current && e.IsRecoveryAlternative && e.Entry.EntityId == current.Entry.EntityId && e.Entry.Entity == current.Entry.Entity)) alternative.Choose(Resolution.Skip);
            current.Choose(resolution);
        }
        finally { _selectionBatch = false; }
        UpdateSelection();
    }
    private bool RecoverySelectionValid => !Entries.Where(e => e.CanApply && e.IsRecoveryAlternative).GroupBy(e => (e.Entry.Entity, e.Entry.EntityId)).Any(g => g.Count() > 1)
        && !Entries.Where(e => e.CanApply && e.Entry.Field.StartsWith("$relink:", StringComparison.Ordinal)).GroupBy(e => e.Entry.Field).Any(g => g.Select(e => (e.Entry.Entity, e.Entry.EntityId)).Distinct().Count() > 1);

    private void PresentResult(SyncRunResult result, IReadOnlyList<(SyncEntry Entry, Resolution Resolution)> choices)
    {
        var presentation = WorkspacePresentation.Result(result, choices);
        ResultSummary = presentation.Summary; ResultRows = presentation.Rows; ShowResult = true;
        Status = presentation.Summary;
    }
}

public sealed partial class EntryViewModel
{
    public string SearchText { get; } = WorkspacePresentation.SearchText(entry);
    public bool IsRecoveryAlternative => Entry.Field == "$retry" || Entry.Field.StartsWith("$relink:", StringComparison.Ordinal);
    public bool AllowsLocal => IsConflict && !Entry.Field.StartsWith("$relink:", StringComparison.Ordinal);
    public bool AllowsGoogle => IsConflict && Entry.Field != "$retry";
    public string PlannedOutcome => Entry.Field.StartsWith("$relink:", StringComparison.Ordinal) ? "Будет установлена связь с найденной записью Google и сохранён локальный файл. Запись Google не создаётся заново." : Entry.Field == "$retry" ? "Будет создана ещё одна запись Google. Предыдущий запрос мог выполниться — возможен дубликат." : Entry.Field.StartsWith("$journal:", StringComparison.Ordinal) ? "Будет сверено состояние незавершённой операции; новые значения не отправляются." : Entry.Field == "$completeAbsent" ? "Будет завершена очистка локальной связи; контакты не создаются." : Entry.IsDestructiveFor(Resolution) ? WorkspacePresentation.DeletionDescription(this) : Entry.Field.StartsWith("$restore:", StringComparison.Ordinal) || Entry.Field == "$restoreCreate" ? $"Выбранная резервная версия {(Resolution == Resolution.UseGoogle ? "Google" : "папки")} будет восстановлена в обе стороны после применения." : ToGoogle ? "Версия из папки будет записана в Google после применения." : "Версия Google будет сохранена в папку после применения.";
}
