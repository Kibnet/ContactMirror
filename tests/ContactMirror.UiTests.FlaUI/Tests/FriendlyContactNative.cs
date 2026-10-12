using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using AppAutomation.TUnit;

namespace ContactMirror.UiTests.FlaUI.Tests;

public sealed partial class IntegratedDemoFlaUiTests
{
    private async Task FriendlyNativeFind(string path)
    {
        (await FriendlyNativeWait("ContactFindButton")).AsButton().Invoke();
        (await FriendlyNativeWait("ContactFieldSearch")).AsTextBox().Enter(path);
        await Task.Delay(100); (await FriendlyNativeWait("FindResult:" + path)).AsButton().Invoke(); await Task.Delay(150);
    }
    protected override async Task ConfigureFriendlyFieldsAsync()
    {
        Session.Inner.MainWindow.SetForeground();
        var type = Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("Editor:data.phoneNumbers.0.type"))!;
        if(type.ControlType==ControlType.ComboBox){var choice=type.AsComboBox();choice.Select("Рабочий мобильный");choice.Collapse();}
        else type.AsTextBox().Enter("workMobile");
        await Task.Delay(150);
        if (Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("ContactFindButton")) is not null) await FriendlyNativeFind("data.birthdays.0.date.day");
        var day = Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("Editor:data.birthdays.0.date.day"))!;
        day.AsTextBox().Enter("29");
        var month = Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("Editor:data.birthdays.0.date.month"))!;
        if(month.ControlType==ControlType.ComboBox){var choice=month.AsComboBox();choice.Select("Февраль");choice.Collapse();}
        else month.AsTextBox().Enter("2");
        await Task.Delay(300);
    }
    private async Task<AutomationElement> FriendlyNativeWait(string id)
    {
        for (var i = 0; i < 120; i++) { var element = Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId(id)); if (element is not null && element.IsEnabled) return element; await Task.Delay(50); }
        throw new TimeoutException("Native contact control not ready: " + id);
    }
    protected override async Task RemoveFriendlyPhotoAsync()
    {
        var window = Session.Inner.MainWindow;
        if (window.FindFirstDescendant(window.ConditionFactory.ByAutomationId("PhotoRemoveButton")) is null)
            window.FindFirstDescendant(window.ConditionFactory.ByAutomationId("PhotoMenuButton"))!.AsButton().Invoke();
        (await FriendlyNativeWait("PhotoRemoveButton")).AsButton().Invoke();
    }
    protected override async Task ReloadFriendlyCardAsync()
    {
        var window = Session.Inner.MainWindow;
        if (window.FindFirstDescendant(window.ConditionFactory.ByAutomationId("ContactReloadButton")) is null)
            window.FindFirstDescendant(window.ConditionFactory.ByAutomationId("ContactActionsButton"))!.AsButton().Invoke();
        (await FriendlyNativeWait("ContactReloadButton")).AsButton().Invoke();
    }
}
