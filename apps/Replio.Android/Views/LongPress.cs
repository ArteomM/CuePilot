using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Replio.Android.Platform;

namespace Replio.Android.Views;

/// <summary>
/// Press and hold on a control with the class «longPress» (a session card, a group chip, a hint). The menu opens only
/// when the finger has stayed on the spot it pressed from the first touch to the end of the hold; pressing and then
/// pulling the screen (to scroll, or on a short list that does not scroll) is a scroll and never opens a menu, however
/// long the finger rested before it moved.
///
/// 1. Holding: the card sinks slowly under the finger.
/// 2. After <see cref="ReadyAfter"/> still: a short vibration, the card is ready. Moving now still cancels it.
/// 3. The menu opens when the finger lifts there, or after <see cref="OpenAfter"/> more of still holding.
///
/// Movement is checked on Android's own touch stream (<see cref="TouchTracker"/>), on Avalonia's pointer moves and on
/// any scroll, at every step and once more right before the menu appears. The tap that would follow is swallowed.
/// </summary>
public sealed class LongPress
{
    private static readonly TimeSpan ReadyAfter = TimeSpan.FromMilliseconds(480);
    private static readonly TimeSpan OpenAfter = TimeSpan.FromMilliseconds(360);

    // After the spring-back, so the menu comes as a result of the hold and not at the same instant.
    private static readonly TimeSpan MenuAfter = TimeSpan.FromMilliseconds(120);
    private const double MoveTolerance = 10;

    private enum Stage
    {
        Idle,
        Holding,
        Ready,
        Opened
    }

    private readonly Control _root;
    private readonly Action<Control> _onLongPress;
    private readonly DispatcherTimer _timer;
    private Control? _target;
    private Point _start;
    private Stage _stage;
    private DateTime _pressedAt;

    public LongPress(Control root, Action<Control> onLongPress)
    {
        _root = root;
        _onLongPress = onLongPress;
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => OnTimer();
        TouchTracker.MovedAway += () => Dispatcher.UIThread.Post(() => Cancel("finger moved"));
        root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(Gestures.ScrollGestureEvent, (_, _) => Cancel("scroll"), RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            // Only a real scroll; a list that grows or shrinks under the finger is not the finger moving.
            if (Math.Abs(e.OffsetDelta.X) > 0.5 || Math.Abs(e.OffsetDelta.Y) > 0.5)
            {
                Cancel("scroll");
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The finger is down and has not left the spot it pressed (as Android reports it).</summary>
    private static bool FingerStill(bool mustBeDown) =>
        !TouchTracker.Active || (!TouchTracker.Moved && (!mustBeDown || TouchTracker.IsDown));

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Reset();
        Control? target = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("longPress"));
        if (target == null)
        {
            return;
        }

        _target = target;
        _start = e.GetPosition(_root);
        _pressedAt = DateTime.UtcNow;
        _stage = Stage.Holding;
        _timer.Interval = ReadyAfter;
        _timer.Start();
        Motion.Hold(target, ReadyAfter);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_stage is not (Stage.Holding or Stage.Ready))
        {
            return;
        }

        Point now = e.GetPosition(_root);
        if (!FingerStill(mustBeDown: false) ||
            Math.Abs(now.X - _start.X) > MoveTolerance || Math.Abs(now.Y - _start.Y) > MoveTolerance)
        {
            Cancel("finger moved");
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        Stage stage = _stage;
        Control? target = _target;
        _timer.Stop();
        if (stage == Stage.Ready && target != null && FingerStill(mustBeDown: false))
        {
            // Held long enough and lifted on the same spot: the menu, and no tap on the card.
            e.Handled = true;
            Open(target, "lifted");
            return;
        }

        if (stage == Stage.Opened)
        {
            e.Handled = true;
        }
        else if (target != null && stage != Stage.Idle)
        {
            Motion.Release(target);
        }

        _stage = Stage.Idle;
        _target = null;
    }

    private void OnTimer()
    {
        _timer.Stop();
        Control? target = _target;
        if (target == null)
        {
            return;
        }

        if (!FingerStill(mustBeDown: true))
        {
            Cancel("finger moved or lifted");
            return;
        }

        if (_stage == Stage.Holding)
        {
            _stage = Stage.Ready;
            PhonePlatform.Log("[ui] long press ready");
            try
            {
                PhonePlatform.Haptic();
            }
            catch (Exception)
            {
                // No vibration on this phone.
            }

            _timer.Interval = OpenAfter;
            _timer.Start();
        }
        else if (_stage == Stage.Ready)
        {
            Open(target, "held");
        }
    }

    private void Open(Control target, string how)
    {
        _timer.Stop();
        _stage = Stage.Opened;
        PhonePlatform.Log($"[ui] long press ({how}, {(DateTime.UtcNow - _pressedAt).TotalMilliseconds:0} ms)");
        Motion.Spring(target);
        DispatcherTimer.RunOnce(() =>
        {
            // A finger that started to move in this last moment still wins: no menu.
            if (how == "held" && !FingerStill(mustBeDown: false))
            {
                PhonePlatform.Log("[ui] long press dropped: finger moved");
                return;
            }

            _onLongPress(target);
        }, MenuAfter);
    }

    /// <summary>The hold is not a hold: the card comes back, nothing opens.</summary>
    private void Cancel(string why)
    {
        if (_stage is not (Stage.Holding or Stage.Ready))
        {
            return;
        }

        if (_stage == Stage.Ready)
        {
            PhonePlatform.Log("[ui] long press cancelled: " + why);
        }

        Reset();
    }

    private void Reset()
    {
        _timer.Stop();
        if (_target != null && _stage is (Stage.Holding or Stage.Ready))
        {
            Motion.Release(_target);
        }

        _stage = Stage.Idle;
        _target = null;
    }
}
