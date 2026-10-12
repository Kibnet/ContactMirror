using System.Text.Json.Nodes;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Authoring.Tests;

public abstract partial class RealPipelineScenariosBase<TSession> where TSession:class,IUiTestSession
{
    protected virtual Task StartEditorVideoAsync()=>Task.CompletedTask;
    protected virtual Task FinishEditorVideoAsync()=>Task.CompletedTask;
    protected virtual Task InspectEditorNativeDirtyLifecycleAsync()=>Task.CompletedTask;
    protected virtual Task SelectEditorPhotoAsync(string file,bool cancel)=>throw new NotSupportedException("Runtime must implement picker boundary.");
    private async Task WaitForEditorAsync(Func<bool> ready)
    {
        for(var n=0;n<150;n++)
        {
            try { if(ready())return; }
            catch(Exception error) when(error.GetType().Name=="UiControlResolutionException") { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The contact editor did not reach the expected visible state.");
    }
    [Test,NotInParallel(DesktopUiConstraint)]
    public async Task New_contact_rare_fields_photo_picker_cancel_replace_view_and_local_delete_are_complete()
    {
        Console.WriteLine("Editor CRUD: open and create.");
        Page.WaitUntilNameContains(static p=>p.AccountLabel,"Демонстрационный аккаунт").ClickButton(static p=>p.ContactsNavigationButton).ClickButton(static p=>p.ContactCreateButton)
            .ClickButton(static p=>p.AddContactName).EnterText(static p=>p.ContactGivenName,"Новый тестовый").EnterText(static p=>p.ContactSurname,"Редакторов");
        Console.WriteLine("Editor CRUD: common fields entered; show all.");
        Page.SetChecked(static p=>p.ContactShowAllFields,true);
        Console.WriteLine("Editor CRUD: search rare category.");
        Page.EnterText(static p=>p.ContactFieldSearch,"relations")
            .ClickButton(static p=>p.AddContactRelation).EnterText(static p=>p.ContactRelationPerson,"Тестовый коллега").EnterText(static p=>p.ContactRelationType,"custom-colleague");
        Console.WriteLine("Editor CRUD: rare fields entered; picker.");
        var photo=Path.Combine(IntegrationRoot,"selected-photo.png");byte[] bytes;
        using(var bitmap=new SkiaSharp.SKBitmap(40,40)){bitmap.Erase(SkiaSharp.SKColors.Teal);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);bytes=encoded.ToArray();}
        await File.WriteAllBytesAsync(photo,bytes);
        await SelectEditorPhotoAsync(photo,cancel:true);
        await Assert.That(Directory.GetFiles(Path.Combine(IntegrationRoot,"workspace"),"*.contact.json",SearchOption.AllDirectories).Length).IsEqualTo(0);
        await SelectEditorPhotoAsync(photo,cancel:false);
        Console.WriteLine("Editor CRUD: photo selected; view and save.");
        Page.ClickButton(static p=>p.PhotoViewButton);
        await WaitForEditorAsync(()=>Page.ContactPhotoClose.IsEnabled);
        Console.WriteLine("Editor CRUD: photo viewer ready.");
        Page.ClickButton(static p=>p.ContactPhotoClose);
        Console.WriteLine("Editor CRUD: photo viewer closed.");
        Page.ClickButton(static p=>p.ContactSaveButton);
        Console.WriteLine("Editor CRUD: save invoked.");
        Page.WaitUntilNameContains(static p=>p.ContactsStatus,"Сохранено в папку",timeoutMs:15000);
        Console.WriteLine("Editor CRUD: saved.");
        var file=Directory.GetFiles(Path.Combine(IntegrationRoot,"workspace","contacts"),"*.contact.json").Single();var saved=JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        await Assert.That(saved["data"]!["relations"]![0]!["person"]!.ToString()).IsEqualTo("Тестовый коллега");await Assert.That(saved["data"]!["relations"]![0]!["type"]!.ToString()).IsEqualTo("custom-colleague");
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot,"workspace",saved["photo"]!.ToString()))).IsEquivalentTo(bytes);
        // A second selection must read the new image bytes, not reuse the same source path.
        using(var bitmap=new SkiaSharp.SKBitmap(40,40)){bitmap.Erase(SkiaSharp.SKColors.Gold);using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);bytes=encoded.ToArray();}
        await File.WriteAllBytesAsync(photo,bytes);Page.ClickButton(static p=>p.ContactEditButton);await SelectEditorPhotoAsync(photo,cancel:false);
        Page.ClickButton(static p=>p.ContactSaveButton).WaitUntilNameContains(static p=>p.ContactsStatus,"Сохранено в папку",timeoutMs:15000);
        saved=JsonNode.Parse(await File.ReadAllTextAsync(file))!;await Assert.That(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot,"workspace",saved["photo"]!.ToString()))).IsEquivalentTo(bytes);
        CaptureCheckpoint("editor-new-rare-fields-photo");
        Page.ClickButton(static p=>p.ContactDeleteButton).ClickButton(static p=>p.ContactDeleteCancel);await Assert.That(File.Exists(file)).IsTrue();
        Page.ClickButton(static p=>p.ContactDeleteButton).ClickButton(static p=>p.ContactDeleteConfirm).WaitUntilNameContains(static p=>p.ContactsStatus,"Контакт перемещён",timeoutMs:15000);await Assert.That(File.Exists(file)).IsFalse();
        await Assert.That(Directory.GetFiles(Path.Combine(IntegrationRoot,"workspace",".contactmirror","editor-trash"),"before.photo",SearchOption.AllDirectories).Length).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(IntegrationRoot,"remote.json"))).IsFalse();CaptureCheckpoint("editor-local-delete-empty");
    }
    [Test,NotInParallel(DesktopUiConstraint)]
    public async Task Contact_editor_saves_card_locally_reopens_and_sends_only_after_manual_check_apply()
    {
        Page.WaitUntilNameContains(static p=>p.AccountLabel,"Демонстрационный аккаунт")
            .ClickButton(static p=>p.CheckChangesButton).WaitUntilNameContains(static p=>p.StatusText,"Сравнение завершено",timeoutMs:30000)
            .ClickButton(static p=>p.ApplyButton).WaitUntilNameContains(static p=>p.StatusText,"Синхронизировано",timeoutMs:30000);
        var workspace=Path.Combine(IntegrationRoot,"workspace");
        var contact=Directory.GetFiles(Path.Combine(workspace,"contacts"),"*.json").Select(path=>(Path:path,Doc:JsonNode.Parse(File.ReadAllText(path))!.AsObject())).First(x=>x.Doc["photo"] is not null);
        var id=contact.Doc["id"]!.ToString(); var google=contact.Doc["google"]!.DeepClone();
        var remoteBefore=await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot,"remote.json"));
        await StartEditorVideoAsync();
        try
        {
            Page.ClickButton(static p=>p.ContactsNavigationButton);
            await WaitForEditorAsync(()=>Page.ContactsSearch.IsEnabled);
            Page.EnterText(static p=>p.ContactsSearch,id);
            await WaitForEditorAsync(()=>Page.ContactOpenButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactOpenButton);
            await WaitForEditorAsync(()=>Page.ContactEditButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactEditButton)
                .EnterText(static p=>p.ContactSurname,"Редакторова")
                .EnterText(static p=>p.ContactPhone,"+1 202 555 0187");
            CaptureCheckpoint("editor-filled-draft");
            await InspectEditorNativeDirtyLifecycleAsync();
            Page.ClickButton(static p=>p.SyncNavigationButton).ClickButton(static p=>p.ContactDecisionStay);
            await Assert.That(Page.ContactSurname.Text).IsEqualTo("Редакторова");
            Page.ClickButton(static p=>p.ContactSaveButton).WaitUntilNameContains(static p=>p.ContactsStatus,"Сохранено в папку",timeoutMs:15000);
            var saved=JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!;
            await Assert.That(saved["data"]!["names"]![0]!["familyName"]!.ToString()).IsEqualTo("Редакторова");
            await Assert.That(JsonNode.DeepEquals(google,saved["google"])).IsTrue();
            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot,"remote.json"))).IsEquivalentTo(remoteBefore);
            Page.ClickButton(static p=>p.ContactReloadButton);
            await WaitForEditorAsync(()=>Page.ContactEditButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactEditButton);
            await Assert.That(Page.ContactSurname.Text).IsEqualTo("Редакторова");
            Page.EnterText(static p=>p.ContactSurname,"Отменённая правка").ClickButton(static p=>p.SyncNavigationButton)
                .ClickButton(static p=>p.ContactDecisionDiscard);
            await WaitForEditorAsync(()=>Page.CheckChangesButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactsNavigationButton);
            await WaitForEditorAsync(()=>Page.ContactsSearch.IsEnabled);
            Page.EnterText(static p=>p.ContactsSearch,id);
            await WaitForEditorAsync(()=>Page.ContactOpenButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactOpenButton);
            await WaitForEditorAsync(()=>Page.ContactEditButton.IsEnabled);
            Page.ClickButton(static p=>p.ContactEditButton).ClickButton(static p=>p.PhotoRemoveButton).ClickButton(static p=>p.ContactSaveButton)
                .WaitUntilNameContains(static p=>p.ContactsStatus,"Сохранено в папку",timeoutMs:15000);
            await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!["photo"] is null).IsTrue();
            CaptureCheckpoint("editor-saved-card");
            Page.ClickButton(static p=>p.SyncNavigationButton).ClickButton(static p=>p.CheckChangesButton)
                .WaitUntilNameContains(static p=>p.StatusText,"Сравнение завершено",timeoutMs:30000);
            CaptureCheckpoint("editor-manual-preview");
            if(Environment.GetEnvironmentVariable("CONTACTMIRROR_EDITOR_VIDEO")=="after")await Task.Delay(1500);
            Page.ClickButton(static p=>p.ApplyButton).WaitUntilNameContains(static p=>p.StatusText,"Синхронизировано",timeoutMs:30000);
            var remote=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(IntegrationRoot,"remote.json")))!;
            var remotePerson=remote["people"]!.AsArray().Single(person=>person?["resourceName"]?.ToString()==google["resourceName"]!.ToString())!;
            await Assert.That(remotePerson["names"]![0]!["familyName"]!.GetValue<string>()).IsEqualTo("Редакторова");
            await Assert.That(remotePerson["phoneNumbers"]![0]!["value"]!.GetValue<string>()).IsEqualTo("+1 202 555 0187");
            Page.ClickButton(static p=>p.CheckChangesButton).WaitUntilNameContains(static p=>p.StatusText,"Изменений нет",timeoutMs:30000);
            CaptureCheckpoint("editor-manual-sync-result");
        }
        finally { await FinishEditorVideoAsync(); }
    }
}
