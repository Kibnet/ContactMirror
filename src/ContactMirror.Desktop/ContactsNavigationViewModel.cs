using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContactMirror.Desktop.Contacts;

namespace ContactMirror.Desktop;

public sealed partial class MainWindowViewModel
{
    public ContactsViewModel Contacts { get; }
    [ObservableProperty] private bool _contactsMode;
    partial void OnFolderInputChanged(string value) { if (!Contacts.IsDirty && !Contacts.IsBusy && !_activity.IsApplying) Folder = value; }
    public bool ShowContactsPage => ContactsMode && FolderReady && !HasPanel || Contacts.ShowDecision || Contacts.ShowDelete || Contacts.ShowPhoto;
    public bool ShowSyncPage => !ShowContactsPage && !HasPanel;
    partial void OnContactsModeChanged(bool value) => NotifyContactsNavigation();
    private void NotifyContactsNavigation()
    {
        OnPropertyChanged(nameof(ShowContactsPage)); OnPropertyChanged(nameof(ShowSyncPage));
    }
    [RelayCommand] private async Task ShowContactsAsync()
    {
        if (!CanConfigure || !await Contacts.ResolveDraftAsync()) return;
        ClosePanel(); ContactsMode = true; if (FolderReady) await Contacts.LoadAsync(Folder);
    }
    [RelayCommand] private async Task ShowSyncAsync()
    {
        if (!CanConfigure || !await Contacts.ResolveDraftAsync()) return;
        ClosePanel(); ContactsMode = false;
    }
    public async Task CommitFolderInputAsync()
    {
        var candidate = FolderInput;
        if (candidate == Folder) return;
        if (!await Contacts.ResolveDraftAsync()) { FolderInput = Folder; return; }
        Folder = candidate;
    }
}
