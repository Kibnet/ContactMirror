using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Desktop.Contacts;

public sealed record ContactCardActions(
    Action<string, Func<EditorPopupContext, Action, Control>> ShowPopup,
    Action<string> EditField,
    Action<string> FindField,
    string? RevealedPath = null);

public static partial class ContactCardControls
{
    private sealed partial class CardBuilder
    {
        private Grid? localRoot;
        private static readonly ContactFieldOption[] Months = [new("", "Месяц"), .. Enumerable.Range(1, 12).Select(n => new ContactFieldOption(n.ToString(), CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetMonthName(n))))];
        private static readonly ContactFieldOption[] Modes = [new("Full", "Полная дата"), new("WithoutYear", "Без года"), new("YearOnly", "Только год"), new("YearAndMonth", "Год и месяц")];

        private Control BuildFriendly()
        {
            var panel = new StackPanel { Spacing = 12 };
            localRoot = new Grid(); localRoot.Children.Add(panel);
            localRoot.DetachedFromVisualTree += (_, _) => retired = true;
            if (captured is null) { panel.Children.Add(Text("Выберите контакт в таблице.")); return localRoot; }
            if (includeIdentity) panel.Children.Add(Identity());
            if (sectionOnly is null or "$entity" or "labels") panel.Children.Add(FriendlyLabels());
            foreach (var section in Sections)
            {
                var fields = section.Fields.Select(name => ContactFieldSchema.Fields.Single(f => f.Name == name))
                    .Where(f => sectionOnly is null or "$entity" || f.Name == sectionOnly)
                    .Where(f => vm.IsEditing
                        ? vm.ShowAllFields || actions?.RevealedPath?.StartsWith("data." + f.Name + ".", StringComparison.Ordinal) == true || Common.Contains(f.Name) || captured["data"]?[f.Name] is JsonArray { Count: > 0 }
                        : f.Name != "names" && captured["data"]?[f.Name] is JsonArray { Count: > 0 } || actions?.RevealedPath?.StartsWith("data." + f.Name + ".", StringComparison.Ordinal) == true).ToArray();
                if (fields.Length == 0) continue;
                if (!(vm.IsEditing && fields.Length == 1 && fields[0].Name == "names")) { var title = Text(section.Title); title.FontSize = 16; title.FontWeight = FontWeight.SemiBold; title.Margin = new(0, 8, 0, 0); panel.Children.Add(title); }
                foreach (var field in fields) panel.Children.Add(Category(field, captured["data"]?[field.Name] as JsonArray));
            }
            if (vm.IsEditing && sectionOnly is null) panel.Children.Add(Button("Добавить поле…", "ContactAddFieldButton", () => actions?.FindField("+")));
            if (sectionOnly is null && (vm.ShowAllFields || captured["extensions"] is JsonObject { Count: > 0 }))
            {
                var extra=new Expander{Header="Локальные дополнительные данные",IsExpanded=vm.ShowAllFields||actions?.RevealedPath is "extensions"||actions?.RevealedPath?.StartsWith("extensions.",StringComparison.Ordinal)==true};
                Identify(extra, "ContactExtensions", "Локальные дополнительные данные");
                Lazy(extra, () => ExtensionNode(["extensions"], "Дополнительные данные", false)); panel.Children.Add(extra);
            }
            if (sectionOnly is not null) return localRoot;
            var service = new Expander { Header = "Служебные сведения Google", IsExpanded = vm.ShowAllFields || actions?.RevealedPath == "google" };
            Identify(service, "ContactReadonlyGoogle", "Служебные сведения Google, только просмотр");
            Lazy(service, () => { var body = new StackPanel { Spacing = 8 }; body.Children.Add(ReadonlyNode(captured["id"], "Идентификатор контакта")); body.Children.Add(ReadonlyNode(captured["google"], "Google")); return body; }); panel.Children.Add(service);
            return localRoot;
        }

