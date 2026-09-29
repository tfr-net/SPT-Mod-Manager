using Avalonia;
using Avalonia.Headless;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(SptModManager.App.Tests.TestAppBuilder))]

namespace SptModManager.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
