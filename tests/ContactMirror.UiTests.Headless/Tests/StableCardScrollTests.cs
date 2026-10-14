using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using AppAutomation.Avalonia.Headless.Session;
using TUnit.Core;
using TUnit.Assertions;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Actual_row_menu_primary_and_remove_preserve_surviving_details_and_scroll()
    {
        await PrepareRealEditor();
        HeadlessRuntime.Dispatch(() =>
        {
            var vm = UxModel().Contacts; vm.BeginEdit();
            vm.Mutate(doc => doc["data"]!["organizations"] = new JsonArray(new JsonObject { ["name"] = "Первая" }, new JsonObject { ["name"] = "Вторая", ["title"] = "Руководитель" }, new JsonObject { ["name"] = "Третья" }), true);
        });
        await Task.Delay(150);
        HeadlessRuntime.Dispatch(() =>
        {
            ((Expander)EditorControl("Details:data.organizations.1")).IsExpanded = true;
            ((Expander)EditorControl("Details:data.organizations.2")).IsExpanded = true;
            EditorControl("RowMenu:organizations:0").BringIntoView();
            ((Button)EditorControl("RowMenu:organizations:0")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        await Task.Delay(80);
        HeadlessRuntime.Dispatch(() => ((Button)EditorControl("SetPrimary:data.organizations.0")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Task.Delay(180);
        bool retained = false;
        HeadlessRuntime.Dispatch(() =>
        {
            retained = ((Expander)EditorControl("Details:data.organizations.1")).IsExpanded && ((Expander)EditorControl("Details:data.organizations.2")).IsExpanded;
            ((Button)EditorControl("RowMenu:organizations:0")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        await Assert.That(retained).IsTrue();
        await Task.Delay(80);
        HeadlessRuntime.Dispatch(() => ((Button)EditorControl("RemoveRow:organizations:0")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Task.Delay(200);
        HeadlessRuntime.Dispatch(() => retained = ((Expander)EditorControl("Details:data.organizations.0")).IsExpanded && ((Expander)EditorControl("Details:data.organizations.1")).IsExpanded
            && ((TextBox)EditorControl("Editor:data.organizations.0.title")).Text == "Руководитель" && ((ScrollViewer)EditorControl("ContactFieldScroll")).Offset.Y > 0);
        CaptureCheckpoint("actual-row-menu-keeps-surviving-details");
        await Assert.That(retained).IsTrue();
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Removing_preceding_row_keeps_focus_caret_and_expansion_on_same_record()
    {
        await PrepareRealEditor();
        HeadlessRuntime.Dispatch(() =>
        {
            var vm = UxModel().Contacts; vm.BeginEdit();
            vm.Mutate(doc => doc["data"]!["organizations"] = new JsonArray(new JsonObject { ["name"] = "Первая" }, new JsonObject { ["name"] = "Вторая", ["title"] = "Руководитель" }, new JsonObject { ["name"] = "Третья" }), true);
        });
        await Task.Delay(150);
        HeadlessRuntime.Dispatch(() =>
        {
            ((Expander)EditorControl("Details:data.organizations.1")).IsExpanded = true;
            var input = (TextBox)EditorControl("Editor:data.organizations.1.title"); input.BringIntoView(); input.Focus(); input.SelectionStart = 2; input.SelectionEnd = 5;
        });
        await Task.Delay(100);
        HeadlessRuntime.Dispatch(() => UxModel().Contacts.Mutate(doc => doc["data"]!["organizations"]!.AsArray().RemoveAt(0), true));
        await Task.Delay(200);
        bool correct = false;
        HeadlessRuntime.Dispatch(() =>
        {
            var input = (TextBox)EditorControl("Editor:data.organizations.0.title");
            correct = input.Text == "Руководитель" && input.IsKeyboardFocusWithin && input.SelectionStart == 2 && input.SelectionEnd == 5 && ((Expander)EditorControl("Details:data.organizations.0")).IsExpanded;
        });
        await Assert.That(correct).IsTrue();
    }
    [Test, NotInParallel("DesktopUi")]
    public async Task Structural_card_change_keeps_scrolled_section_and_expanded_details()
    {
        await PrepareRealEditor();
        HeadlessRuntime.Dispatch(() =>
        {
            var model = UxModel().Contacts;
            model.BeginEdit();
            model.Mutate(doc => doc["data"]!["organizations"] = new JsonArray(new JsonObject { ["name"] = "Проверка прокрутки", ["title"] = "Руководитель" }), true);
        });
        await Task.Delay(150);
        double before = 0;
        HeadlessRuntime.Dispatch(() =>
        {
            var details = (Expander)EditorControl("Details:data.organizations.0");
            details.IsExpanded = true;
            details.BringIntoView();
        });
        await Task.Delay(150);
        HeadlessRuntime.Dispatch(() => before = ((ScrollViewer)EditorControl("ContactFieldScroll")).Offset.Y);
        await Assert.That(before).IsGreaterThan(0);
        CaptureCheckpoint("scroll-before-structure");
        HeadlessRuntime.Dispatch(() => ((Button)EditorControl("AddField:organizations")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await Task.Delay(200);
        double after = 0; bool expanded = false;
        HeadlessRuntime.Dispatch(() =>
        {
            after = ((ScrollViewer)EditorControl("ContactFieldScroll")).Offset.Y;
            expanded = ((Expander)EditorControl("Details:data.organizations.0")).IsExpanded;
        });
        CaptureCheckpoint("scroll-after-structure");
        await Assert.That(after).IsGreaterThan(0);
        await Assert.That(after).IsGreaterThanOrEqualTo(before - 8);
        await Assert.That(expanded).IsTrue();
    }
}
