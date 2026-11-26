using Android.Content;
using Android.Views;

namespace Replio.Android.Platform;

/// <summary>
/// The finger on the Replio screen as Android itself reports it, before the app's own input handling. A press and hold
/// asks it whether the finger is still down where it pressed: a finger that slid (a pull on a short list that does
/// not scroll, a swipe) must never open a menu.
/// </summary>
public static class TouchTracker
{
    private static float _startX;
    private static float _startY;
    private static int _slop = -1;

    /// <summary>At least one touch has come through (mouse-only input never sets it, so nothing is blocked then).</summary>
    public static bool Active { get; private set; }

    /// <summary>A finger is on the screen.</summary>
    public static bool IsDown { get; private set; }

    /// <summary>The finger left the spot it pressed (or a second finger came) since it went down.</summary>
    public static bool Moved { get; private set; }

    /// <summary>Raised once per touch, on the main thread, when <see cref="Moved"/> turns true.</summary>
    public static event Action? MovedAway;

    public static void Track(MotionEvent? e, Context context)
    {
        if (e == null)
        {
            return;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                Active = true;
                IsDown = true;
                Moved = false;
                _startX = e.RawX;
                _startY = e.RawY;
                break;
            case MotionEventActions.Move:
                if (!Moved)
                {
                    if (_slop < 0)
                    {
                        _slop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
                    }

                    if (Math.Abs(e.RawX - _startX) > _slop || Math.Abs(e.RawY - _startY) > _slop)
                    {
                        MarkMoved();
                    }
                }

                break;
            case MotionEventActions.PointerDown:
                MarkMoved();
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                IsDown = false;
                break;
        }
    }

    private static void MarkMoved()
    {
        if (Moved)
        {
            return;
        }

        Moved = true;
        try
        {
            MovedAway?.Invoke();
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[touch] " + ex.Message);
        }
    }
}
