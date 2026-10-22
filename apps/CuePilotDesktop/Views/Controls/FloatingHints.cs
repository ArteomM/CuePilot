using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CuePilotDesktop.Views.Controls;

/// <summary>
/// Hover hints for the overlay and the indicator. WPF shows tooltips only in the active window, and these windows
/// float over the meeting app that has the focus, so their hints are drawn here: same text as the ToolTip,
/// same delay as everywhere else in CuePilot, in the night palette of the floating windows.
/// </summary>
public static class FloatingHints
{
    public static void Attach(Window window)
    {
        var text = new TextBlock
        {
            MaxWidth = 340,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF5))
        };
        var popup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = true,
            IsHitTestVisible = false,
            Focusable = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1E, 0x24, 0x2D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x3C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Child = text
            }
        };

        FrameworkElement? target = null;
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (target is { IsMouseOver: true } && HintOf(target) is string hint)
            {
                text.Text = hint;
                popup.PlacementTarget = target;
                popup.IsOpen = true;
            }
        };

        void Close()
        {
            timer.Stop();
            popup.IsOpen = false;
        }

        window.PreviewMouseMove += (_, e) =>
        {
            FrameworkElement? owner = FindOwner(e.OriginalSource as DependencyObject);
            if (ReferenceEquals(owner, target))
            {
                return;
            }

            Close();
            target = owner;
            if (owner == null)
            {
                return;
            }

            // The built-in tooltip would show a second copy once the window gets the focus.
            ToolTipService.SetIsEnabled(owner, false);
            timer.Interval = TimeSpan.FromMilliseconds(ToolTipService.GetInitialShowDelay(owner));
            timer.Start();
        };
        window.MouseLeave += (_, _) =>
        {
            Close();
            target = null;
        };
        window.PreviewMouseDown += (_, _) => Close();
        window.IsVisibleChanged += (_, _) => Close();
    }

    private static string? HintOf(FrameworkElement element) =>
        element.ToolTip is string hint && hint.Length > 0 ? hint : null;

    private static FrameworkElement? FindOwner(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is FrameworkElement element && HintOf(element) != null)
            {
                return element;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
