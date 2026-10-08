using AppAutomation.Avalonia.Headless.Session;
using AppAutomation.Abstractions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.Core;
using ContactMirror.Desktop;
using System.Text.Json.Nodes;
using TUnit.Core;
using TUnit.Assertions;
using Avalonia.Input;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Connecting_first_keeps_folder_step_visible_until_setup_is_complete()
    {
        Page.ClickButton(static p => p.ConnectGoogleButton)
            .WaitUntilNameContains(static p => p.AccountLabel, "example@example.test");
        bool onboarding = false;
        HeadlessRuntime.Dispatch(() => onboarding = ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).IsOnboarding);
        await Assert.That(onboarding).IsTrue();
        var folder = Path.Combine(Path.GetTempPath(), "ContactMirror-connect-first-" + Guid.NewGuid().ToString("N"));
        Page.EnterText(static p => p.FolderInput, folder);
        HeadlessRuntime.Dispatch(() => onboarding = UxModel().IsOnboarding && !UxModel().CanCheck && UxModel().FolderValidation.Contains("не найдена"));
        await Assert.That(onboarding).IsTrue();
        Directory.CreateDirectory(folder);
        Page.EnterText(static p => p.FolderInput, folder + Path.DirectorySeparatorChar);
        HeadlessRuntime.Dispatch(() => onboarding = ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).IsOnboarding);
        await Assert.That(onboarding).IsFalse();
        CaptureCheckpoint("ux-setup-ready");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Several_fields_of_one_contact_are_one_navigation_item_and_names_are_not_identity()
    {
        var id = Guid.NewGuid();
        var coordinator = new TestSyncCoordinator { OverrideEntries = [UxEntry("name", id, "names"), UxEntry("phone", id, "phoneNumbers"), UxEntry("other", Guid.NewGuid(), "emailAddresses")] };
        HeadlessRuntime.Dispatch(() => ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).ReplaceServices(new TestAccountConnector(), coordinator));
        PreparePreview();
        int count = 0;
        HeadlessRuntime.Dispatch(() => count = Session.Inner.MainWindow.GetVisualDescendants().OfType<ListBox>().Single(c => AutomationProperties.GetAutomationId(c) == "ChangesList").ItemCount);
        await Assert.That(count).IsEqualTo(2);
        CaptureCheckpoint("ux-grouped-same-name");
    }

    private static SyncEntry UxEntry(string key, Guid id, string field, ChangeKind kind = ChangeKind.Upload, EntityKind entity = EntityKind.Contact) => new()
    {
        Key = key, EntityId = id, Entity = entity, Name = "Однофамилец", Field = field, Kind = kind,
        Before = JsonNode.Parse("[]"), Local = JsonNode.Parse("[{\"value\":\"+1 202 555 0199\",\"type\":\"home\"}]"), Google = JsonNode.Parse("[]")
    };

    [Test, NotInParallel("DesktopUi")]
    public async Task Group_selection_preserves_exact_field_choices_and_does_not_include_deletion()
    {
        var id = Guid.NewGuid();
        var coordinator = new TestSyncCoordinator { Partial = false, OverrideEntries = [UxEntry("name", id, "names"), UxEntry("phone", id, "phoneNumbers"), UxEntry("delete", id, "$entity", ChangeKind.DeleteRemote)] };
        ConfigureUx(coordinator); PreparePreview();
        HeadlessRuntime.Dispatch(() =>
        {
            var model = UxModel();
            var checkbox = Session.Inner.MainWindow.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "name");
            checkbox.IsChecked = false;
            model.SelectedGroup!.IsSelected = true;
            checkbox.IsChecked = false;
        });
        await Task.Delay(100);
        Page.ClickButton(static p => p.ApplyButton).WaitUntilNameContains(static p => p.StatusText, "Синхронизировано");
        await Assert.That(coordinator.AppliedChoices.Count).IsEqualTo(1);
        await Assert.That(coordinator.AppliedChoices[0].Key).IsEqualTo("phone");
        bool canApply = true;
        HeadlessRuntime.Dispatch(() => canApply = UxModel().CanApply);
        await Assert.That(canApply).IsFalse();
        CaptureCheckpoint("ux-field-only-result");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Recovery_alternatives_allow_only_valid_sides_and_enforce_identity_exclusivity()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        ConfigureUx(new TestSyncCoordinator { OverrideEntries = [UxEntry("r1", id, "$relink:people/one", ChangeKind.Conflict), UxEntry("r2", id, "$relink:people/two", ChangeKind.Conflict), UxEntry("retry", id, "$retry", ChangeKind.Conflict), UxEntry("collision", other, "$relink:people/two", ChangeKind.Conflict)] });
        PreparePreview();
        bool valid = false;
        HeadlessRuntime.Dispatch(() =>
        {
            var m = UxModel(); m.SelectedEntry = m.Entries[0];
            valid = !m.UseLocalCommand.CanExecute(null) && m.UseGoogleCommand.CanExecute(null);
            m.SelectedGroup!.IsSelected = true;
        });
        await Assert.That(valid).IsTrue();
        HeadlessRuntime.Dispatch(() => valid = UxModel().Entries.All(e => !e.CanApply));
        await Assert.That(valid).IsTrue();
        HeadlessRuntime.Dispatch(() =>
        {
            var m = UxModel(); m.UseGoogleCommand.Execute(null); m.SelectedEntry = m.Entries[1]; m.UseGoogleCommand.Execute(null);
            valid = !m.Entries[0].CanApply && m.Entries[1].CanApply;
            m.SelectedEntry = m.Entries[3]; m.UseGoogleCommand.Execute(null);
            valid &= !m.Entries[3].CanApply && m.Error.Contains("уже выбрана");
            m.SelectedEntry = m.Entries[2];
            valid &= m.UseLocalCommand.CanExecute(null) && !m.UseGoogleCommand.CanExecute(null);
            m.UseLocalCommand.Execute(null);
            valid &= m.Entries[2].CanApply && !m.Entries[1].CanApply && m.PlannedOutcome.Contains("дубликат");
            Console.WriteLine($"Recovery: choices={string.Join(",", m.Entries.Select(e => e.Entry.Key + ":" + e.Resolution + ":" + e.CanApply))}; selected={m.SelectedEntry?.Entry.Key}; error={m.Error}; outcome={m.PlannedOutcome}");
        });
        await Assert.That(valid).IsTrue();
        CaptureCheckpoint("ux-recovery-choice");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Partial_results_distinguish_failed_unknown_and_unattempted_operations()
    {
        var coordinator = new TestSyncCoordinator
        {
            OverrideEntries = [UxEntry("failed", Guid.NewGuid(), "names"), UxEntry("unknown", Guid.NewGuid(), "phoneNumbers"), UxEntry("skipped", Guid.NewGuid(), "biographies")],
            OverrideResult = _ => new(Guid.NewGuid(), 0, 2, 1, [new("failed", "failed", "Не удалось сохранить локальный файл после записи Google."), new("unknown", "unknown", "Ответ Google неизвестен.")])
        };
        ConfigureUx(coordinator); PreparePreview();
        Page.ClickButton(static p => p.ApplyButton).WaitUntilNameContains(static p => p.StatusText, "Выполнено частично");
        string summary = ""; IReadOnlyList<ResultRow> rows = [];
        HeadlessRuntime.Dispatch(() => { summary = UxModel().ResultSummary; rows = UxModel().ResultRows; });
        await Assert.That(summary).Contains("ошибок завершения: 1; не выполнялось: 1; результат неизвестен: 1");
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows.Single(r => r.Outcome == "○ Не выполнялось").Field).IsEqualTo("Заметки");
        await Assert.That(rows.Single(r => r.Outcome == "⚠ Не удалось завершить").Message).Contains("могли уже попасть в Google");
        CaptureCheckpoint("ux-partial-unknown-unattempted");
        Page.ClickButton(static p => p.CheckChangesButton).WaitUntilNameContains(static p => p.StatusText, "Сравнение завершено");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Counted_filters_and_phone_search_preserve_hidden_selection_and_show_no_match_state()
    {
        ConfigureUx(new TestSyncCoordinator { OverrideEntries = [UxEntry("phone", Guid.NewGuid(), "phoneNumbers"), UxEntry("mail", Guid.NewGuid(), "emailAddresses", ChangeKind.Download)] });
        PreparePreview();
        Page.EnterText(static p => p.SearchInput, "202 555 0199");
        // Both sides of a prepared operation are indexed, not only the display name.
        int visible = 0;
        HeadlessRuntime.Dispatch(() => { UxModel().SetFilterCommand.Execute("1"); visible = UxModel().VisibleGroups.Count; });
        await Assert.That(visible).IsEqualTo(1);
        string summary = "";
        HeadlessRuntime.Dispatch(() => summary = UxModel().SelectionSummary);
        await Assert.That(summary).Contains("вне фильтра");
        Page.EnterText(static p => p.SearchInput, "не существующий контакт");
        bool noMatches = false;
        HeadlessRuntime.Dispatch(() => noMatches = UxModel().HasNoMatches && !UxModel().IsEmptyPlan && UxModel().Entries.Count == 2);
        await Assert.That(noMatches).IsTrue();
        CaptureCheckpoint("ux-search-no-matches");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Compact_conflict_shows_both_values_before_buttons_and_panels_preserve_context()
    {
        PreparePreview(); Page.ClickButton(static p => p.NextConflictButton);
        HeadlessRuntime.Dispatch(() => { Session.Inner.MainWindow.Width = 1000; Session.Inner.MainWindow.Height = 680; });
        await Task.Delay(180);
        string left = "", right = ""; double height = 0; bool above = false;
        HeadlessRuntime.Dispatch(() =>
        {
            var window = Session.Inner.MainWindow;
            var details = window.GetVisualDescendants().OfType<ScrollViewer>().Single(c => AutomationProperties.GetAutomationId(c) == "ContactDetailsScroll");
            height = window.GetVisualDescendants().OfType<Grid>().Single(c => AutomationProperties.GetAutomationId(c) == "ComparisonWorkspace").Bounds.Height;
            var m = UxModel(); left = m.DisplayRows[0].Left; right = m.DisplayRows[0].Right;
            var value = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(c => AutomationProperties.GetAutomationId(c) == "DiffRight");
            var choice = window.GetVisualDescendants().OfType<Button>().Single(c => AutomationProperties.GetAutomationId(c) == "UseLocalButton");
            above = value.TranslatePoint(default, window)!.Value.Y + value.Bounds.Height <= choice.TranslatePoint(default, window)!.Value.Y && details.Offset.Y == 0;
        });
        await Assert.That(height).IsGreaterThanOrEqualTo(320);
        await Assert.That(left).Contains("00-02"); await Assert.That(right).Contains("00-03"); await Assert.That(above).IsTrue();
        CaptureCheckpoint("ux-conflict-1000x680");
        Page.ClickButton(static p => p.SettingsButton); CaptureCheckpoint("ux-settings-1000x680");
        Page.ClickButton(static p => p.ClosePanelButton);
        bool restored = false;
        HeadlessRuntime.Dispatch(() => restored = UxModel().SelectedEntry?.Entry.Key == "vera" && !UxModel().HasPanel);
        await Assert.That(restored).IsTrue();
        HeadlessRuntime.Dispatch(() => Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark);
        CaptureCheckpoint("ux-conflict-dark-1000x680");
        HeadlessRuntime.Dispatch(() => Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
        HeadlessRuntime.Dispatch(() => { Session.Inner.MainWindow.Width = 820; Session.Inner.MainWindow.Height = 640; });
        await Task.Delay(160);
        CaptureCheckpoint("ux-narrow-contact-list");
        HeadlessRuntime.Dispatch(() => UxModel().ShowNarrowDetails = true);
        await Task.Delay(200);
        HeadlessRuntime.Dispatch(() =>
        {
            var window = Session.Inner.MainWindow;
            var viewport = window.GetVisualDescendants().OfType<ScrollViewer>().Single(c => AutomationProperties.GetAutomationId(c) == "ContactDetailsScroll");
            var value = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(c => AutomationProperties.GetAutomationId(c) == "DiffRight");
            above = value.TranslatePoint(default, viewport)!.Value.Y >= 0 && value.TranslatePoint(default, viewport)!.Value.Y + value.Bounds.Height <= viewport.Viewport.Height;
        });
        await Assert.That(above).IsTrue();
        CaptureCheckpoint("ux-narrow-contact-details");
        HeadlessRuntime.Dispatch(() => UxModel().BackToListCommand.Execute(null));
        HeadlessRuntime.Dispatch(() => restored = UxModel().ShowList && !UxModel().ShowDetails);
        await Assert.That(restored).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Deletion_modal_traps_focus_rejects_phrase_locally_and_escape_keeps_plan()
    {
        PreparePreview(); Page.EnterText(static p => p.SearchInput, "Глеб").SetChecked(static p => p.DeleteGlebCheckbox, true).ClickButton(static p => p.ApplyButton);
        Page.EnterText(static p => p.DeletePhraseInput, "нет").ClickButton(static p => p.ConfirmDeletesButton);
        bool valid = false;
        HeadlessRuntime.Dispatch(() =>
        {
            var m = UxModel(); valid = m.ShowDeleteConfirmation && m.DeleteValidation.Contains("Введите УДАЛИТЬ") && !m.CanConfigure && !m.ToggleSettingsCommand.CanExecute(null);
            Session.Inner.MainWindow.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            valid &= Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>().Any(c => c.IsKeyboardFocusWithin && AutomationProperties.GetAutomationId(c) is "DeletePhraseInput" or "DismissDeletesButton" or "ConfirmDeletesButton");
        });
        await Assert.That(valid).IsTrue();
        CaptureCheckpoint("ux-delete-inline-validation");
        HeadlessRuntime.Dispatch(() => Session.Inner.MainWindow.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }));
        HeadlessRuntime.Dispatch(() => valid = !UxModel().ShowDeleteConfirmation && UxModel().HasPreview && UxModel().CanConfigure);
        await Assert.That(valid).IsTrue();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Presentation_preserves_missing_null_empty_and_safe_deletion_copy_semantics()
    {
        foreach (var value in new[] { "Нет поля", "Нет значения", "null", "[]", "\"\" (пустая строка)" }) await Assert.That(WorkspacePresentation.Readable(value)).IsEqualTo(value);
        var ordinary = new EntryViewModel(UxEntry("ordinary", Guid.NewGuid(), "$entity", ChangeKind.DeleteRemote));
        await Assert.That(WorkspacePresentation.DeletionDescription(ordinary)).Contains("Другой стороны уже нет");
        var local = new EntryViewModel(UxEntry("restorelocal", Guid.NewGuid(), "$restoreDeleteLocal", ChangeKind.DeleteLocal));
        await Assert.That(WorkspacePresentation.DeletionDescription(local)).Contains("в Google сохранится");
        var unbind = new EntryViewModel(UxEntry("unbind", Guid.NewGuid(), "$restoreUnbind", ChangeKind.DeleteRemote, EntityKind.Group));
        await Assert.That(WorkspacePresentation.DeletionDescription(unbind)).Contains("Локальный файл сохранится");
        await Assert.That(WorkspacePresentation.DeletionDescription(unbind)).Contains("Контакты сохранятся");
        await Assert.That(WorkspacePresentation.Readable("{\"value\":\"+1 202 555 0199\",\"type\":\"home\",\"custom\":\"retained\"}")).Contains("retained");
        await Assert.That(WorkspacePresentation.Present(new DiffRow("Ваши правки", "Значение", "$.data.biographies[0].value", "old", "new", "old"), ordinary).Title).IsEqualTo("Заметка");
        foreach (var literal in new[] { "[1,2]", "{\"value\":\"keep braces\"}" })
        {
            var entry = new EntryViewModel(new SyncEntry { Key = "literal", EntityId = Guid.NewGuid(), Name = "Заметка", Field = "biographies", Kind = ChangeKind.Upload, Local = new JsonArray(new JsonObject { ["value"] = literal }), Google = new JsonArray(new JsonObject { ["value"] = "old" }) });
            var raw = PreviewDiff.Build(entry.Entry);
            var projected = WorkspacePresentation.PresentRows(raw, entry, default);
            await Assert.That(projected.Single(r => r.Title == "Заметка").Right).IsEqualTo(literal);
        }
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Technical_details_keep_exact_three_sides_and_confirmation_commands_cannot_bypass_modal()
    {
        var coordinator = new TestSyncCoordinator { HistoryComplete = true };
        ConfigureUx(coordinator); PreparePreview();
        Page.ClickButton(static p => p.NextConflictButton);
        HeadlessRuntime.Dispatch(() => Session.Inner.MainWindow.GetVisualDescendants().OfType<Expander>().Single(c => AutomationProperties.GetAutomationId(c) == "TechnicalDetails").IsExpanded = true);
        await Task.Delay(150);
        bool exact = false;
        HeadlessRuntime.Dispatch(() =>
        {
            var m = UxModel(); var window = Session.Inner.MainWindow;
            var technical = window.GetVisualDescendants().OfType<ListBox>().Single(c => AutomationProperties.GetAutomationId(c) == "TechnicalDiffRows");
            var values = technical.GetVisualDescendants().OfType<SelectableTextBlock>().Select(c => c.Text).ToArray();
            exact = values.Contains(m.DiffRows[0].Before) && values.Contains(m.DiffRows[0].Google) && values.Contains(m.DiffRows[0].Local);
        });
        await Assert.That(exact).IsTrue();
        HeadlessRuntime.Dispatch(() => Session.Inner.MainWindow.GetVisualDescendants().OfType<ListBox>().Single(c => AutomationProperties.GetAutomationId(c) == "TechnicalDiffRows").BringIntoView());
        await Task.Delay(150);
        CaptureCheckpoint("ux-technical-exact-values");
        Task? directConfirm = null;
        HeadlessRuntime.Dispatch(() => { directConfirm = UxModel().ConfirmDeletesCommand.ExecuteAsync(null); });
        await directConfirm!;
        await Assert.That(coordinator.AppliedCount).IsEqualTo(0);
        Page.ClickButton(static p => p.HistoryButton).WaitUntilHasItemsAtLeast(static p => p.HistoryList, 1);
        Page.SelectListBoxItem(static p => p.HistoryList, Page.HistoryList.Items[0].Text!).ClickButton(static p => p.CleanupBackupButton);
        var mutations = 0; Task? configuration = null; Task? restore = null;
        HeadlessRuntime.Dispatch(() =>
        {
            var m = UxModel(); exact = !m.CanRestoreBackup && !m.CanCleanupBackup && !m.CanChooseAny;
            configuration = m.ConfigureAsync(_ => { mutations++; return Task.CompletedTask; });
            restore = m.RestoreCommand.ExecuteAsync(null);
        });
        await Task.WhenAll(configuration!, restore!);
        await Assert.That(exact).IsTrue(); await Assert.That(mutations).IsEqualTo(0);
        HeadlessRuntime.Dispatch(() => UxModel().DismissBackupDeleteCommand.Execute(null));
    }

    private void ConfigureUx(TestSyncCoordinator coordinator) => HeadlessRuntime.Dispatch(() => UxModel().ReplaceServices(new TestAccountConnector(), coordinator));
    [Test]
    public async Task Theme_resources_meet_text_and_indicator_contrast_targets()
    {
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "ContactMirror.Desktop", "App.axaml"));
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var dictionary = document.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == theme));
            var colors = dictionary.Elements().ToDictionary(e => e.Attributes().Single(a => a.Name.LocalName == "Key").Value, e => e.Attribute("Color")!.Value);
            foreach (var background in new[] { "WorkspaceBrush", "SurfaceBrush", "SelectionBrush" })
            {
                await Assert.That(Contrast(colors["SecondaryTextBrush"], colors[background])).IsGreaterThanOrEqualTo(4.5);
                if (background != "SelectionBrush") await Assert.That(Contrast(colors["StrokeBrush"], colors[background])).IsGreaterThanOrEqualTo(3);
            }
            await Assert.That(Contrast(colors["SelectedSupportingBrush"], theme == "Light" ? "#9DCCEF" : "#125F9B")).IsGreaterThanOrEqualTo(4.5);
            Console.WriteLine($"{theme}: supporting text={Contrast(colors["SecondaryTextBrush"], colors["SurfaceBrush"]):F2}:1; indicator={Contrast(colors["StrokeBrush"], colors["SurfaceBrush"]):F2}:1");
        }
        await Assert.That(Contrast("#FFFFFF", "#315DDC")).IsGreaterThanOrEqualTo(4.5);
        static double Contrast(string first, string second)
        {
            static double Luminance(string hex)
            {
                var rgb = Enumerable.Range(0, 3).Select(i => Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255d).Select(c => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4)).ToArray();
                return rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722;
            }
            var a = Luminance(first); var b = Luminance(second);
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        }
    }
    private MainWindowViewModel UxModel() => (MainWindowViewModel)Session.Inner.MainWindow.DataContext!;
}
