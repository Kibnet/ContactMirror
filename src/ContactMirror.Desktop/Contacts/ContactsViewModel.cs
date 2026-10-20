using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
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
    public string Cell(string column) => ContactValuePresenter.FormatColumn(item.Document, column, labels);
}

public sealed class EditorPopupContext
{
    internal EditorPopupContext(ContactsViewModel owner, JsonObject document, ContactSnapshot snapshot, long generation, string path, bool editing)
    { Owner = owner; Document = document; Root = snapshot.Root; ContactId = snapshot.Id; ContactPath = snapshot.Path; WorkspaceId = snapshot.WorkspaceId; Generation = generation; Path = path; Editing = editing; }
    internal ContactsViewModel Owner { get; }
    internal JsonObject Document { get; }
    internal string Root { get; }
    internal Guid ContactId { get; }
    internal Guid WorkspaceId { get; }
    internal string ContactPath { get; }
    internal bool Editing { get; }
    internal bool Retired { get; set; }
    public long Generation { get; }
    public string Path { get; }
}

public sealed record EditorInvalidInput(string Value, string Error);
public sealed record EditorPopupInputChange(string? ClearPrefix = null, IReadOnlyDictionary<string,EditorInvalidInput>? Invalid = null);

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
    private int selectedFileChange;
    private EditorPopupContext? editorPopup;
    internal Action? BeforeWatch {get;set;}
    private readonly Dictionary<string,string> validationErrors = new();
    private readonly Dictionary<string,string> invalidInputs = new();
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
    public bool HasModal => editorPopup is not null || HasForeignModal;
    public bool HasEditorPopup => editorPopup is not null;
    private bool HasForeignModal => ShowDecision || ShowDelete || ShowPhoto || ShowConfiguration || IsPickingPhoto;
    public bool HasContact => original is not null;
    public bool IsNewContact => original?.IsNew == true;
    public IReadOnlyDictionary<string,string> ValidationErrors { get; }
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
    [ObservableProperty] private bool hasExternalDrift;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string status = "Сохраняем в папку. Отправка в Google — через проверку изменений.";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string fieldSearch = "";
    [ObservableProperty] private Bitmap? photoPreview;
    public event Action? StructureChanged;
    public event Action? FieldVisibilityChanged;
    public event Action? TableChanged;
    public event Action<string>? ErrorFocusRequested;
    public void RefreshStructure() => StructureChanged?.Invoke();
    internal ContactPhotoEdit DraftPhoto => new(photoEdit.Changed, photoEdit.Bytes?.ToArray());
    internal void RemapSyncDraft(SyncLocalEdit previous, SyncLocalEdit updated)
    {
        if (document is null || original is null) return;
        // Only the coordinator's refreshed remote identity may remap a never-saved draft.
        catalog = new(updated.Root, Guid.Empty, [], updated.Labels, []);
        foreach (var field in CapabilityRegistry.WritableFields)
            if (JsonNode.DeepEquals(document["data"]?[field], previous.Document["data"]?[field]))
            {
                if (updated.Document["data"]!.AsObject().ContainsKey(field)) document["data"]![field] = updated.Document["data"]![field]?.DeepClone();
                else document["data"]!.AsObject().Remove(field);
            }
        var before = DocumentCodec.Contact(previous.Document).Labels.ToHashSet(); var pending = DocumentCodec.Contact(document).Labels.ToHashSet();
        var labels = DocumentCodec.Contact(updated.Document).Labels.ToHashSet();
        foreach (var id in before.Union(pending).Where(id => before.Contains(id) != pending.Contains(id)))
        {
            var mapped = previous.LabelResources.TryGetValue(id, out var resource) ? updated.LabelResources.FirstOrDefault(pair => pair.Value == resource).Key : id;
            if (mapped == Guid.Empty) throw new SyncException("groupDependency", "Изменённый ярлык исчез из Google. Проверьте выбор; черновик сохранён.");
            if (pending.Contains(id)) labels.Add(mapped); else labels.Remove(mapped);
        }
        document["id"] = updated.EntityId.ToString(); document["google"] = updated.Document["google"]?.DeepClone();
        document["labels"] = new JsonArray(labels.Select(id => (JsonNode?)JsonValue.Create(id.ToString())).ToArray());
        original = original with { Id = updated.EntityId, Document = (JsonObject)updated.Document.DeepClone() };
        RefreshStructure();
    }
    internal void OpenSyncDraft(SyncLocalEdit edit)
    {
        catalog = new(edit.Root, Guid.Empty, [], edit.Labels.Concat(edit.MissingLabels).ToDictionary(x => x.Key, x => x.Value), []);
        SetSnapshot(new(edit.Root, Guid.Empty, edit.EntityId, "", (JsonObject)edit.Document.DeepClone(), null, "sync-draft", edit.PhotoBytes?.ToArray(), edit.PhotoBytes is { } bytes ? JsonSemantics.Hash(bytes) : null));
        BeginEdit();
    }

    public ContactsViewModel(ContactEditorService service, ApplicationActivity activity, Func<AccountIdentity?> account,
        Action onSaved, bool demo = false, bool persist = true, ContactTablePreferences? preferenceStore = null)
    {
        this.service = service; this.activity = activity; this.account = account; this.onSaved = onSaved;
        preferences = preferenceStore ?? new(demo, persist); Guard = new(activity);
        ValidationErrors = new ReadOnlyDictionary<string,string>(validationErrors);
        Guard.IsTransitionBlocked = () => disposed || HasModal || IsBusy;
        Guard.PrepareTransitionAsync = () => OnUi(ResolveDraftAsync);
        Guard.Changed += Notify;
        activity.Changed += ActivityChanged;
        changedTimer.Elapsed += (_, _) =>
        {
            var version=Volatile.Read(ref fileChangeVersion);
            Dispatcher.UIThread.Post(() =>
            {
                if (!disposed&&!writing&&version==Volatile.Read(ref fileChangeVersion))
                {
                    if (Interlocked.Exchange(ref selectedFileChange,0) != 0) HasExternalDrift = true;
                    Status = HasExternalDrift ? "Файл изменился вне редактора. Загрузите актуальную версию или оставьте черновик." : "Файлы могли измениться вне приложения. Обновите список; черновик сохранён.";
                }
            });
        };
    }
    private static async Task<bool> OnUi(Func<Task<bool>> action) => await Dispatcher.UIThread.InvokeAsync(action);
    private void ActivityChanged() { if (Dispatcher.UIThread.CheckAccess()) Notify(); else Dispatcher.UIThread.Post(Notify); }
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
        foreach (var property in new[] { nameof(IsDirty), nameof(IsActivityBusy), nameof(HasModal), nameof(HasEditorPopup), nameof(HasContact), nameof(IsNewContact), nameof(CanEdit), nameof(CanCreate), nameof(Name), nameof(CountCaption), nameof(IssuesCaption), nameof(Issues), nameof(ReadOnlyReason), nameof(HiddenSelection), nameof(Starred), nameof(ValidationErrors) }) OnPropertyChanged(property);
    }
    public async Task LoadAsync(string root, bool reloadSelected = true)
    {
        var changingRoot = catalog is not null && (string.IsNullOrWhiteSpace(root) || !Path.GetFullPath(root).Equals(catalog.Root, StringComparison.OrdinalIgnoreCase));
        if (changingRoot && (IsDirty || HasModal || IsActivityBusy)) { Error = "Закройте диалог, сохраните или отмените изменения перед сменой папки."; return; }
        if (changingRoot && !Guard.Reset(change: () => { original = null; document = null; photoEdit = new(); ClearDraftUi(); })) return;
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
                else if(Guard.Reset(change:()=>{original=null;document=null;photoEdit=new();ClearDraftUi();}))
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
        if (args.FullPath.Equals(original?.Path,StringComparison.OrdinalIgnoreCase)
            || args is RenamedEventArgs renamed && renamed.OldFullPath.Equals(original?.Path,StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref selectedFileChange,1);
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
    public EditorPopupContext? BeginEditorPopup(string path = "")
    {
        if (disposed || original is null || document is null || catalog is null || IsBusy || HasModal || IsActivityBusy) return null;
        var generation = Guard.Generation;
        EditorPopupContext? context = null;
        if (!Guard.TryMutateChecked(generation,
            () => !disposed && original is not null && document is not null && catalog is not null && !IsBusy && !HasModal,
            () => { editorPopup = context = new(this, document!, original!, generation + 1, path, IsEditing); return false; })) return null;
        Notify(); return context;
    }
    public bool EndEditorPopup(EditorPopupContext context, Action<JsonObject>? confirmedMutation = null, bool rebuild = false, EditorPopupInputChange? inputChange = null)
    {
        if (!ReferenceEquals(context.Owner,this) || context.Retired || !ReferenceEquals(editorPopup,context)) return false;
        var dataChanged = false;
        try
        {
            var accepted = Guard.TryMutateChecked(context.Generation,
                () => !disposed && !IsBusy && !HasForeignModal && ReferenceEquals(document,context.Document)
                    && original is { } selected && selected.Id == context.ContactId && selected.Path == context.ContactPath && selected.Root == context.Root
                    && catalog is { } current && current.Root == context.Root && current.WorkspaceId == context.WorkspaceId
                    && Guid.TryParse(document?["id"]?.ToString(),out var currentId) && currentId == context.ContactId
                    && (confirmedMutation is null && inputChange is null || context.Editing && IsEditing && selected.ReadOnlyReason is null && current.ReadOnlyReason is null),
                () =>
                {
                    var inputValues = inputChange?.Invalid?.ToArray() ?? [];
                    if (inputValues.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.Error)))
                        throw new ArgumentException("Для проблемного ввода нужны путь, значение и сообщение об ошибке.",nameof(inputChange));
                    JsonObject? candidate = null;
                    Dictionary<JsonNode, JsonNode> origins = new(ReferenceEqualityComparer.Instance);
                    if (confirmedMutation is not null)
                    {
                        candidate = (JsonObject)document!.DeepClone();
                        RememberOrigins(document, candidate, origins);
                        confirmedMutation(candidate);
                        DocumentCodec.EnsureProtectedCategoriesUnchanged(candidate, original!.Document);
                        dataChanged = !JsonNode.DeepEquals(document,candidate);
                    }
                    editorPopup = null; context.Retired = true;
                    if (dataChanged) ApplyObjectPatch(document!,candidate!, origins);
                    if (inputChange?.ClearPrefix is { } prefix && !string.IsNullOrWhiteSpace(prefix))
                    {
                        foreach (var key in invalidInputs.Keys.Concat(validationErrors.Keys).Where(key => key == prefix || key.StartsWith(prefix + ".",StringComparison.Ordinal)).Distinct().ToArray())
                        { invalidInputs.Remove(key); validationErrors.Remove(key); }
                    }
                    foreach (var pair in inputValues) { invalidInputs[pair.Key] = pair.Value.Value; validationErrors[pair.Key] = pair.Value.Error; }
                    return dataChanged || inputValues.Length > 0;
                });
            if (!accepted)
            {
                RetirePopup(context);
                Status = "Результат выбора устарел или приложение занято. Текущий черновик сохранён; откройте выбор снова.";
            }
            else if (dataChanged || inputChange is not null) { RefreshValidation(); if (rebuild) StructureChanged?.Invoke(); }
            Notify(); return accepted;
        }
        catch (Exception error)
        {
            RetirePopup(context); Error = error.Message;
            Status = "Не удалось применить выбор. Текущий черновик сохранён."; Notify(); return false;
        }
    }
    public bool IsEditorPopupCurrent(EditorPopupContext? context) => context is not null && !disposed && !context.Retired
        && ReferenceEquals(context.Owner,this) && ReferenceEquals(editorPopup,context) && ReferenceEquals(context.Document,document)
        && context.Generation == Guard.Generation && original is { } selected && context.ContactId == selected.Id && context.ContactPath == selected.Path
        && context.Root == selected.Root && catalog is { } current && context.Root == current.Root && context.WorkspaceId == current.WorkspaceId;
    private void RetirePopup(EditorPopupContext context)
    {
        if (ReferenceEquals(editorPopup,context)) editorPopup = null;
        context.Retired = true;
    }
    // Capture correspondence before the guarded mutation, while both trees have identical structure.
    // This keeps existing row objects through deletes/reorders without matching equal-looking rows.
    private static void RememberOrigins(JsonNode original, JsonNode clone, Dictionary<JsonNode, JsonNode> origins)
    {
        origins.Add(clone, original);
        if (original is JsonObject obj && clone is JsonObject copy)
            foreach (var pair in obj) if (pair.Value is { } value && copy[pair.Key] is { } child) RememberOrigins(value, child, origins);
        if (original is JsonArray array && clone is JsonArray copied)
            for (var i = 0; i < array.Count; i++) if (array[i] is { } value && copied[i] is { } child) RememberOrigins(value, child, origins);
    }
    private static void ApplyObjectPatch(JsonObject current, JsonObject candidate, Dictionary<JsonNode, JsonNode> origins)
    {
        foreach (var key in current.Select(pair => pair.Key).Except(candidate.Select(pair => pair.Key)).ToArray()) current.Remove(key);
        foreach (var pair in candidate)
        {
            if (JsonNode.DeepEquals(current[pair.Key],pair.Value)) continue;
            if (current[pair.Key] is JsonObject oldObject && pair.Value is JsonObject newObject) ApplyObjectPatch(oldObject,newObject, origins);
            else if (current[pair.Key] is JsonArray oldArray && pair.Value is JsonArray newArray) ApplyArrayPatch(oldArray, newArray, origins);
            else current[pair.Key] = pair.Value?.DeepClone();
        }
    }
    private static void ApplyArrayPatch(JsonArray current, JsonArray candidate, Dictionary<JsonNode, JsonNode> origins)
    {
        var existing = current.Where(n => n is not null).ToHashSet(ReferenceEqualityComparer.Instance);
        current.Clear();
        foreach (var child in candidate)
        {
            if (child is not null && origins.TryGetValue(child, out var original) && existing.Contains(original))
            {
                if (original is JsonObject obj && child is JsonObject copy) ApplyObjectPatch(obj, copy, origins);
                else if (original is JsonArray array && child is JsonArray copied) ApplyArrayPatch(array, copied, origins);
                else { current.Add(child.DeepClone()); continue; }
                current.Add(original);
            }
            else current.Add(child?.DeepClone());
        }
    }
    public string GetInput(string path, string fallback) => invalidInputs.GetValueOrDefault(path,fallback);
    public bool SetInvalidInput(string path, string typedValue, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(message) || !IsEditing || !CanEdit || document is null) return false;
        if (invalidInputs.GetValueOrDefault(path) == typedValue && validationErrors.GetValueOrDefault(path) == message) return true;
        if (!Guard.TryMutate(() => { invalidInputs[path] = typedValue; validationErrors[path] = message; })) return false;
        RefreshValidation(); Notify(); return true;
    }
    public bool ClearInput(string path, bool includeChildren = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsEditing || !CanEdit) return false;
        var keys = invalidInputs.Keys.Concat(validationErrors.Keys).Where(key => key == path || includeChildren && key.StartsWith(path + ".",StringComparison.Ordinal)).Distinct().ToArray();
        if (keys.Length == 0) return true;
        if (!Guard.TryMutateChecked(Guard.Generation, () => IsEditing && CanEdit,
            () => { foreach (var key in keys) { invalidInputs.Remove(key); validationErrors.Remove(key); } return false; })) return false;
        RefreshValidation(); Notify(); return true;
    }
    public bool RemoveRowInputs(string categoryPath, int removedIndex)
    {
        if (string.IsNullOrWhiteSpace(categoryPath) || removedIndex < 0 || !IsEditing || !CanEdit) return false;
        string? NewPath(string path)
        {
            if (!path.StartsWith(categoryPath + ".",StringComparison.Ordinal)) return path;
            var start = categoryPath.Length + 1; var end = path.IndexOf('.',start);
            if (end < 0) end = path.Length;
            if (!int.TryParse(path.AsSpan(start,end-start),out var index)) return path;
            return index == removedIndex ? null : index > removedIndex ? categoryPath + "." + (index - 1) + path[end..] : path;
        }
        if (!invalidInputs.Keys.Concat(validationErrors.Keys).Any(path => NewPath(path) != path)) return true;
        if (!Guard.TryMutateChecked(Guard.Generation, () => IsEditing && CanEdit,
            () =>
            {
                foreach (var values in new[] { invalidInputs,validationErrors })
                {
                    var mapped = values.Select(pair => (Path:NewPath(pair.Key),pair.Value)).Where(pair => pair.Path is not null).ToArray();
                    values.Clear(); foreach (var pair in mapped) values[pair.Path!] = pair.Value;
                }
                return false;
            })) return false;
        RefreshValidation(); Notify(); return true;
    }
    public void RequestErrorFocus(string path)
    { if (!disposed && validationErrors.ContainsKey(path)) ErrorFocusRequested?.Invoke(path); }
    public void StayWithDraft()
    { if (HasExternalDrift && !disposed) Status = "Черновик оставлен в карточке. Файл изменился вне редактора; перед повторной записью загрузите актуальную версию."; }
    private void RefreshValidation()
    {
        Error = string.Join("\n",validationErrors.Values);
        if (Status.StartsWith("Не сохранено. Ошибки в полях:", StringComparison.Ordinal) || Status == "Ошибки исправлены. Сохраните изменения.")
            Status = validationErrors.Count > 0 ? $"Не сохранено. Ошибки в полях: {validationErrors.Count}." : "Ошибки исправлены. Сохраните изменения.";
        OnPropertyChanged(nameof(ValidationErrors));
    }
    private void ClearDraftUi()
    {
        if (editorPopup is { } popup) RetirePopup(popup);
        invalidInputs.Clear(); validationErrors.Clear(); HasExternalDrift = false; Interlocked.Exchange(ref selectedFileChange,0);
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
        if (!Guard.Reset(change: () => { original = snapshot; document = (JsonObject)snapshot.Document.DeepClone(); photoEdit = new(); ClearDraftUi(); })) return;
        IsEditing = false; FieldSearch = ""; Error = ""; DecodePhoto(); Notify(); StructureChanged?.Invoke();
    }
    public bool Mutate(Action<JsonObject> mutation, bool rebuild = false)
    {
        if (!IsEditing || !CanEdit || document is null) return false;
        var candidate = (JsonObject)document.DeepClone();
        var origins = new Dictionary<JsonNode, JsonNode>(ReferenceEqualityComparer.Instance); RememberOrigins(document,candidate,origins); mutation(candidate);
        try { DocumentCodec.EnsureProtectedCategoriesUnchanged(candidate, original!.Document); }
        catch (SyncException e) when (e.Code == "protectedCategory") { Error = e.Message; return false; }
        var changed = Guard.TryMutate(() => ApplyObjectPatch(document, candidate, origins));
        if (changed)
        {
            RefreshValidation(); Notify();
            if (rebuild) StructureChanged?.Invoke();
        }
        return changed;
    }
    public void SetStarred(bool value) { if (value != Starred) Mutate(doc => doc["starred"] = value); }
    public void SetLabel(Guid id, bool selected)
    {
        if (id == Guid.Empty || selected == HasLabel(id) || selected && !Labels.ContainsKey(id)) return;
        Mutate(doc => ApplyLabelDelta(doc, [(id, selected)]));
    }
    public void SetLabels(IEnumerable<Guid> selectedKnownIds)
    {
        var selected = selectedKnownIds.ToHashSet();
        var delta = Labels.Keys.Where(id => selected.Contains(id) != HasLabel(id)).Select(id => (id, selected.Contains(id))).ToArray();
        if (delta.Length > 0) Mutate(doc => ApplyLabelDelta(doc, delta));
    }
    public static void ApplyLabelDelta(JsonObject doc, IEnumerable<(Guid Id, bool Selected)> delta)
    {
        var labels = doc["labels"] as JsonArray;
        foreach (var (id, selected) in delta)
        {
            if (id == Guid.Empty) continue;
            var matches = labels?.Select((value,index) => (value,index)).Where(item => Guid.TryParse(item.value?.ToString(), out var existing) && existing == id).Select(item => item.index).ToArray() ?? [];
            if (selected && matches.Length == 0)
            {
                if (labels is null) { labels = new(); doc["labels"] = labels; }
                labels.Add(id.ToString());
            }
            else if (!selected) foreach (var index in matches.Reverse()) labels!.RemoveAt(index);
        }
    }
    public bool HasLabel(Guid id) => (document?["labels"] as JsonArray)?.Any(n => Guid.TryParse(n?.ToString(),out var existing) && existing == id) == true;
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
        RefreshValidation();
    }
    public async Task<bool> SaveAsync(bool fromDecision = false)
    {
        if (original is null || document is null || !IsEditing || IsBusy || IsActivityBusy || editorPopup is not null || ShowDelete || ShowPhoto || ShowConfiguration || IsPickingPhoto || ShowDecision && !fromDecision) return false;
        if (validationErrors.Count > 0 || invalidInputs.Count > 0)
        {
            foreach (var path in invalidInputs.Keys) validationErrors.TryAdd(path,"Проверьте введённое значение.");
            RefreshValidation();Status=$"Не сохранено. Ошибки в полях: {validationErrors.Count}.";RequestErrorFocus(validationErrors.Keys.First());return false;
        }
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
            HasExternalDrift = e.Outcome == ContactWriteOutcome.Uncertain || HasExternalDrift;
            Error=e.Message; Status=e.Outcome == ContactWriteOutcome.Unchanged ? "Запись не выполнена. Черновик остаётся в карточке." : "Результат записи не подтверждён. Черновик сохранён; перечитайте файлы и проверьте восстановление."; return false;
        }
        catch (Exception e) when (e is SyncException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { if (e is SyncException { Code: "localDrift" }) HasExternalDrift = true; Error = e.Message; Status = "Контакт не сохранён. Черновик остаётся в карточке."; return false; }
        finally { try{EndWrite();}finally{IsBusy = false;} }
    }
    public async Task<bool> ResolveDraftAsync()
    {
        if (editorPopup is not null || ShowDelete || ShowPhoto || ShowConfiguration || IsPickingPhoto || IsBusy) return false;
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
        if (original.IsNew) { if (!Guard.Reset(change: () => { original = null; document = null; ClearDraftUi(); })) return; IsEditing = false; PhotoPreview?.Dispose(); PhotoPreview = null; Notify(); StructureChanged?.Invoke(); }
        else { var drift = HasExternalDrift; SetSnapshot(original); HasExternalDrift = drift; }
    }
    public void AskDelete()
    {
        if (!CanEdit || IsEditing || original?.IsNew != false) return;
        ShowDelete = true; Notify();
    }
    public void DismissDelete() { ShowDelete = false; Notify(); }
    public async Task ConfirmDeleteAsync()
    {
        if (!ShowDelete || editorPopup is not null || ShowPhoto || ShowConfiguration || IsPickingPhoto || ShowDecision || IsBusy || activity.IsActive || activity.IsApplying || original is null) return;
        Interlocked.Exchange(ref externalWriteEvent,0);writing=true;Interlocked.Increment(ref fileChangeVersion);changedTimer.Stop();
        IsBusy = true; Error = ""; Status = "Перемещаем контакт в локальную корзину…";
        try
        {
            await Task.Run(() => service.DeleteAsync(original, account()));
            var root = original.Root; original = null; document = null; Guard.Reset(change:ClearDraftUi); ShowDelete = false; PhotoPreview?.Dispose(); PhotoPreview = null;
            Status = "Контакт перемещён в локальную корзину. Google пока не изменён."; onSaved(); StructureChanged?.Invoke(); await LoadAsync(root);
        }
        catch (ContactEditorWriteException e)
        {
            onSaved();
            Error=e.Message;
            if(e.Outcome == ContactWriteOutcome.Committed)
            {
                var root=original!.Root; Guard.Reset(change:()=> { original=null; document=null; ClearDraftUi(); }); ShowDelete=false;
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
        if (original is null || original.IsNew || IsBusy || IsActivityBusy || HasModal) return;
        var selected = original;
        if (!await ResolveDraftAsync()) return;
        if (original?.Id != selected.Id || IsActivityBusy || HasModal || IsBusy) return;
        IsBusy = true;
        try
        {
            var fresh = await Task.Run(() => service.ReadAsync(selected.Root,selected.Id,account()));
            SetSnapshot(fresh); Status = "Загружена актуальная версия контакта.";
        }
        catch (Exception error) when (error is SyncException or IOException or UnauthorizedAccessException)
        { HasExternalDrift = true; Error = error.Message; Status = "Актуальную версию не удалось загрузить. Показанная карточка сохранена; проверьте файл и повторите загрузку."; }
        finally { IsBusy = false; }
    }
    public void Dispose()
    {
        if (editorPopup is { } popup) RetirePopup(popup);
        disposed = true; decision?.TrySetResult("stay"); watcher?.Dispose(); changedTimer.Dispose(); loading?.Cancel(); loading?.Dispose(); querying?.Cancel(); querying?.Dispose();
        PhotoPreview?.Dispose(); Guard.Changed -= Notify; activity.Changed -= ActivityChanged;
    }
}
