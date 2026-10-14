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
public static partial class ContactCardControls
{
    private static readonly HashSet<string> Common = ["names", "phoneNumbers", "emailAddresses", "addresses", "organizations", "birthdays", "biographies", "urls"];
    private static readonly (string Title, string[] Fields)[] Sections =
    [
        ("Имя и обращения",["names","nicknames","genders"]),
        ("Связь",["phoneNumbers","emailAddresses","imClients","sipAddresses","urls","calendarUrls"]),
        ("Организации и занятия",["organizations","occupations"]),
        ("Адреса",["addresses","locations"]),
        ("Даты и события",["birthdays","events"]),
        ("Заметки и интересы",["biographies","interests","miscKeywords"]),
        ("Дополнительные сведения",["relations","locales","externalIds","clientData","userDefined"])
    ];
    public static Control Build(ContactsViewModel vm, ContactCardActions? actions = null, bool includeIdentity = true, string? sectionOnly = null) => new CardBuilder(vm, actions, includeIdentity, sectionOnly).Build();
    public static Control BuildIdentity(ContactsViewModel vm, ContactCardActions actions) => new CardBuilder(vm, actions, false).Identity();
    private sealed record RowRef(int Index, IReadOnlyList<object> Path);
    private sealed record JsonTypeOption(string Type, string Title);
    private static readonly JsonTypeOption[] JsonTypes = [new("string", "Текст"), new("number", "Число"), new("boolean", "Да / нет"), new("object", "Объект"), new("array", "Список"), new("null", "Пустое значение (null)")];

    private sealed partial class CardBuilder(ContactsViewModel vm, ContactCardActions? actions, bool includeIdentity, string? sectionOnly = null)
    {
        private readonly JsonObject? captured = vm.Document;
        private bool retired;
        private bool Editable => !retired && vm.IsEditing && vm.CanEdit && ReferenceEquals(captured, vm.Document);

        public Control Build() => BuildFriendly();

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
                OwnSelection(chooser,PathText(path),token=>
                {
                    if(chooser.SelectedItem is not JsonTypeOption selected||selected.Type==type)return token is null||vm.EndEditorPopup(token);
                    var accepted=token is null?Change(doc=>Set(doc,path,DefaultJson(selected.Type),true),false,PathText(path)):vm.EndEditorPopup(token,doc=>Set(doc,path,DefaultJson(selected.Type),true),inputChange:new(ClearPrefix:PathText(path)));
                    if(accepted){if(token is null)vm.ClearInput(PathText(path));vm.RefreshStructure();}return accepted;
                });
                panel.Children.Add(chooser);
            }
            if (node is JsonObject obj || path.Count == 1)
            {
                var properties = (node as JsonObject)?.Select(pair => pair.Key).ToArray() ?? [];
                foreach (var key in properties)
                {
                    var childPath=Append(path,key);var child=new Expander{Header=key,IsExpanded=properties.Length<=5||actions?.RevealedPath?.StartsWith(PathText(childPath),StringComparison.Ordinal)==true};
                    Identify(child, "Extension:" + PathText(childPath), key);
                    Lazy(child, () =>
                    {
                        var contents = new StackPanel { Spacing = 5 }; contents.Children.Add(ExtensionNode(childPath, key, true));
                        if (vm.IsEditing) contents.Children.Add(Button("Удалить поле", "RemoveExtension:" + PathText(childPath), () => { if (Change(doc => Set(doc, childPath, null), false, PathText(childPath))) { vm.ClearInput(PathText(childPath)); vm.RefreshStructure(); } }));
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
                    if (vm.IsEditing) contents.Children.Add(Button("Удалить значение", "RemoveExtension:" + PathText(row.Path), () => { if (Change(doc => { if (ContactCardControls.Read(doc, path) is JsonArray current && row.Index < current.Count) current.RemoveAt(row.Index); }, false, PathText(row.Path))) { vm.RemoveRowInputs(PathText(path), row.Index); vm.RefreshStructure(); } }));
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
                var input = new TextBox { Text = vm.GetInput(PathText(path), node?.ToString() ?? ""), IsEnabled = Editable, TextWrapping = TextWrapping.Wrap }; Identify(input, "Editor:" + PathText(path), title);
                var validation=Text(vm.ValidationErrors.GetValueOrDefault(PathText(path),""));validation.IsVisible=validation.Text?.Length>0;
                var lastText = input.Text ?? "";
                input.PropertyChanged += (_, e) =>
                {
                    if (e.Property != TextBox.TextProperty) return;
                    if (!Editable) return; var text = input.Text ?? ""; JsonNode? next;
                    if (text == lastText) return; lastText = text;
                    if (type == "number" && !decimal.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                    {var message=title+": введите число.";if(vm.SetInvalidInput(PathText(path),text,message)){validation.Text=message;validation.IsVisible=true;}return;}
                    next = type == "number" ? JsonValue.Create(decimal.Parse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture)) : JsonValue.Create(text);
                    if(Change(doc=>Set(doc,path,next,true),false,PathText(path))){vm.ClearInput(PathText(path));validation.IsVisible=false;}
                };panel.Children.Add(input);panel.Children.Add(validation);
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
                if (Change(doc =>
                {
                    var current = ContactCardControls.Read(doc, path);
                    if (array && current is JsonArray values) values.Add(DefaultJson(selected.Type));
                    else
                    {
                        if (current is not JsonObject) { Set(doc, path, new JsonObject(), true); current = ContactCardControls.Read(doc, path); }
                        current!.AsObject()[key] = DefaultJson(selected.Type);
                    }
                }, false, "add:" + PathText(path)))
                {
                    vm.ClearInput("add:" + PathText(path), includeChildren: false);
                    vm.RefreshStructure();
                }
            })); return panel;
        }

