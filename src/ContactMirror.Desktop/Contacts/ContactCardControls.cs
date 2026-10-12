using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;

namespace ContactMirror.Desktop.Contacts;

/// <summary>Typed schema forms; large repeated categories realize only their visible rows.</summary>
public static class ContactCardControls
{
    private static readonly HashSet<string> Common = ["names", "phoneNumbers", "emailAddresses", "addresses", "organizations", "birthdays", "biographies", "urls"];
    private static readonly (string Title,string[] Fields)[] Sections =
    [
        ("Имя и обращения",["names","nicknames","genders"]),
        ("Связь",["phoneNumbers","emailAddresses","imClients","sipAddresses","urls","calendarUrls"]),
        ("Организации и занятия",["organizations","occupations"]),
        ("Адреса",["addresses","locations"]),
        ("Даты и события",["birthdays","events"]),
        ("Заметки и интересы",["biographies","interests","miscKeywords"]),
        ("Дополнительные сведения",["relations","locales","externalIds","clientData","userDefined"])
    ];
    public static Control Build(ContactsViewModel vm) => new CardBuilder(vm).Build();
    private sealed record RowRef(int Index, IReadOnlyList<object> Path);
    private sealed record JsonTypeOption(string Type, string Title);
    private static readonly JsonTypeOption[] JsonTypes = [new("string", "Текст"), new("number", "Число"), new("boolean", "Да / нет"), new("object", "Объект"), new("array", "Список"), new("null", "Пустое значение (null)")];

    private sealed class CardBuilder(ContactsViewModel vm)
    {
        private readonly JsonObject? captured = vm.Document;
        private bool retired;
        private bool Editable => !retired && vm.IsEditing && vm.CanEdit && ReferenceEquals(captured, vm.Document);

