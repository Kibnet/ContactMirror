using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using ContactMirror.Infrastructure;

namespace ContactMirror.Desktop.Contacts;

public sealed class ContactRowViewModel(ContactCatalogItem item, IReadOnlyDictionary<Guid, string> labels)
{
    public Guid Id => item.Id;
    public string Name => ContactTableQuery.DisplayName(item.Document);
    public ContactCatalogItem Item => item;
    public string Cell(string column) => ContactTableQuery.FormatValues(item.Document, column, labels);
}

public sealed partial class ContactsViewModel : ObservableObject, IDisposable
{
    private readonly ContactEditorService service;
    private readonly ApplicationActivity activity;
    private readonly ContactTablePreferences preferences;
    private readonly Func<AccountIdentity?> account;
    private readonly Action onSaved;
    private CancellationTokenSource? loading, querying;
    private ContactCatalog? catalog;
    private ContactSnapshot? original;
    private JsonObject? document;
    private ContactPhotoEdit photoEdit = new();
    private TaskCompletionSource<string>? decision;
    private FileSystemWatcher? watcher;
    private readonly System.Timers.Timer changedTimer = new(400) { AutoReset = false };
    private bool disposed;
    private volatile bool writing;
    private int externalWriteEvent;
    private int fileChangeVersion;
    internal Action? BeforeWatch {get;set;}
    private readonly Dictionary<string,string> validationErrors = new();
    private int photoVersion;
    public DraftRestartGuard Guard { get; }
    public ContactTableConfiguration Configuration { get; private set; } = ContactTableConfiguration.Default;
    public double TableFraction { get; private set; } = .54;
    public ContactSnapshot? Original => original;
    public string? Root => catalog?.Root;
    public JsonObject? Document => document;
    public IReadOnlyDictionary<Guid, string> Labels => catalog?.Labels ?? new Dictionary<Guid, string>();
    public bool IsDirty => Guard.IsDirty;
    public bool IsActivityBusy => activity.IsActive || activity.IsApplying;
    public bool HasModal => ShowDecision || ShowDelete || ShowPhoto || ShowConfiguration || IsPickingPhoto;
    public bool HasContact => original is not null;
    public bool CanEdit => catalog is not null && HasContact && original?.ReadOnlyReason is null && catalog.ReadOnlyReason is null && !IsBusy && !IsActivityBusy && !HasModal;
    public bool CanCreate => catalog is not null && catalog.ReadOnlyReason is null && !IsBusy && !IsActivityBusy && !HasModal;
    public string Name => document is null ? "Выберите контакт" : ContactTableQuery.DisplayName(document);
    public string CountCaption => catalog is null ? "Выберите папку контактов" : $"Показано {Rows.Count} из {catalog.Contacts.Count}";
    public string IssuesCaption => catalog?.Issues.Count > 0 ? $"Ошибки файлов: {catalog.Issues.Count}" : "";
    public IReadOnlyList<WorkspaceIssue> Issues => catalog?.Issues ?? [];
    public async Task<string> ReadProblemFileAsync(WorkspaceIssue issue)
    {
        if(catalog is null || !Issues.Contains(issue)) throw new InvalidOperationException("Файл не входит в список проблем текущей папки.");
        var root=catalog.Root;
        return await Task.Run(()=>ContactMirror.Infrastructure.Contacts.ContactEditorStore.ReadProblemFileAsync(root,issue.Path));
    }
    public string ReadOnlyReason => original?.ReadOnlyReason ?? catalog?.ReadOnlyReason ?? "";
    public string HiddenSelection => original is not null && !Rows.Any(r => r.Id == original.Id) && !original.IsNew ? "Контакт не попадает в текущий фильтр. Карточка остаётся открытой." : "";
    public bool Starred => document?["starred"]?.GetValue<bool>() == true;
    public byte[]? PhotoBytes => photoEdit.Changed ? photoEdit.Bytes : original?.PhotoBytes;
    public IReadOnlyList<ContactRowViewModel> Rows { get; private set; } = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool showAllFields;
    [ObservableProperty] private bool showDecision;
    [ObservableProperty] private bool showDelete;
    [ObservableProperty] private bool showPhoto;
    [ObservableProperty] private bool showConfiguration;
    [ObservableProperty] private bool isPickingPhoto;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string status = "Сохраняем в папку. Отправка в Google — через проверку изменений.";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string fieldSearch = "";
    [ObservableProperty] private Bitmap? photoPreview;
    public event Action? StructureChanged;
    public event Action? FieldVisibilityChanged;
    public event Action? TableChanged;

