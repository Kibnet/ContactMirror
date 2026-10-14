using System.Text.Json.Nodes;
using AppAutomation.TUnit;
using AppAutomation.Abstractions;
using FlaUI.Core.AutomationElements;
using TUnit.Core;
using TUnit.Assertions;
using System.Runtime.InteropServices;

namespace ContactMirror.UiTests.FlaUI.Tests;

public sealed partial class IntegratedDemoFlaUiTests
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    private AutomationElement? NativeMenuItem(string id)
    {
        var windows = new List<IntPtr>(); var thread = GetWindowThreadProcessId(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, out _);
        EnumThreadWindows(thread, (window, _) => { if (IsWindowVisible(window)) windows.Add(window); return true; }, IntPtr.Zero);
        foreach (var window in windows)
        {
            var root = Session.Inner.MainWindow.Automation.FromHandle(window);
            var item = root.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId(id));
            if (item is not null && !item.Properties.IsOffscreen.ValueOrDefault && item.Properties.BoundingRectangle.ValueOrDefault.Width > 0) return item;
        }
        return null;
    }
    private async Task<(string Path, JsonObject Document)> PrepareSyncEditingNative()
    {
        Page.WaitUntilNameContains(p => p.AccountLabel, "Демонстрационный аккаунт").ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        Page.ClickButton(p => p.ApplyButton).WaitUntilNameContains(p => p.StatusText, "Синхронизировано", timeoutMs: 15000);
        var path = Directory.GetFiles(Path.Combine(IntegrationRoot, "workspace", "contacts"), "*.json").Single(path => JsonNode.Parse(File.ReadAllText(path))!["google"]!["resourceName"]!.ToString() == "people/demo2");
        return (path, JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject());
    }
    private static string SyncEvidence(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "ContactMirror.sln"))) root = root.Parent;
        return Path.Combine(root!.FullName, "chat-artifacts", "ui", "sync-change-editing", file);
    }
    [Test, Category("SyncEditing"), NotInParallel("DesktopUi")]
    public async Task Native_scrolled_structural_edit_keeps_open_details_and_records_window()
    {
        var contact = await PrepareSyncEditingNative();
        contact.Document["data"]!["names"]![0]!["givenName"] = "Проверка прокрутки";
        contact.Document["data"]!["organizations"] = new JsonArray(new JsonObject { ["name"] = "Организация для проверки", ["title"] = "Руководитель" });
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString());
        (await FriendlyNativeWait("ContactsNavigationButton")).AsButton().Invoke();
        (await FriendlyNativeWait("ContactsSearch")).AsTextBox().Enter("Проверка прокрутки"); await Task.Delay(200);
        (await FriendlyNativeWait("ContactOpenButton")).AsButton().Invoke();
        (await FriendlyNativeWait("ContactEditButton")).AsButton().Invoke();
        var phase = Environment.GetEnvironmentVariable("CONTACTMIRROR_SCROLL_BASELINE") == "1" ? "before" : "after";
        var output = SyncEvidence("scroll-" + phase + "-" + DateTime.UtcNow.ToString("HHmmss") + ".mp4");
        await using var video = await NativeWindowVideo.StartAsync(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, output);
        await FriendlyNativeFind("data.organizations.0.title"); await Task.Delay(900);
        var beforePercent = (await FriendlyNativeWait("ContactFieldScroll")).Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
        CaptureCheckpoint("scroll-" + phase + "-before-add");
        (await FriendlyNativeWait("AddField:organizations")).AsButton().Invoke(); await Task.Delay(1500);
        CaptureCheckpoint("scroll-" + phase + "-after-add");
        var details = await FriendlyNativeWait("Details:data.organizations.0");
        var expanded = details.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString() == "Expanded";
        var afterPercent = (await FriendlyNativeWait("ContactFieldScroll")).Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
        Console.WriteLine($"Scroll percentage before={beforePercent}, after={afterPercent}");
        Console.WriteLine("Scroll evidence: " + await video.FinishAsync());
        (await FriendlyNativeWait("ContactCancelButton")).AsButton().Invoke();
        await Assert.That(expanded).IsTrue();
        await Assert.That(beforePercent).IsGreaterThan(0); await Assert.That(afterPercent).IsGreaterThan(0);
    }
    [Test, Category("SyncEditing"), NotInParallel("DesktopUi")]
    public async Task Native_comparison_edit_and_reset_save_only_to_folder_and_record_window()
    {
        var contact = await PrepareSyncEditingNative(); var before = await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot, "remote.json"));
        contact.Document["data"]!["phoneNumbers"]![0]!["value"] = "Локальная правка";
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString());
        Page.ClickButton(p => p.CheckChangesButton).WaitUntilNameContains(p => p.StatusText, "Сравнение завершено");
        var output = SyncEvidence("edit-reset-after-" + DateTime.UtcNow.ToString("HHmmss") + ".mp4");
        await using var video = await NativeWindowVideo.StartAsync(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value, output);
        async Task Open(bool reset)
        {
            Session.Inner.MainWindow.SetForeground();
            var button = Session.Inner.MainWindow.FindAllDescendants().Single(e => e.Properties.AutomationId.ValueOrDefault?.StartsWith("SyncChangeActions:", StringComparison.Ordinal) == true); button.AsButton().Invoke();
            var id = reset ? "SyncChangeReset" : "SyncChangeEdit"; AutomationElement? menuItem = null;
            await Task.Delay(120);
            for (var n = 0; n < 60 && menuItem is null; n++)
            {
                menuItem = NativeMenuItem(id);
                if (menuItem is null) await Task.Delay(50);
            }
            CaptureCheckpoint(reset ? "native-sync-reset-menu" : "native-sync-edit-menu");
            (menuItem ?? throw new TimeoutException(id)).Focus();
            global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            await FriendlyNativeWait("SyncEditSave");
        }
        await Open(false); (await FriendlyNativeWait("Editor:data.phoneNumbers.0.value")).AsTextBox().Enter("Исправлено из сравнения");
        CaptureCheckpoint("native-sync-edit-form"); await Task.Delay(600); (await FriendlyNativeWait("SyncEditSave")).AsButton().Invoke();
        Page.WaitUntilNameContains(p => p.StatusText, "Сохранено в папку"); await Task.Delay(800);
        await Open(true); CaptureCheckpoint("native-sync-reset-form"); await Task.Delay(700); (await FriendlyNativeWait("SyncEditSave")).AsButton().Invoke();
        Page.WaitUntilNameContains(p => p.StatusText, "совпадает с Google"); await Task.Delay(600);
        Console.WriteLine("Sync editing evidence: " + await video.FinishAsync());
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(IntegrationRoot, "remote.json")))).IsEqualTo(Convert.ToHexString(before));
    }
}
