using FlaUI.Core.AutomationElements;
using AppAutomation.FlaUI.Automation;
using AppAutomation.TUnit;
using AppAutomation.Abstractions;
using TUnit.Assertions;

namespace ContactMirror.UiTests.FlaUI.Tests;

public sealed partial class IntegratedDemoFlaUiTests
{
    protected override async Task InspectEditorNativeDirtyLifecycleAsync()
    {
        var keyboard=global::FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT;
        global::FlaUI.Core.Input.Keyboard.Press(keyboard);
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.F4);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.F4);
        global::FlaUI.Core.Input.Keyboard.Release(keyboard);
        for(var n=0;n<100;n++)
        {
            if(Session.Inner.MainWindow.FindFirstDescendant(Session.Inner.ConditionFactory.ByAutomationId("ContactDecisionStay")) is not null)break;
            await Task.Delay(50);
        }
        for(var n=0;n<5;n++)
        {
            global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            await Task.Delay(80);
            var focus=Session.Inner.MainWindow.FindAllDescendants().FirstOrDefault(element=>element.Properties.HasKeyboardFocus.ValueOrDefault);
            await Assert.That(focus?.AutomationId is "ContactDecisionSave" or "ContactDecisionDiscard" or "ContactDecisionStay").IsTrue();
        }
        CaptureCheckpoint("editor-native-close-dirty-focus");
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_S);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_S);
        global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        await Task.Delay(100);await Assert.That(Page.ContactDecisionStay.IsEnabled).IsTrue();
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
        Page.ClickButton(static p=>p.PhotoChangeButton);
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
            edit.Focus();global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
            global::FlaUI.Core.Input.Keyboard.Press(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
            global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
            global::FlaUI.Core.Input.Keyboard.Release(global::FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
            global::FlaUI.Core.Input.Keyboard.Type(file);
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
    private Task<string>? _editorVideoError;
    private Task<string>? _editorVideoOutput;
    protected override async Task StartEditorVideoAsync()
    {
        if(Environment.GetEnvironmentVariable("CONTACTMIRROR_EDITOR_VIDEO")!="after")return;
        NativeWindowCapture.PositionForRecording(Session.Inner.MainWindow.Properties.NativeWindowHandle.Value);
        var output=Path.Combine(Environment.GetEnvironmentVariable("CONTACTMIRROR_EDITOR_VIDEO_DIRECTORY")??Path.Combine(RepositoryRoot(),"chat-artifacts","editor"),$"editor-after-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.mp4");
        var start=new System.Diagnostics.ProcessStartInfo("pwsh") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var argument in new[]{"-NoProfile","-File",@"C:\Users\Kibnet\.codex\skills\record-app-screen\scripts\record_app_window.ps1","-ProcessName","ContactMirror.TestHost.exe","-Output",output,"-DurationSeconds","120","-Fps","15"})start.ArgumentList.Add(argument);
        _video=System.Diagnostics.Process.Start(start)??throw new InvalidOperationException("Editor recorder did not start.");
        _editorVideoError=_video.StandardError.ReadToEndAsync();
        _editorVideoOutput=_video.StandardOutput.ReadToEndAsync();
        try
        {
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            while(!File.Exists(output)||new FileInfo(output).Length<=4096)
            {
                if(_video.HasExited||deadline.Elapsed>TimeSpan.FromSeconds(60))throw new InvalidOperationException("Editor recording did not produce its first frames within 60 seconds.");
                await Task.Delay(100);
            }
            Console.WriteLine("Editor automated native video: "+output);
        }
        catch
        {
            if(!_video.HasExited)_video.Kill(entireProcessTree:true);
            await _video.WaitForExitAsync();_video.Dispose();_video=null;
            throw;
        }
    }
    protected override async Task FinishEditorVideoAsync()
    {
        if(_video is null)return;
        try
        {
            await _video.WaitForExitAsync();
            var stderr=await _editorVideoError!;var stdout=await _editorVideoOutput!;
            if(_video.ExitCode!=0)throw new InvalidOperationException("Editor video recording failed: "+stderr+stdout);
        }
        finally{_video.Dispose();_video=null;}
    }
}
