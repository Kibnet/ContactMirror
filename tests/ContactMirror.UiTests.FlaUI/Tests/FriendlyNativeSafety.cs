using System.Text.Json.Nodes;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using FlaUI.Core.AutomationElements;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.FlaUI.Tests;

public sealed partial class IntegratedDemoFlaUiTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Friendly_native_narrow_multiple_errors_reveal_keeps_inputs_and_saves_only_after_both_fixes()
    {
        Page.WaitUntilNameContains(static p => p.AccountLabel, "Демонстрационный аккаунт")
            .ClickButton(static p => p.CheckChangesButton).WaitUntilNameContains(static p => p.StatusText, "Сравнение завершено", timeoutMs: 30000)
            .ClickButton(static p => p.ApplyButton).WaitUntilNameContains(static p => p.StatusText, "Синхронизировано", timeoutMs: 30000);
        var target = Directory.GetFiles(Path.Combine(IntegrationRoot, "workspace", "contacts"), "*.json").Select(path => (Path: path, Doc: JsonNode.Parse(File.ReadAllText(path))!)).Single(x => x.Doc["photo"] is not null);
        var before = await File.ReadAllBytesAsync(target.Path); var remote = await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot, "remote.json"));
        Page.ClickButton(static p => p.ContactsNavigationButton).EnterText(static p => p.ContactsSearch, target.Doc["id"]!.ToString()).WaitUntilNameContains(static p => p.ContactsCount,"Показано 1 из",timeoutMs:15000).ClickButton(static p => p.ContactOpenButton);
        await FriendlyNativeWait("ContactEditButton"); NativeWindowCapture.Resize(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, 820, 640); Session.Inner.MainWindow.SetForeground();
        Page.ClickButton(static p => p.ContactEditButton);
        await FriendlyNativeFind("data.birthdays.0.date.day");
        var month=(await FriendlyNativeWait("Editor:data.birthdays.0.date.month")).AsComboBox();month.Select("Февраль");month.Collapse();
        (await FriendlyNativeWait("Editor:data.birthdays.0.date.day")).AsTextBox().Enter("30");
        await FriendlyNativeFind("data.organizations.0.fullTimeEquivalentMillipercent"); (await FriendlyNativeWait("Editor:data.organizations.0.fullTimeEquivalentMillipercent")).AsTextBox().Enter("ошибка");
        (await FriendlyNativeWait("ContactSaveButton")).AsButton().Invoke(); await Task.Delay(150);
        await Assert.That(Page.ContactsStatus.Name.Contains("Ошибки в полях: 2",StringComparison.Ordinal)).IsTrue();
        await Assert.That((await FriendlyNativeWait("ContactDraftStatus")).Name.Contains("Ошибки в полях: 2",StringComparison.Ordinal)).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(target.Path)).IsEquivalentTo(before);
        (await FriendlyNativeWait("ContactErrorsButton")).AsButton().Invoke(); await Task.Delay(100); CaptureCheckpoint("friendly-narrow-multiple-errors");
        (await FriendlyNativeWait("ErrorLink:data.birthdays.0.date.day")).AsButton().Invoke(); await Task.Delay(150);
        var day = (await FriendlyNativeWait("Editor:data.birthdays.0.date.day")).AsTextBox(); await Assert.That(day.Text).IsEqualTo("30"); day.Enter("29");
        await FriendlyNativeFind("data.organizations.0.fullTimeEquivalentMillipercent"); var percent = (await FriendlyNativeWait("Editor:data.organizations.0.fullTimeEquivalentMillipercent")).AsTextBox(); await Assert.That(percent.Text).IsEqualTo("ошибка");
        await Assert.That(Page.ContactsStatus.Name.Contains("Ошибки в полях: 1",StringComparison.Ordinal)).IsTrue();
        await Assert.That((await FriendlyNativeWait("ContactDraftStatus")).Name.Contains("Ошибки в полях: 1",StringComparison.Ordinal)).IsTrue();
        CaptureCheckpoint("friendly-narrow-one-error-kept"); percent.Enter("85,5"); await Task.Delay(100);
        await Assert.That(Page.ContactsStatus.Name).IsEqualTo("Ошибки исправлены. Сохраните изменения.");
        await Assert.That((await FriendlyNativeWait("ContactDraftStatus")).Name).IsEqualTo("Не сохранено");
        Page.ClickButton(static p => p.ContactSaveButton).WaitUntilNameContains(static p => p.ContactsStatus, "Сохранено в папку", timeoutMs: 30000);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(target.Path))!; await Assert.That(saved["data"]!["organizations"]![0]!["fullTimeEquivalentMillipercent"]!.GetValue<int>()).IsEqualTo(85500); await Assert.That(JsonNode.DeepEquals(saved["google"], target.Doc["google"])).IsTrue(); await Assert.That(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot, "remote.json"))).IsEquivalentTo(remote); CaptureCheckpoint("friendly-narrow-errors-fixed-saved");
    }
}
