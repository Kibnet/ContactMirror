using Avalonia.Controls;
using ContactMirror.Desktop;
using AppAutomation.Avalonia.Headless.Session;

namespace ContactMirror.UiTests.Headless.Tests;

internal static class HeadlessEditorCleanup
{
    public static void Close(Window window)
    {
        if(window.DataContext is MainWindowViewModel model)
        {
            model.CancelCommand.Execute(null);
            model.Contacts.Decide("discard");
            model.Contacts.ShowDecision=false;model.Contacts.ShowDelete=false;model.Contacts.ShowPhoto=false;model.Contacts.ShowConfiguration=false;
            model.Contacts.CancelEdit();
        }
        window.Close();
    }
}

internal static class HeadlessEditorUi
{
    // Dispatch pumps awaited UI work. Release the test continuation on the pool,
    // since a synchronous Dispatch from the UI completion thread would deadlock.
    public static Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _=CompleteAsync();
        return completion.Task;
        async Task CompleteAsync()
        {
            try{completion.TrySetResult(await HeadlessRuntime.Session.Dispatch<T>(action,CancellationToken.None).ConfigureAwait(false));}
            catch(Exception error){completion.TrySetException(error);}
        }
    }
}
