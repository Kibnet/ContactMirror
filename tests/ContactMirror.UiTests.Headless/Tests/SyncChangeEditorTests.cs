using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AppAutomation.Avalonia.Headless.Session;
using AppAutomation.TUnit;
using AppAutomation.Abstractions;
using ContactMirror.Desktop;
using ContactMirror.Core;
using TUnit.Core;
using TUnit.Assertions;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class IntegratedDemoHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Dependency_refresh_displays_latest_Google_photo_and_reset_saves_same_bytes()
    {
        var contact = await SyncedContact();
        static byte[] Photo(SkiaSharp.SKColor color) { using var bitmap = new SkiaSharp.SKBitmap(8, 8); bitmap.Erase(color); using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100); return encoded.ToArray(); }
        var a = Photo(SkiaSharp.SKColors.Teal); var b = Photo(SkiaSharp.SKColors.OrangeRed);
        var remote = new ContactMirror.Infrastructure.Demo.DemoGoogleGateway(Path.Combine(IntegrationRoot, "remote.json"));
        var resource = contact.Document["google"]!["resourceName"]!.ToString();
        await remote.UpdatePhotoAsync(resource, a);
        var group = await remote.CreateGroupAsync("Недостающий ярлык для фото", new JsonArray());
        await remote.ModifyMembershipAsync(group["resourceName"]!.ToString(), resource, true);
        // A separate model uses this same provider instance, whose synthetic state is cached.
        MainWindowViewModel model = null!; ContactMirror.Desktop.Contacts.SyncChangeEditor view = null!; Window host = null!;
        await HeadlessEditorUi.RunAsync(async () =>
        {
            model = new(new ContactMirror.Infrastructure.Demo.DemoAccountConnector(), new ContactMirror.Application.SyncCoordinator(remote, new ContactMirror.Infrastructure.FileWorkspaceStore()), demo: true, persist: false) { Folder = Path.Combine(IntegrationRoot, "workspace") };
            await model.InitializeAsync(); await model.CheckCommand.ExecuteAsync(null);
            model.SelectedEntry = model.Entries.Single(e => e.Entry.EntityId == Guid.Parse(contact.Document["id"]!.ToString()) && e.Entry.Field == "photo");
            var edit = await model.OpenSyncEditorAsync() ?? throw new InvalidOperationException(model.Error);
            view = new(model, edit, model.CloseSyncEditor); host = new Window { Content = view, Width = 820, Height = 640 }; host.Show(); return true;
        });
        Control Find(string id) => view.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == id);
        void Click(string id) => HeadlessRuntime.Dispatch(() => ((Button)Find(id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        try
        {
            await remote.UpdatePhotoAsync(resource, b);
            await Task.Delay(100); Click("SyncEditLabels"); await Task.Delay(80); Click("SyncEditConfirm");
            await SyncWait(() => Find("SyncEditStatus") is TextBlock text && text.Text?.StartsWith("Ярлыки сохранены", StringComparison.Ordinal) == true);
            uint shown = 0;
            HeadlessRuntime.Dispatch(() =>
            {
                var bitmap = (Avalonia.Media.Imaging.Bitmap)((Image)Find("SyncEditGooglePhoto")).Source!;
                using var stream = new MemoryStream(); bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); using var decoded = SkiaSharp.SKBitmap.Decode(stream.ToArray()); shown = (uint)decoded.GetPixel(0, 0);
            });
            await Assert.That(shown).IsEqualTo((uint)SkiaSharp.SKColors.OrangeRed);
            Click("SyncEditReset"); Click("SyncEditSave"); await SyncWait(() => !model.LocalEditorOpen && model.HasPreview);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!;
            var photoPath = Path.Combine(IntegrationRoot, "workspace", saved["photo"]!.ToString());
            await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(photoPath))).IsEqualTo(Convert.ToHexString(b));
        }
        finally { HeadlessRuntime.Dispatch(() => { view.Dispose(); host.Close(); model.Detach(); }); }
    }
    private MainWindowViewModel SyncModel => (MainWindowViewModel)Session.Inner.MainWindow.DataContext!;
    private async Task<(string Path, JsonObject Document)> SyncedContact()
    {
        Page.WaitUntilNameContains(p => p.AccountLabel, "Демонстрационный аккаунт").ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        Page.ClickButton(p => p.ApplyButton).WaitUntilNameContains(p => p.StatusText, "Синхронизировано", timeoutMs: 15000);
        var path = Directory.GetFiles(Path.Combine(IntegrationRoot, "workspace", "contacts"), "*.json").Single(path => JsonNode.Parse(File.ReadAllText(path))!["google"]!["resourceName"]!.ToString() == "people/demo2");
        return (path, JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject());
    }
    private async Task SyncWait(Func<bool> check)
    {
        for (var n = 0; n < 200; n++) { var ready = false; HeadlessRuntime.Dispatch(() => ready = check()); if (ready) return; await Task.Delay(30); }
        string status = ""; HeadlessRuntime.Dispatch(() => status = SyncModel.Error + ": " + SyncModel.Status + ": " + (Session.Inner.MainWindow.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == "SyncEditStatus")?.Text ?? ""));
        throw new TimeoutException(status);
    }
    private async Task OpenSyncField(string field, bool reset = false, EntityKind? kind = null)
    {
        HeadlessRuntime.Dispatch(() =>
        {
            SyncModel.SelectedEntry = SyncModel.Entries.First(e => e.Entry.Field == field && (kind is null || e.Entry.Entity == kind));
            var list = Session.Inner.MainWindow.GetVisualDescendants().OfType<ListBox>().Single(c => AutomationProperties.GetAutomationId(c) == "FieldChoices");
            list.ScrollIntoView(SyncModel.SelectedEntry);
        });
        await Task.Delay(100);
        HeadlessRuntime.Dispatch(() =>
        {
            var button = (Button)FriendlyControl(SyncModel.SelectedEntry!.EditActionId); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = button.ContextMenu!; var item = menu.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == (reset ? "SyncChangeReset" : "SyncChangeEdit"));
            menu.Close(); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        });
        await SyncWait(() => SyncModel.LocalEditorOpen && Session.Inner.MainWindow.GetVisualDescendants().Any(c => c is Control control && AutomationProperties.GetAutomationId(control) == "SyncEditSave"));
        await Task.Delay(120);
    }
    private void SyncClick(string id) => HeadlessRuntime.Dispatch(() => ((Button)FriendlyControl(id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

    [Test, NotInParallel("DesktopUi")]
    public async Task Reset_to_missing_Google_birthday_clears_invalid_input_buffer_and_saves()
    {
        var contact = await SyncedContact(); contact.Document["data"]!["birthdays"] = new JsonArray(new JsonObject { ["date"] = new JsonObject { ["year"] = 2000, ["month"] = 2, ["day"] = 29 } });
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString());
        Page.ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено"); await OpenSyncField("birthdays");
        HeadlessRuntime.Dispatch(() => ((TextBox)FriendlyControl("Editor:data.birthdays.0.date.year")).Text = "ошибка");
        SyncClick("SyncEditSave"); bool stillOpen = false; HeadlessRuntime.Dispatch(() => stillOpen = SyncModel.LocalEditorOpen); await Assert.That(stillOpen).IsTrue();
        SyncClick("SyncEditReset"); CaptureCheckpoint("sync-birthday-reset-invalid-buffer"); SyncClick("SyncEditSave");
        await SyncWait(() => !SyncModel.LocalEditorOpen && SyncModel.HasPreview);
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!["data"]!.AsObject().ContainsKey("birthdays")).IsFalse();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Remote_only_dependency_refresh_preserves_unrelated_unchecked_contact_and_uncommon_draft_field()
    {
        Page.WaitUntilNameContains(p => p.AccountLabel, "Демонстрационный аккаунт").ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        string? unrelated = null;
        HeadlessRuntime.Dispatch(() =>
        {
            var other = SyncModel.Entries.Last(e => e.Entry.Entity == EntityKind.Contact && e.Entry.Field == "$entity"); other.IsSelected = false;
            unrelated = other.Entry.Google!["resourceName"]!.ToString();
        });
        await OpenSyncField("$entity", kind: EntityKind.Contact);
        SyncClick("SyncEditAllFields"); SyncClick("AddField:interests");
        HeadlessRuntime.Dispatch(() => ((TextBox)FriendlyControl("Editor:data.interests.0.value")).Text = "Краевые сценарии");
        SyncClick("SyncEditLabels"); SyncClick("SyncEditConfirm");
        await SyncWait(() => FriendlyControl("SyncEditStatus") is TextBlock text && text.Text?.StartsWith("Ярлыки сохранены", StringComparison.Ordinal) == true);
        bool retained = false; HeadlessRuntime.Dispatch(() => retained = ((TextBox)FriendlyControl("Editor:data.interests.0.value")).Text == "Краевые сценарии"); await Assert.That(retained).IsTrue();
        SyncClick("SyncEditSave"); await SyncWait(() => !SyncModel.LocalEditorOpen && SyncModel.HasPreview);
        bool skipped = false; HeadlessRuntime.Dispatch(() => skipped = SyncModel.Entries.Where(e => e.Entry.Entity == EntityKind.Contact && e.Entry.Field == "$entity" && e.Entry.Google?["resourceName"]?.ToString() == unrelated).All(e => !e.IsSelected));
        await Assert.That(skipped).IsTrue();
        var contacts = Directory.GetFiles(Path.Combine(IntegrationRoot, "workspace", "contacts"), "*.json");
        await Assert.That(contacts.Length).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contacts[0]))!["data"]!["interests"]![0]!["value"]!.ToString()).IsEqualTo("Краевые сценарии");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Comparison_menu_edits_then_resets_phone_in_folder_without_sending_to_Google()
    {
        var contact = await SyncedContact(); var remote = await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot, "remote.json"));
        contact.Document["data"]!["phoneNumbers"]![0]!["value"] = "Локальная правка";
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString());
        Page.ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        await OpenSyncField("phoneNumbers");
        HeadlessRuntime.Dispatch(() => ((TextBox)FriendlyControl("Editor:data.phoneNumbers.0.value")).Text = "Ручная правка из сравнения");
        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        foreach (var size in new[] { (Width: 1000, Height: 680), (Width: 820, Height: 640) })
        {
            HeadlessRuntime.Dispatch(() => { Session.Inner.MainWindow.Width = size.Width; Session.Inner.MainWindow.Height = size.Height; Avalonia.Application.Current!.RequestedThemeVariant = theme; });
            await Task.Delay(150); CaptureCheckpoint("sync-phone-" + size.Width + "-" + theme.Key);
        }
        HeadlessRuntime.Dispatch(() => { Session.Inner.MainWindow.Width = 1180; Session.Inner.MainWindow.Height = 820; Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default; });
        CaptureCheckpoint("sync-edit-phone-form"); SyncClick("SyncEditSave");
        await SyncWait(() => !SyncModel.LocalEditorOpen && SyncModel.HasPreview);
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!["data"]!["phoneNumbers"]![0]!["value"]!.ToString()).IsEqualTo("Ручная правка из сравнения");
        await OpenSyncField("phoneNumbers", reset: true); CaptureCheckpoint("sync-reset-phone-form"); SyncClick("SyncEditSave");
        await SyncWait(() => !SyncModel.LocalEditorOpen && SyncModel.HasPreview);
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot, "remote.json")))).IsEqualTo(Convert.ToHexString(remote));
        bool converged = false; HeadlessRuntime.Dispatch(() => converged = !SyncModel.Entries.Any(e => e.Entry.Field == "phoneNumbers")); await Assert.That(converged).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Comparison_starred_toggle_has_own_control_and_cancel_keeps_file_unchanged()
    {
        var contact = await SyncedContact(); contact.Document["starred"] = !contact.Document["starred"]!.GetValue<bool>();
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString()); var bytes = await File.ReadAllBytesAsync(contact.Path);
        Page.ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        await OpenSyncField("starred");
        HeadlessRuntime.Dispatch(() => { var toggle = (CheckBox)FriendlyControl("SyncEditStarred"); toggle.IsChecked = !(toggle.IsChecked == true); });
        SyncClick("SyncEditCancel"); SyncClick("SyncEditStay");
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(contact.Path))).IsEqualTo(Convert.ToHexString(bytes));
        SyncClick("SyncEditSave"); await SyncWait(() => !SyncModel.LocalEditorOpen && SyncModel.HasPreview);
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!["starred"]!.GetValue<bool>()).IsEqualTo(!contact.Document["starred"]!.GetValue<bool>());
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Every_writable_category_has_scoped_form_from_comparison()
    {
        var contact = await SyncedContact(); foreach (var field in CapabilityRegistry.WritableFields) contact.Document["data"]![field] = new JsonArray(new JsonObject());
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString());
        Page.ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        foreach (var field in CapabilityRegistry.WritableFields)
        {
            await OpenSyncField(field); bool found = false;
            HeadlessRuntime.Dispatch(() => found = FriendlyControl("SyncChangeEditor").GetVisualDescendants().OfType<Control>().Any(c => AutomationProperties.GetAutomationId(c)?.StartsWith("Editor:data." + field + ".", StringComparison.Ordinal) == true || AutomationProperties.GetAutomationId(c)?.StartsWith("DateMode:data." + field, StringComparison.Ordinal) == true));
            await Assert.That(found).IsTrue(); SyncClick("SyncEditCancel"); await SyncWait(() => !SyncModel.LocalEditorOpen);
        }
    }
}
