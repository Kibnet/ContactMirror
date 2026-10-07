using Avalonia.Headless;
using AppAutomation.Avalonia.Headless.Session;
using ContactMirror.AppAutomation.TestHost;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Infrastructure;

public static class HeadlessSessionHooks
{
    private static HeadlessUnitTestSession? _session;

    [Before(TestSession)]
    public static void SetupSession()
    {
        _session = HeadlessUnitTestSession.StartNew(
            typeof(RenderedHeadlessAppBuilder),
            AvaloniaTestIsolationLevel.PerAssembly);
        HeadlessRuntime.SetSession(_session);
    }

    [After(TestSession)]
    public static void CleanupSession()
    {
        HeadlessRuntime.SetSession(null);
        _session?.Dispose();
        _session = null;
    }
}
