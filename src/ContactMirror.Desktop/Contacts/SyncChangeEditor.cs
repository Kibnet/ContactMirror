using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Desktop.Contacts;

public sealed class SyncChangeEditor : UserControl, IDisposable
{
    private SyncLocalEdit edit;
    private readonly MainWindowViewModel owner;
    private readonly Action close;
    private readonly ContactsViewModel draft;
    private readonly Grid layout = new() { RowDefinitions = new("Auto,Auto,*,Auto,Auto") };
    private readonly ScrollViewer fields = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sourceTime = new() { TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock googleValue = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 90 };
    private readonly Border popup = new() { IsVisible = false, Classes = { "card" }, Padding = new(20), MaxWidth = 550, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private JsonObject groupDocument;
    private Bitmap? googlePhoto;
    private readonly Dictionary<string, bool> expanded = new();
    private bool busy, groupDirty;
    private Control? popupReturnFocus;
    public SyncChangeEditor(MainWindowViewModel owner, SyncLocalEdit edit, Action close)
    {
        this.owner = owner; this.edit = edit; this.close = close;
        UpdateGooglePhoto();
        groupDocument = (JsonObject)edit.Document.DeepClone();
        var activity = new ApplicationActivity();
        draft = new(new ContactEditorService(new DraftOnlyStore(), activity), activity, () => null, () => { }, persist: false);
        if (edit.Kind == EntityKind.Contact) draft.OpenSyncDraft(edit);
        Content = layout; Id(this, "SyncChangeEditor"); KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = "Правка в папке · " + edit.Name, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(sourceTime); UpdateSource();
        heading.Children.Add(new TextBlock { Text = edit.GoogleDocument is null ? "В Google записи нет. Ручная правка сохранится только в папку." : "Сброс заменяет выбранный раздел в папке. Отправка в Google запускается отдельно.", TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(heading);
        if (edit.GoogleDocument is { } remote)
        {
            Id(googleValue, "SyncEditGoogleValue"); Grid.SetRow(googleValue, 1); layout.Children.Add(googleValue);
        }
        fields.Margin = new(0, 12); Grid.SetRow(fields, 2); layout.Children.Add(fields); Rebuild();
        Grid.SetRow(message, 3); Id(message, "SyncEditStatus"); layout.Children.Add(message);
        var actions = new WrapPanel();
        if (edit.CanReset) actions.Children.Add(Button("Сбросить раздел до Google", "SyncEditReset", Reset));
        if (edit.MissingLabels.Count > 0) actions.Children.Add(Button("Сохранить недостающие ярлыки…", "SyncEditLabels", () => ShowChoice("В папку будут сохранены ярлыки: " + string.Join(", ", edit.MissingLabels.Values) + ". Контакт пока не будет записан.", Dependencies, "Сохранить ярлыки")));
        if (edit.CanDelete) actions.Children.Add(Button("Удалить локальную запись…", "SyncEditDelete", () => ShowChoice("Google-версии нет. Переместить этот локальный файл в корзину? Резервная копия сохранится.", async () => { await owner.DeleteSyncEditorAsync(this.edit); close(); await owner.RefreshAfterSyncEditAsync(this.edit); }, "Переместить в корзину")));
        actions.Children.Add(Button("Сохранить в папку", "SyncEditSave", Save)); actions.Children.Add(Button("Отменить", "SyncEditCancel", RequestCloseCore));
        foreach (var action in actions.Children) action.Margin = new(0, 8, 8, 0); Grid.SetRow(actions, 4); layout.Children.Add(actions);
        var popupLayer = new Border { Background = new SolidColorBrush(Color.Parse("#88000000")), IsVisible = false, Child = popup };
        popup.PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) popupLayer.IsVisible = popup.IsVisible; };
        Grid.SetRowSpan(popupLayer, 5); layout.Children.Add(popupLayer);
        draft.StructureChanged += Rebuild;
        draft.FieldVisibilityChanged += Rebuild;
        draft.PropertyChanged += (_, args) => { if (draft.Error.Length > 0) SetMessage(draft.Error); if (args.PropertyName == nameof(draft.PhotoPreview)) Rebuild(); };
    }
    private string RemoteSummary(JsonObject remote) => edit.Kind == EntityKind.Group ? (remote[edit.Field == "$entity" ? "name" : edit.Field]?.ToString() ?? "Нет поля")
        : edit.Field == "photo" ? edit.GooglePhotoBytes is null ? "Без фотографии" : "Фотография из последней проверки"
        : CapabilityRegistry.WritableFields.Contains(edit.Field) ? ContactValuePresenter.Format(remote["data"]?[edit.Field], edit.Field)
        : remote[edit.Field]?.ToString() ?? "Версия из Google";
    private void Rebuild()
    {
        var offset = fields.Offset;
        foreach (var expander in fields.GetVisualDescendants().OfType<Expander>())
            if (AutomationProperties.GetAutomationId(expander) is { } id) expanded[CardRows.Key(expander, id)] = expander.IsExpanded;
        if (edit.Kind == EntityKind.Group) { BuildGroup(); return; }
        var actions = new ContactCardActions(ShowPopup, FocusDraftField, _ => { }, CapabilityRegistry.WritableFields.Contains(edit.Field) ? "data." + edit.Field + ".0" : null);
        var panel = new StackPanel { Spacing = 10 };
        if (edit.Field == "$entity") panel.Children.Add(Button(draft.ShowAllFields ? "Скрыть пустые разделы" : "Показать все разделы", "SyncEditAllFields", () => draft.ShowAllFields = !draft.ShowAllFields));
        if (edit.Field is "starred" or "$entity")
        {
            var starred = new CheckBox { Content = "Избранное", IsChecked = draft.Starred }; Id(starred, "SyncEditStarred");
            starred.IsCheckedChanged += (_, _) => draft.SetStarred(starred.IsChecked == true); panel.Children.Add(starred);
        }
        if (edit.Field is "photo" or "$entity")
        {
            if (googlePhoto is not null) { panel.Children.Add(new TextBlock { Text = "Фото в Google" }); var image = new Image { Source = googlePhoto, Height = 100, Stretch = Stretch.Uniform }; Id(image, "SyncEditGooglePhoto"); panel.Children.Add(image); }
            panel.Children.Add(new TextBlock { Text = "Фото в папке" });
            if (draft.PhotoPreview is not null) panel.Children.Add(new Image { Source = draft.PhotoPreview, Height = 100, Stretch = Stretch.Uniform });
            panel.Children.Add(Button("Выбрать фотографию…", "SyncEditPhotoPick", async () => { if (TopLevel.GetTopLevel(this) is { } top) { await ContactPhotoPicker.PickAsync(top, draft); Rebuild(); } }));
            panel.Children.Add(Button("Удалить фото", "SyncEditPhotoRemove", () => draft.ChangePhoto(null)));
        }
        if (edit.Field != "photo") panel.Children.Add(ContactCardControls.Build(draft, actions, false, edit.Field));
        if (edit.CanReset && edit.Field is "names" or "birthdays" or "biographies" or "genders")
        {
            var category = ContactFieldSchema.Fields.Single(f => f.Name == edit.Field);
            foreach (var field in category.Children.Where(f => f.Children.Count == 0 && f.Name != "metadata"))
            {
                var key = field.Name;
                panel.Children.Add(Button("Взять из Google: " + ContactPresentationCatalog.Title(edit.Field, key), "SyncEditLeaf:" + key, () =>
                {
                    draft.ClearInput("data." + edit.Field + ".0." + key);
                    draft.Mutate(doc =>
                    {
                        var source = edit.GoogleDocument?["data"]?[edit.Field] as JsonArray;
                        if (doc["data"]?[edit.Field] is not JsonArray list || list.FirstOrDefault() is not JsonObject row || source?.FirstOrDefault() is not JsonObject remoteRow)
                        { SetMessage("Для отсутствующей записи используйте сброс раздела."); return; }
                        if (remoteRow.ContainsKey(key)) row[key] = remoteRow[key]?.DeepClone(); else row.Remove(key);
                    }, true);
                }));
            }
        }
        fields.Content = panel; Dispatcher.UIThread.Post(() =>
        {
            foreach (var expander in fields.GetVisualDescendants().OfType<Expander>().ToArray())
                if (AutomationProperties.GetAutomationId(expander) is { } id && expanded.TryGetValue(CardRows.Key(expander, id), out var value)) expander.IsExpanded = value;
            fields.Offset = offset;
        }, DispatcherPriority.Loaded);
    }
    private void BuildGroup()
    {
        var panel = new StackPanel { Spacing = 8 };
        if (edit.Field is "name" or "$entity")
        {
            panel.Children.Add(new TextBlock { Text = "Название ярлыка" }); var input = new TextBox { Text = groupDocument["name"]?.ToString() }; Id(input, "SyncEditGroupName");
            input.TextChanged += (_, _) => { groupDocument["name"] = input.Text ?? ""; groupDirty = true; }; panel.Children.Add(input);
        }
        if (edit.Field is "clientData" or "$entity")
        {
            panel.Children.Add(new TextBlock { Text = "Данные приложений" });
            if (groupDocument["clientData"] is not JsonArray) groupDocument["clientData"] = new JsonArray();
            var rows = (JsonArray)groupDocument["clientData"]!;
            foreach (var row in rows.OfType<JsonObject>().ToArray())
            {
                var grid = new Grid { ColumnDefinitions = new("*,*,Auto") }; var key = new TextBox { Text = row["key"]?.ToString(), PlaceholderText = "Ключ" }; var value = new TextBox { Text = row["value"]?.ToString(), PlaceholderText = "Значение" };
                key.TextChanged += (_, _) => { row["key"] = key.Text ?? ""; groupDirty = true; }; value.TextChanged += (_, _) => { row["value"] = value.Text ?? ""; groupDirty = true; };
                grid.Children.Add(key); Grid.SetColumn(value, 1); grid.Children.Add(value); var remove = Button("Удалить", "SyncEditGroupRemove:" + rows.IndexOf(row), () => { rows.Remove(row); groupDirty = true; Rebuild(); }); Grid.SetColumn(remove, 2); grid.Children.Add(remove); panel.Children.Add(grid);
            }
            panel.Children.Add(Button("Добавить пару", "SyncEditGroupAdd", () => { rows.Add(new JsonObject { ["key"] = "", ["value"] = "" }); groupDirty = true; Rebuild(); }));
        }
        fields.Content = panel;
    }
    private void FocusDraftField(string path) => Dispatcher.UIThread.Post(() =>
    {
        var parts = path.Split('.');
        if (parts.Length > 2 && int.TryParse(parts[2], out var index))
        {
            var rows = fields.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "Rows:" + parts[1]);
            if (rows?.ItemsSource is System.Collections.IEnumerable source && source.Cast<object>().ElementAtOrDefault(index) is { } item) rows.ScrollIntoView(item);
        }
        Dispatcher.UIThread.Post(() => { var input = fields.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "Editor:" + path); input?.BringIntoView(); input?.Focus(); }, DispatcherPriority.Loaded);
    }, DispatcherPriority.Loaded);
    private void Reset()
    {
        if (draft.HasModal || draft.IsPickingPhoto) { SetMessage("Закройте текущий выбор перед сбросом."); return; }
        if (edit.GoogleDocument is not { } remote) return;
        if (edit.Kind == EntityKind.Group)
        {
            foreach (var key in edit.Field == "$entity" ? new[] { "name", "clientData" } : new[] { edit.Field }) Copy(remote, groupDocument, key);
            groupDirty = true; Rebuild();
        }
        else
        {
            foreach (var prefix in edit.Field == "$entity" ? new[] { "data", "labels", "starred", "photo" } : new[] { CapabilityRegistry.WritableFields.Contains(edit.Field) ? "data." + edit.Field : edit.Field }) draft.ClearInput(prefix);
            draft.Mutate(doc =>
            {
                if (CapabilityRegistry.WritableFields.Contains(edit.Field)) Copy(remote["data"]!.AsObject(), doc["data"]!.AsObject(), edit.Field);
                else foreach (var key in edit.Field == "$entity" ? new[] { "data", "labels", "starred" } : edit.Field == "photo" ? [] : new[] { edit.Field }) Copy(remote, doc, key);
            }, true);
            if (edit.Field is "photo" or "$entity") draft.ChangePhoto(edit.GooglePhotoBytes);
        }
        SetMessage("Сброшен только выбранный раздел. Проверьте результат и сохраните в папку.");
    }
    private static void Copy(JsonObject source, JsonObject target, string key) { if (source.ContainsKey(key)) target[key] = source[key]?.DeepClone(); else target.Remove(key); }
    private async Task Save()
    {
        if (draft.HasModal || draft.IsPickingPhoto || draft.ValidationErrors.Count > 0) { SetMessage("Исправьте ошибки и закройте выбор перед сохранением."); return; }
        var doc = edit.Kind == EntityKind.Contact ? draft.Document! : groupDocument;
        await owner.SaveSyncEditorAsync(edit, (JsonObject)doc.DeepClone(), edit.Kind == EntityKind.Contact ? draft.DraftPhoto : new());
        close(); await owner.RefreshAfterSyncEditAsync(edit);
    }
    private async Task Dependencies()
    {
        if (draft.HasModal || draft.IsPickingPhoto) { SetMessage("Закройте текущий выбор перед сохранением ярлыков."); return; }
        var original = edit.Document.ToJsonString(); var old = edit;
        var updated = await owner.SaveSyncDependenciesAsync(edit);
        if (old.ResourceName != updated.ResourceName || old.SourceId != updated.SourceId || old.LocalExists != updated.LocalExists
            || old.LocalExists && !JsonNode.DeepEquals(old.Document, updated.Document)
            || !((old.PhotoBytes ?? []).AsSpan().SequenceEqual(updated.PhotoBytes ?? [])))
            throw new SyncException("localDrift", "Ярлыки сохранены, но контакт изменился. Черновик оставлен в форме; закройте её и сравните версии.");
        draft.RemapSyncDraft(old, updated);
        edit = updated; UpdateGooglePhoto(); UpdateSource(); Rebuild(); SetMessage("Ярлыки сохранены. Контакт ещё не записан: проверьте Google-версию и нажмите «Сохранить в папку».");
    }
    public void RequestClose()
    {
        if (busy || draft.IsPickingPhoto) return;
        RequestCloseCore();
    }
    private void RequestCloseCore()
    {
        if (draft.IsPickingPhoto) return;
        if (draft.HasEditorPopup) { SetMessage("Закройте текущий выбор перед выходом."); return; }
        if (draft.IsDirty || groupDirty)
        {
            ShowChoice("Есть несохранённые правки. Закрыть форму без записи?", () => { close(); return Task.CompletedTask; }, "Закрыть без сохранения");
            if (popup.Child is StackPanel choices) choices.Children.Insert(1, Button("Сохранить в папку", "SyncEditCloseSave", Save));
        }
        else close();
    }
    private void ShowChoice(string text, Func<Task> confirm, string confirmText = "Подтвердить")
    {
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button(confirmText, "SyncEditConfirm", async () => { popup.IsVisible = false; await confirm(); }));
        panel.Children.Add(Button("Остаться", "SyncEditStay", () => popup.IsVisible = false)); popup.Child = panel; popup.IsVisible = true;
        FocusPopup();
    }
    private void ShowPopup(string path, Func<EditorPopupContext, Action, Control> factory)
    {
        if (draft.BeginEditorPopup(path) is not { } token) return;
        popupReturnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        void Close() { draft.EndEditorPopup(token); popup.IsVisible = false; popup.Child = null; Dispatcher.UIThread.Post(() => popupReturnFocus?.Focus()); }
        popup.Child = new ScrollViewer { Content = factory(token, Close), MaxHeight = 450 }; popup.IsVisible = true;
        FocusPopup();
    }
    private void FocusPopup()
    {
        KeyboardNavigation.SetTabNavigation(popup, KeyboardNavigationMode.Cycle);
        Dispatcher.UIThread.Post(() => popup.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && c.IsEnabled)?.Focus(), DispatcherPriority.Loaded);
    }
    private Button Button(string text, string id, Action action) => Button(text, id, () => { action(); return Task.CompletedTask; });
    private Button Button(string text, string id, Func<Task> action)
    {
        var button = new Button { Content = text, MinHeight = 32 }; Id(button, id);
        button.Click += async (_, _) =>
        {
            if (busy) return; busy = true; IsEnabled = false;
            try { await action(); }
            catch (Exception error) { SetMessage(error.Message); }
            finally { busy = false; IsEnabled = true; }
        }; return button;
    }
    private static void Id(Control control, string id) { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, control is Button b ? b.Content?.ToString() ?? id : id); }
    private void SetMessage(string text) { message.Text = text; AutomationProperties.SetName(message, text); }
    private void UpdateGooglePhoto()
    {
        Bitmap? next = null;
        if (edit.GooglePhotoBytes is { } bytes) next = ContactPhotoPreview.DecodeBounded(bytes, 200);
        var previous = googlePhoto; googlePhoto = next; previous?.Dispose();
    }
    private void UpdateSource() { sourceTime.Text = "Область: " + CapabilityRegistry.Title(edit.Field) + " · Google по проверке " + edit.PreparedAt.ToLocalTime().ToString("HH:mm:ss"); googleValue.Text = edit.GoogleDocument is { } remote ? "Google: " + RemoteSummary(remote) : ""; }
    public void Dispose() { draft.StructureChanged -= Rebuild; draft.FieldVisibilityChanged -= Rebuild; draft.Dispose(); googlePhoto?.Dispose(); }
    private sealed class DraftOnlyStore : IContactEditorStore
    {
        public Task<ContactCatalog> LoadAsync(string root, AccountIdentity? account = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContactSnapshot> ReadAsync(string root, Guid id, AccountIdentity? account = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo, AccountIdentity? account = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ContactSnapshot original, AccountIdentity? account = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
