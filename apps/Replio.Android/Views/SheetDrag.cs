using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Replio.Android.Platform;

namespace Replio.Android.Views;

/// <summary>
/// Pull a bottom sheet down to close it, as in Android apps: the sheet follows the finger and the page behind it
/// brightens; let go past a quarter of its height (or with a quick flick) and it slides away, otherwise it glides back.
/// Over a list inside the sheet the pull starts only when the list is at its top, so the list still scrolls. A button
/// the pull started on is not pressed.
/// </summary>
internal sealed class SheetDrag
{
    private const double Slop = 10;
    private const double FlickSpeed = 0.55; // pixels per millisecond

    private readonly Panel _layer;
    private readonly Control _panel;
    private readonly Func<bool> _isSheet;
    private readonly Action _dismiss;
    private readonly IBrush? _backdrop;
    private IPointer? _pointer;
    private Point _start;
    private bool _dragging;
    private double _offset;
    private double _lastY;
    private ulong _lastTime;
    private double _speed;

    public SheetDrag(Panel layer, Control panel, Func<bool> isSheet, Action dismiss)
    {
        _layer = layer;
        _panel = panel;
        _isSheet = isSheet;
        _dismiss = dismiss;
        _backdrop = layer.Background;
        panel.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        panel.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        panel.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        panel.PointerCaptureLost += (_, _) =>
        {
            if (_dragging)
            {
                Finish();
            }
        };
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _pointer = null;
        if (_dragging || !_isSheet() || InScrolledList(e.Source))
        {
            return;
        }

        _layer.Background = _backdrop;
        _pointer = e.Pointer;
        _start = e.GetPosition(_layer);
        _lastY = _start.Y;
        _lastTime = e.Timestamp;
        _speed = 0;
        _offset = 0;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer == null || !ReferenceEquals(e.Pointer, _pointer))
        {
            return;
        }

        Point now = e.GetPosition(_layer);
        double dx = now.X - _start.X;
        double dy = now.Y - _start.Y;
        if (!_dragging)
        {
            if (dy > Slop && dy > Math.Abs(dx) * 1.2)
            {
                // Taken: the sheet follows from here on, without a jump of the slop distance.
                _dragging = true;
                _start = new Point(_start.X, _start.Y + Slop);
                e.Pointer.Capture(_panel);
                Motion.BeginDrag(_panel);
            }
            else
            {
                if (Math.Abs(dx) > Slop * 2 || dy < -Slop)
                {
                    _pointer = null; // a sideways or upward move belongs to the content
                }

                return;
            }
        }

        _offset = Math.Max(0, now.Y - _start.Y);
        if (e.Timestamp > _lastTime)
        {
            double speed = (now.Y - _lastY) / (e.Timestamp - _lastTime);
            _speed = _speed * 0.4 + speed * 0.6;
        }

        _lastY = now.Y;
        _lastTime = e.Timestamp;
        Motion.DragTo(_panel, _offset);
        double left = 1 - Math.Min(1, _offset / Math.Max(1, _panel.Bounds.Height));
        _layer.Background = new SolidColorBrush(Color.FromArgb((byte)(0x99 * left), 0, 0, 0));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || !ReferenceEquals(e.Pointer, _pointer))
        {
            _pointer = null;
            return;
        }

        e.Handled = true;
        Finish();
    }

    private void Finish()
    {
        _dragging = false;
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        bool away = _offset > _panel.Bounds.Height * 0.25 || _speed > FlickSpeed;
        _offset = 0;
        PhonePlatform.Log(away ? "[ui] sheet pulled down: closed" : "[ui] sheet pulled down: back");
        if (away)
        {
            Motion.MarkFlung(_panel);
            _dismiss();
            DispatcherTimer.RunOnce(() => _layer.Background = _backdrop, TimeSpan.FromMilliseconds(320));
        }
        else
        {
            Motion.SnapBack(_panel);
            _layer.Background = _backdrop;
        }
    }

    /// <summary>The finger is on a list in the sheet that is scrolled down: the pull scrolls it up instead.</summary>
    private bool InScrolledList(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .TakeWhile(v => !ReferenceEquals(v, _panel))
            .OfType<ScrollViewer>()
            .Any(list => list.Offset.Y > 0.5) == true;
}
