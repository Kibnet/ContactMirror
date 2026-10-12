using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using ContactMirror.Application.Contacts;
using ContactMirror.Core;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test,NotInParallel("DesktopUi")]
    public async Task Friendly_ui_long_identity_at_820_keeps_scroll_viewport_and_complete_name_accessible()
    {
        var fullName=new string('Ж',1800);
        await FriendlyRealCard(doc=>{doc["data"]!["names"]![0]!["givenName"]=fullName;doc["data"]!["organizations"]=new JsonArray(new JsonObject{["name"]=new string('К',1600)},new JsonObject{["name"]="Вторая организация"});});
        HeadlessRuntime.Dispatch(()=>{UxModel().Contacts.ShowAllFields=false;Session.Inner.MainWindow.Width=820;Session.Inner.MainWindow.Height=640;});await Task.Delay(80);HeadlessRuntime.Dispatch(()=>FriendlyClick("ContactOpenButton"));await Task.Delay(80);
        var usable=false;HeadlessRuntime.Dispatch(()=>{var save=EditorControl("ContactSaveButton");var name=(TextBlock)EditorControl("ContactIdentityName");usable=EditorControl("ContactFieldScroll").Bounds.Height>=80&&save.IsEffectivelyVisible&&EditorControl("ContactFindButton").IsEffectivelyVisible&&name.Text!.Contains(fullName,StringComparison.Ordinal)&&ToolTip.GetTip(name)?.ToString()?.Contains(fullName,StringComparison.Ordinal)==true;});await Assert.That(usable).IsTrue();CaptureCheckpoint("friendly-long-identity-820");HeadlessRuntime.Dispatch(()=>UxModel().Contacts.CancelEdit());
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Friendly_ui_find_distinguishes_records_and_cancelled_stale_date_mode_keeps_untouched_zero()
    {
        await FriendlyRealCard(doc=>{doc["data"]!["phoneNumbers"]=new JsonArray(new JsonObject{["value"]="111",["type"]="home"},new JsonObject{["value"]="222"});doc["data"]!["birthdays"]![0]!["date"]!["year"]=0;});
        HeadlessRuntime.Dispatch(()=>FriendlyClick("ContactFindButton"));await Task.Delay(50);HeadlessRuntime.Dispatch(()=>((TextBox)EditorControl("ContactFieldSearch")).Text="data.phoneNumbers");await Task.Delay(50);
        string first="",second="";HeadlessRuntime.Dispatch(()=>{first=((Button)EditorControl("FindResult:data.phoneNumbers.0.type")).Content!.ToString()!;second=((Button)EditorControl("FindResult:data.phoneNumbers.1.type")).Content!.ToString()!;FriendlyClick("ContactFindClose");});
        await Assert.That(first.Contains("Заполнено",StringComparison.Ordinal)&&first.Contains("111",StringComparison.Ordinal)).IsTrue();await Assert.That(second.Contains("Не заполнено",StringComparison.Ordinal)&&second.Contains("222",StringComparison.Ordinal)).IsTrue();
        HeadlessRuntime.Dispatch(()=>{var mode=(ComboBox)EditorControl("DateMode:data.birthdays.0.date");mode.IsDropDownOpen=true;mode.SelectedItem=((IEnumerable<ContactFieldOption>)mode.ItemsSource!).Single(o=>o.Value=="Full");UxModel().Contacts.Guard.Reset();mode.IsDropDownOpen=false;((TextBox)EditorControl("Editor:data.birthdays.0.date.day")).Text="28";});
        var preserved=false;HeadlessRuntime.Dispatch(()=>preserved=UxModel().Contacts.Document!["data"]!["birthdays"]![0]!["date"]!["year"]?.GetValue<int>()==0&&UxModel().Contacts.Document!["data"]!["birthdays"]![0]!["date"]!["day"]?.GetValue<int>()==28);await Assert.That(preserved).IsTrue();HeadlessRuntime.Dispatch(()=>UxModel().Contacts.CancelEdit());
    }
    private void FriendlyChoice(string id, string value) { var combo = (ComboBox)EditorControl(id); combo.SelectedItem = ((IEnumerable<ContactFieldOption>)combo.ItemsSource!).Single(o => o.Value == value); }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_find_missing_address_reveals_named_control_without_writing_then_explicit_entry_saves()
    {
        var fixture = await FriendlyRealCard(_ => { }); string before = ""; HeadlessRuntime.Dispatch(() => before = UxModel().Contacts.Document!.ToJsonString());
        await FriendlyMenuAction("ContactFindButton", "FindResult:data.addresses.0.poBox"); bool clean = false; HeadlessRuntime.Dispatch(() => { clean = !UxModel().Contacts.IsDirty && before == UxModel().Contacts.Document!.ToJsonString(); ((TextBox)EditorControl("Editor:data.addresses.0.poBox")).Text = "А/я 42"; }); await Assert.That(clean).IsTrue(); await Assert.That(await EditorUi(() => UxModel().Contacts.SaveAsync())).IsTrue();
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json")))!; await Assert.That(saved["data"]!["addresses"]![0]!["poBox"]!.ToString()).IsEqualTo("А/я 42");
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_all_four_date_modes_save_exact_known_parts_without_anchor_year_or_zero_components()
    {
        var fixture = await FriendlyRealCard(_ => { }); var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        foreach (var mode in new[] { "WithoutYear", "YearOnly", "YearAndMonth", "Full" })
        {
            HeadlessRuntime.Dispatch(() => { if (!UxModel().Contacts.IsEditing) UxModel().Contacts.BeginEdit(); }); await Task.Delay(50);
            HeadlessRuntime.Dispatch(() => { FriendlyChoice("DateMode:data.birthdays.0.date", mode); if (mode != "WithoutYear") ((TextBox)EditorControl("Editor:data.birthdays.0.date.year")).Text = "2024"; if (mode != "YearOnly") FriendlyChoice("Editor:data.birthdays.0.date.month", "2"); if (mode is "WithoutYear" or "Full") ((TextBox)EditorControl("Editor:data.birthdays.0.date.day")).Text = "29"; });
            await Assert.That(await EditorUi(() => UxModel().Contacts.SaveAsync())).IsTrue(); var saved = JsonNode.Parse(await File.ReadAllTextAsync(file))!["data"]!["birthdays"]![0]!["date"]!.AsObject();
            var expected = new JsonObject(); if (mode != "WithoutYear") expected["year"] = 2024; if (mode != "YearOnly") expected["month"] = 2; if (mode is "WithoutYear" or "Full") expected["day"] = 29; await Assert.That(JsonNode.DeepEquals(expected, saved)).IsTrue();
        }
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_filter_adapters_store_exact_percent_type_and_partial_date_operands()
    {
        await PrepareRealEditor();
        async Task Open() { HeadlessRuntime.Dispatch(() => FriendlyClick("ContactFiltersButton")); await Task.Delay(50); }
        void Field(string id) { var combo = (ComboBox)EditorControl("ContactFilterField"); combo.SelectedItem = ((IEnumerable<ContactColumnDescriptor>)combo.ItemsSource!).Single(c => c.Id == id); var op = (ComboBox)EditorControl("ContactFilterOperator"); op.SelectedItem = ContactFilterOperator.Equal; }
        await Open(); HeadlessRuntime.Dispatch(() => { Field("data.organizations.fullTimeEquivalentMillipercent"); ((TextBox)EditorControl("ContactFilterValue")).Text = "12,5"; FriendlyClick("ContactFilterAdd"); }); await Task.Delay(50); string raw = ""; HeadlessRuntime.Dispatch(() => raw = UxModel().Contacts.Configuration.Filters.Single().Value); await Assert.That(raw).IsEqualTo("12500");
        HeadlessRuntime.Dispatch(() => { Field("data.phoneNumbers.type"); FriendlyChoice("ContactFilterType", "workMobile"); FriendlyClick("ContactFilterAdd"); }); await Task.Delay(50);
        HeadlessRuntime.Dispatch(() => { Field("data.birthdays.date"); FriendlyChoice("ContactFilterDateMode", "WithoutYear"); FriendlyChoice("ContactFilterMonth", "2"); ((TextBox)EditorControl("ContactFilterDay")).Text = "29"; FriendlyClick("ContactFilterAdd"); }); await Task.Delay(50);
        string[] values = []; HeadlessRuntime.Dispatch(() => { values = UxModel().Contacts.Configuration.Filters.Select(f => f.Value).ToArray(); FriendlyClick("ContactConfigurationClose"); }); await Assert.That(string.Join("|", values)).IsEqualTo("12500|workMobile|0000-02-29");
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_ui_extreme_saved_split_temporarily_clamps_and_six_edit_layouts_keep_footer_visible()
    {
        await FriendlyRealCard(doc => { doc["data"]!["phoneNumbers"]![0]!["type"] = "workMobile"; doc["data"]!["addresses"] = new JsonArray(new JsonObject { ["streetAddress"] = "Очень длинное название улицы для проверки переноса", ["city"] = "Москва" }); });
        HeadlessRuntime.Dispatch(()=>UxModel().Contacts.ShowAllFields=false);await Task.Delay(50);
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark }) foreach (var size in new[] { (1180, 820), (1000, 680), (820, 640) })
            {
                foreach (var fraction in new[] { .25, .8 })
                {
                    HeadlessRuntime.Dispatch(() => { UxModel().Contacts.SetTableFraction(fraction); Session.Inner.MainWindow.Width = size.Item1; Session.Inner.MainWindow.Height = size.Item2; Avalonia.Application.Current!.RequestedThemeVariant = theme; }); await Task.Delay(90);
                    if (size.Item1 < 900) { HeadlessRuntime.Dispatch(() => FriendlyClick("ContactOpenButton")); await Task.Delay(50); }
                    var valid = false; HeadlessRuntime.Dispatch(() => { var save = EditorControl("ContactSaveButton"); var table = EditorControl("ContactsTable"); var point = save.TranslatePoint(default, Session.Inner.MainWindow); valid = UxModel().Contacts.TableFraction == fraction && save.IsEffectivelyVisible && point is { } p && p.Y + save.Bounds.Height < Session.Inner.MainWindow.Bounds.Height && (size.Item1 < 900 ? !table.IsVisible : table.Bounds.Width >= 299); }); await Assert.That(valid).IsTrue();
                }
                CaptureCheckpoint($"friendly-edit-{theme.Key}-{size.Item1}x{size.Item2}");
            }
        }
        finally { HeadlessRuntime.Dispatch(() => { UxModel().Contacts.CancelEdit(); Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default; }); }
    }
}
