using System.Text.Json;
using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public async Task Existing_label_and_starred_UI_save_locally_then_filter_AND_OR_keeps_hidden_draft_and_reopens_preferences()
    {
        var coordinator = new TestSyncCoordinator(); ConfigureUx(coordinator);
        var fixture = await PrepareRealEditor();
        var file = Path.Combine(fixture.Root, "contacts", fixture.Id + ".contact.json");
        var document = JsonSemantics.ParseObject(await File.ReadAllBytesAsync(file), file);
        document["google"] = new JsonObject
        {
            ["resourceName"] = "people/synthetic-label-editor", ["etag"] = "synthetic-original-etag",
            ["person"] = new JsonObject
            {
                ["resourceName"] = "people/synthetic-label-editor",
                ["metadata"] = new JsonObject { ["sources"] = new JsonArray(new JsonObject { ["type"] = "CONTACT", ["id"] = "synthetic-source" }) },
                ["futureGoogleField"] = new JsonObject { ["values"] = new JsonArray("Сохранить", "Сохранить", null) }
            },
            ["futureSnapshotField"] = "Сохранить без изменений"
        };
        var targetLabel = Guid.NewGuid(); var otherLabel = Guid.NewGuid();
        var starredOnly = Guid.NewGuid(); var labeledOnly = Guid.NewGuid(); var neither = Guid.NewGuid();
        await using (var workspace = await new FileWorkspaceStore().OpenAsync(fixture.Root, TestAccountConnector.Identity))
        {
            await workspace.SaveStateAsync(new EntityState
            {
                Id = fixture.Id, Kind = EntityKind.Contact, ResourceName = "people/synthetic-label-editor",
                Baseline = (JsonObject)document["data"]!.DeepClone()
            });
        }
        foreach (var group in new[] { new GroupDocument { Id = otherLabel, Name = "Альфа" }, new GroupDocument { Id = targetLabel, Name = "Команда" } })
            await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "groups", group.Id + ".group.json"), JsonSemantics.Serialize(DocumentCodec.ToJson(group)));
        await File.WriteAllBytesAsync(file, JsonSemantics.Serialize(document));
        foreach (var contact in new[]
        {
            LabelsContact(starredOnly, "Борис", true), LabelsContact(labeledOnly, "Вера", false, targetLabel), LabelsContact(neither, "Глеб", false, otherLabel)
        })
            await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "contacts", contact.Id + ".contact.json"), JsonSemantics.Serialize(DocumentCodec.ToJson(contact)));

        ContactsViewModel? filtersModel = null, reopenedModel = null; object? originalContent = null;
        try
        {
            await EditorUi(() => UxModel().Contacts.LoadAsync(fixture.Root));
            await EditorUi(() => UxModel().Contacts.ReloadSelectedAsync());
            await LabelsClick("ContactEditButton");
            await LabelsToggle(() => (CheckBox)EditorControl("Editor:starred"));
            await LabelsToggle(() => (CheckBox)EditorControl("Editor:labels:" + targetLabel));
            await LifecycleWait(() => UxModel().Contacts.IsDirty && UxModel().Contacts.Starred && UxModel().Contacts.HasLabel(targetLabel), "label and starred checkboxes mutate draft");
            await LabelsClick("ContactSaveButton");
            await LifecycleWait(() => !UxModel().Contacts.IsBusy && !UxModel().Contacts.IsEditing && !UxModel().Contacts.IsDirty, "label and starred local save completes");

            var expected = (JsonObject)document.DeepClone(); expected["starred"] = true; expected["labels"] = new JsonArray(targetLabel.ToString());
            var realStore = new ContactEditorStore();
            var saved = await realStore.ReadAsync(fixture.Root, fixture.Id, TestAccountConnector.Identity);
            var savedBytes = await File.ReadAllBytesAsync(file);
            await Assert.That(JsonSemantics.Equal(saved.Document, expected)).IsTrue();
            await Assert.That(JsonSemantics.Equal(JsonNode.Parse(savedBytes), expected)).IsTrue();
            await Assert.That(JsonNode.DeepEquals(saved.Document["google"], document["google"])).IsTrue();
            await Assert.That(saved.Document["labels"]!.AsArray().Single()!.GetValue<string>()).IsEqualTo(targetLabel.ToString());
            await Assert.That(saved.Document["starred"]!.GetValue<bool>()).IsTrue();
            await Assert.That(coordinator.AppliedCount).IsEqualTo(0);
            await LabelsClick("ContactReloadButton");
            await LifecycleWait(() => !UxModel().Contacts.IsBusy && UxModel().Contacts.Original?.Hash == saved.Hash, "saved flags reopen in main editor");

            var preferencesFile = Path.Combine(fixture.Root, "isolated-label-filter-preferences.json");
            HeadlessRuntime.Dispatch(() =>
            {
                var activity = new ApplicationActivity();
                filtersModel = new(new ContactEditorService(realStore, activity), activity, () => TestAccountConnector.Identity,
                    () => { }, preferenceStore: new(false, true, preferencesFile));
                originalContent = Session.Inner.MainWindow.Content;
                Session.Inner.MainWindow.Width = 1180; Session.Inner.MainWindow.Height = 820;
                Session.Inner.MainWindow.Content = new ContactsView(filtersModel);
            });
            var model = filtersModel!;
            await EditorUi(() => model.LoadAsync(fixture.Root)); await EditorUi(() => model.SelectAsync(fixture.Id));
            await LabelsRows(model, fixture.Id, starredOnly, labeledOnly, neither);
            await LabelsClick("ContactEditButton");
            HeadlessRuntime.Dispatch(() =>
            {
                var name = (TextBox)EditorControl("Editor:data.names.0.familyName"); name.BringIntoView(); name.Focus(); name.SelectAll();
                Session.Inner.MainWindow.KeyTextInput("Черновик после фильтрации");
            });
            await LifecycleWait(() => model.IsDirty, "ordinary name typing makes draft dirty");
            JsonObject draft = null!; HeadlessRuntime.Dispatch(() => draft = model.Document!);
            await LabelsClick("ContactFiltersButton");
            HeadlessRuntime.Dispatch(() =>
            {
                var field = (ComboBox)EditorControl("ContactFilterField");
                field.SelectedItem = field.ItemsSource!.Cast<ContactColumnDescriptor>().Single(column => column.Id == "labels");
                ((ComboBox)EditorControl("ContactFilterOperator")).SelectedItem = ContactFilterOperator.Equal;
                var labels = (ComboBox)EditorControl("ContactFilterLabel");
                labels.SelectedItem = labels.ItemsSource!.Cast<KeyValuePair<Guid, string>>().Single(label => label.Key == targetLabel);
            });
            var labelOperandVisible = false; HeadlessRuntime.Dispatch(() => labelOperandVisible = EditorControl("ContactFilterLabel").IsVisible && !EditorControl("ContactFilterValue").IsVisible);
            await Assert.That(labelOperandVisible).IsTrue();
            await LabelsClick("ContactFilterAdd");
            await LabelsRows(model, fixture.Id, labeledOnly);
            HeadlessRuntime.Dispatch(() =>
            {
                var field = (ComboBox)EditorControl("ContactFilterField");
                field.SelectedItem = field.ItemsSource!.Cast<ContactColumnDescriptor>().Single(column => column.Id == "starred");
                ((ComboBox)EditorControl("ContactFilterOperator")).SelectedItem = ContactFilterOperator.Equal;
                ((ComboBox)EditorControl("ContactFilterBoolean")).SelectedIndex = 1; // Нет.
            });
            var booleanOperandVisible = false; HeadlessRuntime.Dispatch(() => booleanOperandVisible = EditorControl("ContactFilterBoolean").IsVisible && !EditorControl("ContactFilterValue").IsVisible);
            await Assert.That(booleanOperandVisible).IsTrue();
            await LabelsClick("ContactFilterAdd");
            await LabelsRows(model, labeledOnly); // label == Команда AND starred == false.
            var hidden = false; HeadlessRuntime.Dispatch(() => hidden = model.HiddenSelection.Length > 0 && model.HasContact && model.IsEditing && model.IsDirty && ReferenceEquals(model.Document, draft));
            await Assert.That(hidden).IsTrue();
            await LabelsClick("ContactConfigurationClose");
            var retainedName = ""; HeadlessRuntime.Dispatch(() => retainedName = ((TextBox)EditorControl("Editor:data.names.0.familyName")).Text ?? "");
            await Assert.That(retainedName).IsEqualTo("Черновик после фильтрации");
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(savedBytes)).IsTrue();
            CaptureCheckpoint("editor-label-starred-AND-hidden-draft");

            await LabelsClick("ContactFiltersButton");
            await LabelsToggle(() => Session.Inner.MainWindow.GetVisualDescendants().OfType<CheckBox>()
                .Single(control => control.Content?.ToString() == "Достаточно любого условия (ИЛИ)"));
            await LabelsRows(model, fixture.Id, labeledOnly, neither); // label == Команда OR starred == false.
            await LabelsClick("ContactConfigurationClose");
            var retained = false; HeadlessRuntime.Dispatch(() => retained = model.Configuration.MatchAny && model.IsDirty && model.IsEditing && ReferenceEquals(model.Document, draft) && model.HiddenSelection.Length == 0);
            await Assert.That(retained).IsTrue();
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(savedBytes)).IsTrue();

            var persisted = JsonSerializer.Deserialize<Dictionary<string, ContactWorkspacePreferences>>(await File.ReadAllTextAsync(preferencesFile))!.Values.Single();
            await Assert.That(persisted.Table.MatchAny).IsTrue();
            await Assert.That(persisted.Table.Filters[0]).IsEqualTo(new ContactFilterRule("labels", ContactFilterOperator.Equal, targetLabel.ToString()));
            await Assert.That(persisted.Table.Filters[1]).IsEqualTo(new ContactFilterRule("starred", ContactFilterOperator.Equal, "false"));
            HeadlessRuntime.Dispatch(() =>
            {
                var activity = new ApplicationActivity();
                reopenedModel = new(new ContactEditorService(new ContactEditorStore(), activity), activity, () => TestAccountConnector.Identity,
                    () => { }, preferenceStore: new(false, true, preferencesFile));
                Session.Inner.MainWindow.Content = new ContactsView(reopenedModel);
            });
            var reopened = reopenedModel!;
            await EditorUi(() => reopened.LoadAsync(fixture.Root));
            await LabelsRows(reopened, fixture.Id, labeledOnly, neither);
            var restored = false;
            HeadlessRuntime.Dispatch(() => restored = reopened.Configuration.MatchAny && reopened.Configuration.Filters.SequenceEqual(persisted.Table.Filters) && !reopened.IsDirty);
            await Assert.That(restored).IsTrue();
            await LabelsClick("ContactFiltersButton");
            var restoredOr = false; var restoredRules = 0;
            HeadlessRuntime.Dispatch(() =>
            {
                restoredOr = Session.Inner.MainWindow.GetVisualDescendants().OfType<CheckBox>()
                    .Single(control => control.Content?.ToString() == "Достаточно любого условия (ИЛИ)").IsChecked == true;
                restoredRules = Session.Inner.MainWindow.GetVisualDescendants().OfType<Button>()
                    .Count(control => AutomationProperties.GetAutomationId(control)?.StartsWith("RemoveFilter:", StringComparison.Ordinal) == true);
            });
            await Assert.That(restoredOr).IsTrue(); await Assert.That(restoredRules).IsEqualTo(2);
            await LabelsClick("ContactConfigurationClose");
            await Assert.That(coordinator.AppliedCount).IsEqualTo(0);
            await Assert.That((await File.ReadAllBytesAsync(file)).SequenceEqual(savedBytes)).IsTrue();
            CaptureCheckpoint("editor-label-starred-OR-reopened-preferences");
        }
        finally
        {
            HeadlessRuntime.Dispatch(() =>
            {
                if (filtersModel is not null) { filtersModel.ShowConfiguration = false; filtersModel.CancelEdit(); filtersModel.Dispose(); }
                if (reopenedModel is not null) { reopenedModel.ShowConfiguration = false; reopenedModel.Dispose(); }
                if (originalContent is not null) Session.Inner.MainWindow.Content = originalContent;
                UxModel().Contacts.ShowConfiguration = false; UxModel().Contacts.CancelEdit();
            });
        }
    }

    private static ContactDocument LabelsContact(Guid id, string name, bool starred, params Guid[] labels) => new()
    {
        Id = id, Starred = starred, Labels = labels.ToList(), Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = name }) },
        Extensions = new JsonObject { ["fixture"] = "Синтетический контакт для фильтров" }
    };

    private async Task LabelsClick(string id)
    {
        HeadlessRuntime.Dispatch(() => ((Button)EditorControl(id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Task.Delay(30);
    }

    private async Task LabelsToggle(Func<CheckBox> find)
    {
        HeadlessRuntime.Dispatch(() => find().BringIntoView()); await Task.Delay(30);
        HeadlessRuntime.Dispatch(() =>
        {
            var checkbox = find();
            var point = checkbox.TranslatePoint(new Point(12, checkbox.Bounds.Height / 2), Session.Inner.MainWindow)
                ?? throw new InvalidOperationException("Checkbox is not attached to the current contact editor.");
            Session.Inner.MainWindow.MouseDown(point, MouseButton.Left); Session.Inner.MainWindow.MouseUp(point, MouseButton.Left);
        });
        await Task.Delay(30);
    }

    private async Task LabelsRows(ContactsViewModel model, params Guid[] expected)
    {
        await LifecycleWait(() => model.Rows.Select(row => row.Id).Order().SequenceEqual(expected.Order()), "actual filter result rows");
        var actual = ""; var count = "";
        HeadlessRuntime.Dispatch(() =>
        {
            actual = string.Join("|", ((DataGrid)EditorControl("ContactsTable")).ItemsSource.Cast<ContactRowViewModel>().Select(row => row.Id).Order());
            count = model.CountCaption;
        });
        await Assert.That(actual).IsEqualTo(string.Join("|", expected.Order()));
        await Assert.That(count).IsEqualTo($"Показано {expected.Length} из 4");
    }
}