    public ContactsViewModel(ContactEditorService service, ApplicationActivity activity, Func<AccountIdentity?> account,
        Action onSaved, bool demo = false, bool persist = true, ContactTablePreferences? preferenceStore = null)
    {
        this.service = service; this.activity = activity; this.account = account; this.onSaved = onSaved;
        preferences = preferenceStore ?? new(demo, persist); Guard = new(activity);
        Guard.PrepareTransitionAsync = () => OnUi(ResolveDraftAsync);
        Guard.Changed += Notify;
        activity.Changed += ActivityChanged;
        changedTimer.Elapsed += (_, _) =>
        {
            var version=Volatile.Read(ref fileChangeVersion);
            Dispatcher.UIThread.Post(() =>
            { if (!disposed&&!writing&&version==Volatile.Read(ref fileChangeVersion)) Status = "Файлы могли измениться вне приложения. Обновите список; черновик сохранён."; });
        };
    }
    private static async Task<bool> OnUi(Func<Task<bool>> action) => await Dispatcher.UIThread.InvokeAsync(action);
    private void ActivityChanged() => Dispatcher.UIThread.Post(Notify);
    partial void OnIsBusyChanged(bool value) => Notify();
    partial void OnShowDecisionChanged(bool value) => Notify();
    partial void OnShowDeleteChanged(bool value) => Notify();
    partial void OnShowPhotoChanged(bool value) => Notify();
    partial void OnShowConfigurationChanged(bool value) => Notify();
    partial void OnIsPickingPhotoChanged(bool value) => Notify();
    partial void OnSearchChanged(string value) { Configuration = Configuration with { Search = value }; _ = QueryAsync(); }
    partial void OnFieldSearchChanged(string value) => FieldVisibilityChanged?.Invoke();
    partial void OnShowAllFieldsChanged(bool value) => FieldVisibilityChanged?.Invoke();
    private void Notify()
    {
        foreach (var property in new[] { nameof(IsDirty), nameof(IsActivityBusy), nameof(HasModal), nameof(HasContact), nameof(CanEdit), nameof(CanCreate), nameof(Name), nameof(CountCaption), nameof(IssuesCaption), nameof(Issues), nameof(ReadOnlyReason), nameof(HiddenSelection), nameof(Starred) }) OnPropertyChanged(property);
    }
    public async Task LoadAsync(string root, bool reloadSelected = true)
    {
        var changingRoot = catalog is not null && (string.IsNullOrWhiteSpace(root) || !Path.GetFullPath(root).Equals(catalog.Root, StringComparison.OrdinalIgnoreCase));
        if (changingRoot && (IsDirty || HasModal || IsActivityBusy)) { Error = "Закройте диалог, сохраните или отмените изменения перед сменой папки."; return; }
        if (changingRoot && !Guard.Reset(change: () => { original = null; document = null; photoEdit = new(); })) return;
        if (changingRoot) { PhotoPreview?.Dispose(); PhotoPreview = null; IsEditing = false; StructureChanged?.Invoke(); }
        loading?.Cancel(); loading?.Dispose(); loading = new(); var token = loading.Token;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { catalog = null; Rows = []; IsBusy = false; Notify(); TableChanged?.Invoke(); return; }
        IsBusy = true; Error = "";
        try
        {
            var loaded = await Task.Run(() => service.LoadAsync(root, account(), token), token);
            token.ThrowIfCancellationRequested();
            var same = catalog?.Root == loaded.Root && catalog.WorkspaceId == loaded.WorkspaceId;
            catalog = loaded;
            if (!same)
            {
                var saved = preferences.Load(loaded.WorkspaceId, root); Configuration = saved.Table; TableFraction = saved.TableFraction;
                Watch(root);
            }
            if (preferences.Notice.Length > 0) Status = preferences.Notice;
            await QueryAsync();
            if(reloadSelected && original is {IsNew:false} selected && selected.Root==loaded.Root && !IsDirty && !HasModal)
            {
                if(loaded.Contacts.Any(contact=>contact.Id==selected.Id))
                {
                    var fresh=await Task.Run(()=>service.ReadAsync(loaded.Root,selected.Id,account(),token),token);
                    token.ThrowIfCancellationRequested();
                    if(ReferenceEquals(original,selected)&&!IsDirty&&!HasModal)SetSnapshot(fresh);
                }
                else if(Guard.Reset(change:()=>{original=null;document=null;photoEdit=new();}))
                {IsEditing=false;PhotoPreview?.Dispose();PhotoPreview=null;Notify();StructureChanged?.Invoke();}
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SyncException or System.Text.Json.JsonException) { Error = e.Message; }
        finally { if (!token.IsCancellationRequested) IsBusy = false; }
    }
    private void Watch(string root)
    {
        BeforeWatch?.Invoke();
        watcher?.Dispose(); watcher = new(root, "*.json") { IncludeSubdirectories = true, EnableRaisingEvents = true };
        watcher.Changed += FilesChanged; watcher.Created += FilesChanged; watcher.Deleted += FilesChanged; watcher.Renamed += FilesChanged;
    }
    private void FilesChanged(object sender, FileSystemEventArgs args)
    {
        if (disposed || !ReferenceEquals(sender,watcher) || args.FullPath.Contains(Path.DirectorySeparatorChar + ".contactmirror" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        if(writing)
        {
            if(!args.FullPath.Equals(original?.Path,StringComparison.OrdinalIgnoreCase))Interlocked.Exchange(ref externalWriteEvent,1);
            return;
        }
        try { changedTimer.Stop(); changedTimer.Start(); } catch (ObjectDisposedException) { }
    }
    public async Task QueryAsync()
    {
        if (catalog is null) return;
        querying?.Cancel(); querying?.Dispose(); querying = new(); var token = querying.Token;
        var source = catalog; var config = Configuration;
        try
        {
            var rows = await Task.Run(() => ContactTableQuery.Apply(source, config, token).Select(item => new ContactRowViewModel(item, source.Labels)).ToArray(), token);
            if (token.IsCancellationRequested || catalog != source) return;
            Rows = rows; OnPropertyChanged(nameof(Rows)); Notify(); TableChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
    }
    public void Configure(ContactTableConfiguration configuration)
    {
        Configuration = ContactTableQuery.Normalize(configuration) with { Search = Search };
        SavePreferences(); _ = QueryAsync();
    }
    public void SetTableFraction(double value) { TableFraction = Math.Clamp(value, .25, .8); SavePreferences(); }
    private void SavePreferences()
    {
        if (catalog is null) return;
        preferences.Save(catalog.WorkspaceId, catalog.Root, new(Configuration, TableFraction));
        if (preferences.Notice.Length > 0) Status = preferences.Notice;
    }
    public async Task SelectAsync(Guid id)
    {
        if (catalog is null || IsBusy || HasModal || IsActivityBusy || original?.Id == id) return;
        if (!await ResolveDraftAsync()) return;
        IsBusy = true; Error = "";
        try
        {
            var snapshot = await Task.Run(() => service.ReadAsync(catalog.Root, id, account()));
            SetSnapshot(snapshot);
        }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException) { Error = e.Message; }
        finally { IsBusy = false; }
    }
    public void BeginEdit()
    {
        if (!CanEdit) return;
        IsEditing = true; StructureChanged?.Invoke();
    }
    public async Task CreateAsync()
    {
        if (!CanCreate || !await ResolveDraftAsync() || catalog is null) return;
        var id = Guid.NewGuid();
        var doc = new JsonObject { ["schemaVersion"] = 1, ["id"] = id.ToString(), ["data"] = new JsonObject(), ["labels"] = new JsonArray(), ["starred"] = false, ["photo"] = null, ["extensions"] = new JsonObject(), ["google"] = null };
        SetSnapshot(new(catalog.Root, catalog.WorkspaceId, id, Path.Combine(catalog.Root, "contacts", id + ".contact.json"), doc, null, null, null, null, BindingHash: catalog.BindingHash, LabelsHash: catalog.LabelsHash));
        IsEditing = true; Guard.Reset(true); StructureChanged?.Invoke();
    }
    private void SetSnapshot(ContactSnapshot snapshot)
    {
        if (!Guard.Reset(change: () => { original = snapshot; document = (JsonObject)snapshot.Document.DeepClone(); photoEdit = new(); })) return;
        validationErrors.Clear(); IsEditing = false; FieldSearch = ""; Error = ""; DecodePhoto(); Notify(); StructureChanged?.Invoke();
    }
    public bool Mutate(Action<JsonObject> mutation, bool rebuild = false)
    {
        if (!IsEditing || !CanEdit || document is null) return false;
        var changed = Guard.TryMutate(() => mutation(document));
        if (changed)
        {
            Error = ""; Notify();
            if (rebuild) StructureChanged?.Invoke();
        }
        return changed;
    }
    public void SetStarred(bool value) => Mutate(doc => doc["starred"] = value);
    public void SetLabel(Guid id, bool selected) => Mutate(doc =>
    {
        var ids = (doc["labels"] as JsonArray ?? []).Select(n => Guid.Parse(n!.ToString())).ToHashSet();
        if (selected) ids.Add(id); else ids.Remove(id);
        doc["labels"] = new JsonArray(ids.Order().Select(id => JsonValue.Create(id.ToString())).ToArray());
    });
    public bool HasLabel(Guid id) => (document?["labels"] as JsonArray)?.Any(n => n?.ToString() == id.ToString()) == true;
    public bool ChangePhoto(byte[]? bytes)
    {
        if (!IsEditing || !CanEdit) return false;
        if (PhotoEqual(bytes)) return false;
        try { if (bytes is not null) FileWorkspaceStore.ValidateImage(bytes); }
        catch (SyncException error) { Error = error.Message; return false; }
        if (!Guard.TryMutate(() => photoEdit = new(true, bytes?.ToArray()))) return false;
        DecodePhoto(); StructureChanged?.Invoke(); return true;
    }
    internal bool ChangeValidatedPhoto(byte[] bytes)
    {
        if (PhotoEqual(bytes)) return false;
        if (!IsEditing || !CanEdit || !Guard.TryMutate(() => photoEdit = new(true,bytes.ToArray()))) return false;
        DecodePhoto(); StructureChanged?.Invoke(); return true;
    }
    private bool PhotoEqual(byte[]? bytes) => bytes is null ? PhotoBytes is null : PhotoBytes is { } current && current.AsSpan().SequenceEqual(bytes);
    private void DecodePhoto()
    {
        var version = ++photoVersion;
        PhotoPreview?.Dispose(); PhotoPreview = null;
        if (PhotoBytes is not { } bytes) return;
        _ = DecodePhotoAsync(bytes,version);
    }
    private async Task DecodePhotoAsync(byte[] bytes,int version)
    {
        try
        {
            var preview=await Task.Run(()=>ContactPhotoPreview.DecodeBounded(bytes,320));
            if(disposed || photoVersion!=version){preview.Dispose();return;}
            PhotoPreview=preview;
        }
        catch(Exception e) when(e is ArgumentException or IOException or InvalidOperationException)
        { if(!disposed && photoVersion==version) Status="Не удалось показать фотографию. Файл сохранён; попробуйте перечитать карточку."; }
    }
    public void ValidationError(string message, string key = "input")
    {
        if (message.Length == 0) { foreach (var stale in validationErrors.Keys.Where(k => k == key || k.StartsWith(key + ".", StringComparison.Ordinal)).ToArray()) validationErrors.Remove(stale); } else validationErrors[key] = message;
        Error = string.Join("\n", validationErrors.Values);
    }
    public async Task<bool> SaveAsync(bool fromDecision = false)
    {
        if (original is null || document is null || !IsEditing || IsBusy || IsActivityBusy || ShowDelete || ShowPhoto || ShowConfiguration || IsPickingPhoto || ShowDecision && !fromDecision) return false;
        if (validationErrors.Count > 0) { Error = string.Join("\n", validationErrors.Values); return false; }
        if (!IsDirty) { IsEditing = false; StructureChanged?.Invoke(); return true; }
        Interlocked.Exchange(ref externalWriteEvent,0);writing=true;Interlocked.Increment(ref fileChangeVersion);changedTimer.Stop();
        IsBusy = true; Error = ""; Status = "Сохраняем контакт в папку…";
        try
        {
            var snapshot = original; var output = (JsonObject)document.DeepClone(); var photo = photoEdit;
            var saved = await Task.Run(() => service.SaveAsync(snapshot, output, photo, account()));
            SetSnapshot(saved); Status = "Сохранено в папку. Для отправки в Google проверьте изменения.";
            onSaved(); await LoadAsync(saved.Root,reloadSelected:false); return true;
        }
        catch (ContactEditorWriteException e)
        {
            onSaved();
            if(e.Outcome == ContactWriteOutcome.Committed && e.Snapshot is { } saved)
            {
                SetSnapshot(saved); await LoadAsync(saved.Root,reloadSelected:false); Error=e.Message;
                Status="Контакт записан в папку. Завершение записи требует проверки восстановления."; return true;
            }
            Error=e.Message; Status=e.Outcome == ContactWriteOutcome.Unchanged ? "Запись не выполнена. Черновик остаётся в карточке." : "Результат записи не подтверждён. Черновик сохранён; перечитайте файлы и проверьте восстановление."; return false;
        }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Error = e.Message; Status = "Контакт не сохранён. Черновик остаётся в карточке."; return false; }
        finally { try{EndWrite();}finally{IsBusy = false;} }
    }
    public async Task<bool> ResolveDraftAsync()
    {
        if (ShowDelete || ShowPhoto || ShowConfiguration || IsPickingPhoto || IsBusy) return false;
        if (!IsDirty) return true;
        if (decision is not null) return false;
        decision = new(TaskCreationOptions.RunContinuationsAsynchronously); ShowDecision = true;
        var answer = await decision.Task; decision = null;
        if (answer == "save")
        {
            if (!await SaveAsync(fromDecision: true)) { ShowDecision = false; return false; }
            ShowDecision = false; return true;
        }
        ShowDecision = false;
        if (answer != "discard") return false;
        CancelEdit(); return true;
    }
    public void Decide(string answer) => decision?.TrySetResult(answer);
    public void CancelEdit()
    {
        if (IsBusy || IsActivityBusy || HasModal || original is null) return;
        if (original.IsNew) { if (!Guard.Reset(change: () => { original = null; document = null; })) return; validationErrors.Clear(); IsEditing = false; PhotoPreview?.Dispose(); PhotoPreview = null; Notify(); StructureChanged?.Invoke(); }
        else SetSnapshot(original);
    }
    public void AskDelete()
    {
        if (!CanEdit || IsEditing || original?.IsNew != false) return;
        ShowDelete = true; Notify();
    }
    public void DismissDelete() { ShowDelete = false; Notify(); }
    public async Task ConfirmDeleteAsync()
    {
        if (!ShowDelete || IsBusy || activity.IsActive || activity.IsApplying || original is null) return;
        Interlocked.Exchange(ref externalWriteEvent,0);writing=true;Interlocked.Increment(ref fileChangeVersion);changedTimer.Stop();
        IsBusy = true; Error = ""; Status = "Перемещаем контакт в локальную корзину…";
        try
        {
            await Task.Run(() => service.DeleteAsync(original, account()));
            var root = original.Root; original = null; document = null; Guard.Reset(); ShowDelete = false; PhotoPreview?.Dispose(); PhotoPreview = null;
            Status = "Контакт перемещён в локальную корзину. Google пока не изменён."; onSaved(); StructureChanged?.Invoke(); await LoadAsync(root);
        }
        catch (ContactEditorWriteException e)
        {
            onSaved();
            Error=e.Message;
            if(e.Outcome == ContactWriteOutcome.Committed)
            {
                var root=original!.Root; Guard.Reset(change:()=> { original=null; document=null; }); ShowDelete=false;
                PhotoPreview?.Dispose();PhotoPreview=null;StructureChanged?.Invoke();await LoadAsync(root);
                Error=e.Message;
                Status="Контакт удалён из папки. Завершение операции требует проверки восстановления.";
            }
            else Status=e.Outcome == ContactWriteOutcome.Unchanged ? "Удаление не выполнено. Контакт остаётся в папке." : "Результат удаления не подтверждён. Проверьте папку и восстановление.";
        }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException) { Error = e.Message; Status = "Удаление не выполнено. Контакт остаётся в папке; проверьте причину отказа."; }
        finally { try{EndWrite();}finally{IsBusy = false; Notify();} }
    }
    private void EndWrite()
    {
        try
        {
            changedTimer.Stop();Interlocked.Increment(ref fileChangeVersion);
            if(!disposed&&catalog is { } current&&Directory.Exists(current.Root))Watch(current.Root);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Error=string.Join("\n",new[]{Error,"Не удалось следить за файлами папки. Обновляйте список вручную: "+error.Message}.Where(value=>value.Length>0)); }
        finally
        {
            writing=false;
            if(Interlocked.Exchange(ref externalWriteEvent,0)!=0)Status+=" Другие файлы могли измениться. Обновите список; черновик сохранён.";
        }
    }
    public async Task ReloadSelectedAsync()
    {
        if (original is null || original.IsNew || IsBusy) return;
        var id = original.Id;
        if (!await ResolveDraftAsync()) return;
        original = null;
        await SelectAsync(id);
    }
    public void Dispose()
    {
        disposed = true; decision?.TrySetResult("stay"); watcher?.Dispose(); changedTimer.Dispose(); loading?.Cancel(); loading?.Dispose(); querying?.Cancel(); querying?.Dispose();
        PhotoPreview?.Dispose(); Guard.Changed -= Notify; activity.Changed -= ActivityChanged;
    }
}