        private bool Change(Action<JsonObject> mutation, bool rebuild, string key)
        {
            if (!Editable) return false;
            return vm.Mutate(mutation, rebuild);
        }
        private JsonNode? Read(IReadOnlyList<object> path) => captured is null ? null : ContactCardControls.Read(captured, path);
    }

    private static ListBox VirtualRows(RowRef[] rows, Func<RowRef, Control> factory, string id)
    {
        var list = new ListBox
        {
            ItemsSource = rows,
            Height = 350,
            MaxHeight = 350,
            MinHeight = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<RowRef>((row, _) => row is null ? new TextBlock() : factory(row))
        };
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
        return new SelectableTextBlock { Text = title + ": " + (node is null ? "Нет значения" : ContactTableQuery.FormatValue(node)), FontSize = 14, TextWrapping = TextWrapping.Wrap };
    }
    private static string ReadonlyTitle(string name) => name switch { "source" => "Источник", "primary" => "Основное среди источников", "verified" => "Подтверждено", "sources" => "Источники", "etag" => "Версия Google (etag)", "resourceName" => "Идентификатор Google", "id" => "Идентификатор", _ => ContactFieldSchema.FieldTitle(name) };
    private static int FieldPriority(string name) => name switch { "givenName" => 0, "familyName" => 1, "middleName" => 2, "value" or "name" or "person" or "url" => 3, "type" => 4, "metadata" => 100, _ => 10 };
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string text, string id, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 5) }; Identify(button, id, text); button.Click += (_, _) => action(); return button;
    }
    private static void Identify(Control control, string id, string title) { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, title); }
    private static IReadOnlyList<object> Append(IReadOnlyList<object> path, object segment) => path.Append(segment).ToArray();
    private static string PathText(IReadOnlyList<object> path)=>string.Join(".",path.Select(segment=>segment.ToString()!.Replace("%","%25",StringComparison.Ordinal).Replace(".","%2E",StringComparison.Ordinal)));
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
            if (part is int row) { if (current is not JsonArray array || row < 0 || row > array.Count) return; if (row == array.Count) array.Add(new JsonObject()); if (array[row] is null) return; current = array[row]!; }
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
