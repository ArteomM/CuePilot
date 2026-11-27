using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

namespace Replio.Android.Platform.Calls;

/// <summary>Echo colours, Manrope and small builders for the native views over a call (Avalonia does not draw there).</summary>
internal static class CallUi
{
    public static readonly Color Bg = Color.ParseColor("#0E1116");
    public static readonly Color Surface = Color.ParseColor("#171B22");
    public static readonly Color Alt = Color.ParseColor("#1E242D");
    public static readonly Color Border = Color.ParseColor("#2A313C");
    public static readonly Color Text = Color.ParseColor("#E9EEF5");
    public static readonly Color Muted = Color.ParseColor("#9AA5B5");
    public static readonly Color Mint = Color.ParseColor("#5EF0C1");
    public static readonly Color Red = Color.ParseColor("#E5484D");
    public static readonly Color Amber = Color.ParseColor("#E09B1A");

    private static readonly Dictionary<string, Typeface?> Fonts = [];

    public static int Dp(Context context, float dp) =>
        (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, context.Resources!.DisplayMetrics);

    public static Typeface Font(Context context, string weight)
    {
        if (!Fonts.TryGetValue(weight, out Typeface? face))
        {
            try
            {
                face = Typeface.CreateFromAsset(context.Assets, "fonts/Manrope-" + weight + ".ttf");
            }
            catch (Exception)
            {
                face = null;
            }

            Fonts[weight] = face;
        }

        return face ?? (weight is "Bold" or "ExtraBold" ? Typeface.DefaultBold! : Typeface.Default!);
    }

    public static GradientDrawable Round(Context context, Color fill, float radiusDp, Color? stroke = null)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(Dp(context, radiusDp));
        if (stroke is { } line)
        {
            drawable.SetStroke(Dp(context, 1), line);
        }

        return drawable;
    }

    public static TextView Label(Context context, string text, float sizeSp, Color color, string weight = "SemiBold")
    {
        var label = new TextView(context)
        {
            Text = text,
            TextSize = sizeSp
        };
        label.SetTextColor(color);
        label.SetTypeface(Font(context, weight), TypefaceStyle.Normal);
        label.SetLineSpacing(0, 1.15f);
        return label;
    }

    /// <summary>A Material icon from Resources/drawable (white vectors), tinted.</summary>
    public static ImageView Icon(Context context, int drawable, Color tint)
    {
        var icon = new ImageView(context);
        icon.SetImageResource(drawable);
        icon.ImageTintList = ColorStateList.ValueOf(tint);
        icon.SetScaleType(ImageView.ScaleType.FitCenter);
        return icon;
    }

    /// <summary>A round icon button, 44 dp (the touch area) with a 22 dp icon and a spoken name.</summary>
    public static FrameLayout IconButton(Context context, int drawable, Color tint, string description, Action click)
    {
        var button = new FrameLayout(context) { Clickable = true, ContentDescription = description };
        button.AddView(Icon(context, drawable, tint), new FrameLayout.LayoutParams(Dp(context, 22), Dp(context, 22), GravityFlags.Center));
        button.Click += (_, _) => click();
        Pressable(button, 22);
        return button;
    }

    /// <summary>A flat button: a rounded tile with a label, at least 48 dp tall.</summary>
    public static TextView Button(Context context, string text, Color fill, Color textColor, float radiusDp = 16, Color? stroke = null)
    {
        TextView button = Label(context, text, 15, textColor, "Bold");
        button.Gravity = GravityFlags.Center;
        button.SetMinHeight(Dp(context, 48));
        button.SetPadding(Dp(context, 14), 0, Dp(context, 14), 0);
        button.Background = Round(context, fill, radiusDp, stroke);
        button.Clickable = true;
        button.Focusable = false;
        button.Ellipsize = TextUtils.TruncateAt.End;
        button.SetMaxLines(1);
        return button;
    }

    /// <summary>
    /// Touch feedback like Material buttons: a soft ripple from the finger and a small press-in that springs back.
    /// The tap itself still reaches the view's Click.
    /// </summary>
    public static void Pressable(View view, float radiusDp = 20, bool light = true)
    {
        try
        {
            Color wave = light ? Color.Argb(46, 255, 255, 255) : Color.Argb(40, 14, 17, 22);
            view.Foreground = new RippleDrawable(ColorStateList.ValueOf(wave), null, Round(view.Context!, Color.White, radiusDp));
        }
        catch (Exception)
        {
            // An old system without foreground drawables: the press-in is enough.
        }

        view.Touch += (_, e) =>
        {
            switch (e.Event?.ActionMasked)
            {
                case MotionEventActions.Down:
                    view.Animate()?.ScaleX(0.95f).ScaleY(0.95f).SetDuration(90).SetInterpolator(new DecelerateInterpolator()).Start();
                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    view.Animate()?.ScaleX(1f).ScaleY(1f).SetDuration(240).SetInterpolator(new OvershootInterpolator(2.2f)).Start();
                    break;
            }

            e.Handled = false;
        };
    }

    /// <summary>Colours of a switch: mint when on, grey when off.</summary>
    public static ColorStateList Checked(Color on, Color off) =>
        new(new[] { new[] { global::Android.Resource.Attribute.StateChecked }, Array.Empty<int>() }, new[] { on.ToArgb(), off.ToArgb() });

    public static LinearLayout.LayoutParams Weighted(float weight, int height = ViewGroup.LayoutParams.WrapContent) =>
        new(0, height, weight);

    public static LinearLayout.LayoutParams Fill(int height = ViewGroup.LayoutParams.WrapContent) =>
        new(ViewGroup.LayoutParams.MatchParent, height);

    public static void Margins(View view, Context context, float left, float top, float right, float bottom)
    {
        if (view.LayoutParameters is ViewGroup.MarginLayoutParams margins)
        {
            margins.SetMargins(Dp(context, left), Dp(context, top), Dp(context, right), Dp(context, bottom));
            view.LayoutParameters = margins;
        }
    }
}

/// <summary>The Replio sound wave: five rounded bars, drawn in any colour (the logo on the button, the level meter).</summary>
internal sealed class WaveView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private float[] _heights = [0.4f, 0.72f, 1f, 0.6f, 0.34f];

    public WaveView(Context context, Color color, int bars = 5)
        : base(context)
    {
        _paint.Color = color;
        if (bars != 5)
        {
            _heights = Enumerable.Repeat(0.15f, bars).ToArray();
        }
    }

    public Color BarColor
    {
        get => _paint.Color;
        set
        {
            _paint.Color = value;
            Invalidate();
        }
    }

    /// <summary>Bar heights 0..1, left to right.</summary>
    public void SetHeights(float[] heights)
    {
        _heights = heights;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        int count = _heights.Length;
        if (count == 0 || Width == 0)
        {
            return;
        }

        float slot = Width / (float)count;
        float bar = Math.Max(2, slot * 0.5f);
        for (int i = 0; i < count; i++)
        {
            float h = Math.Clamp(_heights[i], 0.08f, 1f) * Height;
            float left = i * slot + (slot - bar) / 2;
            float top = (Height - h) / 2;
            canvas.DrawRoundRect(left, top, left + bar, top + h, bar / 2, bar / 2, _paint);
        }
    }
}
