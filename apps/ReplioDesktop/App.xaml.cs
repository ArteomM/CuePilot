using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ReplioDesktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\ReplioDesktop.Instance";
    private const string ShowEventName = @"Local\ReplioDesktop.Show";

    private static bool _fontDefaultsApplied;

    // Held for the whole life of the process: a second Replio would fail to register every hotkey.
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;

    public App()
    {
        // Not a static constructor: the pack:// URI scheme is registered by Application's own static
        // constructor, which runs after a derived static constructor but before this instance one.
        ApplyFontDefaults();
        ApplyToolTipDefaults();
    }

    /// <summary>
    /// One Replio at a time. Starting it again (shortcut, installer, the build folder) brings the running window
    /// to the front instead of opening a second copy that holds no hotkeys and writes the same files.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew && e.Args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase)))
        {
            // «Перезапустить Replio» (new data folder): the old copy is closing, wait for it instead of showing it.
            try
            {
                createdNew = _instanceMutex.WaitOne(TimeSpan.FromSeconds(20));
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
            }
        })
        {
            IsBackground = true,
            Name = "Replio single instance"
        };
        listener.Start();
        base.OnStartup(e);

        if (Services.LegacyDataImport.IsNeeded())
        {
            ImportCuePilotDataThenStart();
            return;
        }

        // Created here rather than through StartupUri, so a second copy never builds a window it would close at once.
        new MainWindow().Show();
    }

    /// <summary>
    /// First start after CuePilot became Replio: the old sessions, topics and settings are copied in the background
    /// behind a small «Переношу данные» window, then the main window opens with them.
    /// </summary>
    private async void ImportCuePilotDataThenStart()
    {
        ShutdownMode previousMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        bool russian = !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase);
        var splash = new Window
        {
            Title = "Replio",
            Width = 380,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = TryFindResource("EchoBgBrush") as Brush ?? Brushes.Black,
            Content = new TextBlock
            {
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("EchoTextBrush") as Brush ?? Brushes.White,
                Text = russian
                    ? "Готовлю ваши данные. Это займёт немного времени и бывает один раз."
                    : "Preparing your data. This takes a moment and happens once."
            }
        };
        splash.Show();
        // The outcome is written into %AppData%\Replio\imported-from-cuepilot.txt.
        await Task.Run(Services.LegacyDataImport.Run);

        // Closed first: the main window may show a message at start (busy hotkeys), which must not sit behind it.
        splash.Close();
        new MainWindow().Show();
        ShutdownMode = previousMode;
    }

    /// <summary>Hints show up after the cursor has rested on an element for 2 seconds, for every element.</summary>
    private static void ApplyToolTipDefaults()
    {
        try
        {
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(2000));
            ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
        }
        catch (Exception)
        {
            // Keep the system delay if the metadata was already overridden.
        }
    }

    /// <summary>
    /// «Эхо»: Manrope and a 14 px base size for every window, including tooltips, menus and popups,
    /// which do not inherit fonts from their owner window. Must run before the first window is created.
    /// </summary>
    private static void ApplyFontDefaults()
    {
        if (_fontDefaultsApplied)
        {
            return;
        }

        _fontDefaultsApplied = true;
        try
        {
            var font = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Manrope");
            foreach (Type type in new[] { typeof(Window), typeof(ToolTip), typeof(ContextMenu) })
            {
                TextElement.FontFamilyProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(font, FrameworkPropertyMetadataOptions.Inherits));
                TextElement.FontSizeProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.Inherits));
            }
        }
        catch (Exception)
        {
            // A missing font must never stop the app: windows fall back to their own FontFamily.
        }
    }
}