        public Control Identity()
        {
            var grid = new Grid { ColumnDefinitions = new("64,12,*,Auto"), MinHeight = 64 };
            Control avatar;
            if (vm.PhotoBytes is not null)
            {
                var image = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
                image.Bind(Image.SourceProperty, new Binding(nameof(vm.PhotoPreview)) { Source = vm }); Identify(image, "ContactPhotoPreview", "Фотография контакта");
                avatar = Button("", "PhotoViewButton", () => vm.ShowPhoto = true); ((Button)avatar).Content = image; ((Button)avatar).Padding = new(0); ((Button)avatar).Width = 64; ((Button)avatar).Height = 64;AutomationProperties.SetName(avatar,"Посмотреть фотографию контакта");
            }
            else avatar = new Border { Width = 64, Height = 64, CornerRadius = new(32), Background = Brushes.SlateGray, Child = new TextBlock { Text = Initials(vm.Name), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White } };
            grid.Children.Add(avatar);
            var words = new StackPanel { Spacing = 4 }; var name = Text(vm.Name == "Без имени" ? "Контакт без имени" : vm.Name); name.FontSize = 20; name.FontWeight = FontWeight.SemiBold; Identify(name, "ContactIdentityName", "Имя контакта"); words.Children.Add(name);
            ToolTip.SetTip(name,vm.Name);AutomationProperties.SetName(name,vm.Name);
            if (captured is not null)
            {
                var organization = ContactValuePresenter.PreferredSummary(captured, "organizations");
                var count=(captured["data"]?["organizations"] as JsonArray)?.Count??0;
                if(count>1)organization+=" · ещё "+(count-1)+" организаций";
                if (organization.Length > 0) { var company = Text(organization); company.FontSize = 13;ToolTip.SetTip(company,organization); words.Children.Add(company); }
            }
            var identityWords=new ScrollViewer{Content=words,MaxHeight=96,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            Grid.SetColumn(identityWords,2);grid.Children.Add(identityWords);
            if (vm.IsEditing)
            {
                var star = new CheckBox { Content = "Избранное", IsChecked = vm.Starred, IsEnabled = Editable }; Identify(star, "Editor:starred", "Избранное");
                var lastStar = star.IsChecked; star.IsCheckedChanged += (_, _) => { if (star.IsChecked == lastStar) return; lastStar = star.IsChecked; if (Editable) vm.SetStarred(star.IsChecked == true); }; Grid.SetColumn(star, 3); grid.Children.Add(star);
            }
            else if (vm.Starred) { var star = Text("★"); ToolTip.SetTip(star, "В избранном"); Identify(star, "ContactStarStatus", "В избранном"); Grid.SetColumn(star, 3); grid.Children.Add(star); }
            return grid;
        }
        private static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0])).ToUpperInvariant();

        private Control FriendlyLabels()
        {
            var body = new StackPanel { Spacing = 4 }; var box = new WrapPanel { Orientation = Orientation.Horizontal, MaxHeight = 62, ClipToBounds = true }; body.Children.Add(box);
            var assigned = (captured?["labels"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToArray();
            foreach (var raw in assigned.Take(6))
            {
                var title = Guid.TryParse(raw, out var id) && vm.Labels.TryGetValue(id, out var label) ? label : "Неизвестный ярлык";
                var chip = new Border { Padding = new(8, 4), Margin = new(0, 0, 6, 4), CornerRadius = new(10), Child = new TextBlock { Text = title, FontSize = 13, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis } };
                ToolTip.SetTip(chip, title + "\n" + raw); Identify(chip, "ContactLabel:" + raw, title); box.Children.Add(chip);
            }
            var controls = new WrapPanel(); body.Children.Add(controls); var more = Button("Ещё " + Math.Max(0, assigned.Length - 6), "ContactMoreLabelsButton", OpenLabels); more.IsVisible = assigned.Length > 6; controls.Children.Add(more);
            box.LayoutUpdated += (_, _) => { var hidden = assigned.Length - box.Children.Count(c => c.Bounds.Bottom <= 62 && c.Bounds.Height > 0); more.Content = "Ещё " + hidden; more.IsVisible = hidden > 0; };
            if (vm.IsEditing) controls.Children.Add(Button(assigned.Length == 0 ? "+ Добавить ярлык" : "Изменить ярлыки", "ContactLabelsButton", OpenLabels));
            return body;
        }
        private void OpenLabels() => Popup("labels", (token, close) =>
        {
            var body = new StackPanel { Spacing = 12 }; body.Children.Add(Heading("Ярлыки"));
            var original = (captured?["labels"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = new HashSet<string>(original, StringComparer.OrdinalIgnoreCase);
            var query = new TextBox { PlaceholderText = "Найти ярлык…" }; Identify(query, "ContactLabelSearch", "Найти ярлык"); body.Children.Add(query);
            var selected = Text("Выбрано: " + pending.Count); Identify(selected, "ContactLabelCount", selected.Text!); body.Children.Add(selected);
            var list = new ListBox { Height = 220 }; Identify(list, "ContactLabelsList", "Список ярлыков");
            var labels = vm.Labels.Select(p => new ContactFieldOption(p.Key.ToString(), p.Value)).Concat(original.Where(r => !Guid.TryParse(r, out var id) || !vm.Labels.ContainsKey(id)).Select(r => new ContactFieldOption(r, "Неизвестный ярлык"))).ToArray();
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ContactFieldOption>((item, _) =>
            {
                if (item is null) return new TextBlock();
                var check = new CheckBox { Content = new TextBlock { Text = item!.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 }, IsChecked = pending.Contains(item.Value), IsEnabled = vm.IsEditing }; Identify(check, "ChooseLabel:" + item.Value, item.Title);
                check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) pending.Add(item.Value); else pending.Remove(item.Value); selected.Text = "Выбрано: " + pending.Count; AutomationProperties.SetName(selected, selected.Text); }; return check;
            });
            void Filter() => list.ItemsSource = labels.Where(l => l.Title.Contains(query.Text ?? "", StringComparison.CurrentCultureIgnoreCase)).ToArray();
            query.TextChanged += (_, _) => Filter(); Filter(); body.Children.Add(list);
            if (vm.Labels.Count == 0) body.Children.Add(Text("В папке пока нет ярлыков для выбора."));
            var buttons = new WrapPanel(); var done = Button("Готово", "ContactLabelsDone", () =>
            {
                if (vm.IsEditing) vm.EndEditorPopup(token, doc => ContactsViewModel.ApplyLabelDelta(doc, original.Union(pending).Where(raw => original.Contains(raw) != pending.Contains(raw) && Guid.TryParse(raw, out _)).Select(raw => (Guid.Parse(raw), pending.Contains(raw)))), true);
                close();
            }); buttons.Children.Add(done); buttons.Children.Add(Button("Отменить", "ContactLabelsCancel", close)); body.Children.Add(buttons); return body;
        });

        private Control Category(ContactFieldDescriptor field, JsonArray? existing)
        {
            var panel = new StackPanel { Spacing = 8 };
            if (!vm.IsEditing)
            {
                var rows = existing is null ? [] : Enumerable.Range(0, existing.Count).Select(index => new RowRef(index, ["data", field.Name, index])).ToArray();
                Control ViewRow(RowRef row)
                {
                    var grid = new Grid { ColumnDefinitions = new("*,8,32"), MinHeight = 40 };
                    var text = new SelectableTextBlock { Text = ContactValuePresenter.Format(Read(row.Path), field.Name), FontSize = 14, TextWrapping = TextWrapping.Wrap }; Identify(text, "View:" + PathText(row.Path), ContactPresentationCatalog.Title(field.Name)); grid.Children.Add(text);
                    CardRows.Mark(grid, Read(row.Path), PathText(row.Path));
                    var menu = Button("⋮", "RowMenu:" + field.Name + ":" + row.Index, () => Popup(PathText(row.Path), (token, close) =>
                    {
                        var items = new StackPanel { Spacing = 8 }; items.Children.Add(Button("Копировать", "Copy:" + PathText(row.Path), async () => { var clipboard = TopLevel.GetTopLevel(grid)?.Clipboard; if (clipboard is not null) { var entry = Read(row.Path); var text = (entry?["value"] ?? entry?["url"] ?? entry?["person"])?.ToString() ?? ContactValuePresenter.Format(entry, field.Name); var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(text)); await clipboard.SetDataAsync(data); } close(); }));
                        if (vm.ReadOnlyReason.Length==0&&!vm.IsBusy&&!vm.IsActivityBusy) items.Children.Add(Button("Редактировать это поле", "EditRow:" + field.Name + ":" + row.Index, () => { close(); var path = PathText(row.Path) + "." + (field.Children.FirstOrDefault(c => c.Name is "value" or "person" or "name" or "url")?.Name ?? field.Children.First().Name); actions?.EditField(path); })); return items;
                    })); menu.MinWidth = 32; menu.MinHeight = 32; AutomationProperties.SetName(menu, "Действия для " + ContactPresentationCatalog.Title(field.Name).ToLowerInvariant()); Grid.SetColumn(menu, 2); grid.Children.Add(menu); return grid;
                }
                    if (rows.Length > 5) panel.Children.Add(VirtualRows(rows, ViewRow, "Rows:" + field.Name)); else foreach (var row in rows) panel.Children.Add(ViewRow(row));
                if (rows.Length == 0) { panel.Children.Add(Text(field.Title + ": не заполнено")); panel.Children.Add(Button("Заполнить поле", "EditMissing:" + field.Name, () => actions?.EditField(actions.RevealedPath ?? "data." + field.Name + ".0." + field.Children.First(c => c.Name != "metadata").Name))); }
                if (rows.Length > 0 && (field.Name is not ("phoneNumbers" or "emailAddresses") || actions?.RevealedPath?.StartsWith("data." + field.Name + ".", StringComparison.Ordinal) == true))
                {
                    var detail = new Expander { Header = "Подробности · " + field.Title, IsExpanded = actions?.RevealedPath?.StartsWith("data." + field.Name + ".", StringComparison.Ordinal) == true }; Lazy(detail, () => { var values = new StackPanel { Spacing = 8 }; foreach (var row in rows) foreach (var child in field.Children) values.Children.Add(ViewField(child, Append(row.Path, child.Name), field.Name)); return values; }); panel.Children.Add(detail);
                }
                return panel;
            }
            if (field.Name != "names") { var title = Text(field.Title); title.FontSize = 14; title.FontWeight = FontWeight.SemiBold; panel.Children.Add(title); }
            var count = existing?.Count ?? 0;
            var entries = Enumerable.Range(0, count).Select(index => new RowRef(index, ["data", field.Name, index])).ToArray();
            if (count > 5) panel.Children.Add(VirtualRows(entries, row => SchemaRow(field, row), "Rows:" + field.Name)); else foreach (var row in entries) panel.Children.Add(SchemaRow(field, row));
            if (count == 0 && field.Name is "names" or "phoneNumbers" or "emailAddresses") panel.Children.Add(PlaceholderRow(field));
            else if (count == 0 && actions?.RevealedPath?.StartsWith("data." + field.Name + ".", StringComparison.Ordinal) == true) panel.Children.Add(SchemaRow(field, new RowRef(0, ["data", field.Name, 0])));
            var add = Button("+ " + (field.Name == "phoneNumbers" ? "Телефон" : field.Name == "emailAddresses" ? "Электронная почта" : field.Title), "AddField:" + field.Name, () =>
            {
                var index = (Read(new object[] { "data", field.Name }) as JsonArray)?.Count ?? 0;
                if (Change(doc => {
                var data = doc["data"] as JsonObject ?? new JsonObject(); if (doc["data"] is null) doc["data"] = data;
                var array = data[field.Name] as JsonArray ?? new JsonArray(); if (data[field.Name] is null) data[field.Name] = array;
                if (!field.IsSingleton || array.Count == 0) array.Add(new JsonObject());
                }, true, field.Path)) actions?.EditField("data." + field.Name + "." + index + "." + field.Children.Where(c => c.Name != "metadata").OrderBy(c => FieldPriority(c.Name)).First().Name);
            }); add.IsEnabled = Editable && (!field.IsSingleton || count == 0); panel.Children.Add(add); return panel;
        }
        private Control ViewField(ContactFieldDescriptor field, IReadOnlyList<object> path, string category)
        {
            if (field.Children.Count > 0) { var group = new StackPanel { Spacing = 4 }; foreach (var child in field.Children) group.Children.Add(ViewField(child, Append(path, child.Name), category)); return group; }
            var text = new SelectableTextBlock { Text = ContactPresentationCatalog.Title(category, field.Name) + ": " + (Read(path) is { } value ? ContactValuePresenter.Format(value, category, field.Name) : "Не задано"), FontSize = 14, TextWrapping = TextWrapping.Wrap }; Identify(text, "View:" + PathText(path), text.Text!); return text;
        }
        private Control PlaceholderRow(ContactFieldDescriptor field)
        {
            var panel = new StackPanel { Spacing = 8 }; var keys = field.Name == "names" ? new[] { "givenName", "familyName" } : new[] { "value" };
            foreach (var key in keys)
            {
                var input = new TextBox { PlaceholderText = ContactPresentationCatalog.Title(field.Name, key), MinHeight = 36 }; Identify(input, "Editor:data." + field.Name + ".0." + key, ContactPresentationCatalog.Title(field.Name, key));
                input.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty || !Editable) return; var text = input.Text ?? ""; if (text.Length == 0 && Read(new object[] { "data", field.Name, 0, key }) is null) return; Change(doc => { var data = doc["data"] as JsonObject ?? new JsonObject(); if (doc["data"] is null) doc["data"] = data; var list = data[field.Name] as JsonArray ?? new JsonArray(); if (data[field.Name] is null) data[field.Name] = list; if (list.Count == 0) list.Add(new JsonObject()); list[0]![key] = text; }, false, "data." + field.Name); }; panel.Children.Add(input);
            }
            if (field.Name is "phoneNumbers" or "emailAddresses") panel.Children.Add(Scalar(field.Children.Single(c => c.Name == "type"), new object[] { "data", field.Name, 0, "type" }, field.Name)); return panel;
        }
        private Control SchemaRow(ContactFieldDescriptor category, RowRef row)
        {
            var panel = new StackPanel { Spacing = 8, Margin = new(0, 0, 0, 8) };
            CardRows.Mark(panel, Read(row.Path), PathText(row.Path));
            var children = category.Children.Where(c => c.Name != "metadata").OrderBy(c => FieldPriority(c.Name)).ToArray();
            var main = children.Where(c => IsMain(category.Name, c.Name)).ToArray();
            var mainGrid = Pairs(main.Select(c => SchemaField(c, Append(row.Path, c.Name), category.Name)).ToArray(), category.Name is "names" or "phoneNumbers" or "emailAddresses"); var rowBody = new Grid { ColumnDefinitions = new("*,6,32") }; rowBody.Children.Add(mainGrid); panel.Children.Add(rowBody);
            var extras = children.Except(main).Concat(category.Children.Where(c => c.Name == "metadata")).ToArray();
            if (extras.Length > 0)
            {
                var details = new Expander { Header = category.Name == "names" ? "Другие части имени" : "Дополнительные сведения", IsExpanded = vm.ShowAllFields || actions?.RevealedPath?.StartsWith(PathText(row.Path) + ".", StringComparison.Ordinal) == true }; Identify(details, "Details:" + PathText(row.Path), "Дополнительные сведения записи");
                Lazy(details, () => { var fields = new StackPanel { Spacing = 8 }; foreach (var child in extras) fields.Children.Add(SchemaField(child, Append(row.Path, child.Name), category.Name)); if (Read(row.Path) is JsonObject obj) AddUnknown(fields, obj, category.Children.Select(c => c.Name).ToHashSet()); return fields; }); panel.Children.Add(details);
            }
            var menu = Button("⋮", "RowMenu:" + category.Name + ":" + row.Index, () => Popup(PathText(row.Path), (token, close) =>
            {
                var body = new StackPanel { Spacing = 8 }; body.Children.Add(Heading(category.Title));
                body.Children.Add(Button("Сделать основным", "SetPrimary:" + PathText(row.Path), () => { vm.EndEditorPopup(token, doc => { if (doc["data"]?[category.Name] is JsonArray list) foreach (var entry in list.OfType<JsonObject>()) { if (ReferenceEquals(entry, list[row.Index])) { if (entry["metadata"] is not JsonObject) entry["metadata"] = new JsonObject(); entry["metadata"]!["sourcePrimary"] = true; } else if (entry["metadata"] is JsonObject metadata && metadata["sourcePrimary"]?.ToString() == "true") metadata["sourcePrimary"] = false; } }, true); close(); }));
                body.Children.Add(Button("Снять предпочтение", "ClearPrimary:" + PathText(row.Path), () => { vm.EndEditorPopup(token, doc => Set(doc, Append(Append(row.Path, "metadata"), "sourcePrimary"), null), true); close(); }));
                body.Children.Add(Button("Удалить "+category.Title.ToLowerInvariant(),"RemoveRow:"+category.Name+":"+row.Index,()=>
                {
                    if(vm.EndEditorPopup(token,doc=>{if(doc["data"]?[category.Name] is JsonArray list&&row.Index<list.Count)list.RemoveAt(row.Index);}))
                    {
                        vm.RemoveRowInputs("data."+category.Name,row.Index);close();vm.RefreshStructure();
                        var count=(captured?["data"]?[category.Name] as JsonArray)?.Count??0;
                        var next=Math.Max(0,Math.Min(row.Index,count-1));
                        var first = category.Name is "birthdays" or "events" ? "date" : children.First(c => c.Name != "metadata").Name;
                        actions?.EditField(count == 0 ? "add:data." + category.Name : "data."+category.Name+"."+next+"."+first);
                    }
                    else close();
                }));return body;
            })); menu.HorizontalAlignment = HorizontalAlignment.Right; menu.VerticalAlignment = VerticalAlignment.Top; menu.MinHeight = 32; menu.MinWidth = 32; menu.Padding = new(4); Grid.SetColumn(menu, 2); rowBody.Children.Add(menu); return panel;
        }
        private static bool IsMain(string category, string property) => category switch
        {
            "names" => property is "givenName" or "familyName",
            "addresses" => property is "streetAddress" or "extendedAddress" or "city" or "region" or "postalCode" or "country" or "type",
            "organizations" => property is "name" or "title" or "type" or "department" or "current" or "startDate" or "endDate",
            "birthdays" => property == "date",
            "events" => property is "date" or "type",
            "biographies" => property == "value",
            _ => property is "value" or "type" or "key" or "person" or "username" or "protocol" or "url"
        };
        private static Control Pairs(Control[] controls, bool compact = false)
        {
            var grid = new Grid(); foreach (var control in controls) grid.Children.Add(control);
            void Layout(double width) { var columns = width >= (compact ? 330 : 560) ? 2 : 1; grid.ColumnDefinitions = new(columns == 2 ? "*,12,*" : "*"); grid.RowDefinitions = new(string.Join(',', Enumerable.Repeat("Auto", (controls.Length + columns - 1) / columns))); for (var i = 0; i < controls.Length; i++) { Grid.SetColumn(controls[i], columns == 2 ? (i % 2) * 2 : 0); Grid.SetRow(controls[i], i / columns); controls[i].Margin = new(0, 0, 0, 8); } }
            Layout(0); grid.SizeChanged += (_, _) => Layout(grid.Bounds.Width); return grid;
        }
        private Control SchemaField(ContactFieldDescriptor field, IReadOnlyList<object> path, string category)
        {
            if (field.IsDate) return DateEditor(field, path, category);
            if (field.Children.Count == 0) return Scalar(field, path, category);
            var panel = new StackPanel { Spacing = 8 }; foreach (var child in field.Children) panel.Children.Add(SchemaField(child, Append(path, child.Name), category)); return panel;
        }
        private Control Scalar(ContactFieldDescriptor field, IReadOnlyList<object> path, string category)
        {
            var key = PathText(path); var current = Read(path); var title = ContactPresentationCatalog.Title(category, field.Name);
            var panel = new StackPanel { Spacing = 4 };
            if (field.Type == "boolean")
            {
                var check = new CheckBox { Content = title, IsThreeState = true, IsChecked = current is JsonValue value && value.TryGetValue<bool>(out var boolValue) ? boolValue : null, IsEnabled = Editable }; Identify(check, "Editor:" + key, title + ", не задано/да/нет");
                var lastChecked = check.IsChecked; check.IsCheckedChanged += (_, _) => { if (check.IsChecked == lastChecked) return; lastChecked = check.IsChecked; if (!Editable) return; Change(doc => { if (field.Name == "sourcePrimary" && check.IsChecked == true && doc["data"]?[category] is JsonArray entries) foreach (var entry in entries.OfType<JsonObject>()) if (entry["metadata"] is JsonObject metadata && metadata["sourcePrimary"]?.ToString() == "true") metadata["sourcePrimary"] = false; Set(doc, path, check.IsChecked is null ? null : JsonValue.Create(check.IsChecked.Value)); }, false, key); }; panel.Children.Add(check); return panel;
            }
            var label = Text(title); label.FontSize = 13; panel.Children.Add(label);
            var choices = ContactPresentationCatalog.Options(category, field.Name);
            if (choices.Count > 0)
            {
                var options = new List<ContactFieldOption> { new("", field.Name == "type" ? "Тип не задан" : "Не задано") }; options.AddRange(choices);
                if (current is not null && !options.Any(o => o.Value == current.ToString())) options.Add(new(current.ToString(), current.ToString()));
                ContactFieldOption? customChoice = null;
                if (ContactPresentationCatalog.AllowsCustom(category, field.Name)) { customChoice = new("$custom", "Свой вариант…"); options.Add(customChoice); }
                panel.Children.Add(OptionControl(key, options, current?.ToString() ?? "", option => doc => Set(doc, path, option.Value.Length == 0 ? null : JsonValue.Create(option.Value)), () => CustomType(path, title, current?.ToString() ?? ""), customChoice,title)); return panel;
            }
            var raw = current?.ToString() ?? ""; if (field.Name == "fullTimeEquivalentMillipercent" && current is not null) raw = ContactValuePresenter.Format(current, category, field.Name).TrimEnd('%');
            var input = new TextBox { Text = vm.GetInput(key, raw), PlaceholderText = field.Type == "integer" ? "Не задано" : title, MinHeight = 36, AcceptsReturn = category == "biographies" && field.Name == "value", TextWrapping = category == "biographies" ? TextWrapping.Wrap : TextWrapping.NoWrap, IsEnabled = Editable }; if (input.AcceptsReturn) input.MinHeight = 96;
            Identify(input, "Editor:" + key, title); var last = input.Text;
            var line = new Grid { ColumnDefinitions = new("*,28") }; line.Children.Add(input);
            var clear = Button("×", "Clear:" + key, () => { if (Change(doc => Set(doc, path, null), false, key)) { vm.ClearInput(key); last = ""; input.Text = ""; } }); clear.IsVisible = !string.IsNullOrEmpty(input.Text); clear.MinWidth = 28; clear.MinHeight = 28; clear.Padding = new(0); AutomationProperties.SetName(clear, "Очистить: " + title); Grid.SetColumn(clear, 1); line.Children.Add(clear); panel.Children.Add(line);
            var validation = Text(vm.ValidationErrors.GetValueOrDefault(key, "")); validation.IsVisible = validation.Text?.Length > 0; Identify(validation, "Error:" + key, "Ошибка: " + title); panel.Children.Add(validation);
            input.PropertyChanged += (_, e) =>
            {
                if (e.Property != TextBox.TextProperty) return;
                clear.IsVisible = !string.IsNullOrEmpty(input.Text); if (!Editable || input.Text == last) return; last = input.Text; var text = input.Text ?? ""; JsonNode? value; string error = "";
                if (text.Length == 0 && field.Type is "integer" or "number") value = null;
                else if (field.Name == "fullTimeEquivalentMillipercent") { if (ContactValuePresenter.TryParsePercentInput(text, out var percent, out error)) value = JsonValue.Create(percent); else value = null; }
                else if (field.Type == "integer") { if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) value = JsonValue.Create(number); else { value = null; error = title + ": введите целое число."; } }
                else if (field.Type == "number") { if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) value = JsonValue.Create(number); else { value = null; error = title + ": введите число."; } }
                else value = JsonValue.Create(text);
                validation.Text = error; validation.IsVisible = error.Length > 0;
                if (error.Length > 0) { vm.SetInvalidInput(key, text, error); return; }
                if (Change(doc => Set(doc, path, value), false, key)) vm.ClearInput(key);
            };
            return panel;
        }
        private Control OptionControl(string key, IReadOnlyList<ContactFieldOption> options, string selected, Func<ContactFieldOption, Action<JsonObject>> mutation, Action? custom = null, ContactFieldOption? customChoice = null,string? title=null)
        {
            var combo = new ComboBox { ItemsSource = options, DisplayMemberBinding = new Binding(nameof(ContactFieldOption.Title)), SelectedItem = options.FirstOrDefault(o => o.Value == selected) ?? options[0], HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 36 }; Identify(combo, "Editor:" + key, title??key);
            void Describe(){if(combo.SelectedItem is ContactFieldOption option){ToolTip.SetTip(combo,option.Title);AutomationProperties.SetName(combo,(title??key)+": "+option.Title);}}
            Describe();combo.PropertyChanged+=(_,e)=>{if(e.Property==ComboBox.SelectedItemProperty)Describe();};
            EditorPopupContext? context = null; ContactFieldOption? pending = null; var applying = false; var cancelled = false; var original = combo.SelectedItem;
            void Apply(ContactFieldOption option, EditorPopupContext? token)
            {
                if (ReferenceEquals(option, customChoice)) { if (token is not null) vm.EndEditorPopup(token); applying = true; combo.SelectedItem = original; applying = false; custom?.Invoke(); return; }
                var success = token is not null ? vm.EndEditorPopup(token, mutation(option)) : Editable && Change(mutation(option), false, key);
                if (success) { vm.ClearInput(key); original = combo.SelectedItem; } else { applying = true; combo.SelectedItem = original; applying = false; }
            }
            combo.PropertyChanged += (_, e) =>
            {
                if (e.Property != ComboBox.IsDropDownOpenProperty) return;
                if (combo.IsDropDownOpen) { context = vm.BeginEditorPopup(key); pending = null; cancelled = false; if (context is null) combo.IsDropDownOpen = false; }
                else if (context is { } token) { context = null; if (cancelled || pending is null) { vm.EndEditorPopup(token); applying = true; combo.SelectedItem = original; applying = false; } else Apply(pending, token); }
            };
            combo.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) cancelled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            combo.SelectionChanged += (_, _) => { if (applying || combo.SelectedItem is not ContactFieldOption choice || ReferenceEquals(choice, original)) return; if (context is not null) pending = choice; else if (Editable) Apply(choice, null); }; return combo;
        }
        private void CustomType(IReadOnlyList<object> path, string title, string current) => Popup(PathText(path), (token, close) =>
        {
            var form = new StackPanel { Spacing = 12 }; form.Children.Add(Heading("Свой вариант: " + title)); var text = new TextBox { Text = current, MinHeight = 36 }; Identify(text, "CustomTypeInput", "Свой вариант"); form.Children.Add(text); var error = Text(""); form.Children.Add(error);
            form.Children.Add(Button("Применить", "CustomTypeApply", () => { if (string.IsNullOrWhiteSpace(text.Text)) { error.Text = "Укажите свой вариант."; return; } vm.EndEditorPopup(token, doc => Set(doc, path, JsonValue.Create(text.Text)), true); close(); })); form.Children.Add(Button("Отменить", "CustomTypeCancel", close)); return form;
        });
        private Control DateEditor(ContactFieldDescriptor field, IReadOnlyList<object> path, string category)
        {
            var key = PathText(path); var existing = Read(path); var selected = ContactValuePresenter.TryDateMode(existing, out var mode) ? mode : ContactDateMode.WithoutYear;
            if (Enum.TryParse<ContactDateMode>(vm.GetInput(key + ".mode", selected.ToString()), out var bufferedMode)) selected = bufferedMode;
            var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(Text(ContactPresentationCatalog.Title(category, field.Name)));
            var year = new TextBox { Text = vm.GetInput(key + ".year", existing?["year"]?.ToString() is "0" ? "" : existing?["year"]?.ToString() ?? ""), PlaceholderText = "Год", MinHeight = 36 }; Identify(year, "Editor:" + key + ".year", "Год");
            var day = new TextBox { Text = vm.GetInput(key + ".day", existing?["day"]?.ToString() is "0" ? "" : existing?["day"]?.ToString() ?? ""), PlaceholderText = "День", MinHeight = 36 }; Identify(day, "Editor:" + key + ".day", "День");
            var month = new ComboBox { ItemsSource = Months, DisplayMemberBinding = new Binding(nameof(ContactFieldOption.Title)), SelectedItem = Months.FirstOrDefault(m => m.Value == vm.GetInput(key + ".month", existing?["month"]?.ToString() ?? "")) ?? Months[0], MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch }; Identify(month, "Editor:" + key + ".month", "Месяц");
            var parts = new Grid { ColumnDefinitions = new("64,8,*,8,84") }; parts.Children.Add(day); Grid.SetColumn(month, 2); parts.Children.Add(month); Grid.SetColumn(year, 4); parts.Children.Add(year); panel.Children.Add(parts);
            var precision = new ComboBox { ItemsSource = Modes, DisplayMemberBinding = new Binding(nameof(ContactFieldOption.Title)), SelectedItem = Modes.Single(m => m.Value == selected.ToString()), MinHeight = 36 }; Identify(precision, "DateMode:" + key, "Какие части даты известны"); panel.Children.Add(precision);
            var error = Text(vm.ValidationErrors.FirstOrDefault(p => p.Key.StartsWith(key + ".", StringComparison.Ordinal)).Value ?? ""); error.IsVisible = error.Text?.Length > 0; Identify(error, "Error:" + key, "Ошибка даты"); panel.Children.Add(error);
            void Visibility() { day.IsVisible = selected is ContactDateMode.Full or ContactDateMode.WithoutYear; month.IsVisible = selected != ContactDateMode.YearOnly; year.IsVisible = selected != ContactDateMode.WithoutYear; }
            Visibility(); var touched = new HashSet<string>(new[] { "year", "month", "day", "mode" }.Where(part => vm.ValidationErrors.ContainsKey(key + "." + part)));
            bool Update(string part,EditorPopupContext? token=null)
            {
                if(token is null&&!Editable)return false;
                var pendingTouched=new HashSet<string>(touched){part};
                var y = int.TryParse(year.Text, out var yy) ? yy : (int?)null;
                var d = int.TryParse(day.Text, out var dd) ? dd : (int?)null;
                var m = int.TryParse((month.SelectedItem as ContactFieldOption)?.Value, out var mm) ? mm : (int?)null;
                var invalidInteger = year.IsVisible && !string.IsNullOrEmpty(year.Text) && y is null || day.IsVisible && !string.IsNullOrEmpty(day.Text) && d is null;
                var valid = ContactValuePresenter.TryCreateDate(selected, y, m, d, out var date, out var message); if (invalidInteger) { valid = false; message = "Введите целое число для года и дня."; }
                if(!valid)
                {
                    var typed=part=="year"?year.Text??"":part=="day"?day.Text??"":part=="mode"?selected.ToString():(month.SelectedItem as ContactFieldOption)?.Value??"";
                    var accepted=token is null?vm.SetInvalidInput(key+"."+part,typed,message):vm.EndEditorPopup(token,inputChange:new(Invalid:new Dictionary<string,EditorInvalidInput>{{key+"."+part,new(typed,message)}}));
                    if(accepted){touched=pendingTouched;error.Text=message;error.IsVisible=true;}return accepted;
                }
                void Patch(JsonObject doc)
                {
                    var target=ReadFrom(doc,path) as JsonObject;
                    if(target is null){Set(doc,path,date);return;}
                    foreach(var component in new[]{"year","month","day"})
                        if(pendingTouched.Contains(component)||pendingTouched.Contains("mode"))
                        {if(date![component] is { } value)target[component]=value.DeepClone();else target.Remove(component);}
                }
                var changed=token is null?Change(Patch,false,key):vm.EndEditorPopup(token,Patch,inputChange:new(ClearPrefix:key));
                if(changed){touched=pendingTouched;if(token is null)vm.ClearInput(key);error.IsVisible=false;}return changed;
            }
            var lastYear = year.Text; var lastDay = day.Text;
            year.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty || year.Text == lastYear) return; lastYear = year.Text; Update("year"); }; day.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty || day.Text == lastDay) return; lastDay = day.Text; Update("day"); };
            void ModeVisibility(){if(precision.SelectedItem is ContactFieldOption chosen)selected=Enum.Parse<ContactDateMode>(chosen.Value);Visibility();}
            OwnSelection(month,key+".month",token=>Update("month",token));
            OwnSelection(precision,key+".mode",token=>{ModeVisibility();return Update("mode",token);},ModeVisibility);
            panel.Children.Add(Button("Очистить дату", "Clear:" + key, () => { if (Change(doc => Set(doc, path, null), false, key)) { vm.ClearInput(key); vm.RefreshStructure(); } })); return panel;
        }
        private void OwnSelection(ComboBox combo,string key,Func<EditorPopupContext?,bool> confirmed,Action? restored=null)
        {
            EditorPopupContext? context = null; object? pending = null; var original = combo.SelectedItem; var applying = false; var cancelled = false;
            void Restore() { applying = true; combo.SelectedItem = original; applying = false;restored?.Invoke(); }
            combo.PropertyChanged+=(_,e)=>
            {
                if(e.Property!=ComboBox.IsDropDownOpenProperty)return;
                if(combo.IsDropDownOpen){context=vm.BeginEditorPopup(key);pending=null;cancelled=false;if(context is null)combo.IsDropDownOpen=false;}
                else if(context is { } token)
                {
                    context=null;
                    if(cancelled||pending is null){vm.EndEditorPopup(token);Restore();}
                    else if(confirmed(token))original=combo.SelectedItem;
                    else{vm.EndEditorPopup(token);Restore();}
                }
            };
            combo.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) cancelled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            combo.SelectionChanged+=(_,_)=>{if(applying||ReferenceEquals(original,combo.SelectedItem))return;if(context is not null)pending=combo.SelectedItem;else if(Editable&&confirmed(null))original=combo.SelectedItem;else Restore();};
        }
        private static JsonNode? ReadFrom(JsonObject doc, IReadOnlyList<object> path) => ContactCardControls.Read(doc, path);
        private static TextBlock Heading(string title) => new() { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private void Popup(string path, Func<EditorPopupContext, Action, Control> factory)
        {
            if (actions is not null) { actions.ShowPopup(path, factory); return; }
            if (localRoot is null || vm.BeginEditorPopup(path) is not { } context) return;
            var overlay = new Border { Background = Brushes.DimGray, Padding = new(16), MaxWidth = 440, MaxHeight = 500, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            void Close() { vm.EndEditorPopup(context); localRoot.Children.Remove(overlay); }
            overlay.Child = factory(context, Close); localRoot.Children.Add(overlay); KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle); overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        }
    }
}
