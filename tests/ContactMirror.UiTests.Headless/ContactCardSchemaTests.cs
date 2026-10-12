using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using ContactMirror.Application;
using ContactMirror.Application.Contacts;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.Core;
using ContactMirror.Desktop.Contacts;
using ContactMirror.Infrastructure;
using ContactMirror.Infrastructure.Contacts;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Contact_card_exposes_all_111_effective_leaves_and_roundtrips_typed_controls_without_touching_Google()
    {
        var oracle = GoldenLeaves(); var document = CardDocument();
        foreach (var category in CapabilityRegistry.WritableFields) document["data"]![category] = new JsonArray(new JsonObject());
        using var card = await OpenCard(document);
        await ExpandCard(card);
        string[] ids = [];
        HeadlessRuntime.Dispatch(() => ids = CardControls(card.Root).Select(AutomationProperties.GetAutomationId).Where(id => id?.StartsWith("Editor:data.", StringComparison.Ordinal) == true).Select(id => id!).Order().ToArray());
        await Assert.That(oracle.Count).IsEqualTo(111);
        await Assert.That(string.Join("\n", ids)).IsEqualTo(string.Join("\n", oracle.Select(leaf => "Editor:" + leaf.Path).Order()));
        await Assert.That(ids.Any(id => id.Contains(".primary", StringComparison.Ordinal) || id.Contains(".verified", StringComparison.Ordinal) || id.Contains(".source.", StringComparison.Ordinal))).IsFalse();
        var expected = (JsonObject)document.DeepClone();
        HeadlessRuntime.Dispatch(() =>
        {
            foreach (var leaf in oracle)
            {
                var editor = CardControls(card.Root).Single(control => AutomationProperties.GetAutomationId(control) == "Editor:" + leaf.Path);
                if (editor is TextBox text) text.Text = leaf.Value.ToString();
                else if (editor is CheckBox checkbox) checkbox.IsChecked = leaf.Value.GetValue<bool>();
                else if (editor is ComboBox choice)
                    choice.SelectedItem = ((IEnumerable<ContactFieldOption>)choice.ItemsSource!).Single(option => option.Value == leaf.Value.GetValue<string>());
                else throw new InvalidOperationException("Поле не имеет typed editor: " + leaf.Path);
                SetGolden(expected, leaf.Path, leaf.Value.DeepClone());
            }
        });
        await Task.Delay(100);
        var saved = await OnCardUi(() => card.Model.SaveAsync());
        await Assert.That(saved).IsTrue();
        await Assert.That(card.Store.Saves).IsEqualTo(1);
        await Assert.That(JsonSemantics.Equal(card.Store.Document, expected)).IsTrue();
        await Assert.That(JsonSemantics.Equal(card.Store.Document["google"], document["google"])).IsTrue();
        await Assert.That(JsonSemantics.Equal(card.Store.Document["extensions"], document["extensions"])).IsTrue();
        var realRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-card-schema-real-" + Guid.NewGuid().ToString("N"))).FullName;
        var contactId = Guid.Parse(document["id"]!.GetValue<string>());
        await using (var initialized = await new FileWorkspaceStore().OpenAsync(realRoot, TestAccountConnector.Identity))
        {
            await initialized.SaveStateAsync(new EntityState
            {
                Id = contactId, Kind = EntityKind.Contact,
                ResourceName = document["google"]!["resourceName"]!.GetValue<string>(),
                Baseline = (JsonObject)document["data"]!.DeepClone()
            });
        }
        var realFile = Path.Combine(realRoot, "contacts", contactId + ".contact.json");
        await File.WriteAllBytesAsync(realFile, JsonSemantics.Serialize(document));
        var realStore = new ContactEditorStore();
        var original = await realStore.ReadAsync(realRoot, contactId, TestAccountConnector.Identity);
        await Assert.That(original.ReadOnlyReason).IsNull();
        var persisted = await realStore.SaveAsync(original, (JsonObject)card.Store.Document.DeepClone(), new ContactPhotoEdit(), TestAccountConnector.Identity);
        var reopenedReal = await realStore.ReadAsync(realRoot, contactId, TestAccountConnector.Identity);
        var actualBytes = await File.ReadAllBytesAsync(realFile);
        await Assert.That(persisted.Path).IsEqualTo(realFile);
        await Assert.That(persisted.Hash).IsEqualTo(JsonSemantics.Hash(actualBytes));
        await Assert.That(reopenedReal.RawBytes!.SequenceEqual(actualBytes)).IsTrue();
        await Assert.That(JsonSemantics.Equal(JsonNode.Parse(actualBytes), expected)).IsTrue();
        await Assert.That(JsonSemantics.Equal(reopenedReal.Document, expected)).IsTrue();
        await Assert.That(JsonNode.DeepEquals(reopenedReal.Document["google"], document["google"])).IsTrue();
        await Assert.That(JsonNode.DeepEquals(reopenedReal.Document["extensions"], document["extensions"])).IsTrue();
        await Assert.That(card.Model.IsDirty).IsFalse();
        await Assert.That(card.Model.IsEditing).IsFalse();
        HeadlessRuntime.Dispatch(() => card.Root = ContactCardControls.Build(card.Model));
        Console.WriteLine("Contact card independent schema oracle: 23 categories, 111 typed leaves changed; UI output saved/reopened through real ContactEditorStore/file CAS, Google/extensions unchanged.");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Invalid_numeric_input_survives_another_edit_blocks_save_and_correcting_it_allows_retry()
    {
        var document = CardDocument(); document["data"]!["birthdays"] = JsonNode.Parse("[{\"date\":{\"month\":2,\"day\":29}}]");
        document["data"]!["names"] = JsonNode.Parse("[{\"givenName\":\"Анна\"}]");
        using var card = await OpenCard(document); await ExpandCard(card);
        HeadlessRuntime.Dispatch(() =>
        {
            CardText(card, "Editor:data.birthdays.0.date.year").Text = "не число";
            CardText(card, "Editor:data.names.0.givenName").Text = "Анна после правки";
        });
        await Task.Delay(50);
        await Assert.That(await OnCardUi(() => card.Model.SaveAsync())).IsFalse();
        await Assert.That(card.Store.Saves).IsEqualTo(0);
        await Assert.That(card.Model.IsDirty).IsTrue();
        await Assert.That(card.Model.Error).Contains("целое число");
        HeadlessRuntime.Dispatch(() => CardText(card, "Editor:data.birthdays.0.date.year").Text = "2024");
        await Task.Delay(50);
        await Assert.That(await OnCardUi(() => card.Model.SaveAsync())).IsTrue();
        await Assert.That(card.Store.Document["data"]!["birthdays"]![0]!["date"]!["year"]!.GetValue<int>()).IsEqualTo(2024);
        await Assert.That(card.Store.Document["data"]!["names"]![0]!["givenName"]!.GetValue<string>()).IsEqualTo("Анна после правки");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Primary_is_exclusive_clear_removes_property_and_extensions_keep_types_null_and_duplicates()
    {
        var document = CardDocument(); document["data"]!["phoneNumbers"] = JsonNode.Parse("[{\"value\":\"a\",\"metadata\":{\"sourcePrimary\":true}},{\"value\":\"b\"}]");
        using var card = await OpenCard(document); await ExpandCard(card);
        HeadlessRuntime.Dispatch(() =>
        {
            ((CheckBox)CardControls(card.Root).Single(control => AutomationProperties.GetAutomationId(control) == "Editor:data.phoneNumbers.1.metadata.sourcePrimary")).IsChecked = true;
            CardText(card, "Editor:extensions.nested.text").Text = "[1,2]";
            CardText(card, "Editor:extensions.nested.number").Text = "12.5";
            ((CheckBox)CardControls(card.Root).Single(control => AutomationProperties.GetAutomationId(control) == "Editor:extensions.nested.flag")).IsChecked = true;
            var clear = CardControls(card.Root).OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "Clear:data.phoneNumbers.0.value");
            clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        await Task.Delay(70);
        await Assert.That(await OnCardUi(() => card.Model.SaveAsync())).IsTrue();
        var saved = card.Store.Document;
        await Assert.That(saved["data"]!["phoneNumbers"]![0]!.AsObject().ContainsKey("value")).IsFalse();
        await Assert.That(saved["data"]!["phoneNumbers"]![0]!["metadata"]!["sourcePrimary"]!.GetValue<bool>()).IsFalse();
        await Assert.That(saved["data"]!["phoneNumbers"]![1]!["metadata"]!["sourcePrimary"]!.GetValue<bool>()).IsTrue();
        await Assert.That(saved["extensions"]!["nested"]!["text"]!.GetValueKind()).IsEqualTo(JsonValueKind.String);
        await Assert.That(saved["extensions"]!["nested"]!["text"]!.GetValue<string>()).IsEqualTo("[1,2]");
        await Assert.That(saved["extensions"]!["nested"]!["number"]!.GetValue<decimal>()).IsEqualTo(12.5m);
        await Assert.That(saved["extensions"]!["nested"]!["flag"]!.GetValue<bool>()).IsTrue();
        await Assert.That(saved["extensions"]!["empty"]).IsNull();
        await Assert.That(saved["extensions"]!.AsObject().ContainsKey("empty")).IsTrue();
        await Assert.That(JsonSemantics.Equal(saved["extensions"]!["duplicates"], document["extensions"]!["duplicates"])).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Two_thousand_repeated_contact_values_use_bounded_realization_and_UI_stays_available()
    {
        var document = CardDocument(); document["data"]!["phoneNumbers"] = new JsonArray(Enumerable.Range(0, 2000)
            .Select(index => (JsonNode)new JsonObject { ["value"] = "Телефон " + index }).ToArray());
        using var card = await OpenCard(document, allFields: false);
        HeadlessRuntime.Dispatch(()=>card.Root.GetVisualDescendants().OfType<ListBox>().Single(control=>AutomationProperties.GetAutomationId(control)=="Rows:phoneNumbers").BringIntoView());
        await Task.Delay(200);
        int count = 0, realized = 0;
        HeadlessRuntime.Dispatch(() =>
        {
            var list = card.Root.GetVisualDescendants().OfType<ListBox>().Single(control => AutomationProperties.GetAutomationId(control) == "Rows:phoneNumbers");
            count = list.ItemCount; realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
        });
        await Assert.That(count).IsEqualTo(2000); await Assert.That(realized).IsGreaterThan(0); await Assert.That(realized).IsLessThan(50);
        var timer = Stopwatch.StartNew(); HeadlessRuntime.Dispatch(() => card.Model.ValidationError("", "test")); timer.Stop();
        await Assert.That(timer.ElapsedMilliseconds).IsLessThan(250);
        CaptureCheckpoint("contact-editor-large-schema-card");
        Console.WriteLine($"Contact card: 2000 rows, {realized} realized, UI ping {timer.ElapsedMilliseconds}ms.");
    }

    private static IEnumerable<Control> CardControls(Control root) => root.GetLogicalDescendants().OfType<Control>();
    private static TextBox CardText(CardFixture card, string id) => CardControls(card.Root).OfType<TextBox>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static Task<T> OnCardUi<T>(Func<Task<T>> action) => HeadlessEditorUi.RunAsync(action);
    private async Task<CardFixture> OpenCard(JsonObject document, bool allFields = true)
    {
        Console.WriteLine("Schema card: construct model.");
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ContactMirror-card-schema-" + Guid.NewGuid().ToString("N"))).FullName;
        var store = new CardStore(folder, document); ContactsViewModel model = null!; Control root = null!; object? before = null;
        HeadlessRuntime.Dispatch(() => model = new(new ContactEditorService(store, store.Activity), store.Activity, () => null, () => { }, demo: true, persist: false));
        await OnCardUi(async()=>{await model.LoadAsync(folder);return true;});
        Console.WriteLine("Schema card: catalog loaded.");
        await OnCardUi(async()=>{await model.SelectAsync(store.Id);return true;});
        Console.WriteLine("Schema card: snapshot selected.");
        HeadlessRuntime.Dispatch(() =>
        {
            Console.WriteLine("Schema card: begin editing.");
            model.BeginEdit(); model.ShowAllFields = allFields;
            Console.WriteLine("Schema card: build controls.");
            root = ContactCardControls.Build(model);
            Console.WriteLine("Schema card: assign content.");
            before = Session.Inner.MainWindow.Content; Session.Inner.MainWindow.Width = 1000; Session.Inner.MainWindow.Height = 680;
            Session.Inner.MainWindow.Content = new ScrollViewer { Content = root };
            Console.WriteLine("Schema card: content assigned.");
        });
        Console.WriteLine("Schema card: controls attached.");
        await Task.Delay(100);
        return new(model, store, root, () => { Session.Inner.MainWindow.Content = before; });
    }
    private static async Task ExpandCard(CardFixture card)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var expanded = 0;
            HeadlessRuntime.Dispatch(() =>
            {
                foreach (var expander in CardControls(card.Root).OfType<Expander>().Where(control => !control.IsExpanded).ToArray()) { expander.IsExpanded = true; expanded++; }
            });
            await Task.Delay(30); if (expanded == 0) break;
        }
    }
    private sealed class CardFixture(ContactsViewModel model, CardStore store, Control root, Action restore) : IDisposable
    {
        public ContactsViewModel Model => model;
        public CardStore Store => store;
        public Control Root { get; set; } = root;
        public void Dispose()
        {
            HeadlessRuntime.Dispatch(() => { model.CancelEdit(); model.Dispose(); restore(); });
            Directory.Delete(store.Root);
        }
    }
    private sealed class CardStore(string root, JsonObject initial) : IContactEditorStore
    {
        public ApplicationActivity Activity { get; } = new();
        public string Root => root;
        public Guid Id { get; } = Guid.Parse(initial["id"]!.GetValue<string>());
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public JsonObject Document { get; private set; } = (JsonObject)initial.DeepClone();
        public int Saves { get; private set; }
        private ContactSnapshot Snapshot() => new(root, WorkspaceId, Id, Path.Combine(root, "fixture.contact.json"), (JsonObject)Document.DeepClone(), JsonSemantics.Serialize(Document), "fixture-hash", null, null);
        public Task<ContactCatalog> LoadAsync(string path, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default) => Task.FromResult(new ContactCatalog(root, WorkspaceId, [new(Id, "fixture.contact.json", (JsonObject)Document.DeepClone())], new Dictionary<Guid, string>(), []));
        public Task<ContactSnapshot> ReadAsync(string path, Guid id, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<ContactSnapshot> SaveAsync(ContactSnapshot original, JsonObject document, ContactPhotoEdit photo, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default)
        { DocumentCodec.Contact(document); Saves++; Document = (JsonObject)document.DeepClone(); return Task.FromResult(Snapshot()); }
        public Task DeleteAsync(ContactSnapshot original, AccountIdentity? activeAccount = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed record GoldenLeaf(string Path, JsonNode Value);
    private static IReadOnlyList<GoldenLeaf> GoldenLeaves()
    {
        using var stream = typeof(CapabilityRegistry).Assembly.GetManifestResourceStream("ContactMirror.Core.people-schema.json")!;
        var definitions = JsonNode.Parse(stream)!["schemas"]!.AsObject(); var result = new List<GoldenLeaf>();
        foreach (var category in CapabilityRegistry.WritableFields) Inspect(definitions["Person"]!["properties"]![category]!.AsObject(), "data." + category, null);
        return result;
        void Inspect(JsonObject schema, string path, string? referenceParent)
        {
            if (schema["readOnly"]?.GetValue<bool>() == true || referenceParent == "FieldMetadata" && !path.EndsWith(".sourcePrimary", StringComparison.Ordinal)) return;
            if (schema["type"]?.GetValue<string>() == "array") { Inspect(schema["items"]!.AsObject(), path + ".0", null); return; }
            if (schema["$ref"] is JsonValue reference)
            { var definition = reference.GetValue<string>(); foreach (var pair in definitions[definition]!["properties"]!.AsObject()) Inspect(pair.Value!.AsObject(), path + "." + pair.Key, definition); return; }
            JsonNode value = schema["type"]?.GetValue<string>() switch
            {
                "integer" => JsonValue.Create(path.EndsWith(".year", StringComparison.Ordinal) ? 2024 : path.EndsWith(".month", StringComparison.Ordinal) ? 2 : path.EndsWith(".day", StringComparison.Ordinal) ? 29 : 85000)!,
                "boolean" => JsonValue.Create(true)!,
                _ => schema["enum"] is JsonArray options ? options[^1]!.DeepClone() : JsonValue.Create("Тест " + path)!
            };
            result.Add(new(path, value));
        }
    }
    private static void SetGolden(JsonObject document, string path, JsonNode value)
    {
        var parts = path.Split('.'); JsonNode current = document;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (current is JsonArray array) current = array[int.Parse(parts[index])]!;
            else { var obj = current.AsObject(); obj[parts[index]] ??= new JsonObject(); current = obj[parts[index]]!; }
        }
        current.AsObject()[parts[^1]] = value;
    }
    private static JsonObject CardDocument() => new()
    {
        ["schemaVersion"] = 1, ["id"] = Guid.NewGuid().ToString(), ["data"] = new JsonObject(), ["labels"] = new JsonArray(), ["starred"] = false, ["photo"] = null,
        ["extensions"] = JsonNode.Parse("{\"nested\":{\"text\":\"исходный текст\",\"number\":3,\"flag\":false},\"empty\":null,\"duplicates\":[\"a\",\"a\"]}"),
        ["google"] = JsonNode.Parse("{\"resourceName\":\"people/schemafixture\",\"etag\":\"unmodified-etag\",\"futureField\":{\"raw\":[1,null,true]},\"snapshot\":{\"names\":[{\"displayName\":\"Профиль Google\",\"metadata\":{\"verified\":true,\"source\":{\"type\":\"PROFILE\",\"id\":\"preserved\"}}}]}}")
    };
}
