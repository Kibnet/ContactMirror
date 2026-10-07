using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using ContactMirror.UiTests.Authoring.Pages;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Authoring.Tests;

public abstract partial class MainWindowScenariosBase<TSession> : UiTestBase<TSession, MainWindowPage>
    where TSession : class, IUiTestSession
{
    protected virtual void CaptureCheckpoint(string state) { }
    protected void PreparePreview() => Page.EnterText(static page => page.FolderInput, Path.Combine(Path.GetTempPath(), "ContactMirror-ui-test"))
        .ClickButton(static page => page.ConnectGoogleButton)
        .WaitUntilNameContains(static page => page.AccountLabel, "example@example.test")
        .ClickButton(static page => page.CheckChangesButton)
        .WaitUntilNameContains(static page => page.StatusText, "Сравнение завершено");
    [Test]
    [NotInParallel(DesktopUiConstraint)]
    public async Task Onboarding_preview_apply_partial_result()
    {
        CaptureCheckpoint("onboarding");
        Page.EnterText(static page => page.FolderInput, Path.Combine(Path.GetTempPath(), "ContactMirror-ui-test"))
            .ClickButton(static page => page.ConnectGoogleButton)
            .WaitUntilNameContains(static page => page.AccountLabel, "example@example.test")
            .ClickButton(static page => page.CheckChangesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Сравнение завершено");
        CaptureCheckpoint("preview");
        Page.ClickButton(static page => page.NextConflictButton);
        CaptureCheckpoint("conflict");
        Page.ClickButton(static page => page.UseLocalButton)
            .WaitUntilNameContains(static page => page.ApplyButton, "3")
            .ClickButton(static page => page.ApplyButton)
            .WaitUntilNameContains(static page => page.StatusText, "Выполнено частично");
        CaptureCheckpoint("partial-result");
        await Assert.That(Page.StatusText.Name).Contains("1 ошибок");
    }
    [Test]
    [NotInParallel(DesktopUiConstraint)]
    public async Task Search_and_conflict_choices_change_the_visible_plan()
    {
        PreparePreview();
        Page.EnterText(static page => page.SearchInput, "Вера");
        await Assert.That(Page.ChangesList.Items.Count).IsEqualTo(1);
        Page.ClickButton(static page => page.NextConflictButton)
            .ClickButton(static page => page.UseGoogleButton)
            .WaitUntilNameContains(static page => page.ApplyButton, "3")
            .ClickButton(static page => page.SkipConflictButton)
            .WaitUntilNameContains(static page => page.ApplyButton, "2");
        await Assert.That(Page.ApplyButton.Name).IsEqualTo("Применить 2");
    }
    [Test]
    [NotInParallel(DesktopUiConstraint)]
    public async Task Deletes_are_unchecked_and_significant_deletion_requires_phrase()
    {
        PreparePreview();
        Page.EnterText(static page => page.SearchInput, "Глеб");
        await Assert.That(Page.DeleteGlebCheckbox.IsChecked).IsFalse();
        Page.EnterText(static page => page.SearchInput, "Даша");
        await Assert.That(Page.BlockedCheckbox.IsEnabled).IsFalse();
        Page.EnterText(static page => page.SearchInput, "Глеб")
            .SetChecked(static page => page.DeleteGlebCheckbox, true)
            .ClickButton(static page => page.ApplyButton)
            .EnterText(static page => page.DeletePhraseInput, "нет")
            .ClickButton(static page => page.ConfirmDeletesButton);
        await Assert.That(Page.StatusText.Name).Contains("Сравнение завершено");
        CaptureCheckpoint("delete-confirmation");
        Page.EnterText(static page => page.DeletePhraseInput, "УДАЛИТЬ")
            .ClickButton(static page => page.ConfirmDeletesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Выполнено частично");
    }
    [Test]
    [NotInParallel(DesktopUiConstraint)]
    public async Task History_restore_first_shows_a_reviewable_plan()
    {
        PreparePreview();
        Page.ClickButton(static page => page.HistoryButton)
            .WaitUntilHasItemsAtLeast(static page => page.HistoryList, 1);
        Page.SelectListBoxItem(static page => page.HistoryList, Page.HistoryList.Items[0].Text!)
            .ClickButton(static page => page.RestoreButton)
            .WaitUntilNameContains(static page => page.StatusText, "Восстановление");
        CaptureCheckpoint("restore-preview");
        await Assert.That(Page.ApplyButton.IsEnabled).IsTrue();
    }
}
