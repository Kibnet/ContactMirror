using System.Diagnostics;
using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ContactMirror.Core;
using ContactMirror.Desktop;
using TUnit.Core;
using TUnit.Assertions;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Large_diff_is_virtualized_responsive_and_selection_cancels_old_details()
    {
        MainWindowViewModel model = null!;
        var before = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)new JsonObject { ["value"] = "old" + i }).ToArray());
        var local = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)new JsonObject { ["value"] = i + new string('Ж', 1200) }).ToArray());
        var entry = new EntryViewModel(new SyncEntry { Key = "large-diff", EntityId = Guid.NewGuid(), Name = "Синтетический длинный контакт", Field = "phoneNumbers", Kind = ChangeKind.Upload, Before = before, Local = local, Google = before.DeepClone() });
        HeadlessRuntime.Dispatch(() =>
        {
            model = (MainWindowViewModel)Session.Inner.MainWindow.DataContext!;
            model.ReplaceServices(new ContactMirror.AppAutomation.TestHost.TestAccountConnector(), new ContactMirror.AppAutomation.TestHost.TestSyncCoordinator { OverrideEntries = [entry.Entry, UxEntry("small", Guid.NewGuid(), "phoneNumbers")] });
        });
        PreparePreview();
        var watch = Stopwatch.StartNew(); var count = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(5) && count == 0)
        {
            await Task.Delay(20); HeadlessRuntime.Dispatch(() => count = model.DiffRows.Count);
        }
        await Assert.That(count).IsEqualTo(2000);
        CaptureCheckpoint("contact-edit-large-diff");
        var ping = Stopwatch.StartNew(); HeadlessRuntime.Dispatch(() => model.ShowSettings = true); ping.Stop();
        await Assert.That(ping.ElapsedMilliseconds).IsLessThan(2000);
        var realized = 0;
        HeadlessRuntime.Dispatch(() =>
        {
            var list = Session.Inner.MainWindow.GetVisualDescendants().OfType<ListBox>().Single(x => AutomationProperties.GetAutomationId(x) == "DiffRows");
            realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Session.Inner.MainWindow.Width = 1000; Session.Inner.MainWindow.Height = 680;
        });
        await Assert.That(realized).IsGreaterThan(0); await Assert.That(realized).IsLessThan(60);
        CaptureCheckpoint("contact-edit-large-settings-compact");
        HeadlessRuntime.Dispatch(() => model.ClosePanelCommand.Execute(null));
        await Task.Delay(120);
        double detailsHeight = 0;
        HeadlessRuntime.Dispatch(() => detailsHeight = Session.Inner.MainWindow.GetVisualDescendants().OfType<ScrollViewer>().Single(x => AutomationProperties.GetAutomationId(x) == "ContactDetailsScroll").Bounds.Height);
        await Assert.That(detailsHeight).IsGreaterThan(100);
        HeadlessRuntime.Dispatch(() =>
        {
            model.ShowSettings = false; model.SelectedEntry = null; model.SelectedEntry = model.Entries[0];
            model.SelectedEntry = model.Entries[1];
        });
        watch.Restart(); count = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(5) && count == 0)
        {
            await Task.Delay(20); HeadlessRuntime.Dispatch(() => count = model.DiffRows.Count);
        }
        await Assert.That(count).IsGreaterThan(0);
        await Assert.That(count).IsLessThan(10);
        HeadlessRuntime.Dispatch(() =>
        {
            var details = Session.Inner.MainWindow.GetVisualDescendants().OfType<ScrollViewer>().Single(x => AutomationProperties.GetAutomationId(x) == "ContactDetailsScroll");
            details.Offset = new Avalonia.Vector(0, Math.Max(0, details.Extent.Height - details.Viewport.Height - 80));
        });
        await Task.Delay(200);
        CaptureCheckpoint("contact-edit-compact-values");
        Console.WriteLine($"Diff rows=2000, realized={realized}, UI settings ping={ping.ElapsedMilliseconds}ms; no display rows truncated.");
    }
}
