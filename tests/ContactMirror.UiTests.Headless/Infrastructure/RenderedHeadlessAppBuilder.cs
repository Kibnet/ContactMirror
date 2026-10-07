using Avalonia;
using Avalonia.Headless;
using ContactMirror.AppAutomation.TestHost;

namespace ContactMirror.UiTests.Headless.Infrastructure;

public sealed class RenderedHeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure(
            () => (Avalonia.Application)Activator.CreateInstance(ContactMirrorAppLaunchHost.AvaloniaAppType)!)
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
