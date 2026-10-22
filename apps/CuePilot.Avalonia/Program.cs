using Avalonia;

namespace CuePilot.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Popups, menus and tooltips draw inside their window on macOS, so hiding the window from
            // screen sharing (NSWindow.sharingType) hides them too.
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
