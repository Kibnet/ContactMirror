using AppAutomation.Avalonia.Headless.Session;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ContactMirror.Core;
using ContactMirror.Desktop;
using ContactMirror.Desktop.Contacts;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class IntegratedDemoHeadlessTests
{
    protected override async Task SelectEditorPhotoAsync(string file,bool cancel)
    {
        await EditorAdapterClick("PhotoMenuButton");
        await EditorAdapterWait("PhotoChangeButton");
        // The menu is real UI. Only the OS-owned dialog boundary uses the existing injected stream opener.
        await EditorAdapterClick("PhotoMenuCancel");
        Task<bool> picking=null!;
        HeadlessRuntime.Dispatch(()=> {picking=ContactPhotoPicker.PickAsync(((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).Contacts,()=>Task.FromResult<Stream?>(cancel?null:File.OpenRead(file)));});
        var changed=await HeadlessEditorUi.RunAsync(()=>picking);if(changed==cancel)throw new InvalidOperationException("Picker did not respect the requested accept/cancel operation.");await Task.Delay(120);
    }

    protected override async Task AddEditorRelationAsync(string person,string customType)
    {
        await EditorAdapterClick("ContactAddFieldButton");
        await EditorAdapterWait("ContactFieldSearch");Page.EnterText(static p=>p.ContactFieldSearch,"relations");
        await EditorAdapterClick("FindResult:data.relations.0");
        await EditorAdapterWait("Editor:data.relations.0.person");
        await EditorAdapterClick("ContactFindButton");
        await EditorAdapterWait("ContactFieldSearch");Page.EnterText(static p=>p.ContactFieldSearch,"data.relations.0.person");
        await EditorAdapterClick("FindResult:data.relations.0.person");
        await EditorAdapterWait("Editor:data.relations.0.person");Page.EnterText(static p=>p.ContactRelationPerson,person);
        HeadlessRuntime.Dispatch(()=>
        {
            var choice=(ComboBox)EditorAdapterControl("Editor:data.relations.0.type");
            choice.IsDropDownOpen=true;
            choice.SelectedItem=((IEnumerable<ContactFieldOption>)choice.ItemsSource!).Single(option=>option.Value=="$custom");
            choice.IsDropDownOpen=false;
        });
        await EditorAdapterWait("CustomTypeInput");
        HeadlessRuntime.Dispatch(()=>((TextBox)EditorAdapterControl("CustomTypeInput")).Text=customType);
        await EditorAdapterClick("CustomTypeApply");await EditorAdapterWait("Editor:data.relations.0.type");
        HeadlessRuntime.Dispatch(()=>
        {
            if(((ComboBox)EditorAdapterControl("Editor:data.relations.0.type")).SelectedItem is not ContactFieldOption option||option.Value!=customType)
                throw new InvalidOperationException("Confirmed custom relation type must remain the selected raw token.");
        });
    }
    protected override async Task RemoveEditorPhotoAsync() {await EditorAdapterClick("PhotoMenuButton");await EditorAdapterClick("PhotoRemoveButton");}
    protected override async Task ReloadEditorCardAsync() {await EditorAdapterClick("ContactActionsButton");await EditorAdapterClick("ContactReloadButton");}
    protected override async Task DeleteEditorContactAsync() {await EditorAdapterClick("ContactActionsButton");await EditorAdapterClick("ContactDeleteButton");}

    private Control EditorAdapterControl(string id)=>Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>().Single(control=>AutomationProperties.GetAutomationId(control)==id);
    private async Task EditorAdapterClick(string id)
    {
        await EditorAdapterWait(id);
        HeadlessRuntime.Dispatch(()=>((Button)EditorAdapterControl(id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Task.Delay(50);
    }
    private async Task EditorAdapterWait(string id)
    {
        for(var attempt=0;attempt<150;attempt++)
        {
            var ready=false;
            HeadlessRuntime.Dispatch(()=>ready=Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>()
                .Any(control=>AutomationProperties.GetAutomationId(control)==id&&control.IsEffectivelyVisible&&control.IsEnabled));
            if(ready)return;await Task.Delay(50);
        }
        throw new TimeoutException("Visible contact editor control did not become ready: "+id);
    }
}
