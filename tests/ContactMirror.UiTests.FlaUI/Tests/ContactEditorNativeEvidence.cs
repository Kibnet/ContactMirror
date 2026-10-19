using FlaUI.Core.AutomationElements;
using AppAutomation.FlaUI.Automation;
using AppAutomation.TUnit;
using AppAutomation.Abstractions;
using TUnit.Assertions;
using System.Runtime.InteropServices;

namespace ContactMirror.UiTests.FlaUI.Tests;

public sealed partial class IntegratedDemoFlaUiTests
{
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr EditorForegroundWindow();

    private async Task ActivateEditorWindowAsync()
    {
        var window=Session.Inner.MainWindow;
        window.SetForeground();
        if(EditorForegroundWindow()!=window.Properties.NativeWindowHandle.Value)
        {
            // A background runner may be denied activation. Click only the raised test caption.
            var caption=window.TitleBar ?? throw new InvalidOperationException("Native test window caption is unavailable.");
            NativeWindowCapture.RaiseForInput(window.Properties.NativeWindowHandle.Value,true);
            try { caption.Click(); }
            finally { NativeWindowCapture.RaiseForInput(window.Properties.NativeWindowHandle.Value,false); }
        }
        for(var n=0;n<100;n++)
        {
            if(EditorForegroundWindow()==window.Properties.NativeWindowHandle.Value)return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Native keyboard input requires the test window in the foreground.");
    }

    private async Task<AutomationElement?> DecisionKeyboardFocusAsync()
    {
        AutomationElement? focus=null;
        var window=Session.Inner.MainWindow;
        for(var n=0;n<100;n++)
        {
            focus=window.Automation.FocusedElement();
            if(focus?.Properties.ProcessId.ValueOrDefault==window.Properties.ProcessId.Value
                && focus.AutomationId is "ContactDecisionSave" or "ContactDecisionDiscard" or "ContactDecisionStay")return focus;
            await Task.Delay(50);
        }
        Console.WriteLine($"Native focus wait expired: process={focus?.Properties.ProcessId.ValueOrDefault}, id={focus?.AutomationId}");
        return null;
    }

    protected override async Task InspectEditorNativeDirtyLifecycleAsync()
    {
        if(_editorWindowVideo is null)NativeWindowCapture.Resize(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value,1180,820);
        await ActivateEditorWindowAsync();
        var keyboard=global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT;
        global::FlaUI.Core.Input.Keyboard.Press(keyboard);
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.F4);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.F4);
        global::FlaUI.Core.Input.Keyboard.Release(keyboard);
        await FriendlyNativeWait("ContactDecisionStay");
        for(var n=0;n<5;n++)
        {
            await ActivateEditorWindowAsync();
            global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            await Task.Delay(80);
            var focus=await DecisionKeyboardFocusAsync();
            Console.WriteLine($"Native TAB {n}: foreground={EditorForegroundWindow()}, target={Session.Inner.MainWindow.Properties.NativeWindowHandle.Value}, focusedProcess={focus?.Properties.ProcessId.ValueOrDefault}, id={focus?.AutomationId}");
            CaptureCheckpoint("editor-native-dialog-tab-"+n);
            await Assert.That(focus?.AutomationId is "ContactDecisionSave" or "ContactDecisionDiscard" or "ContactDecisionStay").IsTrue();
        }
        CaptureCheckpoint("editor-native-close-dirty-focus");
        await ActivateEditorWindowAsync();
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_S);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_S);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        await Task.Delay(100);await Assert.That(Page.ContactDecisionStay.IsEnabled).IsTrue();
        await ActivateEditorWindowAsync();
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        for(var n=0;n<100;n++){if(Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("ContactDecisionStay")) is null)break;await Task.Delay(50);}
        await Assert.That(Page.ContactSurname.Text).IsEqualTo("Редакторова");
        foreach(var configuration in new[]{("ContactColumnsButton","editor-native-columns"),("ContactFiltersButton","editor-native-filters")})
        {
            Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId(configuration.Item1))!.AsButton().Invoke();
            await Task.Delay(150);CaptureCheckpoint(configuration.Item2);
            Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("ContactConfigurationClose"))!.AsButton().Invoke();
            await Task.Delay(100);
        }
    }
    protected override async Task SelectEditorPhotoAsync(string file,bool cancel)
    {
        await EditorNativeClick("PhotoMenuButton");await EditorNativeClick("PhotoChangeButton");
        global::FlaUI.Core.AutomationElements.Window? dialog=null;
        for(var n=0;n<50;n++){dialog=Session.Inner.MainWindow.ModalWindows.FirstOrDefault();if(dialog is not null)break;await Task.Delay(100);}
        if(dialog is null)throw new InvalidOperationException("Native photo picker did not open.");
        dialog.SetForeground();
        if(cancel){global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);}
        else
        {
            if(!File.Exists(file))throw new FileNotFoundException("Synthetic picker input must exist.",file);
            var edit=dialog.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("1148"))?.FindFirstDescendant(Session.Inner.ConditionFactory.ByControlType(global::FlaUI.Core.Definitions.ControlType.Edit))?.AsTextBox()
                ?? dialog.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("1001"))?.AsTextBox()
                ?? throw new InvalidOperationException("Native file name input unavailable.");
            // Address the native filename control directly; injected keystrokes can lose
            // characters while the shell finishes opening the dialog.
            edit.Text = file;
            for(var n=0;n<100&&edit.Text!=file;n++)await Task.Delay(50);
            await Assert.That(edit.Text).IsEqualTo(file);
            Console.WriteLine("Native picker expected: "+file+"; filename: "+edit.Text);
            edit.Focus();
            for(var n=0;n<100&&!edit.Properties.HasKeyboardFocus.ValueOrDefault;n++)await Task.Delay(50);
            await Assert.That(edit.Properties.HasKeyboardFocus.ValueOrDefault).IsTrue();
            global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        }
        for(var n=0;n<50&&Session.Inner.MainWindow.ModalWindows.Length>0;n++)await Task.Delay(100);
        if(Session.Inner.MainWindow.ModalWindows.FirstOrDefault() is { } remaining)
        {
            var evidence=Path.Combine(RepositoryRoot(),"chat-artifacts","editor","picker-unclosed.png");
            NativeWindowCapture.Save(remaining.Properties.NativeWindowHandle.Value,evidence);
            Console.WriteLine("Unclosed native picker: "+evidence);
        }
        await Assert.That(Session.Inner.MainWindow.ModalWindows.Length).IsEqualTo(0);
        if(!cancel)
        {
            for(var n=0;n<150;n++)
            {
                if(Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("PhotoViewButton")) is not null)break;
                await Task.Delay(100);
            }
            CaptureCheckpoint("editor-native-picker-selected");
            Console.WriteLine("Native picker status: "+Page.ContactsStatus.Name);
        }
    }
    protected override async Task AddEditorRelationAsync(string person,string customType)
    {
        await EditorNativeClick("ContactAddFieldButton");
        (await EditorNativeWait("ContactFieldSearch")).AsTextBox().Text = "relations";
        await EditorNativeClick("FindResult:data.relations.0");
        await EditorNativeWait("Editor:data.relations.0.person");
        await EditorNativeClick("ContactFindButton");
        (await EditorNativeWait("ContactFieldSearch")).AsTextBox().Text = "data.relations.0.person";
        await EditorNativeClick("FindResult:data.relations.0.person");
        (await EditorNativeWait("Editor:data.relations.0.person")).AsTextBox().Text = person;
        var typeChoice=(await EditorNativeWait("Editor:data.relations.0.type")).AsComboBox();typeChoice.Select("Свой вариант…");typeChoice.Collapse();
        (await EditorNativeWait("CustomTypeInput")).AsTextBox().Text = customType;
        await EditorNativeClick("CustomTypeApply");
        var selected=(await EditorNativeWait("Editor:data.relations.0.type")).AsComboBox().SelectedItem?.Text;
        await Assert.That(selected).IsEqualTo(customType);
    }
    protected override async Task RemoveEditorPhotoAsync() {await EditorNativeClick("PhotoMenuButton");await EditorNativeClick("PhotoRemoveButton");}
    protected override async Task ReloadEditorCardAsync() {await EditorNativeClick("ContactActionsButton");await EditorNativeClick("ContactReloadButton");}
    protected override async Task DeleteEditorContactAsync() {await EditorNativeClick("ContactActionsButton");await EditorNativeClick("ContactDeleteButton");}
    private async Task EditorNativeClick(string id) => (await EditorNativeWait(id)).AsButton().Invoke();
    private async Task<AutomationElement> EditorNativeWait(string id)
    {
        for(var attempt=0;attempt<150;attempt++)
        {
            var control=Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId(id));
            if(control is {IsEnabled:true}&&!control.Properties.IsOffscreen.ValueOrDefault)return control;
            await Task.Delay(50);
        }
        throw new TimeoutException("Visible native contact editor control did not become ready: "+id);
    }
    private NativeWindowVideo? _editorWindowVideo;
    protected override async Task StartEditorVideoAsync()
    {
        var phase=Environment.GetEnvironmentVariable("CONTACTMIRROR_EDITOR_VIDEO");
        if(phase is not ("before" or "after"))return;
        NativeWindowCapture.PositionForRecording(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value);
        var directory=Path.Combine(Environment.GetEnvironmentVariable("CONTACTMIRROR_EDITOR_VIDEO_DIRECTORY")??Path.Combine(RepositoryRoot(),"chat-artifacts","editor"),$"editor-{phase}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
        var output=Path.Combine(directory,"contact-editor.mp4");
        _editorWindowVideo=await NativeWindowVideo.StartAsync(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value,output,5);
        Console.WriteLine($"Editor exact-HWND recording ready: {_editorWindowVideo.Width}x{_editorWindowVideo.Height}; first frame: {_editorWindowVideo.FirstFramePath}");
    }
    protected override async Task FinishEditorVideoAsync()
    {
        if(_editorWindowVideo is not { } recording)return;
        _editorWindowVideo=null;
        Console.WriteLine("Editor exact-HWND native video: "+await recording.FinishAsync());
    }
}
