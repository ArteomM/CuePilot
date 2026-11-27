#if REPLIO_DEMO
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Replio.Android.Platform;

namespace Replio.Android.Views;

/// <summary>
/// Emulator build of CI only: a finger made of code on the first session card, with timing adb cannot give. It goes
/// through the activity like a real touch, so Android's touch stream and the app both see it.
/// </summary>
public partial class MainView
{
    /// <summary>
    /// <paramref name="pull"/>: the finger rests 650 ms (past the moment the card is ready), then slides down 300 px and
    /// lifts: no menu. Otherwise it rests 650 ms and lifts on the spot: the menu.
    /// </summary>
    private void DemoTouch(bool pull)
    {
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                Button? card = this.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Classes.Contains("sessionCard") && b.IsEffectivelyVisible && b.Bounds.Height > 0);
                TopLevel? top = TopLevel.GetTopLevel(this);
                MainActivity? activity = MainActivity.Current;
                Point? center = card?.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), top!);
                if (center == null || top == null || activity == null)
                {
                    PhonePlatform.Log("[demo] touch: no session card");
                    return;
                }

                int[] origin = new int[2];
                activity.Window?.DecorView?.FindViewById(global::Android.Resource.Id.Content)?.GetLocationInWindow(origin);
                float x = (float)(center.Value.X * top.RenderScaling) + origin[0];
                float y = (float)(center.Value.Y * top.RenderScaling) + origin[1];
                long down = SystemClock.UptimeMillis();
                var handler = new Handler(Looper.MainLooper!);

                void Send(MotionEventActions action, float atY, long at) =>
                    handler.PostAtTime(() =>
                    {
                        MotionEvent? e = MotionEvent.Obtain(down, SystemClock.UptimeMillis(), action, x, atY, 0);
                        if (e == null)
                        {
                            return;
                        }

                        activity.DispatchTouchEvent(e);
                        e.Recycle();
                    }, down + at);

                PhonePlatform.Log($"[demo] touch: {(pull ? "rest, then pull" : "rest, then lift")} at {x:0},{y:0}");
                Send(MotionEventActions.Down, y, 0);
                if (pull)
                {
                    for (int i = 1; i <= 10; i++)
                    {
                        Send(MotionEventActions.Move, y + 30 * i, 640 + 16 * i);
                    }

                    Send(MotionEventActions.Up, y + 300, 820);
                }
                else
                {
                    Send(MotionEventActions.Up, y, 650);
                }
            }
            catch (Exception ex)
            {
                PhonePlatform.Log("[demo] touch failed: " + ex.Message);
            }
        }, TimeSpan.FromMilliseconds(1200));
    }
}
#endif
