using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ContactMirror.Core;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_many_unknown_labels_and_long_label_remain_readable_with_cancel_preserving_bytes()
    {
        var ids = Enumerable.Range(0,8).Select(_ => Guid.NewGuid()).ToArray();
        var fixture = await FriendlyRealCard(doc => doc["labels"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id.ToString())!).ToArray()), edit: false);
        HeadlessRuntime.Dispatch(() => { UxModel().Contacts.ShowAllFields = false; Session.Inner.MainWindow.Width = 820; Session.Inner.MainWindow.Height = 640; }); await Task.Delay(60); HeadlessRuntime.Dispatch(() => FriendlyClick("ContactOpenButton")); await Task.Delay(60); CaptureCheckpoint("friendly-many-unknown-labels-820");
        await FriendlyMenuAction("ContactMoreLabelsButton", "ContactLabelsCancel");
        var title = "Длинное название группы " + new string('Я',300);
        foreach (var id in ids) await File.WriteAllBytesAsync(Path.Combine(fixture.Root,"groups",id+".group.json"),JsonSemantics.Serialize(DocumentCodec.ToJson(new GroupDocument { Id = id, Name = id == ids[0] ? title : "Группа " + id })));
        await EditorUi(() => UxModel().Contacts.LoadAsync(fixture.Root)); await EditorUi(() => UxModel().Contacts.ReloadSelectedAsync()); HeadlessRuntime.Dispatch(() => UxModel().Contacts.BeginEdit()); await Task.Delay(80);
        var file = Path.Combine(fixture.Root,"contacts",fixture.Id+".contact.json"); var before = await File.ReadAllBytesAsync(file);
        HeadlessRuntime.Dispatch(() => FriendlyClick("ContactLabelsButton")); await Task.Delay(100);
        HeadlessRuntime.Dispatch(() => ((TextBox)EditorControl("ContactLabelSearch")).Text = "Длинное название группы"); await Task.Delay(100); CaptureCheckpoint("friendly-long-label-popup-820");
        bool visible = false; HeadlessRuntime.Dispatch(() => visible = EditorControl("ChooseLabel:"+ids[0]).IsEffectivelyVisible && EditorControl("ContactLabelsDone").IsEffectivelyVisible && ((TextBlock)((CheckBox)EditorControl("ChooseLabel:"+ids[0])).Content!).Text == title); await Assert.That(visible).IsTrue();
        HeadlessRuntime.Dispatch(() => FriendlyClick("ContactLabelsCancel")); await Assert.That(await File.ReadAllBytesAsync(file)).IsEquivalentTo(before); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_corrected_extension_name_clears_only_its_add_error_and_saves()
    {
        var fixture = await FriendlyRealCard(doc => { doc["extensions"]!["number"] = 10; doc["extensions"]!["group"] = new JsonObject(); });
        HeadlessRuntime.Dispatch(() => { ((TextBox)EditorControl("Editor:extensions.number")).Text = "ошибка"; FriendlyClick("AddExtension:extensions.group"); ((TextBox)EditorControl("ExtensionNewKey:extensions")).Text = "number"; FriendlyClick("AddExtension:extensions"); });
        HeadlessRuntime.Dispatch(() => { ((TextBox)EditorControl("ExtensionNewKey:extensions")).Text = "Новое поле"; FriendlyClick("AddExtension:extensions"); }); await Task.Delay(80);
        bool retained = false; HeadlessRuntime.Dispatch(() => retained = !UxModel().Contacts.ValidationErrors.ContainsKey("add:extensions") && UxModel().Contacts.ValidationErrors.ContainsKey("add:extensions.group") && UxModel().Contacts.ValidationErrors.ContainsKey("extensions.number")); await Assert.That(retained).IsTrue();
        await Assert.That(await EditorUi(() => UxModel().Contacts.SaveAsync())).IsFalse(); await Task.Delay(80);
        HeadlessRuntime.Dispatch(() => { ((TextBox)EditorControl("Editor:extensions.number")).Text = "12"; ((TextBox)EditorControl("ExtensionNewKey:extensions.group")).Text = "Вложенное поле"; FriendlyClick("AddExtension:extensions.group"); }); await Task.Delay(80);
        await Assert.That(await EditorUi(() => UxModel().Contacts.SaveAsync())).IsTrue();
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root,"contacts",fixture.Id+".contact.json")))!; await Assert.That(saved["extensions"]!["Новое поле"]!.ToString()).IsEqualTo("");
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_error_focus_realizes_nested_extension_array_index_50_and_preserves_input()
    {
        await FriendlyRealCard(doc => doc["extensions"]!["nested"] = new JsonObject { ["a.b"] = new JsonArray(Enumerable.Range(0,60).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) });
        HeadlessRuntime.Dispatch(() => UxModel().Contacts.SetInvalidInput("extensions.nested.a%2Eb.50", "не число", "Введите число"));
        await FriendlyMenuAction("ContactErrorsButton", "ErrorLink:extensions.nested.a%2Eb.50"); await Task.Delay(180);
        bool focused = false; HeadlessRuntime.Dispatch(() => { var input = (TextBox)EditorControl("Editor:extensions.nested.a%2Eb.50"); focused = input.IsFocused && input.Text == "не число"; }); await Assert.That(focused).IsTrue(); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_date_row_removal_focuses_remaining_date_then_add_button()
    {
        await FriendlyRealCard(doc => doc["data"]!["events"] = new JsonArray(new JsonObject { ["date"] = new JsonObject { ["month"] = 5, ["day"] = 21 } },new JsonObject { ["date"] = new JsonObject { ["year"] = 2020 } }));
        await FriendlyMenuAction("RowMenu:events:0", "RemoveRow:events:0"); await Task.Delay(120); bool focused = false;
        HeadlessRuntime.Dispatch(() => { focused = EditorControl("Editor:data.events.0.date.year").IsFocused; if (!focused) Console.WriteLine("Date focus: " + Avalonia.Automation.AutomationProperties.GetAutomationId(Session.Inner.MainWindow.FocusManager!.GetFocusedElement() as Control ?? Session.Inner.MainWindow) + ", visible=" + EditorControl("Editor:data.events.0.date.year").IsEffectivelyVisible + ", modal=" + UxModel().Contacts.HasModal); }); await Assert.That(focused).IsTrue();
        await FriendlyMenuAction("RowMenu:events:0", "RemoveRow:events:0"); await Task.Delay(120);
        HeadlessRuntime.Dispatch(() => focused = EditorControl("AddField:events").IsFocused); await Assert.That(focused).IsTrue(); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Friendly_ui_dotted_extension_key_does_not_share_invalid_buffer_with_nested_field()
    {
        await FriendlyRealCard(doc=>{doc["extensions"]!["a.b"]=10;doc["extensions"]!["a"]=new JsonObject{["b"]=20};});
        HeadlessRuntime.Dispatch(()=>((TextBox)EditorControl("Editor:extensions.a%2Eb")).Text="не число");await FriendlyMenuAction("ContactFindButton","FindResult:data.names.0.familyName");
        string direct="",nested="";HeadlessRuntime.Dispatch(()=>{direct=((TextBox)EditorControl("Editor:extensions.a%2Eb")).Text!;nested=((TextBox)EditorControl("Editor:extensions.a.b")).Text!;});await Assert.That(direct).IsEqualTo("не число");await Assert.That(nested).IsEqualTo("20");await Assert.That(await EditorUi(()=>UxModel().Contacts.SaveAsync())).IsFalse();HeadlessRuntime.Dispatch(()=>UxModel().Contacts.CancelEdit());
    }
    private void FriendlyClick(string id) => ((Button)EditorControl(id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private async Task FriendlyMenuAction(string menu, string action) { HeadlessRuntime.Dispatch(() => FriendlyClick(menu)); await Task.Delay(50); if (action.StartsWith("FindResult:", StringComparison.Ordinal)) { HeadlessRuntime.Dispatch(() => ((TextBox)EditorControl("ContactFieldSearch")).Text = action["FindResult:".Length..]); await Task.Delay(50); } HeadlessRuntime.Dispatch(() => FriendlyClick(action)); await Task.Delay(50); }
    private async Task<(string Root, Guid Id)> FriendlyRealCard(Action<JsonObject> arrange, bool edit = true)
    {
        var fixture = await PrepareRealEditor(); var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject(); arrange(doc); DocumentCodec.Contact(doc); await File.WriteAllBytesAsync(file, JsonSemantics.Serialize(doc));
        await Task.Delay(350); await EditorUi(() => UxModel().Contacts.LoadAsync(fixture.Root)); await EditorUi(() => UxModel().Contacts.ReloadSelectedAsync());
        var ready = false; for (var i = 0; i < 100; i++) { HeadlessRuntime.Dispatch(() => ready = (!edit || UxModel().Contacts.CanEdit) && JsonNode.DeepEquals(doc, UxModel().Contacts.Document)); if (ready) break; await Task.Delay(30); }
        if (!ready) { var error = ""; HeadlessRuntime.Dispatch(() => error = UxModel().Contacts.Error); throw new InvalidOperationException("Synthetic fixture did not finish loading: " + error); }
        HeadlessRuntime.Dispatch(() => { UxModel().Contacts.ShowAllFields = true; if (edit) UxModel().Contacts.BeginEdit(); }); await Task.Delay(80); return fixture;
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_clear_removes_property_without_recreating_empty_string_or_touching_siblings()
    {
        await FriendlyRealCard(doc => doc["data"]!["names"]![0]!["middleName"] = "Сохранённое отчество"); string middle = ""; bool missing = false;
        HeadlessRuntime.Dispatch(() => { FriendlyClick("Clear:data.names.0.familyName"); var name = UxModel().Contacts.Document!["data"]!["names"]![0]!.AsObject(); missing = !name.ContainsKey("familyName"); middle = name["middleName"]!.ToString(); });
        await Assert.That(missing).IsTrue(); await Assert.That(middle).IsEqualTo("Сохранённое отчество"); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_invalid_percent_survives_add_and_index_shift_then_remove_clears_only_its_error()
    {
        var fixture = await FriendlyRealCard(doc => doc["data"]!["organizations"] = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject { ["name"] = "Компания " + i, ["fullTimeEquivalentMillipercent"] = 85000 }).ToArray()));
        var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json"); var before = await File.ReadAllBytesAsync(file); string text = ""; bool error = false;
        HeadlessRuntime.Dispatch(() => ((TextBox)EditorControl("Editor:data.organizations.2.fullTimeEquivalentMillipercent")).Text = "ошибка"); await Task.Delay(50); HeadlessRuntime.Dispatch(() => FriendlyClick("AddField:organizations")); await Task.Delay(50);
        HeadlessRuntime.Dispatch(() => { text = ((TextBox)EditorControl("Editor:data.organizations.2.fullTimeEquivalentMillipercent")).Text!; error = UxModel().Contacts.ValidationErrors.ContainsKey("data.organizations.2.fullTimeEquivalentMillipercent"); });
        await Assert.That(text).IsEqualTo("ошибка"); await Assert.That(error).IsTrue(); await Assert.That(await EditorUi(() => UxModel().Contacts.SaveAsync())).IsFalse(); await Assert.That(await File.ReadAllBytesAsync(file)).IsEquivalentTo(before);
        await FriendlyMenuAction("RowMenu:organizations:0", "RemoveRow:organizations:0");
        HeadlessRuntime.Dispatch(() => { text = ((TextBox)EditorControl("Editor:data.organizations.1.fullTimeEquivalentMillipercent")).Text!; error = UxModel().Contacts.ValidationErrors.ContainsKey("data.organizations.1.fullTimeEquivalentMillipercent") && !UxModel().Contacts.ValidationErrors.ContainsKey("data.organizations.2.fullTimeEquivalentMillipercent"); });
        await Assert.That(text).IsEqualTo("ошибка"); await Assert.That(error).IsTrue();
        await FriendlyMenuAction("RowMenu:organizations:1", "RemoveRow:organizations:1"); HeadlessRuntime.Dispatch(() => error = UxModel().Contacts.ValidationErrors.Count > 0); await Assert.That(error).IsFalse(); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_numeric_extension_keeps_invalid_input_through_find_and_reindexes_after_removal()
    {
        await FriendlyRealCard(doc => doc["extensions"]!["numbers"] = new JsonArray(10, 20, 30)); string text = ""; bool error = false;
        HeadlessRuntime.Dispatch(() => ((TextBox)EditorControl("Editor:extensions.numbers.2")).Text = "не число"); await FriendlyMenuAction("ContactFindButton", "FindResult:data.names.0.familyName");
        HeadlessRuntime.Dispatch(() => { text = ((TextBox)EditorControl("Editor:extensions.numbers.2")).Text!; FriendlyClick("RemoveExtension:extensions.numbers.0"); }); await Task.Delay(60);
        await Assert.That(text).IsEqualTo("не число"); HeadlessRuntime.Dispatch(() => { text = ((TextBox)EditorControl("Editor:extensions.numbers.1")).Text!; error = UxModel().Contacts.ValidationErrors.ContainsKey("extensions.numbers.1"); }); await Assert.That(text).IsEqualTo("не число"); await Assert.That(error).IsTrue(); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_primary_changes_selected_and_previous_true_only_and_preserves_absent_metadata()
    {
        await FriendlyRealCard(doc => doc["data"]!["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "111", ["type"] = "home", ["metadata"] = new JsonObject { ["sourcePrimary"] = true } }, new JsonObject { ["value"] = "222" }, new JsonObject { ["value"] = "333" })); JsonObject expected = null!; bool equal = false;
        HeadlessRuntime.Dispatch(() => { expected = (JsonObject)UxModel().Contacts.Document!.DeepClone(); expected["data"]!["phoneNumbers"]![0]!["metadata"]!["sourcePrimary"] = false; expected["data"]!["phoneNumbers"]![2]!["metadata"] = new JsonObject { ["sourcePrimary"] = true }; }); await FriendlyMenuAction("RowMenu:phoneNumbers:2", "SetPrimary:data.phoneNumbers.2"); HeadlessRuntime.Dispatch(() => equal = JsonNode.DeepEquals(expected, UxModel().Contacts.Document)); await Assert.That(equal).IsTrue(); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_literal_custom_token_and_cancelled_date_popup_are_nonmutating_and_block_save()
    {
        await FriendlyRealCard(doc => doc["data"]!["phoneNumbers"]![0]!["type"] = "$custom"); string before = ""; bool blocked = false, unchanged = false;
        HeadlessRuntime.Dispatch(()=>{Session.Inner.MainWindow.Width=820;Session.Inner.MainWindow.Height=640;});await Task.Delay(50);HeadlessRuntime.Dispatch(()=>FriendlyClick("ContactOpenButton"));await Task.Delay(50);
        HeadlessRuntime.Dispatch(() => { before = UxModel().Contacts.Document!.ToJsonString(); var type = (ComboBox)EditorControl("Editor:data.phoneNumbers.0.type"); type.IsDropDownOpen = true; type.IsDropDownOpen = false; unchanged = before == UxModel().Contacts.Document!.ToJsonString() && !UxModel().Contacts.HasModal && !UxModel().Contacts.IsDirty; var month = (ComboBox)EditorControl("Editor:data.birthdays.0.date.month"); month.IsDropDownOpen = true; blocked = UxModel().Contacts.HasModal; month.SelectedItem = ((IEnumerable<ContactFieldOption>)month.ItemsSource!).Single(o => o.Value == "3"); month.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); month.IsDropDownOpen = false; });
        await Assert.That(blocked).IsTrue(); await Assert.That(unchanged).IsTrue(); HeadlessRuntime.Dispatch(() => { unchanged = before == UxModel().Contacts.Document!.ToJsonString() && !UxModel().Contacts.HasModal && !UxModel().Contacts.IsDirty; if (!unchanged) Console.WriteLine("Popup cancellation: dirty=" + UxModel().Contacts.IsDirty + ", modal=" + UxModel().Contacts.HasModal + ", before=" + before + ", after=" + UxModel().Contacts.Document!.ToJsonString()); }); await Assert.That(unchanged).IsTrue(); HeadlessRuntime.Dispatch(() => UxModel().Contacts.CancelEdit());
    }
}