        public Control Build()
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.DetachedFromVisualTree+=(_,_)=>retired=true;
            if (captured is null) { panel.Children.Add(Text("Выберите контакт в таблице, чтобы открыть карточку.")); return panel; }
            panel.Children.Add(Photo());
            var starred = new CheckBox { Content = "Избранное", IsChecked = vm.Starred, IsEnabled = Editable };
            Identify(starred, "Editor:starred", "Избранное");
            starred.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty && Editable) { vm.SetStarred(starred.IsChecked == true); vm.ValidationError("", "starred"); } };
            panel.Children.Add(starred); panel.Children.Add(Labels());
            foreach (var section in Sections)
            {
                var fields=section.Fields.Select(name=>ContactFieldSchema.Fields.Single(field=>field.Name==name))
                    .Where(field=>(vm.ShowAllFields || Common.Contains(field.Name) || captured["data"]?[field.Name] is JsonArray {Count: >0}) && MatchesField(field)).ToArray();
                if(fields.Length==0)continue;
                var heading=Text(section.Title);heading.FontSize=17;heading.FontWeight=FontWeight.SemiBold;heading.Margin=new(0,10,0,0);panel.Children.Add(heading);
                foreach(var field in fields) panel.Children.Add(Category(field,captured["data"]?[field.Name] as JsonArray));
            }
            if (vm.ShowAllFields || captured["extensions"] is JsonObject { Count: > 0 })
            {
                var extensions = new Expander { Header = "Локальные дополнительные данные", IsExpanded = vm.ShowAllFields || vm.FieldSearch.Length > 0 };
                Identify(extensions, "ContactExtensions", "Локальные дополнительные данные");
                Lazy(extensions, () => ExtensionNode(["extensions"], "Дополнительные данные", false)); panel.Children.Add(extensions);
            }
            var service = new Expander { Header = "Сведения Google и идентификаторы — только просмотр" };
            Identify(service, "ContactReadonlyGoogle", "Сведения Google и идентификаторы, только просмотр");
            Lazy(service, () =>
            {
                var details = new StackPanel { Spacing = 6 };
                details.Children.Add(ReadonlyNode(captured["id"], "Идентификатор контакта"));
                details.Children.Add(ReadonlyNode(captured["schemaVersion"], "Версия формата"));
                details.Children.Add(ReadonlyNode(captured["google"], "Google")); return details;
            }); panel.Children.Add(service);
            return panel;
        }

        private Control Photo()
        {
            var panel = new StackPanel { Spacing = 6 };
            if (vm.PhotoBytes is not null)
            {
                var image = new Image { Width = 180, Height = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                image.Bind(Image.SourceProperty,new Binding(nameof(vm.PhotoPreview)){Source=vm});
                Identify(image, "ContactPhotoPreview", "Фотография контакта"); panel.Children.Add(image);
            }
            else panel.Children.Add(Text("Фотография не задана"));
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            if(vm.PhotoBytes is not null) actions.Children.Add(Button("Посмотреть", "PhotoViewButton", () => vm.ShowPhoto = true));
            if (vm.IsEditing)
            {
                var change = new Button { Content = vm.PhotoBytes is null ? "Добавить фотографию" : "Заменить", Margin = new Thickness(0, 0, 8, 6), IsEnabled = Editable };
                Identify(change, "PhotoChangeButton", "Выбрать фотографию контакта");
                change.Click += async (_, _) => { if (Editable && TopLevel.GetTopLevel(change) is { } owner) await ContactPhotoPicker.PickAsync(owner, vm); };
                actions.Children.Add(change);
                if(vm.PhotoBytes is not null)
                {
                    var remove = Button("Удалить фото", "PhotoRemoveButton", () => { if (Editable && vm.ChangePhoto(null)) vm.ValidationError("", "photo"); });
                    remove.IsEnabled = Editable; actions.Children.Add(remove);
                }
            }
            panel.Children.Add(actions); return panel;
        }

        private Control Labels()
        {
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(Text("Ярлыки"));
            if (vm.Labels.Count == 0) panel.Children.Add(Text("В папке нет ярлыков"));
            foreach (var label in vm.Labels.OrderBy(pair => pair.Value, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)))
            {
                var checkbox = new CheckBox { Content = label.Value, IsChecked = vm.HasLabel(label.Key), IsEnabled = Editable };
                Identify(checkbox, "Editor:labels:" + label.Key, "Ярлык " + label.Value);
                checkbox.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty && Editable) { vm.SetLabel(label.Key, checkbox.IsChecked == true); vm.ValidationError("", "labels"); } };
                panel.Children.Add(checkbox);
            }
            if (captured?["labels"] is JsonArray assigned)
                foreach (var missing in assigned.Where(value => !Guid.TryParse(value?.ToString(), out var id) || !vm.Labels.ContainsKey(id)))
                    panel.Children.Add(Text("Не найден ярлык: " + missing));
            return panel;
        }

        private bool MatchesField(ContactFieldDescriptor field) => string.IsNullOrWhiteSpace(vm.FieldSearch)
            || field.Title.Contains(vm.FieldSearch, StringComparison.CurrentCultureIgnoreCase)
            || field.Path.Contains(vm.FieldSearch, StringComparison.OrdinalIgnoreCase)
            || field.Children.Any(MatchesField);

        private Control Category(ContactFieldDescriptor field, JsonArray? existing)
        {
            var count = existing?.Count ?? 0;
            var expanded = count > 0 && Common.Contains(field.Name) || vm.FieldSearch.Length > 0 || vm.IsEditing && field.Name == "names";
            var category = new Expander { Header = field.Title + (count > 0 ? $" · {count}" : ""), IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch };
            Identify(category, "Category:" + field.Name, field.Title);
            Lazy(category, () =>
            {
                var panel = new StackPanel { Spacing = 8 };
                if (field.IsSingleton) panel.Children.Add(Text("Одна запись для контакта"));
                var rows = Enumerable.Range(0, count).Select(index => new RowRef(index, ["data", field.Name, index])).ToArray();
                if (count <= 5) foreach (var row in rows) panel.Children.Add(SchemaRow(field, row));
                else panel.Children.Add(VirtualRows(rows, row => SchemaRow(field, row), "Rows:" + field.Name));
                if (count == 0) panel.Children.Add(Text("Не задано"));
                if (vm.IsEditing)
                {
                    var add = Button("Добавить: " + field.Title.ToLowerInvariant(), "AddField:" + field.Name, () =>
                    {
                        Change(doc =>
                        {
                            var data = doc["data"] as JsonObject ?? new JsonObject(); if (doc["data"] is null) doc["data"] = data;
                            var array = data[field.Name] as JsonArray ?? new JsonArray(); if (data[field.Name] is null) data[field.Name] = array;
                            if (!field.IsSingleton || array.Count == 0) array.Add(new JsonObject());
                        }, true, field.Path);
                    });
                    add.IsEnabled = Editable && (!field.IsSingleton || count == 0); panel.Children.Add(add);
                }
                return panel;
            });
            return category;
        }

        private Control SchemaRow(ContactFieldDescriptor category, RowRef row)
        {
            var panel = new StackPanel { Spacing = 7, Margin = new Thickness(8, 4, 8, 12) };
            if (vm.IsEditing)
            {
                var remove = Button("Удалить запись", $"RemoveRow:{category.Name}:{row.Index}", () =>
                    Change(doc => { if (doc["data"]?[category.Name] is JsonArray array && row.Index < array.Count) array.RemoveAt(row.Index); }, true, category.Path));
                remove.IsEnabled = Editable; panel.Children.Add(remove);
            }
            foreach (var child in category.Children.OrderBy(child => FieldPriority(child.Name)))
            {
                var childPath = Append(row.Path, child.Name);
                if (vm.ShowAllFields || vm.FieldSearch.Length > 0 || Read(childPath) is not null || CommonLeaf(category.Name, child.Name))
                    panel.Children.Add(SchemaField(child, childPath, category.Name));
            }
            var obj = Read(row.Path) as JsonObject;
            if (obj is not null)
            {
                var known = category.Children.Select(child => child.Name).ToHashSet(StringComparer.Ordinal);
                AddUnknown(panel, obj, known);
            }
            return panel;
        }

        private Control SchemaField(ContactFieldDescriptor field, IReadOnlyList<object> path, string category)
        {
            if (field.Children.Count == 0) return Scalar(field, path, category);
            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(8, 2, 0, 4) };
            if (field.Name != "metadata") panel.Children.Add(Text(field.Title));
            if (field.IsDate && vm.IsEditing) panel.Children.Add(Text("Неизвестную часть даты оставьте пустой или укажите 0."));
            foreach (var child in field.Children) panel.Children.Add(SchemaField(child, Append(path, child.Name), category));
            if (Read(path) is JsonObject obj) AddUnknown(panel, obj, field.Children.Select(child => child.Name).ToHashSet(StringComparer.Ordinal));
            return panel;
        }

        private Control Scalar(ContactFieldDescriptor field, IReadOnlyList<object> path, string category)
        {
            var panel = new StackPanel { Spacing = 3 };
            var key = PathText(path); var id = "Editor:" + key;
            var current = Read(path);
            if (!vm.IsEditing)
            {
                panel.Children.Add(Text(field.Title));
                var value = new TextBox { Text = current is null ? "Не задано" : ContactTableQuery.FormatValue(current), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
                Identify(value, id, field.Title + ", только просмотр"); panel.Children.Add(value); return panel;
            }
            if (field.Type == "boolean")
            {
                var checkbox = new CheckBox { Content = field.Title, IsThreeState = true, IsChecked = current is JsonValue scalar && scalar.TryGetValue<bool>(out var value) ? value : null, IsEnabled = Editable };
                Identify(checkbox, id, field.Title + "; не задано, да или нет");
                checkbox.PropertyChanged += (_, e) =>
                {
                    if (e.Property != ToggleButton.IsCheckedProperty || !Editable) return;
                    Change(doc =>
                    {
                        if (field.Name == "sourcePrimary" && checkbox.IsChecked == true && doc["data"]?[category] is JsonArray entries)
                            foreach (var entry in entries.OfType<JsonObject>())
                                if (entry["metadata"] is JsonObject metadata && metadata["sourcePrimary"]?.GetValueKind() == JsonValueKind.True) metadata["sourcePrimary"] = false;
                        Set(doc, path, checkbox.IsChecked is null ? null : JsonValue.Create(checkbox.IsChecked.Value));
                    }, field.Name == "sourcePrimary", key);
                };
                panel.Children.Add(checkbox); return panel;
            }
            panel.Children.Add(Text(field.Title));
            if (field.Options.Count > 0)
            {
                var options = new[] { new ContactFieldOption("", "Не задано") }.Concat(field.Options).ToArray();
                var choice = new ComboBox { ItemsSource = options, DisplayMemberBinding = new Binding(nameof(ContactFieldOption.Title)), SelectedItem = options.FirstOrDefault(option => option.Value == current?.ToString()) ?? options[0], HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = Editable };
                Identify(choice, id, field.Title);
                choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is ContactFieldOption selected && Editable) Change(doc => Set(doc, path, selected.Value.Length == 0 ? null : JsonValue.Create(selected.Value)), false, key); };
                panel.Children.Add(choice); return panel;
            }
            var input = new TextBox { Text = current?.ToString() ?? "", IsEnabled = Editable, PlaceholderText = field.Type == "integer" ? "Целое число; пусто — не задано" : "Не задано", TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = category == "biographies" && field.Name == "value", MinHeight = category == "biographies" && field.Name == "value" ? 72 : 0 };
            Identify(input, id, field.Title);
            var lastText=input.Text??"";
            input.TextChanged += (_, _) =>
            {
                if (!Editable) return;
                var text = input.Text ?? "";
                if(text==lastText)return;lastText=text;
                JsonNode? next; string? error = null;
                if (field.Type == "integer")
                {
                    next = text.Length == 0 ? null : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(text);
                    if (text.Length > 0 && next?.GetValueKind() == JsonValueKind.String) error = field.Title + ": введите целое число.";
                }
                else if (field.Type == "number")
                {
                    next = text.Length == 0 ? null : decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(text);
                    if (text.Length > 0 && next?.GetValueKind() == JsonValueKind.String) error = field.Title + ": введите число.";
                }
                else next = JsonValue.Create(text);
                if (Change(doc => Set(doc, path, next), false, key)) vm.ValidationError(error ?? "", key);
            };
            panel.Children.Add(input);
            var clear = Button("Очистить поле", "Clear:" + key, () => Change(doc => Set(doc, path, null), true, key));
            clear.IsEnabled = Editable; panel.Children.Add(clear); return panel;
        }

        private void AddUnknown(StackPanel panel, JsonObject obj, HashSet<string> known)
        {
            var other = obj.Where(pair => !known.Contains(pair.Key)).ToArray();
            if (other.Length == 0) return;
            var readonlyDetails = new Expander { Header = "Дополнительные сведения — только просмотр" };
            Lazy(readonlyDetails, () =>
            {
                var details = new StackPanel { Spacing = 5 };
                foreach (var pair in other) details.Children.Add(ReadonlyNode(pair.Value, ReadonlyTitle(pair.Key)));
                return details;
            }); panel.Children.Add(readonlyDetails);
        }

        private Control ExtensionNode(IReadOnlyList<object> path, string title, bool allowType)
        {
            var node = Read(path); var panel = new StackPanel { Spacing = 5, Margin = new Thickness(4, 2, 0, 8) };
            panel.Children.Add(Text(title)); var type = JsonType(node);
            if (vm.IsEditing && allowType)
            {
                var chooser = new ComboBox { ItemsSource = JsonTypes, DisplayMemberBinding = new Binding(nameof(JsonTypeOption.Title)), SelectedItem = JsonTypes.Single(option => option.Type == type), IsEnabled = Editable, HorizontalAlignment = HorizontalAlignment.Stretch };
                Identify(chooser, "ExtensionType:" + PathText(path), title + ": тип значения");
                chooser.SelectionChanged += (_, _) => { if (Editable && chooser.SelectedItem is JsonTypeOption selected && selected.Type != type) Change(doc => Set(doc, path, DefaultJson(selected.Type), true), true, PathText(path)); };
                panel.Children.Add(chooser);
            }
            if (node is JsonObject obj || path.Count == 1)
            {
                var properties = (node as JsonObject)?.Select(pair => pair.Key).ToArray() ?? [];
                foreach (var key in properties)
                {
                    var childPath = Append(path, key); var child = new Expander { Header = key, IsExpanded = properties.Length <= 5 };
                    Identify(child, "Extension:" + PathText(childPath), key);
                    Lazy(child, () =>
                    {
                        var contents = new StackPanel { Spacing = 5 }; contents.Children.Add(ExtensionNode(childPath, key, true));
                        if (vm.IsEditing) contents.Children.Add(Button("Удалить поле", "RemoveExtension:" + PathText(childPath), () => Change(doc => Set(doc, childPath, null), true, PathText(childPath))));
                        return contents;
                    }); panel.Children.Add(child);
                }
                if (vm.IsEditing) panel.Children.Add(AddExtension(path, false));
            }
            else if (node is JsonArray array)
            {
                var rows = Enumerable.Range(0, array.Count).Select(index => new RowRef(index, Append(path, index))).ToArray();
                Control BuildRow(RowRef row)
                {
                    var contents = new StackPanel { Spacing = 4 }; contents.Children.Add(ExtensionNode(row.Path, "Значение " + (row.Index + 1), true));
                    if (vm.IsEditing) contents.Children.Add(Button("Удалить значение", "RemoveExtension:" + PathText(row.Path), () => Change(doc => { if (ContactCardControls.Read(doc, path) is JsonArray current && row.Index < current.Count) current.RemoveAt(row.Index); }, true, PathText(row.Path))));
                    return contents;
                }
                if (rows.Length <= 5) foreach (var row in rows) panel.Children.Add(BuildRow(row));
                else panel.Children.Add(VirtualRows(rows, BuildRow, "ExtensionRows:" + PathText(path)));
                if (vm.IsEditing) panel.Children.Add(AddExtension(path, true));
            }
            else if (!vm.IsEditing) panel.Children.Add(ReadonlyNode(node, "Значение"));
            else if (type == "boolean")
            {
                var check = new CheckBox { Content = "Да", IsChecked = node?.GetValue<bool>() == true, IsEnabled = Editable }; Identify(check, "Editor:" + PathText(path), title);
                check.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty && Editable) Change(doc => Set(doc, path, JsonValue.Create(check.IsChecked == true), true), false, PathText(path)); }; panel.Children.Add(check);
            }
            else if (type == "null") panel.Children.Add(Text("Пустое значение (null). Выберите тип для ввода."));
            else
            {
                var input = new TextBox { Text = node?.ToString() ?? "", IsEnabled = Editable, TextWrapping = TextWrapping.Wrap }; Identify(input, "Editor:" + PathText(path), title);
                var lastText=input.Text??"";
                input.TextChanged += (_, _) =>
                {
                    if (!Editable) return; var text = input.Text ?? ""; JsonNode? next;
                    if(text==lastText)return;lastText=text;
                    if (type == "number" && !decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                    { vm.Mutate(_ => { }); vm.ValidationError(title + ": введите число.", PathText(path)); return; }
                    next = type == "number" ? JsonValue.Create(decimal.Parse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture)) : JsonValue.Create(text);
                    Change(doc => Set(doc, path, next, true), false, PathText(path));
                }; panel.Children.Add(input);
            }
            return panel;
        }

        private Control AddExtension(IReadOnlyList<object> path, bool array)
        {
            var panel = new StackPanel { Spacing = 4 };
            var name = new TextBox { PlaceholderText = "Название дополнительного поля", IsVisible = !array, IsEnabled = Editable }; Identify(name, "ExtensionNewKey:" + PathText(path), "Название дополнительного поля");
            var type = new ComboBox { ItemsSource = JsonTypes, DisplayMemberBinding = new Binding(nameof(JsonTypeOption.Title)), SelectedIndex = 0, IsEnabled = Editable }; Identify(type, "ExtensionNewType:" + PathText(path), "Тип нового значения");
            panel.Children.Add(name); panel.Children.Add(type);
            panel.Children.Add(Button(array ? "Добавить значение" : "Добавить поле", "AddExtension:" + PathText(path), () =>
            {
                if (!Editable || type.SelectedItem is not JsonTypeOption selected) return;
                var key = name.Text ?? "";
                if (!array && string.IsNullOrWhiteSpace(key)) { vm.ValidationError("Укажите название дополнительного поля.", "add:" + PathText(path)); return; }
                var existing = Read(path);
                if (!array && existing is JsonObject obj && obj.ContainsKey(key)) { vm.ValidationError("Поле с таким названием уже существует.", "add:" + PathText(path)); return; }
                Change(doc =>
                {
                    var current = ContactCardControls.Read(doc, path);
                    if (array && current is JsonArray values) values.Add(DefaultJson(selected.Type));
                    else
                    {
                        if (current is not JsonObject) { Set(doc, path, new JsonObject(), true); current = ContactCardControls.Read(doc, path); }
                        current!.AsObject()[key] = DefaultJson(selected.Type);
                    }
                }, true, "add:" + PathText(path));
            })); return panel;
        }

        private bool Change(Action<JsonObject> mutation, bool rebuild, string key)
        {
            if (!Editable) return false;
            vm.ValidationError("", key);
            return vm.Mutate(mutation, rebuild);
        }
        private JsonNode? Read(IReadOnlyList<object> path) => captured is null ? null : ContactCardControls.Read(captured, path);
    }

    private static ListBox VirtualRows(RowRef[] rows, Func<RowRef, Control> factory, string id)
    {
        var list = new ListBox { ItemsSource = rows, Height = 350, MaxHeight = 350, MinHeight = 120, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<RowRef>((row, _) => factory(row!)) };
        Identify(list, id, "Список значений; используйте прокрутку для остальных записей"); return list;
    }
    private static void Lazy(Expander expander, Func<Control> factory)
    {
        if (expander.IsExpanded) expander.Content = factory();
        expander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty && expander.IsExpanded && expander.Content is null) expander.Content = factory(); };
    }
    private static Control ReadonlyNode(JsonNode? node, string title)
    {
        if (node is JsonObject obj)
        {
            var expander = new Expander { Header = title };
            Lazy(expander, () => { var panel = new StackPanel { Spacing = 3 }; foreach (var pair in obj) panel.Children.Add(ReadonlyNode(pair.Value, ReadonlyTitle(pair.Key))); return panel; }); return expander;
        }
        if (node is JsonArray array)
        {
            var expander = new Expander { Header = title + $" · {array.Count}" };
            Lazy(expander, () => { var rows = Enumerable.Range(0, array.Count).Select(index => new RowRef(index, [])).ToArray(); return VirtualRows(rows, row => ReadonlyNode(array[row.Index], "Запись " + (row.Index + 1)), "ReadonlyRows:" + title); }); return expander;
        }
        return new TextBox { Text = title + ": " + (node is null ? "Нет значения" : ContactTableQuery.FormatValue(node)), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    }
    private static string ReadonlyTitle(string name) => name switch { "source" => "Источник", "primary" => "Основное среди источников", "verified" => "Подтверждено", "sources" => "Источники", "etag" => "Версия Google (etag)", "resourceName" => "Идентификатор Google", "id" => "Идентификатор", _ => ContactFieldSchema.FieldTitle(name) };
    private static int FieldPriority(string name) => name switch { "givenName" => 0, "familyName" => 1, "middleName" => 2, "value" or "name" or "person" or "url" => 3, "type" => 4, "metadata" => 100, _ => 10 };
    private static bool CommonLeaf(string category, string name) => name == "metadata" || (category switch
    {
        "names" => name is "givenName" or "familyName" or "middleName",
        "addresses" => name is "streetAddress" or "city" or "region" or "postalCode" or "country" or "type",
        "organizations" => name is "name" or "title" or "department" or "type",
        "birthdays" or "events" => name is "date" or "type" or "text",
        _ => name is "value" or "type" or "url" or "key" or "username" or "protocol" or "person"
    });
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string text, string id, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 5) }; Identify(button, id, text); button.Click += (_, _) => action(); return button;
    }
    private static void Identify(Control control, string id, string title) { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, title); }
    private static IReadOnlyList<object> Append(IReadOnlyList<object> path, object segment) => path.Append(segment).ToArray();
    private static string PathText(IReadOnlyList<object> path) => string.Join(".", path.Select(segment => segment.ToString()));
    private static JsonNode? Read(JsonObject document, IReadOnlyList<object> path)
    {
        JsonNode? current = document;
        foreach (var part in path)
        {
            if (part is int index) current = current is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;
            else current = current is JsonObject obj ? obj[part.ToString()!] : null;
            if (current is null) break;
        }
        return current;
    }
    private static void Set(JsonObject document, IReadOnlyList<object> path, JsonNode? value, bool keepNull = false)
    {
        JsonNode current = document;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var part = path[index];
            if (part is int row) { if (current is not JsonArray array || row < 0 || row >= array.Count || array[row] is null) return; current = array[row]!; }
            else
            {
                if (current is not JsonObject obj) return; var key = part.ToString()!;
                if (obj[key] is null) obj[key] = path[index + 1] is int ? new JsonArray() : new JsonObject();
                current = obj[key]!;
            }
        }
        var last = path[^1];
        if (last is int position) { if (current is JsonArray array && position >= 0 && position < array.Count) array[position] = value?.DeepClone(); }
        else if (current is JsonObject obj)
        {
            var key = last.ToString()!; if (value is null && !keepNull) obj.Remove(key); else obj[key] = value?.DeepClone();
        }
    }
    private static string JsonType(JsonNode? node) => node switch { JsonObject => "object", JsonArray => "array", null => "null", _ => node.GetValueKind() switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" } };
    private static JsonNode? DefaultJson(string type) => type switch { "object" => new JsonObject(), "array" => new JsonArray(), "number" => JsonValue.Create(0), "boolean" => JsonValue.Create(false), "null" => null, _ => JsonValue.Create("") };
}
