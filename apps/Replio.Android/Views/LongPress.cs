using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Replio.Android.Platform;

namespace Replio.Android.Views;

/// <summary>
/// Press and hold, as in Android apps: on a control with the class «longPress» (a session card, a group chip, a hint)
/// the control sinks slowly under the finger; after about half a second the phone vibrates, the control springs back
/// and its menu slides up. The tap that would follow is swallowed. The menu opens only if the finger stayed where it
/// pressed the whole time: any slide (a scroll, a pull on a short list that does not scroll) cancels it, checked both
/// here and on Android's own touch stream (<see cref="TouchTracker"/>).
/// </summary>
public sealed class LongPress
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(520);

    // After the spring-back, so the menu comes as a result of the hold and not at the same instant.
    private static readonly TimeSpan MenuAfter = TimeSpan.FromMilliseconds(140);
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
        TouchTracker.MovedAway += () => Dispatcher.UIThread.Post(Cancel);
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(Gestures.ScrollGestureEvent, (_, _) => Cancel(), RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => Cancel(), RoutingStrategies.Bubble, handledEventsToo: true);
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
        Motion.Hold(_target, Delay);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_target == null || !_timer.IsEnabled)
        {
            return;
        }

        if (TouchTracker.Moved)
        {
            Cancel();
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
        if (_target != null && !_fired)
        {
            Motion.Release(_target);
        }

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
            if (_target != null)
            {
                Motion.Release(_target);
            }

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

        // The finger slid or is already up: a scroll or a pull, not a hold.
        if (TouchTracker.Active && (TouchTracker.Moved || !TouchTracker.IsDown))
        {
            Cancel();
            return;
        }

        _fired = true;
        PhonePlatform.Log("[ui] long press");
        try
        {
            PhonePlatform.Haptic();
        }
        catch (Exception)
        {
            // No vibration on this phone.
        }

        Motion.Spring(target);
        DispatcherTimer.RunOnce(() => _onLongPress(target), MenuAfter);
    }
}
