using AppAutomation.Abstractions;

namespace ContactMirror.UiTests.Authoring.Pages;

[UiControl("ConnectGoogleButton", UiControlType.Button, "ConnectGoogleButton")]
[UiControl("CheckChangesButton", UiControlType.Button, "CheckChangesButton")]
[UiControl("ApplyButton", UiControlType.Button, "ApplyButton")]
[UiControl("FolderInput", UiControlType.TextBox, "FolderInput")]
[UiControl("SearchInput", UiControlType.TextBox, "SearchInput")]
[UiControl("AccountLabel", UiControlType.Label, "AccountLabel")]
[UiControl("StatusText", UiControlType.Label, "StatusText")]
[UiControl("HistoryButton", UiControlType.Button, "HistoryButton")]
[UiControl("HistoryList", UiControlType.ListBox, "HistoryList")]
[UiControl("RestoreButton", UiControlType.Button, "RestoreButton")]
[UiControl("CleanupBackupButton", UiControlType.Button, "CleanupBackupButton")]
[UiControl("ConfirmCleanupBackupButton", UiControlType.Button, "ConfirmCleanupBackupButton")]
[UiControl("CapabilitiesButton", UiControlType.Button, "CapabilitiesButton")]
[UiControl("SettingsButton", UiControlType.Button, "SettingsButton")]
[UiControl("AppVersionText", UiControlType.Label, "AppVersionText")]
[UiControl("UpdateStatusText", UiControlType.Label, "UpdateStatusText")]
[UiControl("DownloadUpdateButton", UiControlType.Button, "DownloadUpdateButton")]
[UiControl("RestartUpdateButton", UiControlType.Button, "RestartUpdateButton")]
[UiControl("CheckUpdatesButton", UiControlType.Button, "CheckUpdatesButton")]
[UiControl("UseLocalButton", UiControlType.Button, "UseLocalButton")]
[UiControl("NextConflictButton", UiControlType.Button, "NextConflictButton")]
[UiControl("ChangesList", UiControlType.ListBox, "ChangesList")]
[UiControl("DeleteGlebCheckbox", UiControlType.CheckBox, "gleb")]
[UiControl("BlockedCheckbox", UiControlType.CheckBox, "blocked")]
[UiControl("UseGoogleButton", UiControlType.Button, "UseGoogleButton")]
[UiControl("SkipConflictButton", UiControlType.Button, "SkipConflictButton")]
[UiControl("CancelButton", UiControlType.Button, "CancelButton")]
[UiControl("DeletePhraseInput", UiControlType.TextBox, "DeletePhraseInput")]
[UiControl("ConfirmDeletesButton", UiControlType.Button, "ConfirmDeletesButton")]
public sealed partial class MainWindowPage : UiPage
{
    public MainWindowPage(IUiControlResolver resolver) : base(resolver)
    {
    }
}
