using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ContactMirror.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class IntegratedDemoHeadlessTests
{
    private Control FriendlyControl(string id)=>Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>().Single(c=>AutomationProperties.GetAutomationId(c)==id);
    protected override async Task ConfigureFriendlyFieldsAsync()
    {
        HeadlessRuntime.Dispatch(()=>
        {
            var type=(ComboBox)FriendlyControl("Editor:data.phoneNumbers.0.type");type.SelectedItem=((IEnumerable<ContactFieldOption>)type.ItemsSource!).Single(o=>o.Value=="workMobile");
        });
        await EditorAdapterClick("ContactFindButton");await EditorAdapterWait("ContactFieldSearch");
        HeadlessRuntime.Dispatch(()=>((TextBox)FriendlyControl("ContactFieldSearch")).Text="data.birthdays.0.date.day");
        await EditorAdapterClick("FindResult:data.birthdays.0.date.day");await EditorAdapterWait("Editor:data.birthdays.0.date.day");
        HeadlessRuntime.Dispatch(()=>
        {
            ((TextBox)FriendlyControl("Editor:data.birthdays.0.date.day")).Text="29";
            var month=(ComboBox)FriendlyControl("Editor:data.birthdays.0.date.month");month.SelectedItem=((IEnumerable<ContactFieldOption>)month.ItemsSource!).Single(o=>o.Value=="2");
        });
    }
    protected override async Task RemoveFriendlyPhotoAsync()
    {
        HeadlessRuntime.Dispatch(()=>((Button)FriendlyControl("PhotoMenuButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Task.Delay(50);HeadlessRuntime.Dispatch(()=>((Button)FriendlyControl("PhotoRemoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
    }
    protected override async Task ReloadFriendlyCardAsync()
    {
        // The saved status precedes the asynchronous catalog refresh; wait until the card accepts actions.
        await EditorAdapterWait("ContactEditButton");
        await EditorAdapterClick("ContactActionsButton");
        await EditorAdapterClick("ContactReloadButton");
    }
}
