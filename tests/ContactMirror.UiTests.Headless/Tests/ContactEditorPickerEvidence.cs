using AppAutomation.Avalonia.Headless.Session;
using ContactMirror.Desktop;
using ContactMirror.Desktop.Contacts;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class IntegratedDemoHeadlessTests
{
    protected override async Task SelectEditorPhotoAsync(string file,bool cancel)
    {
        Task<bool> picking=null!;
        HeadlessRuntime.Dispatch(()=> {picking=ContactPhotoPicker.PickAsync(((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).Contacts,()=>Task.FromResult<Stream?>(cancel?null:File.OpenRead(file)));});
        var changed=await HeadlessEditorUi.RunAsync(()=>picking);if(changed==cancel)throw new InvalidOperationException("Picker did not respect the requested accept/cancel operation.");await Task.Delay(120);
    }
}
