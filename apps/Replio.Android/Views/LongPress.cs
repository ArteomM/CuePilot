using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Replio.Android.Views;

/// <summary>
/// Press and hold, as in Android apps: half a second on a control with the class «longPress» (a session card, a
/// group chip, a hint) calls back with that control, after a short vibration; the tap that would follow is
/// swallowed. Moving the finger (scrolling) cancels it.
/// </summary>
public sealed class LongPress
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(480);
    private const double MoveTolerance = 12;

    private readonly Control _root;
    private readonly Action<Control> _onLongPress;
    private readonly DispatcherTimer _timer;
    private Control? _target;
    private Point _start;
    private bool _fired;

    public LongPress(Control root, Action<Control> onLongPress)
    {
        _root = root;
        _onLongPress = onLongPress;
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += (_, _) => Fire();
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Cancel();
        _fired = false;
        _target = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("longPress"));
        if (_target == null)
        {
            return;
        }

        _start = e.GetPosition(_root);
        _timer.Start();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_target == null || !_timer.IsEnabled)
        {
            return;
        }

        Point now = e.GetPosition(_root);
        if (Math.Abs(now.X - _start.X) > MoveTolerance || Math.Abs(now.Y - _start.Y) > MoveTolerance)
        {
            Cancel();
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _timer.Stop();
        _target = null;
        if (_fired)
        {
            // The finger is lifted after a long press: no tap on the card under it.
            _fired = false;
            e.Handled = true;
        }
    }

    private void Cancel()
    {
        _timer.Stop();
        if (!_fired)
        {
            _target = null;
        }
    }

    private void Fire()
    {
        _timer.Stop();
        Control? target = _target;
        if (target == null)
        {
            return;
        }

        _fired = true;
        try
        {
            Replio.Android.Platform.PhonePlatform.Haptic();
        }
        catch (Exception)
        {
            // No vibration on this phone.
        }

        _onLongPress(target);
    }
}
