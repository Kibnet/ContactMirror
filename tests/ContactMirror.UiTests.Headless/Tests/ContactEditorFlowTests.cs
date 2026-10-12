using System.Text.Json.Nodes;
using AppAutomation.Avalonia.Headless.Session;
using AppAutomation.Avalonia.Headless.Automation;
using AppAutomation.TUnit;
using AppAutomation.Abstractions;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ContactMirror.Application.Contacts;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.Core;
using ContactMirror.Desktop;
using ContactMirror.Desktop.Contacts;
using ContactMirror.Infrastructure;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    private Control EditorControl(string id)=>Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>().Single(c=>AutomationProperties.GetAutomationId(c)==id);
    private Task<T> EditorUi<T>(Func<Task<T>> operation) => HeadlessEditorUi.RunAsync(operation);
    private Task EditorUi(Func<Task> operation) => HeadlessEditorUi.RunAsync(async()=>{await operation();return true;});
    private async Task<(string Root,Guid Id)> PrepareRealEditor(bool offline=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"ContactMirror-editor-flow-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await using(var session=await new FileWorkspaceStore().OpenAsync(root,TestAccountConnector.Identity)){}
        var id=Guid.NewGuid(); var doc=DocumentCodec.ToJson(new ContactDocument {Id=id,Data=new JsonObject { ["names"]=new JsonArray(new JsonObject {["givenName"]="Анна",["familyName"]="Примерова"}),["phoneNumbers"]=new JsonArray(new JsonObject {["value"]="+1 202 555 0199"}),["birthdays"]=new JsonArray(new JsonObject {["date"]=new JsonObject {["month"]=2,["day"]=29}}) },Extensions=new JsonObject {["note"]="Синтетические данные"}});
        await File.WriteAllBytesAsync(Path.Combine(root,"contacts",id+".contact.json"),JsonSemantics.Serialize(doc));
        if(!offline)Page.ClickButton(static p=>p.ConnectGoogleButton).WaitUntilNameContains(static p=>p.AccountLabel,"example@example.test");
        HeadlessRuntime.Dispatch(()=> {var model=UxModel();model.Folder=root;model.ContactsMode=true;});
        await EditorUi(()=>UxModel().Contacts.LoadAsync(root));await EditorUi(()=>UxModel().Contacts.SelectAsync(id));await Task.Delay(120);return(root,id);
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Ordinary_character_typing_keeps_same_editor_focus_and_full_value_then_reopens_file()
    {
        var fixture=await PrepareRealEditor();
        try
        {
            HeadlessRuntime.Dispatch(()=>UxModel().Contacts.BeginEdit());await Task.Delay(80);
            TextBox input=null!;
            HeadlessRuntime.Dispatch(()=>
            {
                input=(TextBox)EditorControl("Editor:data.names.0.familyName");input.BringIntoView();input.Focus();input.SelectAll();
                foreach(var c in "Редакторова")Session.Inner.MainWindow.KeyTextInput(c.ToString());
            });
            await Task.Delay(80);bool focus=false;string value="";
            HeadlessRuntime.Dispatch(()=> {focus=ReferenceEquals(input,EditorControl("Editor:data.names.0.familyName"))&&input.IsKeyboardFocusWithin;value=input.Text??"";});
            await Assert.That(focus).IsTrue();await Assert.That(value).IsEqualTo("Редакторова");
            await Assert.That(await EditorUi(()=>UxModel().Contacts.SaveAsync())).IsTrue();
            await EditorUi(()=>UxModel().Contacts.ReloadSelectedAsync());
            await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root,"contacts",fixture.Id+".contact.json")))!["data"]!["names"]![0]!["familyName"]!.ToString()).IsEqualTo("Редакторова");
            foreach(var theme in new[]{ThemeVariant.Light,ThemeVariant.Dark})foreach(var size in new[]{(Width:1180,Height:820),(Width:1000,Height:680),(Width:820,Height:640)})
            {
                HeadlessRuntime.Dispatch(()=> {UxModel().AccountLabel="Демонстрационный аккаунт";Session.Inner.MainWindow.Width=size.Width;Session.Inner.MainWindow.Height=size.Height;Avalonia.Application.Current!.RequestedThemeVariant=theme;});await Task.Delay(130);
                if(size.Width>=900)
                {
                    bool headerFits=false;
                    HeadlessRuntime.Dispatch(()=>
                    {
                        var folder=EditorControl("HeaderFolderLabel");var navigation=EditorControl("ContactsNavigationButton");
                        var folderPoint=folder.TranslatePoint(new Point(folder.Bounds.Width,0),Session.Inner.MainWindow)!.Value;
                        var navigationPoint=navigation.TranslatePoint(default,Session.Inner.MainWindow)!.Value;
                        headerFits=folderPoint.X<=navigationPoint.X;
                    });
                    await Assert.That(headerFits).IsTrue();
                }
                if(size.Width<900){HeadlessRuntime.Dispatch(()=>((Button)EditorControl("ContactOpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(80);}
                CaptureCheckpoint($"editor-{theme.Key}-{size.Width}x{size.Height}");
            }
        }
        finally{HeadlessRuntime.Dispatch(()=> {UxModel().Contacts.CancelEdit();Avalonia.Application.Current!.RequestedThemeVariant=ThemeVariant.Default;});}
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Offline_folder_transition_clears_old_card_and_new_contact_is_saved_only_in_selected_folder()
    {
        var first=await PrepareRealEditor(offline:true);var second=Path.Combine(Path.GetTempPath(),"ContactMirror-editor-offline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(second);
        await using(var session=await new FileWorkspaceStore().OpenAsync(second,TestAccountConnector.Identity)){}
        try
        {
            HeadlessRuntime.Dispatch(()=>UxModel().Folder=second);
            for(var n=0;n<100;n++){bool ready=false;HeadlessRuntime.Dispatch(()=>ready=UxModel().Contacts.Root==second&&!UxModel().Contacts.IsBusy);if(ready)break;await Task.Delay(30);}
            bool cleared=false;HeadlessRuntime.Dispatch(()=>cleared=UxModel().Contacts.Root==second&&!UxModel().Contacts.HasContact&&!UxModel().IsConnected);await Assert.That(cleared).IsTrue();
            await EditorUi(()=>UxModel().Contacts.CreateAsync());Guid created=Guid.Empty;
            HeadlessRuntime.Dispatch(()=> {created=UxModel().Contacts.Original!.Id;UxModel().Contacts.Mutate(doc=>doc["data"]!["names"]=new JsonArray(new JsonObject {["givenName"]="Новый офлайн"}));});
            await Assert.That(await EditorUi(()=>UxModel().Contacts.SaveAsync())).IsTrue();
            await Assert.That(File.Exists(Path.Combine(second,"contacts",created+".contact.json"))).IsTrue();
            await Assert.That(Directory.GetFiles(Path.Combine(first.Root,"contacts"),"*.json").Length).IsEqualTo(1);
        }
        finally{HeadlessRuntime.Dispatch(()=>UxModel().Contacts.CancelEdit());}
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task External_file_drift_keeps_draft_refuses_overwrite_and_stay_does_not_change_contact()
    {
        var fixture=await PrepareRealEditor();var file=Path.Combine(fixture.Root,"contacts",fixture.Id+".contact.json");
        HeadlessRuntime.Dispatch(()=> {UxModel().Contacts.BeginEdit();UxModel().Contacts.Mutate(doc=>doc["data"]!["names"]![0]!["familyName"]="Черновик");});
        var external=JsonNode.Parse(await File.ReadAllTextAsync(file))!;external["data"]!["names"]![0]!["familyName"]="Внешняя правка";await File.WriteAllTextAsync(file,external.ToJsonString());
        await Assert.That(await EditorUi(()=>UxModel().Contacts.SaveAsync())).IsFalse();
        bool retained=false;HeadlessRuntime.Dispatch(()=>retained=UxModel().Contacts.IsDirty&&UxModel().Contacts.Document!["data"]!["names"]![0]!["familyName"]!.ToString()=="Черновик");await Assert.That(retained).IsTrue();
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(file))!["data"]!["names"]![0]!["familyName"]!.ToString()).IsEqualTo("Внешняя правка");
        Task transition=null!;HeadlessRuntime.Dispatch(()=> {transition=UxModel().ShowSyncCommand.ExecuteAsync(null);});await Task.Delay(80);
        HeadlessRuntime.Dispatch(()=>UxModel().Contacts.Decide("stay"));await EditorUi(()=>transition);
        bool staying=false;HeadlessRuntime.Dispatch(()=>staying=UxModel().ContactsMode&&UxModel().Contacts.IsDirty);await Assert.That(staying).IsTrue();
        CaptureCheckpoint("editor-drift-draft-preserved");HeadlessRuntime.Dispatch(()=>UxModel().Contacts.CancelEdit());
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Configuration_modal_blocks_direct_navigation_and_sort_headers_cycle_to_off_with_shift_multiple()
    {
        await PrepareRealEditor();HeadlessRuntime.Dispatch(()=>((Button)EditorControl("ContactColumnsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        await EditorUi(()=>UxModel().ShowSyncCommand.ExecuteAsync(null));
        bool blocked=false;HeadlessRuntime.Dispatch(()=>blocked=UxModel().ContactsMode&&UxModel().Contacts.HasModal&&!UxModel().CanConfigure);await Assert.That(blocked).IsTrue();
        HeadlessRuntime.Dispatch(()=>((Button)EditorControl("ContactConfigurationClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        HeadlessRuntime.Dispatch(()=>((Button)EditorControl("SortColumn:displayName")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(80);
        bool descending=false;HeadlessRuntime.Dispatch(()=>descending=UxModel().Contacts.Configuration.Sorts.Single().Descending);await Assert.That(descending).IsTrue();
        HeadlessRuntime.Dispatch(()=>((Button)EditorControl("SortColumn:displayName")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(80);
        int sortCount=-1;HeadlessRuntime.Dispatch(()=>sortCount=UxModel().Contacts.Configuration.Sorts.Count);await Assert.That(sortCount).IsEqualTo(0);
        HeadlessRuntime.Dispatch(()=>((Button)EditorControl("SortColumn:displayName")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(80);
        HeadlessRuntime.Dispatch(()=>
        {
            var header=(Button)EditorControl("SortColumn:data.phoneNumbers.value");
            header.RaiseEvent(new KeyEventArgs {RoutedEvent=InputElement.KeyDownEvent,Key=Key.Space,KeyModifiers=KeyModifiers.Shift});
            header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });await Task.Delay(80);
        string priorities="";HeadlessRuntime.Dispatch(()=>priorities=string.Join("|",UxModel().Contacts.Configuration.Sorts.Select(rule=>rule.ColumnId)));
        await Assert.That(priorities).IsEqualTo("displayName|data.phoneNumbers.value");
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Table_preferences_reopen_by_workspace_keep_order_width_filter_sort_and_ignore_transient_search()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"ContactMirror-preferences-"+Guid.NewGuid().ToString("N"))).FullName;var file=Path.Combine(root,"preferences.json");var id=Guid.NewGuid();
        var table=ContactTableConfiguration.Default with {Columns=[new("starred",97),new("displayName",311)],Filters=[new("data.phoneNumbers.value",ContactFilterOperator.Contains,"202")],Sorts=[new("starred",true),new("displayName")],Search="not persisted"};
        new ContactTablePreferences(false,true,file).Save(id,root,new(table,.67));var reopened=new ContactTablePreferences(false,true,file).Load(id,root);
        var output=Path.Combine(root,"fresh-process.json");var executable=Path.Combine(FindRepositoryRoot(),"tests","ContactMirror.AppAutomation.TestHost","bin","Release","net10.0","ContactMirror.TestHost.dll");
        var start=new System.Diagnostics.ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};
        foreach(var argument in new[]{executable,"--editor-preferences-probe",file,id.ToString(),root,output})start.ArgumentList.Add(argument);
        using(var process=System.Diagnostics.Process.Start(start)!){await process.WaitForExitAsync();await Assert.That(process.ExitCode).IsEqualTo(0);}
        var fresh=System.Text.Json.JsonSerializer.Deserialize<ContactWorkspacePreferences>(await File.ReadAllTextAsync(output))!;
        await Assert.That(string.Join("|",fresh.Table.Columns)).IsEqualTo(string.Join("|",table.Columns));await Assert.That(fresh.Table.Search).IsEqualTo("");
        await Assert.That(string.Join("|",reopened.Table.Columns)).IsEqualTo(string.Join("|",table.Columns));await Assert.That(reopened.Table.Filters.Single().Value).IsEqualTo("202");await Assert.That(reopened.Table.Sorts.Count).IsEqualTo(2);await Assert.That(reopened.Table.Search).IsEqualTo("");await Assert.That(reopened.TableFraction).IsEqualTo(.67);
        var other=new ContactTablePreferences(false,true,file).Load(Guid.NewGuid(),root);await Assert.That(other.Table.Columns.Count).IsEqualTo(ContactTableConfiguration.Default.Columns.Count);
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Narrow_card_returns_to_table_and_actual_column_order_width_survive_fresh_process()
    {
        var fixture=await PrepareRealEditor();var preferences=Path.Combine(fixture.Root,"isolated-table.json");
        var activity=new ContactMirror.Application.ApplicationActivity();ContactsViewModel model=null!;object? previous=null;
        HeadlessRuntime.Dispatch(()=>model=new(new ContactEditorService(new ContactMirror.Infrastructure.Contacts.ContactEditorStore(),activity),activity,()=>TestAccountConnector.Identity,()=>{},preferenceStore:new(false,true,preferences)));
        await EditorUi(()=>model.LoadAsync(fixture.Root));
        HeadlessRuntime.Dispatch(()=>
        {
            previous=Session.Inner.MainWindow.Content;Session.Inner.MainWindow.Width=820;Session.Inner.MainWindow.Height=820;
            model.Configure(ContactTableConfiguration.Default with{Columns=[new("displayName",230),new("data.phoneNumbers.value",190)]});
            Session.Inner.MainWindow.Content=new ContactsView(model);
        });await Task.Delay(120);
        HeadlessRuntime.Dispatch(()=>((Button)EditorControl("ContactOpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        for(var n=0;n<100;n++){bool ready=false;HeadlessRuntime.Dispatch(()=>ready=model.HasContact&&!model.IsBusy);if(ready)break;await Task.Delay(20);}
        bool hidden=false;HeadlessRuntime.Dispatch(()=>hidden=!EditorControl("ContactsTable").IsVisible);await Assert.That(hidden).IsTrue();
        for(var index=0;index<12;index++)
        {
            bool visibleFocus=false;
            HeadlessRuntime.Dispatch(()=>
            {
                var modifiers=index%2==0?RawInputModifiers.None:RawInputModifiers.Shift;
                Session.Inner.MainWindow.KeyPress(Key.Tab,modifiers,PhysicalKey.Tab,null);Session.Inner.MainWindow.KeyRelease(Key.Tab,modifiers,PhysicalKey.Tab,null);
                visibleFocus=Session.Inner.MainWindow.FocusManager!.GetFocusedElement() is Control {IsEffectivelyVisible:true} && !EditorControl("ContactsTable").IsKeyboardFocusWithin;
            });
            await Assert.That(visibleFocus).IsTrue();
        }
        CaptureCheckpoint("editor-narrow-card");
        HeadlessRuntime.Dispatch(()=>((Button)EditorControl("ContactExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(80);
        double tableWidth=0;HeadlessRuntime.Dispatch(()=>tableWidth=EditorControl("ContactsTable").Bounds.Width);await Assert.That(tableWidth).IsGreaterThan(700);
        bool returnedFocus=false;HeadlessRuntime.Dispatch(()=>returnedFocus=EditorControl("ContactsSearch").IsKeyboardFocusWithin);await Assert.That(returnedFocus).IsTrue();
        HeadlessRuntime.Dispatch(()=>
        {
            var grid=(DataGrid)EditorControl("ContactsTable");grid.Columns[0].Width=new DataGridLength(311);grid.Columns[0].DisplayIndex=1;
        });await Task.Delay(80);
        HeadlessRuntime.Dispatch(()=>
        {
            var grid=(DataGrid)EditorControl("ContactsTable");var point=grid.TranslatePoint(new Avalonia.Point(30,grid.Bounds.Height-20),Session.Inner.MainWindow)!.Value;
            Session.Inner.MainWindow.MouseDown(point,MouseButton.Left);Session.Inner.MainWindow.MouseUp(point,MouseButton.Left);
        });await Task.Delay(80);
        HeadlessRuntime.Dispatch(()=>Console.WriteLine("Narrow columns actual: "+string.Join("|",((DataGrid)EditorControl("ContactsTable")).Columns.Select(column=>$"{column.Tag}:{column.DisplayIndex}:{column.ActualWidth}"))+"; persisted config: "+string.Join("|",model.Configuration.Columns)));
        var catalog=await new ContactMirror.Infrastructure.Contacts.ContactEditorStore().LoadAsync(fixture.Root);
        var output=Path.Combine(fixture.Root,"narrow-fresh-process.json");
        var start=new System.Diagnostics.ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};
        foreach(var argument in new[]{Path.Combine(FindRepositoryRoot(),"tests","ContactMirror.AppAutomation.TestHost","bin","Release","net10.0","ContactMirror.TestHost.dll"),"--editor-preferences-probe",preferences,catalog.WorkspaceId.ToString(),fixture.Root,output})start.ArgumentList.Add(argument);
        using(var process=System.Diagnostics.Process.Start(start)!){await process.WaitForExitAsync();await Assert.That(process.ExitCode).IsEqualTo(0);}
        var restored=System.Text.Json.JsonSerializer.Deserialize<ContactWorkspacePreferences>(await File.ReadAllTextAsync(output))!;
        await Assert.That(restored.Table.Columns[0].Id).IsEqualTo("data.phoneNumbers.value");
        await Assert.That(restored.Table.Columns[1].Id).IsEqualTo("displayName");
        await Assert.That(Math.Abs(restored.Table.Columns[1].Width-311)).IsLessThan(2);
        CaptureCheckpoint("editor-narrow-reordered-table");
        HeadlessRuntime.Dispatch(()=>{model.Dispose();Session.Inner.MainWindow.Content=previous;});
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Ten_thousand_catalog_rows_are_virtualized_search_cancels_old_query_and_UI_remains_available()
    {
        var folder=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"ContactMirror-editor-large-"+Guid.NewGuid().ToString("N"))).FullName;
        var store=new LargeEditorCatalog(folder);var activity=new ContactMirror.Application.ApplicationActivity();ContactsViewModel model=null!;object? before=null;
        HeadlessRuntime.Dispatch(()=>model=new(new ContactEditorService(store,activity),activity,()=>null,()=>{},persist:false));
        await EditorUi(()=>model.LoadAsync(folder));
        HeadlessRuntime.Dispatch(()=> {before=Session.Inner.MainWindow.Content;Session.Inner.MainWindow.Content=new ContactsView(model);});await Task.Delay(200);
        int realized=0,total=0;
        HeadlessRuntime.Dispatch(()=> {var grid=(DataGrid)EditorControl("ContactsTable");total=grid.ItemsSource.Cast<object>().Count();realized=grid.GetVisualDescendants().OfType<DataGridRow>().Count();});
        await Assert.That(total).IsEqualTo(10000);await Assert.That(realized).IsGreaterThan(0);await Assert.That(realized).IsLessThan(50);
        var timer=System.Diagnostics.Stopwatch.StartNew();HeadlessRuntime.Dispatch(()=> {model.Search="Контакт 0";model.Search="Контакт 09999";});timer.Stop();await Assert.That(timer.ElapsedMilliseconds).IsLessThan(250);
        for(var n=0;n<100;n++){int count=0;HeadlessRuntime.Dispatch(()=>count=model.Rows.Count);if(count==1)break;await Task.Delay(30);}
        int matches=0;string name="";HeadlessRuntime.Dispatch(()=> {matches=model.Rows.Count;name=model.Rows.FirstOrDefault()?.Name??"";});await Assert.That(matches).IsEqualTo(1);await Assert.That(name).IsEqualTo("Контакт 09999");
        CaptureCheckpoint("editor-10000-filtered");Console.WriteLine($"10000 catalog rows, {realized} realized, UI input {timer.ElapsedMilliseconds}ms; stale query cancelled.");
        HeadlessRuntime.Dispatch(()=> {model.Dispose();Session.Inner.MainWindow.Content=before;});
    }
    private sealed class LargeEditorCatalog(string root):IContactEditorStore
    {
        private readonly Guid workspace=Guid.NewGuid();
        private readonly IReadOnlyList<ContactCatalogItem> items=Enumerable.Range(0,10000).Select(index=>
        {
            var id=Guid.NewGuid();var doc=DocumentCodec.ToJson(new ContactDocument {Id=id,Data=new JsonObject {["names"]=new JsonArray(new JsonObject {["givenName"]=$"Контакт {index:D5}"}),["phoneNumbers"]=new JsonArray(new JsonObject {["value"]="+1 202 555 0199"})}});return new ContactCatalogItem(id,Path.Combine(root,id+".contact.json"),doc);
        }).ToArray();
        public Task<ContactCatalog> LoadAsync(string path,AccountIdentity? activeAccount=null,CancellationToken cancellationToken=default)=>Task.FromResult(new ContactCatalog(root,workspace,items,new Dictionary<Guid,string>(),[]));
        public Task<ContactSnapshot> ReadAsync(string path,Guid id,AccountIdentity? activeAccount=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<ContactSnapshot> SaveAsync(ContactSnapshot original,JsonObject document,ContactPhotoEdit photo,AccountIdentity? activeAccount=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task DeleteAsync(ContactSnapshot original,AccountIdentity? activeAccount=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
