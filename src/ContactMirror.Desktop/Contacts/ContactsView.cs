using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ContactMirror.Application.Contacts;

namespace ContactMirror.Desktop.Contacts;

public sealed class ContactsView : UserControl
{
    private readonly ContactsViewModel vm;
    private readonly ContactThumbnailLoader thumbnails = new();
    private readonly Grid root = new() { RowDefinitions = new("Auto,Auto,*,Auto") };
    private readonly Grid panes = new() { ColumnDefinitions = new(".54*,10,.46*") };
    private readonly DataGrid table = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true, CanUserReorderColumns = true, CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly Border card = new() { Classes = { "card" }, Padding = new(0) };
    private readonly Border modal = new() { IsVisible = false, Classes = { "card" }, MaxWidth = 690, MaxHeight = 590, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new(24) };
    private readonly TextBox search = new() { PlaceholderText = "Поиск по имени, телефону, почте и другим полям…" };
    private readonly TextBlock count = new(), status = new() { Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
    private readonly TextBlock readOnly = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
    private readonly Button problems;
    private readonly StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly GridSplitter splitter = new() { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
    private string columnSignature = "";
    private bool updating, narrowCard, expanded;
    private string? configurationPanel;
    private Bitmap? fullPhoto;
    private TextBlock? draftIndicator;
    private TextBlock? contactTitle;
    private TextBlock? hiddenSelectionNote;
    private ScrollViewer? fieldScroll;
    public ContactsView(ContactsViewModel model)
    {
        vm = model; DataContext = vm; Content = root; Id(this, "ContactsPage"); Id(table, "ContactsTable"); Id(search, "ContactsSearch");
        Id(status,"ContactsStatus"); Id(count,"ContactsCount"); Id(error,"ContactsError");
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0,0,0,10) };
        var title = new StackPanel { Spacing = 2 }; title.Children.Add(new TextBlock { Text = "Контакты", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold }); title.Children.Add(count);
        problems=Button("Ошибки файлов","ContactIssuesButton",()=>OpenConfiguration("issues"));title.Children.Add(problems);heading.Children.Add(title);
        tools.Children.Add(Button("Обновить", "ContactRefreshButton", async () => { if (await vm.ResolveDraftAsync() && vm.Original?.Root is { } selectedRoot) await vm.LoadAsync(selectedRoot); else if (!vm.IsDirty) await RefreshCatalogAsync(); }));
        tools.Children.Add(Button("Столбцы", "ContactColumnsButton", () => OpenConfiguration("columns")));
        tools.Children.Add(Button("Фильтры", "ContactFiltersButton", () => OpenConfiguration("filters")));
        tools.Children.Add(Button("Сортировка", "ContactSortsButton", () => OpenConfiguration("sorts")));
        tools.Children.Add(Button("Новый контакт", "ContactCreateButton", async () => { await vm.CreateAsync(); narrowCard = vm.HasContact; LayoutPanes(); })); Grid.SetColumn(tools,1); heading.Children.Add(tools); root.Children.Add(heading);
        search.TextChanged += (_,_) => { if (vm.Search != search.Text) vm.Search = search.Text ?? ""; }; search.Margin = new(0,0,0,10); Grid.SetRow(search,1); root.Children.Add(search);
        panes.Children.Add(table); Grid.SetColumn(splitter,1); panes.Children.Add(splitter); Grid.SetColumn(card,2); panes.Children.Add(card); Grid.SetRow(panes,2); root.Children.Add(panes);
        var messages = new StackPanel { Spacing = 4, Margin = new(0,8,0,0) }; messages.Children.Add(readOnly); messages.Children.Add(error); messages.Children.Add(status); Grid.SetRow(messages,3); root.Children.Add(messages);
        Grid.SetRowSpan(modal,4); root.Children.Add(modal);
        table.SelectionChanged += async (_,_) =>
        {
            if (updating || table.SelectedItem is not ContactRowViewModel row) return;
            await vm.SelectAsync(row.Id); UpdateSelectedRow(); narrowCard = vm.HasContact; LayoutPanes();
        };
        table.ColumnReordered += (_,_) => StoreColumnLayout();
        table.ColumnDisplayIndexChanged += (_,_) => { if(!updating) Dispatcher.UIThread.Post(StoreColumnLayout); };
        table.AddHandler(PointerReleasedEvent, (_,_) => StoreColumnLayout(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        splitter.AddHandler(PointerReleasedEvent, (_,_) => { if (panes.Bounds.Width > 0) vm.SetTableFraction(table.Bounds.Width / panes.Bounds.Width); }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        SizeChanged += (_,_) => LayoutPanes();
        vm.StructureChanged += RebuildCard; vm.FieldVisibilityChanged += RebuildFields; vm.TableChanged += UpdateTable; vm.PropertyChanged += Changed;
        AddHandler(KeyDownEvent, HandleKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_,_) => { vm.StructureChanged -= RebuildCard; vm.FieldVisibilityChanged -= RebuildFields; vm.TableChanged -= UpdateTable; vm.PropertyChanged -= Changed; fullPhoto?.Dispose(); };
        UpdateTable(); RebuildCard(); UpdateState();
    }
    public Func<Task>? RefreshCatalog { get; set; }
    private Task RefreshCatalogAsync() => RefreshCatalog?.Invoke() ?? Task.CompletedTask;
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        UpdateState();
        if (args.PropertyName is nameof(vm.ShowDecision) or nameof(vm.ShowDelete) or nameof(vm.ShowPhoto)) UpdateModal();
        if (args.PropertyName == nameof(vm.IsEditing)) RebuildCard();
    }
    private void UpdateState()
    {
        count.Text = vm.CountCaption; status.Text = vm.Status; error.Text = vm.Error; error.IsVisible = vm.Error.Length > 0;
        AutomationProperties.SetName(status,vm.Status); AutomationProperties.SetName(count,vm.CountCaption); AutomationProperties.SetName(error,vm.Error);
        if (draftIndicator is not null) { draftIndicator.Text=vm.IsDirty ? "Не сохранено" : "Нет изменений"; AutomationProperties.SetName(draftIndicator,draftIndicator.Text); }
        if (contactTitle is not null) contactTitle.Text=vm.Name;
        if (hiddenSelectionNote is not null){hiddenSelectionNote.Text=vm.HiddenSelection;hiddenSelectionNote.IsVisible=vm.HiddenSelection.Length>0;}
        readOnly.Text=vm.ReadOnlyReason;readOnly.IsVisible=vm.ReadOnlyReason.Length>0;
        problems.Content=vm.IssuesCaption;problems.IsVisible=vm.Issues.Count>0;problems.IsEnabled=!vm.IsBusy&&!vm.IsActivityBusy&&!vm.HasModal;
        var blocked = vm.IsBusy || vm.IsActivityBusy || vm.HasModal;
        tools.IsEnabled = search.IsEnabled = panes.IsEnabled = !blocked;
        if (tools.Children.LastOrDefault() is Button create) create.IsEnabled = vm.CanCreate;
        if(FindById("ContactEditButton") is Button edit)edit.IsEnabled=vm.CanEdit;
        if(FindById("ContactDeleteButton") is Button delete)delete.IsEnabled=vm.CanEdit;
    }
    private void UpdateTable()
    {
        thumbnails.Reset();
        updating = true;
        try
        {
            var signature = string.Join("|", vm.Configuration.Columns.Select(c => $"{c.Id}:{c.Width}")) + string.Join("|",vm.Configuration.Sorts);
            if (columnSignature != signature)
            {
                columnSignature = signature; table.Columns.Clear();
                foreach (var setting in vm.Configuration.Columns)
                {
                    var descriptor = ContactTableQuery.Columns.First(c => c.Id == setting.Id);
                    var ruleIndex = vm.Configuration.Sorts.ToList().FindIndex(s => s.ColumnId == setting.Id);
                    var rule = ruleIndex < 0 ? null : vm.Configuration.Sorts[ruleIndex];
                    var shift = false;
                    var header = Button(descriptor.Title + (rule is null ? "" : (rule.Descending ? " ↓" : " ↑") + (vm.Configuration.Sorts.Count > 1 ? $" {ruleIndex+1}" : "")), "SortColumn:" + setting.Id, () => { SortHeader(setting.Id,shift); shift=false; });
                    header.PointerPressed += (_,e) => shift=e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    header.KeyDown += (_,e) => shift=e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    header.Padding = new(4); header.FontSize = 12;
                    table.Columns.Add(new DataGridTemplateColumn
                    {
                        Header = header, Width = new DataGridLength(setting.Width), MinWidth = 60, MaxWidth = 1200,
                        Tag = setting.Id,
                        CellTemplate = new FuncDataTemplate<ContactRowViewModel>((row,_) =>
                        {
                            var value = row?.Cell(setting.Id) ?? "";
                            if(setting.Id=="photo" && row is not null && vm.Root is { } root)return new ContactAvatar(row,root,thumbnails);
                            if (setting.Id == "displayName" && row is not null)
                            {
                                var open = Button(value,"ContactOpenButton",async () => { await vm.SelectAsync(row.Id); UpdateSelectedRow(); narrowCard = vm.HasContact; LayoutPanes(); });
                                open.Classes.Add("quiet"); open.HorizontalAlignment=HorizontalAlignment.Stretch; ToolTip.SetTip(open,value); return open;
                            }
                            var text = new TextBlock { Text = setting.Id == "photo" ? value.Length > 0 ? "● Фото" : "—" : value, Margin = new(8,6), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                            ToolTip.SetTip(text,value); return text;
                        })
                    });
                }
            }
            table.ItemsSource = vm.Rows; UpdateSelectedRow();
        }
        finally { updating = false; }
        count.Text = vm.CountCaption;
    }
    private void UpdateSelectedRow()
    {
        var previous = updating; updating = true;
        table.SelectedItem = vm.Rows.FirstOrDefault(r => r.Id == vm.Original?.Id); updating = previous;
    }
    private void SortHeader(string id, bool multiple)
    {
        var old = vm.Configuration.Sorts.FirstOrDefault(s => s.ColumnId == id);
        var sorts = multiple ? vm.Configuration.Sorts.ToList() : new List<ContactSortRule>();
        if (old is null) sorts.Add(new(id));
        else if (!old.Descending) { var index=sorts.FindIndex(s=>s.ColumnId==id);if(index<0)sorts.Add(new(id,true));else sorts[index]=new(id,true); }
        else sorts.RemoveAll(s=>s.ColumnId==id);
        vm.Configure(vm.Configuration with { Sorts = sorts });
    }
    private void StoreColumnLayout()
    {
        if (updating || table.Columns.Count == 0) return;
        var columns = table.Columns.OrderBy(c => c.DisplayIndex).Select(c => new ContactColumnSetting((string)c.Tag!, Math.Clamp(c.ActualWidth,60,1200))).ToArray();
        if (columns.Any(c => !double.IsFinite(c.Width))) return;
        // Saving widths does not replace controls under the pointer.
        if (!columns.SequenceEqual(vm.Configuration.Columns)) { columnSignature = string.Join("|",columns.Select(c => $"{c.Id}:{c.Width}"))+string.Join("|",vm.Configuration.Sorts); vm.Configure(vm.Configuration with { Columns = columns }); }
    }
    private void RebuildCard()
    {
        var retainFieldFocus = FindById("ContactFieldSearch") is TextBox { IsKeyboardFocusWithin: true };
        var body = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var heading = new StackPanel { Spacing = 6, Margin = new(16,14) };
        var title = new Grid { ColumnDefinitions = new("*,Auto") }; contactTitle=new TextBlock { Text = vm.Name, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap }; title.Children.Add(contactTitle);
        var expand = Button(IsNarrow ? "← Список" : expanded ? "Рядом с таблицей" : "Развернуть", "ContactExpandButton", async () =>
        { if (IsNarrow) { if (await vm.ResolveDraftAsync()) narrowCard = false; } else expanded = !expanded; LayoutPanes(); }); Grid.SetColumn(expand,1); title.Children.Add(expand); heading.Children.Add(title);
        if (vm.ReadOnlyReason.Length > 0) heading.Children.Add(Note(vm.ReadOnlyReason));
        hiddenSelectionNote=Note(vm.HiddenSelection);hiddenSelectionNote.IsVisible=vm.HiddenSelection.Length>0;heading.Children.Add(hiddenSelectionNote);
        if (vm.HasContact)
        {
            var fieldSearch = new TextBox { Text = vm.FieldSearch, PlaceholderText = "Найти поле в карточке…" }; Id(fieldSearch,"ContactFieldSearch"); fieldSearch.TextChanged += (_,_) =>
            { if(ReferenceEquals(FindById("ContactFieldSearch"),fieldSearch)&&vm.FieldSearch!=(fieldSearch.Text??""))vm.FieldSearch=fieldSearch.Text??""; }; heading.Children.Add(fieldSearch);
            var all = new CheckBox { Content = "Показать все поля", IsChecked = vm.ShowAllFields }; Id(all,"ContactShowAllFields"); all.IsCheckedChanged += (_,_) => vm.ShowAllFields = all.IsChecked == true; heading.Children.Add(all);
        }
        body.Children.Add(heading);
        Control content = vm.HasContact ? ContactCardControls.Build(vm) : Note("Выберите строку таблицы, чтобы увидеть все данные контакта. Можно создать новый контакт в выбранной папке.");
        var scroll = new ScrollViewer { Content = content, Margin = new(16,0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; fieldScroll=scroll;Grid.SetRow(scroll,1); body.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(16,12) };
        if (vm.IsEditing)
        {
            var save = Button("Сохранить в папку", "ContactSaveButton", async () => await vm.SaveAsync()); save.Classes.Add("primary"); footer.Children.Add(save);
            footer.Children.Add(Button("Отменить", "ContactCancelButton", () => vm.CancelEdit())); draftIndicator=new TextBlock { Text = vm.IsDirty ? "Не сохранено" : "Нет изменений", VerticalAlignment = VerticalAlignment.Center }; Id(draftIndicator,"ContactDraftStatus"); footer.Children.Add(draftIndicator);
        }
        else if (vm.HasContact)
        {
            var edit = Button("Редактировать", "ContactEditButton", vm.BeginEdit); edit.IsEnabled = vm.CanEdit; edit.Classes.Add("primary"); footer.Children.Add(edit);
            var delete = Button("Удалить…", "ContactDeleteButton", vm.AskDelete); delete.IsEnabled = vm.CanEdit; footer.Children.Add(delete);
            footer.Children.Add(Button("Перечитать", "ContactReloadButton", async () => await vm.ReloadSelectedAsync()));
        }
        Grid.SetRow(footer,2); body.Children.Add(footer); card.Child = body; LayoutPanes();
        if (retainFieldFocus) Dispatcher.UIThread.Post(() => { if (FindById("ContactFieldSearch") is TextBox input) { input.Focus(); input.CaretIndex = input.Text?.Length ?? 0; } });
    }
    private void RebuildFields()
    {
        if(fieldScroll is not null)fieldScroll.Content=ContactCardControls.Build(vm);
    }
    private void LayoutPanes()
    {
        var narrow = IsNarrow;
        var singlePane = narrow || expanded;
        var showCard = singlePane && (narrowCard || expanded);
        var focusCard = showCard && table.IsVisible;
        var focusTable = singlePane && !showCard && card.IsVisible && card.IsKeyboardFocusWithin;
        panes.ColumnDefinitions = new(narrow || expanded ? (narrowCard || expanded ? "0,0,*" : "*,0,0") : $"{vm.TableFraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,10,{(1-vm.TableFraction).ToString(System.Globalization.CultureInfo.InvariantCulture)}*");
        table.IsVisible = !singlePane || !showCard;
        card.IsVisible = !singlePane || showCard;
        splitter.IsVisible = !narrow && !expanded;
        if(FindById("ContactExpandButton") is Button expand)expand.Content=narrow?"← Список":expanded?"Рядом с таблицей":"Развернуть";
        if(focusCard || focusTable) Dispatcher.UIThread.Post(() =>
        {
            if(vm.HasModal) return;
            if(card.IsVisible && !table.IsVisible) (FindById("ContactFieldSearch") ?? FindById("ContactExpandButton"))?.Focus();
            else if(table.IsVisible && !card.IsVisible) search.Focus();
        });
    }
    private bool IsNarrow => (TopLevel.GetTopLevel(this)?.Bounds.Width ?? Bounds.Width) < 900;
    private void OpenConfiguration(string panel) { if (vm.IsBusy || vm.IsActivityBusy || vm.HasModal) return; configurationPanel = panel; vm.ShowConfiguration=true; BuildConfiguration(); UpdateState(); }
    private void CloseModal() { configurationPanel = null; vm.ShowConfiguration=false; vm.ShowPhoto = false; fullPhoto?.Dispose(); fullPhoto = null; modal.IsVisible = false; UpdateState(); }
    private void BuildConfiguration()
    {
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(new TextBlock { Text = configurationPanel switch { "columns" => "Столбцы таблицы", "filters" => "Фильтры", "issues"=>"Проблемные файлы", _ => "Сортировка" }, FontSize = 22 });
        if (configurationPanel == "columns") BuildColumnsForm(form);
        else if (configurationPanel == "filters") BuildFiltersForm(form);
        else if(configurationPanel=="issues")BuildIssuesForm(form);
        else BuildSortsForm(form);
        form.Children.Add(Button("Готово", "ContactConfigurationClose", CloseModal)); ShowModal(form,"ContactConfigurationClose");
    }
    private void BuildIssuesForm(StackPanel form)
    {
        form.Children.Add(Note("Эти файлы исключены из таблицы: сначала исправьте формат или повторяющиеся идентификаторы. Просмотр не меняет файлы."));
        var list=new ListBox {Height=180,ItemsSource=vm.Issues,ItemTemplate=new FuncDataTemplate<ContactMirror.Application.WorkspaceIssue>((issue,_)=>new TextBlock {Text=issue is null?"":issue.Path+"\n"+issue.Message,TextWrapping=Avalonia.Media.TextWrapping.Wrap})};Id(list,"ContactIssuesList");form.Children.Add(list);
        var raw=new TextBox {IsReadOnly=true,AcceptsReturn=true,TextWrapping=Avalonia.Media.TextWrapping.NoWrap,Height=200};Id(raw,"ContactIssueRaw");form.Children.Add(raw);
        list.SelectionChanged+=async(_,_)=>
        {
            if(list.SelectedItem is not ContactMirror.Application.WorkspaceIssue issue)return;
            try{var text=await vm.ReadProblemFileAsync(issue);if(Equals(list.SelectedItem,issue))raw.Text=text;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ContactMirror.Core.SyncException or InvalidOperationException or System.Text.DecoderFallbackException){raw.Text="Не удалось прочитать файл: "+e.Message;}
        };
        form.Children.Add(Button("Открыть исходный файл…","ContactIssueOpenFile",()=>
        {
            if(list.SelectedItem is not ContactMirror.Application.WorkspaceIssue issue)return;
            try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(issue.Path){UseShellExecute=true});}
            catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception){raw.Text="Не удалось открыть файл: "+e.Message;}
        }));
    }
    private void BuildColumnsForm(StackPanel form)
    {
        form.Children.Add(Note("Выберите поля. Перетаскивайте заголовки таблицы для изменения порядка, меняйте ширину за край заголовка."));
        var find = new TextBox { PlaceholderText = "Найти столбец…" }; Id(find,"ContactColumnSearch"); form.Children.Add(find);
        var list = new ListBox { Height = 300 }; Id(list,"ContactColumnList");
        list.ItemTemplate = new FuncDataTemplate<ContactColumnDescriptor>((descriptor,_) =>
        {
            if (descriptor is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
            var box = new CheckBox { Content = descriptor.Title, IsChecked = vm.Configuration.Columns.Any(c=>c.Id==descriptor.Id) }; Id(box,"Column:"+descriptor.Id); row.Children.Add(box);
            box.IsCheckedChanged += (_,_) =>
            {
                var columns = vm.Configuration.Columns.ToList();
                if (box.IsChecked == true && !columns.Any(c=>c.Id==descriptor.Id)) columns.Add(new(descriptor.Id));
                else if (box.IsChecked != true) columns.RemoveAll(c=>c.Id==descriptor.Id);
                if (columns.Count == 0) { box.IsChecked = true; return; } vm.Configure(vm.Configuration with { Columns = columns });
            };
            var up = Button("↑", "ColumnUp:"+descriptor.Id, () => MoveColumn(descriptor.Id,-1)); Grid.SetColumn(up,1); row.Children.Add(up);
            var down = Button("↓", "ColumnDown:"+descriptor.Id, () => MoveColumn(descriptor.Id,1)); Grid.SetColumn(down,2); row.Children.Add(down); return row;
        });
        list.ItemsSource = ContactTableQuery.Columns;
        find.TextChanged += (_,_) => list.ItemsSource = ContactTableQuery.Columns.Where(c=>c.Title.Contains(find.Text ?? "",StringComparison.CurrentCultureIgnoreCase)).ToArray(); form.Children.Add(list);
        form.Children.Add(Button("Обычный набор столбцов", "ContactColumnsReset", () => { vm.Configure(vm.Configuration with { Columns=ContactTableConfiguration.Default.Columns }); BuildConfiguration(); }));
    }
    private void MoveColumn(string id,int direction)
    {
        var columns=vm.Configuration.Columns.ToList(); var index=columns.FindIndex(c=>c.Id==id); var next=index+direction;
        if(index<0||next<0||next>=columns.Count)return; (columns[index],columns[next])=(columns[next],columns[index]); vm.Configure(vm.Configuration with {Columns=columns});
    }
    private static string OperatorTitle(ContactFilterOperator value) => value switch
    { ContactFilterOperator.HasValue=>"Заполнено",ContactFilterOperator.Missing=>"Не заполнено",ContactFilterOperator.Contains=>"Содержит",ContactFilterOperator.NotContains=>"Не содержит",ContactFilterOperator.Equal=>"Равно",ContactFilterOperator.NotEqual=>"Не равно",ContactFilterOperator.Greater=>"Больше",ContactFilterOperator.GreaterOrEqual=>"Не меньше",ContactFilterOperator.Less=>"Меньше",_=>"Не больше" };
    private ComboBox FieldCombo(string id)
    {
        var combo=new ComboBox { ItemsSource=ContactTableQuery.Columns, SelectedIndex=1, MaxDropDownHeight=360, HorizontalAlignment=HorizontalAlignment.Stretch, ItemTemplate=new FuncDataTemplate<ContactColumnDescriptor>((c,_)=>new TextBlock {Text=c?.Title}) }; Id(combo,id); return combo;
    }
    private void BuildFiltersForm(StackPanel form)
    {
        var any=new CheckBox {Content="Достаточно любого условия (ИЛИ)",IsChecked=vm.Configuration.MatchAny}; any.IsCheckedChanged+=(_,_)=>vm.Configure(vm.Configuration with {MatchAny=any.IsChecked==true}); form.Children.Add(any);
        var selected=new StackPanel {Spacing=4};
        foreach(var rule in vm.Configuration.Filters)
        {
            var value=rule.ColumnId=="labels"&&Guid.TryParse(rule.Value,out var labelId)?vm.Labels.GetValueOrDefault(labelId,"Не найден ярлык")
                :rule.ColumnId=="starred"&&bool.TryParse(rule.Value,out var starred)?starred?"Да":"Нет":rule.Value;
            var captured=rule; selected.Children.Add(Button($"{ContactTableQuery.Columns.First(c=>c.Id==rule.ColumnId).Title}: {OperatorTitle(rule.Operator)} {value}   ×","RemoveFilter:"+vm.Configuration.Filters.ToList().IndexOf(rule),()=> {vm.Configure(vm.Configuration with {Filters=vm.Configuration.Filters.Where(r=>r!=captured).ToArray()});BuildConfiguration();}));
        }
        form.Children.Add(new ScrollViewer {Content=selected,MaxHeight=160});
        var field=FieldCombo("ContactFilterField"); var operation=new ComboBox {HorizontalAlignment=HorizontalAlignment.Stretch,ItemTemplate=new FuncDataTemplate<ContactFilterOperator>((o,_)=>new TextBlock {Text=OperatorTitle(o)})}; Id(operation,"ContactFilterOperator");
        form.Children.Add(field);form.Children.Add(operation);
        var input=new TextBox {PlaceholderText="Значение для сравнения; дата — ГГГГ-ММ-ДД"};Id(input,"ContactFilterValue");form.Children.Add(input);
        var label=new ComboBox {ItemsSource=vm.Labels.OrderBy(l=>l.Value).ToArray(),SelectedIndex=vm.Labels.Count>0?0:-1,HorizontalAlignment=HorizontalAlignment.Stretch,ItemTemplate=new FuncDataTemplate<KeyValuePair<Guid,string>>((l,_)=>new TextBlock {Text=l.Value})};Id(label,"ContactFilterLabel");form.Children.Add(label);
        var boolean=new ComboBox {ItemsSource=new[]{"Да","Нет"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};Id(boolean,"ContactFilterBoolean");form.Children.Add(boolean);
        var hint=Note("");form.Children.Add(hint);
        void Inputs()
        {
            var descriptor=(ContactColumnDescriptor)field.SelectedItem!;var op=operation.SelectedItem is ContactFilterOperator o?o:ContactFilterOperator.HasValue;
            var hasOperand=op is not (ContactFilterOperator.HasValue or ContactFilterOperator.Missing);
            label.IsVisible=hasOperand&&descriptor.Id=="labels"&&op is ContactFilterOperator.Equal or ContactFilterOperator.NotEqual;
            boolean.IsVisible=hasOperand&&descriptor.Type=="boolean";
            input.IsVisible=hasOperand&&!label.IsVisible&&!boolean.IsVisible;
        }
        void Operations() {var descriptor=(ContactColumnDescriptor)field.SelectedItem!; operation.ItemsSource=Enum.GetValues<ContactFilterOperator>().Where(o=>ContactTableQuery.IsSupported(descriptor,o)).ToArray();operation.SelectedIndex=0;Inputs();}
        field.SelectionChanged+=(_,_)=>Operations();operation.SelectionChanged+=(_,_)=>Inputs();Operations();
        form.Children.Add(Button("Добавить условие","ContactFilterAdd",()=>
        {
            if(field.SelectedItem is not ContactColumnDescriptor f || operation.SelectedItem is not ContactFilterOperator o)return;
            var value=label.IsVisible&&label.SelectedItem is KeyValuePair<Guid,string> chosen?chosen.Key.ToString():boolean.IsVisible?(boolean.SelectedIndex==0?"true":"false"):input.Text??"";
            if(label.IsVisible&&label.SelectedItem is null){hint.Text="В папке нет ярлыков для выбора.";return;}
            if(!ContactTableQuery.IsValidOperand(f,o,value)){hint.Text="Проверьте формат: целое/десятичное число или дата ГГГГ-ММ-ДД (неизвестная часть — 0).";return;}
            vm.Configure(vm.Configuration with {Filters=vm.Configuration.Filters.Append(new(f.Id,o,value)).ToArray()});BuildConfiguration();
        }));
        form.Children.Add(Button("Сбросить фильтры","ContactFiltersReset",()=> {vm.Configure(vm.Configuration with {Filters=[]});BuildConfiguration();}));
    }
    private void BuildSortsForm(StackPanel form)
    {
        form.Children.Add(Note("Правила применяются сверху вниз. Пустые значения идут в конце; для нескольких значений используется минимальное."));
        foreach(var rule in vm.Configuration.Sorts)
        {
            var captured=rule; var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=4};
            row.Children.Add(Button(ContactTableQuery.Columns.First(c=>c.Id==rule.ColumnId).Title + (rule.Descending?" ↓":" ↑"),"SortRule:"+rule.ColumnId,()=> {vm.Configure(vm.Configuration with {Sorts=vm.Configuration.Sorts.Select(r=>r==captured?r with {Descending=!r.Descending}:r).ToArray()});BuildConfiguration();}));
            row.Children.Add(Button("↑","SortUp:"+rule.ColumnId,()=> {var rules=vm.Configuration.Sorts.ToList();var i=rules.IndexOf(captured);if(i>0){(rules[i-1],rules[i])=(rules[i],rules[i-1]);vm.Configure(vm.Configuration with {Sorts=rules});BuildConfiguration();}}));
            row.Children.Add(Button("×","RemoveSort:"+rule.ColumnId,()=> {vm.Configure(vm.Configuration with {Sorts=vm.Configuration.Sorts.Where(r=>r!=captured).ToArray()});BuildConfiguration();}));form.Children.Add(row);
        }
        var field=FieldCombo("ContactSortField");form.Children.Add(field);
        form.Children.Add(Button("Добавить правило","ContactSortAdd",()=> {if(field.SelectedItem is ContactColumnDescriptor f){vm.Configure(vm.Configuration with {Sorts=vm.Configuration.Sorts.Append(new(f.Id)).ToArray()});BuildConfiguration();}}));
    }
    private async void UpdateModal()
    {
        if(vm.ShowDecision)
        {
            var form=new StackPanel {Spacing=12};form.Children.Add(new TextBlock {Text="Сохранить изменения контакта?",FontSize=22});form.Children.Add(Note("Вы редактировали карточку. Сохранение запишет контакт только в папку."));
            form.Children.Add(Button("Сохранить","ContactDecisionSave",()=>vm.Decide("save")));form.Children.Add(Button("Не сохранять","ContactDecisionDiscard",()=>vm.Decide("discard")));form.Children.Add(Button("Остаться в карточке","ContactDecisionStay",()=>vm.Decide("stay")));ShowModal(form,"ContactDecisionStay");
        }
        else if(vm.ShowDelete)
        {
            var form=new StackPanel {Spacing=12};form.Children.Add(new TextBlock {Text="Удалить «"+vm.Name+"» из папки?",FontSize=22,TextWrapping=Avalonia.Media.TextWrapping.Wrap});form.Children.Add(Note("Контакт и локальное фото попадут в корзину с резервной копией. Удаление в Google потребует отдельной проверки и подтверждения."));form.Children.Add(Button("Отмена","ContactDeleteCancel",vm.DismissDelete));form.Children.Add(Button("Удалить из папки","ContactDeleteConfirm",async()=>await vm.ConfirmDeleteAsync()));ShowModal(form,"ContactDeleteCancel");
        }
        else if(vm.ShowPhoto)
        {
            var bytes=vm.PhotoBytes;
            if(bytes is null){vm.ShowPhoto=false;return;}
            try {var decoded=await Task.Run(()=> {return ContactPhotoPreview.DecodeBounded(bytes,1200);});if(!vm.ShowPhoto){decoded.Dispose();return;}fullPhoto?.Dispose();fullPhoto=decoded;var form=new StackPanel {Spacing=10};form.Children.Add(new Image {Source=decoded,MaxHeight=460,Stretch=Avalonia.Media.Stretch.Uniform});form.Children.Add(Button("Закрыть фото","ContactPhotoClose",CloseModal));ShowModal(form,"ContactPhotoClose");}
            catch(Exception e) when(e is ArgumentException or IOException){vm.ValidationError("Не удалось открыть фото: "+e.Message);vm.ShowPhoto=false;}
        }
        else if(configurationPanel is null){modal.IsVisible=false;UpdateState();}
    }
    private void ShowModal(Control content,string focus)
    {
        modal.Child=new ScrollViewer {Content=content};modal.IsVisible=true;UpdateState();Dispatcher.UIThread.Post(()=>FindById(focus)?.Focus());
    }
    private Control? FindById(string id)=>Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<Control>().FirstOrDefault(c=>AutomationProperties.GetAutomationId(c)==id);
    private async void HandleKey(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.S&&e.KeyModifiers.HasFlag(KeyModifiers.Control)){if(!vm.HasModal)await vm.SaveAsync();e.Handled=true;return;}
        if(e.Key==Key.F&&e.KeyModifiers.HasFlag(KeyModifiers.Control)&&!modal.IsVisible){search.Focus();e.Handled=true;return;}
        if(e.Key==Key.Enter&&table.IsKeyboardFocusWithin&&table.SelectedItem is ContactRowViewModel row){await vm.SelectAsync(row.Id);narrowCard=vm.HasContact;LayoutPanes();e.Handled=true;return;}
        if(e.Key==Key.Escape){if(vm.ShowDecision)vm.Decide("stay");else if(vm.ShowDelete)vm.DismissDelete();else if(modal.IsVisible)CloseModal();else if(narrowCard&&IsNarrow){if(await vm.ResolveDraftAsync()){narrowCard=false;LayoutPanes();}}else return;e.Handled=true;return;}
        if(e.Key==Key.Tab&&modal.IsVisible)
        {
            var controls=Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(modal).OfType<Control>().Where(c=>c.Focusable&&c.IsEnabled&&c.IsVisible).ToArray();if(controls.Length==0)return;var i=Array.FindIndex(controls,c=>c.IsKeyboardFocusWithin);controls[(i+(e.KeyModifiers.HasFlag(KeyModifiers.Shift)?controls.Length-1:1))%controls.Length].Focus();e.Handled=true;
        }
    }
    private static TextBlock Note(string text)=>new() {Text=text,TextWrapping=Avalonia.Media.TextWrapping.Wrap,Classes={"caption"}};
    private static void Id(Control control,string id)=>AutomationProperties.SetAutomationId(control,id);
    private static Button Button(string text,string id,Action action){var button=new Button {Content=text};Id(button,id);button.Click+=(_,_)=>action();return button;}
    private static Button Button(string text,string id,Func<Task> action){var button=new Button {Content=text};Id(button,id);button.Click+=async(_,_)=>await action();return button;}
}
