using AppAutomation.Avalonia.Headless.Automation;
using AppAutomation.Avalonia.Headless.Session;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using ContactMirror.AppAutomation.TestHost;
using ContactMirror.UiTests.Authoring.Pages;
using ContactMirror.UiTests.Authoring.Tests;
using ContactMirror.Desktop;
using Avalonia.Styling;
using TUnit.Core;
using TUnit.Assertions;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;

namespace ContactMirror.UiTests.Headless.Tests;

[InheritsTests]
public sealed class MainWindowHeadlessTests
    : MainWindowScenariosBase<MainWindowHeadlessTests.HeadlessRuntimeSession>
{
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Prepared_update_reflects_busy_guard_and_blocks_settings_after_ambiguous_apply()
    {
        var activity = new ContactMirror.Application.ApplicationActivity();
        var service = new ContactMirror.Application.Updates.ApplicationUpdateService(new UpdateBackend(), activity);
        MainWindowViewModel? model = null;
        HeadlessRuntime.Dispatch(() =>
        {
            model = new MainWindowViewModel(new TestAccountConnector(), new TestSyncCoordinator(), demo: true, persist: false, activity: activity, updates: service) { ShowSettings = true };
            Session.Inner.MainWindow.DataContext = model;
        });
        await service.CheckAsync(); await service.DownloadAsync();
        using (activity.TryEnter())
        {
            bool canRestart = true, canConfigure = true;
            HeadlessRuntime.Dispatch(() => { canRestart = model!.Updates.CanRestart; canConfigure = model.CanConfigure; });
            await Assert.That(canRestart).IsFalse(); await Assert.That(canConfigure).IsFalse();
            CaptureCheckpoint("update-ready-busy");
        }
        bool ready = false;
        HeadlessRuntime.Dispatch(() => ready = model!.Updates.CanRestart);
        await Assert.That(ready).IsTrue();
        CaptureCheckpoint("update-ready");
        await service.ApplyAndRestartAsync();
        var mutations = 0;
        await model!.ConfigureAsync(_ => { mutations++; return Task.CompletedTask; });
        await Assert.That(mutations).IsEqualTo(0);
        HeadlessRuntime.Dispatch(model.Detach);
    }
    private sealed class UpdateBackend : ContactMirror.Application.Updates.IApplicationUpdateBackend
    {
        public bool IsSupported => true;
        public bool IsConfigured => true;
        public string CurrentVersion => "0.2.0";
        public bool HasAmbiguousApply => true;
        public Task<string?> CheckAsync(CancellationToken token) => Task.FromResult<string?>("0.2.1");
        public Task DownloadAsync(IProgress<int> progress, CancellationToken token) { progress.Report(100); return Task.CompletedTask; }
        public void ApplyAndRestart() => throw new IOException("fixture failed after launch");
    }
    protected override void CaptureCheckpoint(string state)
    {
        if (state == "backup-cleanup-confirmation")
            HeadlessRuntime.Dispatch(() => Session.Inner.MainWindow.GetVisualDescendants().OfType<Control>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "ConfirmCleanupBackupButton").BringIntoView());
        var root = FindRepositoryRoot();
        var path = Session.Inner.CaptureScreenshot(Path.Combine(root, "chat-artifacts", "ui", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), state + ".png"));
        Console.WriteLine($"Rendered UI screenshot ({state}): {path}");
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Synchronous_large_planning_runs_off_UI_throttles_progress_and_can_be_cancelled()
    {
        var coordinator = new TestSyncCoordinator { SynchronousCpuPrepare = true };
        var uiThread = 0; var progressNotifications = 0;
        HeadlessRuntime.Dispatch(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            var model = (MainWindowViewModel)Session.Inner.MainWindow.DataContext!;
            model.ReplaceServices(new TestAccountConnector(), coordinator);
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.Status) && model.Status.StartsWith("Большая адресная книга")) progressNotifications++;
            };
        });
        Page.EnterText(static page => page.FolderInput, Path.Combine(Path.GetTempPath(), "ContactMirror-ui-large-cancel"))
            .ClickButton(static page => page.ConnectGoogleButton)
            .WaitUntilNameContains(static page => page.AccountLabel, "example@example.test");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Page.ClickButton(static page => page.CheckChangesButton);
        await coordinator.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(coordinator.PrepareThreadId).IsNotEqualTo(uiThread);
        Page.WaitUntilNameContains(static page => page.StatusText, "Большая адресная книга")
            .ClickButton(static page => page.CancelButton)
            .WaitUntilNameContains(static page => page.StatusText, "Операция остановлена");
        stopwatch.Stop();
        await Assert.That(stopwatch.Elapsed.TotalSeconds).IsLessThan(2);
        await Assert.That(progressNotifications).IsLessThan(10);
        await Assert.That(Page.CheckChangesButton.IsEnabled).IsTrue();
        Console.WriteLine($"Large synchronous planning: cancellation in {stopwatch.ElapsedMilliseconds}ms; progress status notifications={progressNotifications}; UI thread={uiThread}, coordinator thread={coordinator.PrepareThreadId}");
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Ten_thousand_changes_are_virtualized_and_search_remains_usable()
    {
        var coordinator = new TestSyncCoordinator { LargeEntryCount = 10000 };
        HeadlessRuntime.Dispatch(() => ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).ReplaceServices(new TestAccountConnector(), coordinator));
        Page.EnterText(static page => page.FolderInput, Path.Combine(Path.GetTempPath(), "ContactMirror-ui-10000"))
            .ClickButton(static page => page.ConnectGoogleButton)
            .WaitUntilNameContains(static page => page.AccountLabel, "example@example.test")
            .ClickButton(static page => page.CheckChangesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Контактов: 10000");
        CaptureCheckpoint("preview-10000-virtualized");
        var realized = 0; var total = 0;
        HeadlessRuntime.Dispatch(() =>
        {
            var list = Session.Inner.MainWindow.GetVisualDescendants().OfType<ListBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "ChangesList");
            total = list.ItemCount;
            realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
        });
        await Assert.That(total).IsEqualTo(10000);
        await Assert.That(realized).IsGreaterThan(0);
        await Assert.That(realized).IsLessThan(100);
        Page.EnterText(static page => page.SearchInput, "Контакт 09999");
        var visible = 0;
        HeadlessRuntime.Dispatch(() => visible = ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).VisibleEntries.Count);
        await Assert.That(visible).IsEqualTo(1);
        Console.WriteLine($"10,000 entries: realized ListBoxItem controls={realized}; filtered entries={visible}");
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Empty_state_and_dark_compact_layout_are_rendered()
    {
        HeadlessRuntime.Dispatch(() =>
        {
            var viewModel = (MainWindowViewModel)Session.Inner.MainWindow.DataContext!;
            viewModel.ReplaceServices(new TestAccountConnector(), new TestSyncCoordinator { Empty = true });
        });
        Page.EnterText(static page => page.FolderInput, Path.Combine(Path.GetTempPath(), "ContactMirror-ui-empty"))
            .ClickButton(static page => page.ConnectGoogleButton)
            .WaitUntilNameContains(static page => page.AccountLabel, "example@example.test")
            .ClickButton(static page => page.CheckChangesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Изменений нет");
        HeadlessRuntime.Dispatch(() =>
        {
            Session.Inner.MainWindow.Width = 1000;
            Session.Inner.MainWindow.Height = 680;
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        });
        CaptureCheckpoint("empty-dark-1000x680");
        await TUnit.Assertions.Assert.That(Page.ApplyButton.IsEnabled).IsFalse();
        HeadlessRuntime.Dispatch(() => Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Default);
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Cancel_pending_scan_restores_working_controls()
    {
        HeadlessRuntime.Dispatch(() => ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).ReplaceServices(new TestAccountConnector(), new TestSyncCoordinator { BlockPrepare = true }));
        Page.EnterText(static page => page.FolderInput, Path.Combine(Path.GetTempPath(), "ContactMirror-ui-cancel"))
            .ClickButton(static page => page.ConnectGoogleButton)
            .WaitUntilNameContains(static page => page.AccountLabel, "example@example.test")
            .ClickButton(static page => page.CheckChangesButton)
            .ClickButton(static page => page.CancelButton)
            .WaitUntilNameContains(static page => page.StatusText, "Операция остановлена");
        await TUnit.Assertions.Assert.That(Page.CheckChangesButton.IsEnabled).IsTrue();
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Choosing_local_deleted_group_requires_explicit_deletion_confirmation()
    {
        var coordinator = new TestSyncCoordinator { GroupDeletionConflict = true };
        HeadlessRuntime.Dispatch(() => ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).ReplaceServices(new TestAccountConnector(), coordinator));
        PreparePreview();
        Page.ClickButton(static page => page.NextConflictButton)
            .ClickButton(static page => page.UseLocalButton)
            .ClickButton(static page => page.ApplyButton);
        await Assert.That(coordinator.AppliedCount).IsEqualTo(0);
        Page.EnterText(static page => page.DeletePhraseInput, "УДАЛИТЬ")
            .ClickButton(static page => page.ConfirmDeletesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Выполнено частично");
        await Assert.That(coordinator.AppliedCount).IsEqualTo(1);
        await Assert.That(coordinator.AppliedChoices[0].Resolution).IsEqualTo(ContactMirror.Core.Resolution.UseLocal);
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Backup_cleanup_requires_confirmation_and_disables_restore_after_removal()
    {
        var coordinator = new TestSyncCoordinator { HistoryComplete = true };
        HeadlessRuntime.Dispatch(() => ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).ReplaceServices(new TestAccountConnector(), coordinator));
        PreparePreview();
        Page.ClickButton(static page => page.HistoryButton).WaitUntilHasItemsAtLeast(static page => page.HistoryList, 1);
        Page.SelectListBoxItem(static page => page.HistoryList, Page.HistoryList.Items[0].Text!)
            .ClickButton(static page => page.CleanupBackupButton);
        await Assert.That(coordinator.CleanupCalls).IsEqualTo(0);
        CaptureCheckpoint("backup-cleanup-confirmation");
        Page.ClickButton(static page => page.ConfirmCleanupBackupButton)
            .WaitUntilNameContains(static page => page.StatusText, "Резервная копия удалена");
        await Assert.That(coordinator.CleanupCalls).IsEqualTo(1);
        Page.SelectListBoxItem(static page => page.HistoryList, Page.HistoryList.Items[0].Text!);
        await Assert.That(Page.CleanupBackupButton.IsEnabled).IsFalse();
        await Assert.That(Page.RestoreButton.IsEnabled).IsFalse();
        await Assert.That(coordinator.AppliedCount).IsEqualTo(0);
    }
    [Test]
    [NotInParallel("DesktopUi")]
    public async Task Unresolved_partial_run_cannot_remove_its_backup()
    {
        var coordinator = new TestSyncCoordinator();
        HeadlessRuntime.Dispatch(() => ((MainWindowViewModel)Session.Inner.MainWindow.DataContext!).ReplaceServices(new TestAccountConnector(), coordinator));
        PreparePreview();
        Page.ClickButton(static page => page.HistoryButton).WaitUntilHasItemsAtLeast(static page => page.HistoryList, 1);
        Page.SelectListBoxItem(static page => page.HistoryList, Page.HistoryList.Items[0].Text!);
        await Assert.That(Page.CleanupBackupButton.IsEnabled).IsFalse();
        await Assert.That(Page.RestoreButton.IsEnabled).IsTrue();
        await Assert.That(coordinator.CleanupCalls).IsEqualTo(0);
    }
    protected override HeadlessRuntimeSession LaunchSession()
    {
        var inner = DesktopAppSession.Launch(ContactMirrorAppLaunchHost.CreateHeadlessLaunchOptions());
        try
        {
            HeadlessRuntime.Dispatch(inner.MainWindow.Show);
            return new HeadlessRuntimeSession(inner);
        }
        catch
        {
            inner.Dispose();
            throw;
        }
    }

    protected override ValueTask<IReadOnlyList<UiFailureArtifact>> CollectFailureArtifactsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Session.Inner.CaptureScreenshot(Path.Combine(
            AppContext.BaseDirectory,
            "artifacts", "ui-failures", "avalonia-headless",
            Guid.NewGuid().ToString("N"), "test-failure.png"));
        return ValueTask.FromResult<IReadOnlyList<UiFailureArtifact>>(
        [new UiFailureArtifact("screenshot", "test-failure",
            Path.GetRelativePath(AppContext.BaseDirectory, path),
            "image/png", false, path)]);
    }

    protected override MainWindowPage CreatePage(HeadlessRuntimeSession session)
    {
        return new MainWindowPage(new HeadlessControlResolver(session.Inner.MainWindow));
    }

    public sealed class HeadlessRuntimeSession : IUiTestSession
    {
        public HeadlessRuntimeSession(DesktopAppSession inner)
        {
            Inner = inner;
        }

        public DesktopAppSession Inner { get; }

        public void Dispose()
        {
            try
            {
                HeadlessRuntime.Dispatch(Inner.MainWindow.Close);
            }
            finally
            {
                Inner.Dispose();
            }
        }
    }
}
