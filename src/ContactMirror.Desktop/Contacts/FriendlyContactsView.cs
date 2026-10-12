using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Desktop.Contacts;

public sealed partial class ContactsView
{
    private ContactCardActions Actions() => new(ShowEditorPopup, EditField, OpenFinder, revealedField);
    private bool IsNarrow => (TopLevel.GetTopLevel(this)?.Bounds.Width ?? Bounds.Width) < 900;
    private void RebuildCard()
    {
        draftIndicator = null; contactTitle = null; hiddenSelectionNote = null;
        var body = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var header = new StackPanel { Spacing = 8, Margin = new(16, 12) };
        if (vm.HasContact)
        {
            header.Children.Add(ContactCardControls.BuildIdentity(vm, Actions()));
            var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(Button("Найти поле…", "ContactFindButton", () => OpenFinder("")));
            if (vm.IsEditing) toolbar.Children.Add(Button("Изменить фото…", "PhotoMenuButton", OpenPhotoMenu));
            toolbar.Children.Add(Button(IsNarrow ? "← Список" : expanded ? "Рядом с таблицей" : "Развернуть", "ContactExpandButton", async () => { if (IsNarrow) { if (await vm.ResolveDraftAsync()) narrowCard = false; } else expanded = !expanded; LayoutPanes(); }));
            toolbar.Children.Add(Button("⋮", "ContactActionsButton", OpenContactActions));
            foreach (var button in toolbar.Children.OfType<Button>()) { button.Margin = new(0, 0, 8, 4); button.MinHeight = 32; }
            header.Children.Add(toolbar);
        }
        hiddenSelectionNote = Note(vm.HiddenSelection); hiddenSelectionNote.IsVisible = vm.HiddenSelection.Length > 0; header.Children.Add(hiddenSelectionNote); body.Children.Add(header);
        var scroll = new ScrollViewer { Content = vm.HasContact ? ContactCardControls.Build(vm, Actions(), false) : Note("Выберите контакт или создайте новый."), Margin = new(16, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        fieldScroll=scroll;Id(scroll,"ContactFieldScroll");Grid.SetRow(scroll,1);body.Children.Add(scroll);
        var bottom = new StackPanel { Spacing = 6, Margin = new(16, 12) };
        if (vm.HasExternalDrift)
        {
            bottom.Children.Add(Note("Файл изменился вне редактора.")); var recovery = new WrapPanel(); recovery.Children.Add(Button("Загрузить актуальную версию", "ContactDriftReload", async () => await vm.ReloadSelectedAsync())); recovery.Children.Add(Button("Оставить черновик", "ContactDriftStay", vm.StayWithDraft)); bottom.Children.Add(recovery);
        }
        var footer = new WrapPanel { Orientation = Orientation.Horizontal };
        if (vm.IsEditing)
        {
            var save = Button("Сохранить в папку", "ContactSaveButton", async () => await vm.SaveAsync()); save.Classes.Add("primary"); save.IsEnabled = (vm.IsDirty || vm.IsNewContact) && !vm.IsBusy && !vm.HasModal && !vm.IsActivityBusy; footer.Children.Add(save);
            footer.Children.Add(Button("Отменить", "ContactCancelButton", vm.CancelEdit));
            draftIndicator=Note(vm.IsBusy?"Сохраняем…":vm.ValidationErrors.Count>0?$"Ошибки в полях: {vm.ValidationErrors.Count}":vm.IsDirty?"Не сохранено":"Нет изменений");Id(draftIndicator,"ContactDraftStatus");footer.Children.Add(draftIndicator);
            var errors = Button("Показать ошибки", "ContactErrorsButton", OpenErrors); errors.IsVisible = vm.ValidationErrors.Count > 0; bottom.Children.Add(errors);
        }
        else if (vm.HasContact)
        {
            if(vm.ReadOnlyReason.Length==0){var edit=Button("Редактировать","ContactEditButton",vm.BeginEdit);edit.Classes.Add("primary");edit.IsEnabled=vm.CanEdit;footer.Children.Add(edit);}
            footer.Children.Add(Note(vm.ReadOnlyReason.Length>0?"Контакт доступен только для просмотра":"Изменения сохраняются в папку"));
            if (vm.Status.StartsWith("Сохранено в папку", StringComparison.Ordinal)) footer.Children.Add(Button("Синхронизация", "ContactSavedSyncButton", () => FindById("SyncNavigationButton")?.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent))));
        }
        foreach (var control in footer.Children) control.Margin = new(0, 0, 8, 4); bottom.Children.Add(footer); Grid.SetRow(bottom, 2); body.Children.Add(bottom); card.Child = body; LayoutPanes();
    }
    private void RebuildFields() { if (fieldScroll is not null) fieldScroll.Content = ContactCardControls.Build(vm, Actions(), false); }
    private void LayoutPanes()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var available = Math.Max(0, panes.Bounds.Width - 10); var narrow = IsNarrow || available > 0 && available < 720; var singlePane = narrow || expanded; var showCard = singlePane && (narrowCard || expanded);
        var fraction = available > 0 && !singlePane ? Math.Clamp(vm.TableFraction, 300 / available, 1 - 420 / available) : vm.TableFraction;
        panes.ColumnDefinitions = new(singlePane ? (showCard ? "0,0,*" : "*,0,0") : $"{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,10,{(1 - fraction).ToString(System.Globalization.CultureInfo.InvariantCulture)}*");
        table.IsVisible = !singlePane || !showCard; card.IsVisible = !singlePane || showCard; splitter.IsVisible = !singlePane;
        if (FindById("ContactExpandButton") is Button expand) expand.Content = narrow ? "← Список" : expanded ? "Рядом с таблицей" : "Развернуть";
        if (focused is not null && !focused.IsEffectivelyVisible) Dispatcher.UIThread.Post(() => { if (vm.HasModal) return; var target = card.IsVisible ? FindById("ContactFindButton") : search; if (target?.IsEffectivelyVisible == true) target.Focus(); }, DispatcherPriority.Loaded);
    }
    private void ShowEditorPopup(string path, Func<EditorPopupContext, Action, Control> factory)
    {
        if (vm.BeginEditorPopup(path) is not { } token) return;
        ownedPopup = token; popupReturnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        void Close() { CloseEditorPopup(token); }
        var content = factory(token, Close); modal.Child = new ScrollViewer { Content = content }; modal.IsVisible = true;
        KeyboardNavigation.SetTabNavigation(modal, KeyboardNavigationMode.Cycle); UpdateState();
        Dispatcher.UIThread.Post(() => content.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && c.IsEnabled)?.Focus());
    }
    private void CloseEditorPopup(EditorPopupContext token)
    {
        if (!ReferenceEquals(ownedPopup, token)) return;
        vm.EndEditorPopup(token); ownedPopup = null; modal.IsVisible = false; modal.Child = null; UpdateState(); popupReturnFocus?.Focus(); popupReturnFocus = null;
    }
    private void EditField(string path) { if(!vm.IsEditing)vm.BeginEdit();FocusField(path); }
    private void FocusField(string path)
    {
        var add = path.StartsWith("add:", StringComparison.Ordinal);
        var targetPath = add ? path[4..] : path;
        var revealPath = add && targetPath.StartsWith("data.", StringComparison.Ordinal) ? targetPath + ".0" : targetPath;
        revealedField = revealPath; RebuildFields();
        var document = vm.Document;
        var parts = targetPath.Split('.');
        var lists = new List<(string Id, int Index)>();
        if (parts.Length > 2 && parts[0] == "data" && int.TryParse(parts[2], out var index)) lists.Add(("Rows:" + parts[1], index));
        if (parts[0] == "extensions")
        {
            JsonNode? node = document?["extensions"];
            for (var i = 1; i < parts.Length; i++)
            {
                if (node is JsonArray array && int.TryParse(parts[i], out var position))
                {
                    lists.Add(("ExtensionRows:" + string.Join('.', parts.Take(i)), position));
                    node = position >= 0 && position < array.Count ? array[position] : null;
                }
                else node = node is JsonObject obj ? obj[Uri.UnescapeDataString(parts[i])] : null;
            }
        }
        bool Current() => ReferenceEquals(document, vm.Document) && revealedField == revealPath && !vm.HasModal;
        void Focus()
        {
            if (!Current()) return;
            var control = add ? FindById(parts[0] == "extensions" ? "ExtensionNewKey:" + targetPath : "AddField:" + parts[1]) : FindById((vm.IsEditing ? "Editor:" : "View:") + targetPath);
            if (control is null && vm.IsEditing && targetPath.EndsWith(".mode", StringComparison.Ordinal)) control = FindById("DateMode:" + targetPath[..^5]);
            if (control is null && vm.IsEditing && targetPath.EndsWith(".date", StringComparison.Ordinal))
                control = new[] { "day", "month", "year" }.Select(part => FindById("Editor:" + targetPath + "." + part)).FirstOrDefault(c => c?.IsEffectivelyVisible == true) ?? FindById("DateMode:" + targetPath);
            control ??= parts.Length > 1 ? FindById("EditMissing:" + parts[1]) : null;
            if (control is null && path == "google") control = FindById("ContactReadonlyGoogle");
            control?.BringIntoView(); control?.Focus();
        }
        void Realize(int step)
        {
            if (!Current()) return;
            if (step == lists.Count) { Dispatcher.UIThread.Post(Focus, DispatcherPriority.Loaded); return; }
            var (id, position) = lists[step];
            if (FindById(id) is ListBox rows && rows.ItemsSource is System.Collections.IEnumerable source && source.Cast<object>().ElementAtOrDefault(position) is { } item) { rows.BringIntoView(); rows.ScrollIntoView(item); }
            Dispatcher.UIThread.Post(() => Realize(step + 1), DispatcherPriority.Loaded);
        }
        Dispatcher.UIThread.Post(() => Realize(0), DispatcherPriority.Loaded);
    }
    private sealed record FieldResult(string Path, string Title, string Context, string Category, bool Existing);
    private void OpenFinder(string seed) => ShowEditorPopup("finder", (token, close) =>
    {
        var form = new StackPanel { Spacing = 12 }; form.Children.Add(new TextBlock { Text = seed == "+" ? "Добавить поле" : "Найти поле", FontSize = 20, FontWeight = FontWeight.SemiBold });
        var query = new TextBox { Text = seed == "+" ? "" : seed.Length > 0 ? seed : finderQuery, PlaceholderText = "Имя поля или раздел…", MinHeight = 36 }; Id(query, "ContactFieldSearch"); form.Children.Add(query);
        var results = new ListBox { Height = 280 }; Id(results, "ContactFindResults");
        var items = new List<FieldResult>();
        foreach (var category in ContactFieldSchema.Fields)
        {
            var count = (vm.Document?["data"]?[category.Name] as JsonArray)?.Count ?? 0;
            if (seed == "+") { items.Add(new("data." + category.Name + ".0", category.Title, count > 0 ? $"Уже есть · {count}" : "Не заполнено", category.Name, count > 0)); continue; }
            foreach (var leaf in Leaves(category)) for (var i = 0; i < Math.Max(1, count); i++)
            {
                var path = "data." + category.Name + "." + i + leaf.Path[(category.Path.Length)..];
                JsonNode? value=vm.Document;
                foreach(var part in path.Split('.'))value=value is JsonArray array&&int.TryParse(part,out var position)?position<array.Count?array[position]:null:value is JsonObject obj?obj[part]:null;
                var filled=value is not null&&value.ToString().Length>0;
                var summary=count>0?ContactValuePresenter.Format(vm.Document?["data"]?[category.Name]?[i],category.Name):"";
                var context=category.Title+" · "+(count>0?"Запись "+(i+1)+" из "+count+" · ":"")+(filled?"Заполнено":"Не заполнено")+(summary.Length>0?" · "+summary:"");
                items.Add(new(path,ContactPresentationCatalog.Title(category.Name,leaf.Name),context,category.Name,filled));
            }
        }
        if (seed != "+") items.Add(new("google", "Служебные сведения Google", "Только просмотр", "google", true));
        results.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FieldResult>((result, _) =>
        {
            if (result is null) return new TextBlock();
            var item = result!; var button = Button(item.Title + "\n" + item.Context, "FindResult:" + item.Path, () =>
            {
                if (seed == "+" && vm.IsEditing && (!item.Existing || !ContactFieldSchema.Fields.Single(f => f.Name == item.Category).IsSingleton))
                {
                    var index = (vm.Document?["data"]?[item.Category] as JsonArray)?.Count ?? 0;
                    vm.EndEditorPopup(token, doc => { if (doc["data"] is not JsonObject) doc["data"] = new JsonObject(); if (doc["data"]![item.Category] is not JsonArray) doc["data"]![item.Category] = new JsonArray(); doc["data"]![item.Category]!.AsArray().Add(new JsonObject()); }, true); close();
                    var field = ContactFieldSchema.Fields.Single(f => f.Name == item.Category); FocusField("data." + item.Category + "." + index + "." + field.Children.First(c => c.Name != "metadata").Name);
                }
                else { close(); FocusField(item.Path); }
            }); button.HorizontalContentAlignment = HorizontalAlignment.Left; button.HorizontalAlignment = HorizontalAlignment.Stretch; return button;
        });
        void Filter() { finderQuery = query.Text ?? ""; results.ItemsSource = items.Where(i => (i.Title + " " + i.Context + " " + i.Path).Contains(finderQuery, StringComparison.CurrentCultureIgnoreCase)).ToArray(); }
        query.TextChanged += (_, _) => Filter(); Filter(); form.Children.Add(results); form.Children.Add(Button("Закрыть", "ContactFindClose", close)); return form;
    });
    private static IEnumerable<ContactFieldDescriptor> Leaves(ContactFieldDescriptor field) => field.Children.Count == 0 ? [field] : field.Children.SelectMany(Leaves);
    private void OpenPhotoMenu() => ShowEditorPopup("photo", (token, close) =>
    {
        var form = new StackPanel { Spacing = 12 }; form.Children.Add(new TextBlock { Text = "Фотография", FontSize = 20 });
        form.Children.Add(Button("Выбрать фотографию…", "PhotoChangeButton", async () => { close(); if (TopLevel.GetTopLevel(this) is { } owner) await ContactPhotoPicker.PickAsync(owner, vm); }));
        if (vm.PhotoBytes is not null) { form.Children.Add(Button("Посмотреть", "PhotoMenuViewButton", () => { close(); vm.ShowPhoto = true; })); form.Children.Add(Button("Удалить фото", "PhotoRemoveButton", () => { close(); vm.ChangePhoto(null); })); }
        form.Children.Add(Button("Отменить", "PhotoMenuCancel", close)); return form;
    });
    private void OpenContactActions() => ShowEditorPopup("contact", (token, close) =>
    {
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(Button("Перечитать", "ContactReloadButton", async () => { close(); await vm.ReloadSelectedAsync(); }));
        if (!vm.IsEditing && vm.ReadOnlyReason.Length == 0) form.Children.Add(Button("Удалить контакт…", "ContactDeleteButton", () => { close(); vm.AskDelete(); }));
        form.Children.Add(Button("Служебные сведения Google", "ContactGoogleDetailsButton", () => { close(); FocusField("google"); })); form.Children.Add(Button("Закрыть", "ContactActionsClose", close)); return form;
    });
    private void OpenErrors() => ShowEditorPopup("errors", (token, close) =>
    {
        var form=new StackPanel{Spacing=12};form.Children.Add(new TextBlock{Text=$"Поля с ошибками: {vm.ValidationErrors.Count}",FontSize=20});
        foreach(var pair in vm.ValidationErrors)
        {
            var path=pair.Key;var parts=path.Split('.');var title=parts[0]=="extensions"?"Дополнительные данные · "+Uri.UnescapeDataString(parts[^1]):parts.Length>2?ContactPresentationCatalog.Title(parts[1],parts[^1]):"Поле контакта";var caption=title+": "+pair.Value;
            var link=Button(caption,"ErrorLink:"+path,()=>{close();FocusField(path);});
            link.Content=new TextBlock{Text=caption,TextWrapping=TextWrapping.Wrap,MaxWidth=560};link.HorizontalContentAlignment=HorizontalAlignment.Left;AutomationProperties.SetName(link,caption);form.Children.Add(link);
        }
        form.Children.Add(Button("Закрыть", "ContactErrorsClose", close)); return form;
    });
}
