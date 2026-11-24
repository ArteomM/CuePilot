using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Replio.Avalonia.Views;

namespace Replio.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the main window hides Replio into the menu bar; «Выход» there quits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // First start after CuePilot became Replio: copy the old data (never overwrites, runs once).
            ReplioDesktop.Services.LegacyDataImport.Run();
            desktop.MainWindow = new MainWindow(MainWindow.ResolveProjectRoot());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
