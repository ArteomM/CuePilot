using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Replio.Android.Views;

/// <summary>
/// Motion of the phone screen, after Material: a sheet slides up from the bottom while the page behind it dims, a
/// dialog grows out of the middle, a page slides in a little from the side, and a card held under the finger sinks
/// slowly before its menu opens. Nothing appears or disappears in one frame.
/// </summary>
internal static class Motion
{
    public enum Kind
    {
        /// <summary>From the bottom edge (action sheets, the calls setup).</summary>
        Sheet,

        /// <summary>From the middle, growing (questions and messages).</summary>
        Pop
    }

    private static readonly TransformOperations Rest = TransformOperations.Parse("translate(0px, 0px)");
    private static readonly TransformOperations FullSize = TransformOperations.Parse("scale(1)");

    // A newer Show or Hide of the same overlay wins over an older one that has not finished yet.
    private static readonly ConditionalWeakTable<Control, StrongBox<int>> Generations = new();

    private static int Next(Control layer) => ++Generations.GetValue(layer, _ => new StrongBox<int>()).Value;

    private static bool Current(Control layer, int generation) =>
        Generations.TryGetValue(layer, out StrongBox<int>? box) && box.Value == generation;

    private static DoubleTransition Fade(int ms, Easing? easing = null) => new()
    {
        Property = Visual.OpacityProperty,
        Duration = TimeSpan.FromMilliseconds(ms),
        Easing = easing ?? new CubicEaseOut()
    };

    private static TransformOperationsTransition Move(int ms, Easing easing) => new()
    {
        Property = Visual.RenderTransformProperty,
        Duration = TimeSpan.FromMilliseconds(ms),
        Easing = easing
    };

    private static TransformOperations TranslateY(double y) => TransformOperations.Parse(FormattableString.Invariant($"translate(0px, {y:0.#}px)"));

    private static TransformOperations TranslateX(double x) => TransformOperations.Parse(FormattableString.Invariant($"translate({x:0.#}px, 0px)"));

    private static TransformOperations Scale(double s) => TransformOperations.Parse(FormattableString.Invariant($"scale({s:0.###})"));

    /// <summary>Shows an overlay: the backdrop fades in, the panel slides up (sheet) or grows (dialog).</summary>
    public static void Show(Control layer, Control panel, Kind kind)
    {
        int generation = Next(layer);
        layer.Transitions = null;
        panel.Transitions = null;
        layer.Opacity = 0;
        panel.Opacity = kind == Kind.Sheet ? 1 : 0;
        // Far below for the first frame; the real offset is the panel's height, known after layout.
        panel.RenderTransform = kind == Kind.Sheet ? TranslateY(2000) : Scale(0.9);
        layer.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Current(layer, generation))
            {
                return;
            }

            if (kind == Kind.Sheet)
            {
                panel.RenderTransform = TranslateY(Math.Max(240, panel.Bounds.Height + 24));
            }

            layer.Transitions = new Transitions { Fade(220) };
            panel.Transitions = kind == Kind.Sheet
                ? new Transitions { Move(340, new QuinticEaseOut()) }
                : new Transitions { Move(260, new BackEaseOut()), Fade(200) };
            layer.Opacity = 1;
            panel.Opacity = 1;
            panel.RenderTransform = kind == Kind.Sheet ? Rest : FullSize;
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Hides an overlay the reverse way and then calls <paramref name="done"/> (to clear its content); a Show in the
    /// meantime keeps it open and skips <paramref name="done"/>.
    /// </summary>
    public static void Hide(Control layer, Control panel, Kind kind, Action? done = null)
    {
        int generation = Next(layer);
        if (!layer.IsVisible)
        {
            done?.Invoke();
            return;
        }

        layer.Transitions = new Transitions { Fade(200) };
        panel.Transitions = kind == Kind.Sheet
            ? new Transitions { Move(220, new CubicEaseIn()) }
            : new Transitions { Move(180, new CubicEaseIn()), Fade(160) };
        layer.Opacity = 0;
        if (kind == Kind.Sheet)
        {
            panel.RenderTransform = TranslateY(Math.Max(240, panel.Bounds.Height + 24));
        }
        else
        {
            panel.Opacity = 0;
            panel.RenderTransform = Scale(0.94);
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (!Current(layer, generation))
            {
                return;
            }

            layer.IsVisible = false;
            done?.Invoke();
        }, TimeSpan.FromMilliseconds(230));
    }

    /// <summary>A page that just opened fades in and slides a little from the side it came from.</summary>
    public static void Enter(Control page, double fromX)
    {
        page.Transitions = null;
        page.Opacity = 0;
        page.RenderTransform = TranslateX(fromX);
        Dispatcher.UIThread.Post(() =>
        {
            page.Transitions = new Transitions { Fade(220), Move(300, new QuinticEaseOut()) };
            page.Opacity = 1;
            page.RenderTransform = Rest;
        }, DispatcherPriority.Background);
    }

    /// <summary>Press and hold: the card sinks slowly while the finger stays, so the menu does not come as a surprise.</summary>
    public static void Hold(Control target, TimeSpan holdTime)
    {
        target.Transitions = null;
        target.RenderTransform = FullSize;
        target.Opacity = 1;
        target.Transitions = new Transitions { Move((int)holdTime.TotalMilliseconds, new SineEaseOut()), Fade((int)holdTime.TotalMilliseconds, new SineEaseOut()) };
        target.RenderTransform = Scale(0.95);
        target.Opacity = 0.82;
    }

    /// <summary>The finger moved or let go early: the card comes back quickly.</summary>
    public static void Release(Control target)
    {
        target.Transitions = new Transitions { Move(160, new CubicEaseOut()), Fade(160) };
        target.RenderTransform = FullSize;
        target.Opacity = 1;
    }

    /// <summary>The hold is long enough: the card springs back, the menu follows.</summary>
    public static void Spring(Control target)
    {
        target.Transitions = new Transitions { Move(320, new BackEaseOut()), Fade(200) };
        target.RenderTransform = FullSize;
        target.Opacity = 1;
    }
}
