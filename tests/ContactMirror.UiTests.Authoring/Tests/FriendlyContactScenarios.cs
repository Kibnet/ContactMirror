using System.Text.Json.Nodes;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Authoring.Tests;

public abstract partial class RealPipelineScenariosBase<TSession> where TSession : class, IUiTestSession
{
    protected virtual Task ConfigureFriendlyFieldsAsync() => throw new NotSupportedException("Runtime must exercise type and partial-date controls.");
    protected virtual Task RemoveFriendlyPhotoAsync() { Page.ClickButton(static p=>p.PhotoRemoveButton); return Task.CompletedTask; }
    protected virtual Task ReloadFriendlyCardAsync() { Page.ClickButton(static p=>p.ContactReloadButton); return Task.CompletedTask; }

    [Test, NotInParallel(DesktopUiConstraint)]
    public async Task Friendly_contact_type_partial_date_photo_save_reopen_and_manual_sync()
    {
        Page.WaitUntilNameContains(static p=>p.AccountLabel,"Демонстрационный аккаунт")
            .ClickButton(static p=>p.CheckChangesButton).WaitUntilNameContains(static p=>p.StatusText,"Сравнение завершено",timeoutMs:30000)
            .ClickButton(static p=>p.ApplyButton).WaitUntilNameContains(static p=>p.StatusText,"Синхронизировано",timeoutMs:30000);
        var workspace=Path.Combine(IntegrationRoot,"workspace");
        var target=Directory.GetFiles(Path.Combine(workspace,"contacts"),"*.json")
            .Select(path=>(Path:path,Doc:JsonNode.Parse(File.ReadAllText(path))!.AsObject())).Single(x=>x.Doc["photo"] is not null);
        var id=target.Doc["id"]!.ToString();
        var remoteBefore=await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot,"remote.json"));
        await StartEditorVideoAsync();
        try
        {
            Page.ClickButton(static p=>p.ContactsNavigationButton);
            await WaitForEditorAsync(()=>Page.ContactsSearch.IsEnabled);
            Page.EnterText(static p=>p.ContactsSearch,id);
            await WaitForEditorAsync(()=>Page.ContactsCount.Name.Contains("Показано 1 из",StringComparison.Ordinal));
            await WaitForEditorAsync(()=>Page.ContactOpenButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactOpenButton);await WaitForEditorAsync(()=>Page.ContactEditButton.IsEnabled);CaptureCheckpoint("friendly-contact-view");
            await Task.Delay(1000);
            Page.ClickButton(static p=>p.ContactEditButton).EnterText(static p=>p.ContactPhone,"+1 202 555 0187");
            await ConfigureFriendlyFieldsAsync();CaptureCheckpoint("friendly-contact-type-date-edit");await Task.Delay(1000);
            await RemoveFriendlyPhotoAsync();
            Page.ClickButton(static p=>p.ContactSaveButton).WaitUntilNameContains(static p=>p.ContactsStatus,"Сохранено в папку",timeoutMs:30000);
            var saved=JsonNode.Parse(await File.ReadAllTextAsync(target.Path))!;
            await Assert.That(saved["data"]!["phoneNumbers"]![0]!["value"]!.ToString()).IsEqualTo("+1 202 555 0187");
            await Assert.That(saved["data"]!["phoneNumbers"]![0]!["type"]!.ToString()).IsEqualTo("workMobile");
            await Assert.That(saved["data"]!["birthdays"]![0]!["date"]!["month"]!.GetValue<int>()).IsEqualTo(2);
            await Assert.That(saved["data"]!["birthdays"]![0]!["date"]!["day"]!.GetValue<int>()).IsEqualTo(29);
            await Assert.That(saved["data"]!["birthdays"]![0]!["date"]!["year"]?.GetValue<int>()??0).IsEqualTo(0);
            await Assert.That(saved["photo"] is null).IsTrue();
            await Assert.That(JsonNode.DeepEquals(saved["google"],target.Doc["google"])).IsTrue();
            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot,"remote.json"))).IsEquivalentTo(remoteBefore);
            await ReloadFriendlyCardAsync();await WaitForEditorAsync(()=>Page.ContactEditButton.IsEnabled);CaptureCheckpoint("friendly-contact-saved-reopened");await Task.Delay(1000);
            Page.ClickButton(static p=>p.SyncNavigationButton).ClickButton(static p=>p.CheckChangesButton)
                .WaitUntilNameContains(static p=>p.StatusText,"Сравнение завершено",timeoutMs:30000);
            CaptureCheckpoint("friendly-contact-manual-preview");await Task.Delay(1500);
            Page.ClickButton(static p=>p.ApplyButton).WaitUntilNameContains(static p=>p.StatusText,"Синхронизировано",timeoutMs:30000);
            Page.ClickButton(static p=>p.CheckChangesButton).WaitUntilNameContains(static p=>p.StatusText,"согласованы",timeoutMs:30000);
            CaptureCheckpoint("friendly-contact-no-op");await Task.Delay(1000);
        }
        finally { await FinishEditorVideoAsync(); }
    }
}
