using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// What Replio draws over a call: the draggable button (before a recording the logo, during it a split pill: the timer
/// on one side opens the menu, «Что сказать» on the other gives a hint at once), a compact menu at the top of the
/// screen that leaves the call's own buttons free, and the hint card. Native Android views, since the Avalonia screen
/// only lives inside the app. The windows belong to the «Слушать звонок» accessibility service when it is on (no
/// overlay permission needed, and Android then lets Replio record next to the call), otherwise to the app with the
/// overlay permission.
/// </summary>
internal sealed class OverlayController
{
    private const string PrefsName = "replio_calls";

    private readonly CallAssistant _assistant;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly Action _tick;

    private Context? _host;
    private IWindowManager? _windows;
    private WindowManagerTypes _type;

    private FrameLayout? _bubble;
    private WindowManagerLayoutParams? _bubbleParams;
    private View? _bubbleTile;
    private LinearLayout? _bubblePill;
    private TextView? _bubbleTimer;
    private WaveView? _bubbleLevel;
    private ImageView? _bubbleSpeakerOff;
    private View? _bubbleSay;
    private bool _dockRight = true;
    private bool _dragging;
    private TextView? _tip;
    private FrameLayout? _hideTarget;
    private bool _overHideTarget;

    private MenuRoot? _menu;
    private LinearLayout? _menuBody;
    private WindowManagerLayoutParams? _menuParams;
    private Action? _refreshMenu;
    private DateTime _menuClosedAt = DateTime.MinValue;
    private DateTime _stopArmedUntil = DateTime.MinValue;

    private FrameLayout? _hint;
    private WindowManagerLayoutParams? _hintParams;
    private LinearLayout? _hintCard;
    private TextView? _hintText;
    private TextView? _hintTitle;
    private TextView? _hintMore;
    private ScrollView? _hintScroll;
    private View? _hintButtons;
    private TextView? _hintRetry;
    private Action? _retry;
    private ObjectAnimator? _pulse;
    private bool _hintThinking;
    private bool _hintError;
    private bool? _bubbleShowsPill;

    public OverlayController(CallAssistant assistant)
    {
        _assistant = assistant;
        _tick = Tick;
    }

    public bool BubbleVisible => _bubble != null;

    public bool MenuVisible => _menu != null;

    /// <summary>The hint card is up and shows an answer someone may be reading (not «Думаю…», not an error).</summary>
    public bool HintShowsAnswer => _hint != null && !_hintThinking && !_hintError;

    public bool HintVisible => _hint != null;

    private static ISharedPreferences Prefs =>
        global::Android.App.Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    /// <summary>Picks the window owner: the accessibility service, else the app with the overlay permission.</summary>
    private bool EnsureHost()
    {
        Context? host = null;
        WindowManagerTypes type;
        if (ReplioAccessibilityService.Instance is { } service)
        {
            host = service;
            type = WindowManagerTypes.AccessibilityOverlay;
        }
        else
        {
            Context app = global::Android.App.Application.Context;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M && !Settings.CanDrawOverlays(app))
            {
                PhonePlatform.Log("[calls] No accessibility service and no overlay permission: the button cannot be shown.");
                return false;
            }

            host = app;
            type = WindowManagerTypes.ApplicationOverlay;
        }

        if (!ReferenceEquals(host, _host))
        {
            HideAll();
            _host = host;
            _type = type;
            _windows = host.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        }

        return _windows != null;
    }

    /// <summary>The accessibility service went away: its windows went with it.</summary>
    public void OnHostLost()
    {
        _bubble = null;
        _bubbleTile = null;
        _bubblePill = null;
        _bubbleSay = null;
        _hideTarget = null;
        _menu = null;
        _menuBody = null;
        _menuParams = null;
        _refreshMenu = null;
        _pulse?.Cancel();
        _pulse = null;
        _hint = null;
        _hintCard = null;
        _hintText = null;
        _hintTitle = null;
        _hintMore = null;
        _hintScroll = null;
        _hintButtons = null;
        _hintRetry = null;
        _bubbleShowsPill = null;
        _tip = null;
        _host = null;
        _windows = null;
        _handler.RemoveCallbacks(_tick);
    }

    // ---------------------------------------------------------------- the button

    public void ShowBubble(bool withTip)
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        if (_bubble == null)
        {
            BuildBubble(_host);
        }

        Refresh();
        if (withTip && !_assistant.IsRecording)
        {
            ShowTip(CallText.Get("BubbleTip"), 7000, null);
        }
    }

    public void HideBubble()
    {
        HideTip();
        HideHideTarget();
        Remove(ref _bubble);
        _bubbleTile = null;
        _bubblePill = null;
        _bubbleSay = null;
        _bubbleShowsPill = null;
        _handler.RemoveCallbacks(_tick);
    }

    public void HideAll()
    {
        HideMenu(animate: false);
        HideHint(animate: false);
        HideBubble();
    }

    private void BuildBubble(Context context)
    {
        int size = CallUi.Dp(context, 60);
        var root = new FrameLayout(context);

        // The logo tile: mint square with the dark wave, like the app icon. A tap records the call.
        var tile = new FrameLayout(context) { Background = CallUi.Round(context, CallUi.Mint, 20) };
        tile.Elevation = CallUi.Dp(context, 8);
        var logo = new WaveView(context, CallUi.Bg);
        tile.AddView(logo, new FrameLayout.LayoutParams(CallUi.Dp(context, 30), CallUi.Dp(context, 30), GravityFlags.Center));
        tile.ContentDescription = CallText.Get("MenuRecordCall");
        root.AddView(tile, new FrameLayout.LayoutParams(size, size));
        _bubbleTile = tile;

        // While recording: a split pill. The timer half (red dot, time, level or a crossed-out speaker) opens the menu,
        // the «Что сказать» half asks for a hint right away.
        var pill = new LinearLayout(context) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        pill.SetGravity(GravityFlags.CenterVertical);
        pill.Background = CallUi.Round(context, CallUi.Bg, 26);
        pill.Elevation = CallUi.Dp(context, 8);

        var timerPart = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        timerPart.SetGravity(GravityFlags.CenterVertical);
        timerPart.SetPadding(CallUi.Dp(context, 14), 0, CallUi.Dp(context, 10), 0);
        var dot = new View(context) { Background = CallUi.Round(context, CallUi.Red, 6) };
        timerPart.AddView(dot, new LinearLayout.LayoutParams(CallUi.Dp(context, 12), CallUi.Dp(context, 12)));
        _bubbleTimer = CallUi.Label(context, "00:00", 16, CallUi.Text, "ExtraBold");
        _bubbleTimer.SetPadding(CallUi.Dp(context, 8), 0, CallUi.Dp(context, 6), 0);
        timerPart.AddView(_bubbleTimer);
        _bubbleLevel = new WaveView(context, CallUi.Mint, 4);
        timerPart.AddView(_bubbleLevel, new LinearLayout.LayoutParams(CallUi.Dp(context, 18), CallUi.Dp(context, 18)));
        _bubbleSpeakerOff = CallUi.Icon(context, Resource.Drawable.ic_rp_volume_off, CallUi.Amber);
        _bubbleSpeakerOff.Visibility = ViewStates.Gone;
        timerPart.AddView(_bubbleSpeakerOff, new LinearLayout.LayoutParams(CallUi.Dp(context, 20), CallUi.Dp(context, 20)));
        pill.AddView(timerPart, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));

        var divider = new View(context);
        divider.SetBackgroundColor(CallUi.Border);
        pill.AddView(divider, new LinearLayout.LayoutParams(CallUi.Dp(context, 1), CallUi.Dp(context, 28)));

        var sayPart = new LinearLayout(context) { Orientation = Orientation.Horizontal, ContentDescription = CallText.Get("MenuWhatToSay") };
        sayPart.SetGravity(GravityFlags.CenterVertical);
        sayPart.SetPadding(CallUi.Dp(context, 10), 0, CallUi.Dp(context, 16), 0);
        sayPart.AddView(CallUi.Icon(context, Resource.Drawable.ic_rp_quote, CallUi.Mint), new LinearLayout.LayoutParams(CallUi.Dp(context, 18), CallUi.Dp(context, 18)));
        TextView sayLabel = CallUi.Label(context, CallText.Get("MenuWhatToSay"), 14.5f, CallUi.Mint, "ExtraBold");
        sayLabel.SetPadding(CallUi.Dp(context, 6), 0, 0, 0);
        sayPart.AddView(sayLabel);
        pill.AddView(sayPart, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
        _bubbleSay = sayPart;

        root.AddView(pill, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, CallUi.Dp(context, 52)));
        _bubblePill = pill;

        AttachDrag(root);

        // The side and height where the user left the button last time.
        var metrics = context.Resources!.DisplayMetrics!;
        int margin = CallUi.Dp(context, 8);
        _dockRight = Prefs.GetBoolean("bubble_right", true);
        float yFraction = Prefs.GetFloat("bubble_y", 0.5f);
        _bubbleParams = NewParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits);
        _bubbleParams.Gravity = GravityFlags.Top | GravityFlags.Left;
        _bubbleParams.X = _dockRight ? metrics.WidthPixels - size - margin : margin;
        _bubbleParams.Y = (int)(metrics.HeightPixels * Math.Clamp(yFraction, 0.05f, 0.85f));
        _bubble = root;

        // The pill is wider than the logo: on the right edge it grows to the left instead of off the screen.
        root.LayoutChange += (_, e) =>
        {
            if (_dragging || !_dockRight || _bubbleParams == null || !ReferenceEquals(_bubble, root) || e.Right - e.Left == e.OldRight - e.OldLeft)
            {
                return;
            }

            int width = root.Context!.Resources!.DisplayMetrics!.WidthPixels;
            _bubbleParams.X = width - root.Width - CallUi.Dp(root.Context, 8);
            Update(root, _bubbleParams);
        };

        // Pops in instead of appearing at once.
        root.Alpha = 0f;
        root.ScaleX = root.ScaleY = 0.5f;
        Add(root, _bubbleParams);
        root.Animate()?.Alpha(1f).ScaleX(1f).ScaleY(1f).SetDuration(320).SetInterpolator(new OvershootInterpolator(1.6f))?.Start();
    }

    /// <summary>
    /// Drag anywhere, let go: the button springs to the nearer side and remembers it; dropped on the «×» at the bottom it
    /// hides until the call ends. A tap records (logo), opens the menu (timer) or asks for a hint («Что сказать»).
    /// </summary>
    private void AttachDrag(View view)
    {
        float downX = 0, downY = 0;
        int startX = 0, startY = 0;
        bool moved = false;
        view.Touch += (_, e) =>
        {
            if (_bubbleParams == null || e.Event == null || _host == null)
            {
                return;
            }

            MotionEvent ev = e.Event;
            int slop = CallUi.Dp(_host, 8);
            View? pressed = _assistant.IsRecording ? _bubblePill : _bubbleTile;
            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    downX = ev.RawX;
                    downY = ev.RawY;
                    startX = _bubbleParams.X;
                    startY = _bubbleParams.Y;
                    moved = false;
                    pressed?.Animate()?.ScaleX(0.92f).ScaleY(0.92f).SetDuration(100)?.Start();
                    break;
                case MotionEventActions.Move:
                    if (!moved && (Math.Abs(ev.RawX - downX) > slop || Math.Abs(ev.RawY - downY) > slop))
                    {
                        moved = true;
                        _dragging = true;
                        HideTip();
                        ShowHideTarget();
                    }

                    if (moved)
                    {
                        _bubbleParams.X = startX + (int)(ev.RawX - downX);
                        _bubbleParams.Y = startY + (int)(ev.RawY - downY);
                        Update(view, _bubbleParams);
                        HighlightHideTarget(ev.RawX, ev.RawY);
                    }

                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    pressed?.Animate()?.ScaleX(1f).ScaleY(1f).SetDuration(260).SetInterpolator(new OvershootInterpolator(2.4f))?.Start();
                    if (moved)
                    {
                        _dragging = false;
                        bool hide = _overHideTarget;
                        HideHideTarget();
                        if (hide)
                        {
                            _assistant.HideBubbleForThisCall();
                            break;
                        }

                        SnapToEdge(view);
                    }
                    else if (ev.Action == MotionEventActions.Up)
                    {
                        HideTip();
                        OnBubbleTap(ev.RawX, ev.RawY);
                    }

                    break;
            }

            e.Handled = true;
        };
    }

    private void OnBubbleTap(float rawX, float rawY)
    {
        if (!_assistant.IsRecording)
        {
            // The first tap records the call: no menu in between (the menu is a tap on the timer later).
            _bubble?.PerformHapticFeedback(FeedbackConstants.VirtualKey);
            _assistant.StartFromBubble();
            return;
        }

        if (_bubbleSay != null && Inside(_bubbleSay, rawX, rawY))
        {
            _assistant.WhatToSay();
            return;
        }

        // A tap outside the open menu (on this button) already closed it: do not open it again at once.
        if (_menu == null && DateTime.Now - _menuClosedAt < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        ToggleMenu();
    }

    private static bool Inside(View view, float rawX, float rawY)
    {
        int[] spot = new int[2];
        view.GetLocationOnScreen(spot);
        return rawX >= spot[0] && rawX <= spot[0] + view.Width && rawY >= spot[1] && rawY <= spot[1] + view.Height;
    }

    /// <summary>To the nearer side with a small spring; the side and the height are remembered for the next call.</summary>
    private void SnapToEdge(View view)
    {
        if (_bubbleParams == null || _host == null)
        {
            return;
        }

        var metrics = _host.Resources!.DisplayMetrics!;
        int margin = CallUi.Dp(_host, 8);
        _dockRight = _bubbleParams.X + view.Width / 2 >= metrics.WidthPixels / 2;
        int fromX = _bubbleParams.X;
        int toX = _dockRight ? metrics.WidthPixels - view.Width - margin : margin;
        _bubbleParams.Y = Math.Clamp(_bubbleParams.Y, margin, Math.Max(margin, metrics.HeightPixels - view.Height - CallUi.Dp(_host, 96)));
        Prefs.Edit()?.PutBoolean("bubble_right", _dockRight)?.PutFloat("bubble_y", _bubbleParams.Y / (float)Math.Max(1, metrics.HeightPixels))?.Apply();

        ValueAnimator? spring = ValueAnimator.OfFloat(0f, 1f);
        if (spring == null)
        {
            _bubbleParams.X = toX;
            Update(view, _bubbleParams);
            return;
        }

        WindowManagerLayoutParams p = _bubbleParams;
        spring.SetDuration(320);
        spring.SetInterpolator(new OvershootInterpolator(1.1f));
        spring.Update += (_, _) =>
        {
            if (!view.IsAttachedToWindow || !ReferenceEquals(p, _bubbleParams))
            {
                return;
            }

            p.X = (int)(fromX + (toX - fromX) * spring.AnimatedFraction);
            Update(view, p);
        };
        spring.Start();
    }

    /// <summary>The round «×» at the bottom while the button is dragged: drop the button there to hide it for this call.</summary>
    private void ShowHideTarget()
    {
        if (_host == null || _hideTarget != null)
        {
            return;
        }

        Context context = _host;
        var target = new FrameLayout(context)
        {
            Background = CallUi.Round(context, Color.Argb(230, 14, 17, 22), 32, CallUi.Border),
            ContentDescription = CallText.Get("BubbleHideTarget")
        };
        target.AddView(CallUi.Icon(context, Resource.Drawable.ic_rp_close, CallUi.Text),
            new FrameLayout.LayoutParams(CallUi.Dp(context, 26), CallUi.Dp(context, 26), GravityFlags.Center));
        WindowManagerLayoutParams p = NewParams(CallUi.Dp(context, 64), CallUi.Dp(context, 64),
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable);
        p.Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal;
        p.Y = CallUi.Dp(context, 56);
        target.Alpha = 0f;
        target.ScaleX = target.ScaleY = 0.6f;
        Add(target, p);
        target.Animate()?.Alpha(1f).ScaleX(1f).ScaleY(1f).SetDuration(180)?.Start();
        _hideTarget = target;
        _overHideTarget = false;
    }

    private void HighlightHideTarget(float rawX, float rawY)
    {
        if (_hideTarget is not { } target || _host == null || target.Width == 0)
        {
            return;
        }

        int[] spot = new int[2];
        target.GetLocationOnScreen(spot);
        float cx = spot[0] + target.Width / 2f;
        float cy = spot[1] + target.Height / 2f;
        bool over = Math.Abs(rawX - cx) < CallUi.Dp(_host, 72) && Math.Abs(rawY - cy) < CallUi.Dp(_host, 72);
        if (over == _overHideTarget)
        {
            return;
        }

        _overHideTarget = over;
        target.Background = CallUi.Round(_host, over ? CallUi.Red : Color.Argb(230, 14, 17, 22), 32, over ? CallUi.Red : CallUi.Border);
        target.Animate()?.ScaleX(over ? 1.25f : 1f).ScaleY(over ? 1.25f : 1f).SetDuration(140)?.Start();
        if (over)
        {
            target.PerformHapticFeedback(FeedbackConstants.VirtualKey);
        }
    }

    private void HideHideTarget()
    {
        View? target = _hideTarget;
        _hideTarget = null;
        _overHideTarget = false;
        Remove(ref target);
    }

    /// <summary>A short note next to the button; <paramref name="click"/> null opens the menu (or records) on a tap.</summary>
    public void ShowTip(string text, int ms, Action? click)
    {
        if (_host == null || _bubbleParams == null || _bubble == null)
        {
            return;
        }

        HideTip();
        Context context = _host;
        _tip = CallUi.Label(context, text, 14, CallUi.Text, "SemiBold");
        _tip.SetPadding(CallUi.Dp(context, 14), CallUi.Dp(context, 12), CallUi.Dp(context, 14), CallUi.Dp(context, 12));
        _tip.Background = CallUi.Round(context, CallUi.Bg, 16, CallUi.Border);
        _tip.Elevation = CallUi.Dp(context, 8);
        _tip.SetMaxWidth(CallUi.Dp(context, 240));
        _tip.Click += (_, _) =>
        {
            HideTip();
            if (click != null)
            {
                click();
            }
            else
            {
                OnBubbleTap(-1, -1);
            }
        };
        // Under the button on its side (above it when the button sits low), so it never covers the timer pill.
        WindowManagerLayoutParams p = NewParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotFocusable);
        p.Gravity = GravityFlags.Top | (_dockRight ? GravityFlags.Right : GravityFlags.Left);
        p.X = CallUi.Dp(context, 8);
        int bubbleHeight = _bubble.Height > 0 ? _bubble.Height : CallUi.Dp(context, 60);
        int screen = context.Resources!.DisplayMetrics!.HeightPixels;
        int below = _bubbleParams.Y + bubbleHeight + CallUi.Dp(context, 8);
        p.Y = below + CallUi.Dp(context, 120) < screen ? below : Math.Max(0, _bubbleParams.Y - CallUi.Dp(context, 80));
        Add(_tip, p);
        TextView shown = _tip;
        _handler.PostDelayed(() =>
        {
            if (ReferenceEquals(_tip, shown))
            {
                HideTip();
            }
        }, ms);
    }

    private void HideTip()
    {
        View? tip = _tip;
        _tip = null;
        Remove(ref tip);
    }

    // ---------------------------------------------------------------- the menu

    public void ToggleMenu()
    {
        if (_menu != null)
        {
            HideMenu();
        }
        else
        {
            ShowMenu();
        }
    }

    /// <summary>
    /// A compact card at the top of the screen, no dim: the call's own buttons at the bottom stay visible and work, and a
    /// touch there closes the menu. <paramref name="topics"/> swaps the buttons for the group list inside the open card.
    /// </summary>
    public void ShowMenu(bool topics = false)
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        if (_menu != null && _menuBody != null)
        {
            FillMenu(_host, _menuBody, topics);
            Refresh();
            return;
        }

        Context context = _host;
        MenuRoot? root = null;
        root = new MenuRoot(
            context,
            () => HideMenu(),
            () =>
            {
                if (_menu is { } menu && ReferenceEquals(menu, root))
                {
                    menu.Animate()?.TranslationY(0).Alpha(1f).SetDuration(220).SetInterpolator(new DecelerateInterpolator(2f))?.Start();
                }
            });
        root.Orientation = Orientation.Vertical;
        int pad = CallUi.Dp(context, 14);
        root.SetPadding(pad, CallUi.Dp(context, 10), pad, CallUi.Dp(context, 12));
        root.Background = CallUi.Round(context, CallUi.Bg, 24, CallUi.Border);
        root.Elevation = CallUi.Dp(context, 12);

        var body = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var transition = new LayoutTransition();
        transition.EnableTransitionType(LayoutTransitionType.Changing);
        transition.SetDuration(200);
        body.LayoutTransition = transition;
        root.AddView(body, CallUi.Fill());
        _menuBody = body;
        FillMenu(context, body, topics);

        // A small handle at the bottom: the card is pushed up to close.
        var handle = new View(context) { Background = CallUi.Round(context, CallUi.Border, 2) };
        var handleParams = new LinearLayout.LayoutParams(CallUi.Dp(context, 40), CallUi.Dp(context, 4)) { Gravity = GravityFlags.CenterHorizontal };
        handleParams.TopMargin = CallUi.Dp(context, 10);
        root.AddView(handle, handleParams);

        var metrics = context.Resources!.DisplayMetrics!;
        int width = Math.Min(metrics.WidthPixels - CallUi.Dp(context, 24), CallUi.Dp(context, 440));
        WindowManagerLayoutParams p = NewParams(width, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotTouchModal | WindowManagerFlags.WatchOutsideTouch);
        p.Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal;
        p.Y = CallUi.Dp(context, 8);
        _menu = root;
        _menuParams = p;
        _stopArmedUntil = DateTime.MinValue;

        // Drops in from the top edge.
        root.Alpha = 0f;
        root.TranslationY = -CallUi.Dp(context, 48);
        Add(root, p);
        root.Animate()?.Alpha(1f).TranslationY(0).SetDuration(260).SetInterpolator(new DecelerateInterpolator(2.2f))?.Start();
        Refresh();
        StartTicking();
    }

    private void FillMenu(Context context, LinearLayout body, bool topics)
    {
        _refreshMenu = null;
        body.RemoveAllViews();
        bool rec = _assistant.IsRecording;

        // What Replio hears first (the speakerphone matters most), then «collapse».
        var head = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        var status = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var line = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        line.SetGravity(GravityFlags.CenterVertical);
        var dot = new View(context) { Background = CallUi.Round(context, CallUi.Red, 5) };
        line.AddView(dot, new LinearLayout.LayoutParams(CallUi.Dp(context, 10), CallUi.Dp(context, 10)) { RightMargin = CallUi.Dp(context, 8) });
        TextView timer = CallUi.Label(context, string.Empty, 20, CallUi.Text, "ExtraBold");
        line.AddView(timer);
        var level = new WaveView(context, CallUi.Mint, 10);
        line.AddView(level, new LinearLayout.LayoutParams(CallUi.Dp(context, 70), CallUi.Dp(context, 16)) { LeftMargin = CallUi.Dp(context, 10) });
        status.AddView(line);
        TextView idleTitle = CallUi.Label(context, "Replio", 19, CallUi.Text, "ExtraBold");
        status.AddView(idleTitle);
        var hearingRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        hearingRow.SetGravity(GravityFlags.CenterVertical);
        ImageView hearingIcon = CallUi.Icon(context, Resource.Drawable.ic_rp_volume_up, CallUi.Muted);
        hearingRow.AddView(hearingIcon, new LinearLayout.LayoutParams(CallUi.Dp(context, 16), CallUi.Dp(context, 16)) { RightMargin = CallUi.Dp(context, 6) });
        TextView hearing = CallUi.Label(context, string.Empty, 13, CallUi.Muted, "SemiBold");
        hearingRow.AddView(hearing, CallUi.Weighted(1));
        var hearingParams = CallUi.Fill();
        hearingParams.TopMargin = CallUi.Dp(context, 2);
        status.AddView(hearingRow, hearingParams);
        head.AddView(status, CallUi.Weighted(1));
        FrameLayout collapse = CallUi.IconButton(context, Resource.Drawable.ic_rp_close, CallUi.Muted, CallText.Get("MenuClose"), () => HideMenu());
        head.AddView(collapse, new LinearLayout.LayoutParams(CallUi.Dp(context, 44), CallUi.Dp(context, 44)) { LeftMargin = CallUi.Dp(context, 8) });
        body.AddView(head, CallUi.Fill());

        TextView? stop = null;
        if (topics)
        {
            body.AddView(BuildTopicList(context));
        }
        else if (rec)
        {
            stop = BuildStop(context);
            var stopParams = CallUi.Fill(CallUi.Dp(context, 48));
            stopParams.TopMargin = CallUi.Dp(context, 12);
            body.AddView(stop, stopParams);
            var tilesParams = CallUi.Fill();
            tilesParams.TopMargin = CallUi.Dp(context, 10);
            body.AddView(BuildTiles(context), tilesParams);
            body.AddView(BuildAutoHints(context));
        }
        else
        {
            body.AddView(BuildRecord(context));
            TextView note = CallUi.Label(context, CallText.Get("MenuNotRecording"), 12.5f, CallUi.Muted, "Medium");
            note.SetPadding(CallUi.Dp(context, 4), CallUi.Dp(context, 8), CallUi.Dp(context, 4), 0);
            body.AddView(note);
            body.AddView(BuildAutoHints(context));
        }

        body.AddView(BuildBottomRow(context, topics));

        var levels = Enumerable.Repeat(0.28f, 10).ToArray();
        bool dotOn = true;
        _refreshMenu = () =>
        {
            bool recording = _assistant.IsRecording;
            line.Visibility = recording ? ViewStates.Visible : ViewStates.Gone;
            idleTitle.Visibility = recording ? ViewStates.Gone : ViewStates.Visible;
            timer.Text = _assistant.ElapsedText;
            dotOn = !dotOn;
            dot.Alpha = dotOn ? 1f : 0.3f;
            Array.Copy(levels, 1, levels, 0, levels.Length - 1);
            // A floor keeps quiet moments as low bars, not a dashed line that looks broken.
            levels[^1] = 0.28f + (recording ? _assistant.Level * 0.72f : 0f);
            level.SetHeights((float[])levels.Clone());

            string text = _assistant.HearingLine(out bool problem, out bool speaker);
            hearing.Text = text;
            hearing.SetTextColor(problem ? CallUi.Amber : CallUi.Muted);
            hearingIcon.SetImageResource(speaker ? Resource.Drawable.ic_rp_volume_up : Resource.Drawable.ic_rp_volume_off);
            hearingIcon.ImageTintList = global::Android.Content.Res.ColorStateList.ValueOf(problem ? CallUi.Amber : CallUi.Muted);
            hearingRow.Visibility = text.Length == 0 ? ViewStates.Gone : ViewStates.Visible;

            if (stop != null)
            {
                bool armed = DateTime.Now < _stopArmedUntil;
                stop.Text = CallText.Get(armed ? "MenuStopConfirm" : "MenuStop");
                stop.Background = CallUi.Round(context, armed ? CallUi.Red : CallUi.Surface, 16, armed ? CallUi.Red : CallUi.Border);
                stop.SetTextColor(armed ? Color.White : CallUi.Red);
            }

            if (recording != rec && !topics && _menuBody != null && ReferenceEquals(_menuBody, body))
            {
                // Started or stopped while the menu is open: its buttons change with it.
                body.Post(() =>
                {
                    if (ReferenceEquals(_menuBody, body))
                    {
                        FillMenu(context, body, topics: false);
                    }
                });
            }
        };
    }

    /// <summary>«Записать звонок» with the microphone sign (before a recording).</summary>
    private View BuildRecord(Context context)
    {
        var button = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, ContentDescription = CallText.Get("MenuRecordCall") };
        button.SetGravity(GravityFlags.Center);
        button.Background = CallUi.Round(context, CallUi.Mint, 18);
        button.SetMinimumHeight(CallUi.Dp(context, 56));
        button.AddView(CallUi.Icon(context, Resource.Drawable.ic_rp_mic, CallUi.Bg), new LinearLayout.LayoutParams(CallUi.Dp(context, 22), CallUi.Dp(context, 22)));
        TextView label = CallUi.Label(context, CallText.Get("MenuRecordCall"), 17, CallUi.Bg, "ExtraBold");
        label.SetPadding(CallUi.Dp(context, 10), 0, 0, 0);
        button.AddView(label);
        button.Click += (_, _) =>
        {
            HideMenu();
            _assistant.StartFromBubble();
        };
        CallUi.Pressable(button, 18, light: false);
        var p = CallUi.Fill(CallUi.Dp(context, 56));
        p.TopMargin = CallUi.Dp(context, 12);
        button.LayoutParameters = p;
        return button;
    }

    /// <summary>Stopping takes two taps: the first turns the button red and asks once more for 3 s.</summary>
    private TextView BuildStop(Context context)
    {
        TextView stop = CallUi.Button(context, CallText.Get("MenuStop"), CallUi.Surface, CallUi.Red, 16, CallUi.Border);
        stop.Click += (_, _) =>
        {
            if (DateTime.Now < _stopArmedUntil)
            {
                _stopArmedUntil = DateTime.MinValue;
                HideMenu();
                _assistant.Stop();
                return;
            }

            _stopArmedUntil = DateTime.Now.AddSeconds(3);
            stop.PerformHapticFeedback(FeedbackConstants.VirtualKey);
            Refresh();
            _handler.PostDelayed(Refresh, 3100);
        };
        CallUi.Pressable(stop, 16);
        return stop;
    }

    /// <summary>Four tiles in one row under the thumb, a Material icon and a short name each.</summary>
    private View BuildTiles(Context context)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        int gap = CallUi.Dp(context, 8);
        bool playing = PhoneAudioSource.HasProjection;
        View[] tiles =
        [
            Tile(context, Resource.Drawable.ic_rp_quote, "MenuWhatToSay", primary: true, active: false, () =>
            {
                HideMenu();
                _assistant.WhatToSay();
            }),
            Tile(context, Resource.Drawable.ic_rp_notes, "MenuRecap", primary: false, active: false, () =>
            {
                HideMenu();
                _assistant.Recap();
            }),
            Tile(context, Resource.Drawable.ic_rp_screenshot, "MenuSnapshotShort", primary: false, active: false, () =>
            {
                HideMenu();
                _assistant.Snapshot();
            }),
            Tile(context, Resource.Drawable.ic_rp_eq, "MenuPlaybackShort", primary: false, active: playing, () =>
            {
                HideMenu();
                _assistant.TogglePlayback();
            })
        ];
        for (int i = 0; i < tiles.Length; i++)
        {
            var p = CallUi.Weighted(1, ViewGroup.LayoutParams.MatchParent);
            p.LeftMargin = i == 0 ? 0 : gap;
            row.AddView(tiles[i], p);
        }

        return row;
    }

    private static View Tile(Context context, int icon, string titleKey, bool primary, bool active, Action click)
    {
        var tile = new LinearLayout(context) { Orientation = Orientation.Vertical, Clickable = true };
        tile.SetGravity(GravityFlags.CenterHorizontal);
        tile.SetPadding(CallUi.Dp(context, 4), CallUi.Dp(context, 12), CallUi.Dp(context, 4), CallUi.Dp(context, 10));
        tile.SetMinimumHeight(CallUi.Dp(context, 78));
        tile.Background = primary
            ? CallUi.Round(context, CallUi.Mint, 18)
            : CallUi.Round(context, active ? CallUi.Alt : CallUi.Surface, 18, active ? CallUi.Mint : CallUi.Border);
        Color ink = primary ? CallUi.Bg : CallUi.Text;
        tile.AddView(CallUi.Icon(context, icon, primary ? CallUi.Bg : CallUi.Mint), new LinearLayout.LayoutParams(CallUi.Dp(context, 24), CallUi.Dp(context, 24)));
        string title = CallText.Get(titleKey);
        TextView label = CallUi.Label(context, title, 12.5f, ink, primary ? "ExtraBold" : "Bold");
        label.Gravity = GravityFlags.Center;
        label.SetMaxLines(2);
        label.SetPadding(0, CallUi.Dp(context, 6), 0, 0);
        CallUi.FitWords(label);
        tile.AddView(label, CallUi.Fill());
        tile.ContentDescription = title;
        tile.Click += (_, _) => click();
        CallUi.Pressable(tile, 18, light: !primary);
        return tile;
    }

    /// <summary>«Подсказка на каждый вопрос» with a switch: the whole row toggles it, the knob slides.</summary>
    private View BuildAutoHints(Context context)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(CallUi.Dp(context, 14), CallUi.Dp(context, 6), CallUi.Dp(context, 8), CallUi.Dp(context, 6));
        row.SetMinimumHeight(CallUi.Dp(context, 52));
        row.Background = CallUi.Round(context, CallUi.Surface, 18, CallUi.Border);
        string title = CallText.Get("MenuAutoHints");
        row.AddView(CallUi.Label(context, title, 14.5f, CallUi.Text, "Bold"), CallUi.Weighted(1));

        var toggle = new Switch(context) { Checked = _assistant.AutoHints, ContentDescription = title };
        toggle.ThumbTintList = CallUi.Checked(CallUi.Mint, CallUi.Muted);
        toggle.TrackTintList = CallUi.Checked(Color.Argb(110, 94, 240, 193), CallUi.Border);
        toggle.CheckedChange += (_, e) => _assistant.AutoHints = e.IsChecked;
        var toggleParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = CallUi.Dp(context, 8) };
        row.AddView(toggle, toggleParams);
        row.Click += (_, _) => toggle.Toggle();
        CallUi.Pressable(row, 18);

        var p = CallUi.Fill();
        p.TopMargin = CallUi.Dp(context, 10);
        row.LayoutParameters = p;
        return row;
    }

    /// <summary>The group (needed least on a call, so at the bottom) and «Открыть Replio».</summary>
    private View BuildBottomRow(Context context, bool topics)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var topic = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true };
        topic.SetGravity(GravityFlags.CenterVertical);
        topic.SetPadding(CallUi.Dp(context, 12), 0, CallUi.Dp(context, 8), 0);
        topic.AddView(CallUi.Icon(context, Resource.Drawable.ic_rp_folder, CallUi.Muted), new LinearLayout.LayoutParams(CallUi.Dp(context, 18), CallUi.Dp(context, 18)));
        TextView topicName = CallUi.Label(context, CallText.Format("MenuTopic", _assistant.CurrentTopicName), 13.5f, CallUi.Text, "SemiBold");
        topicName.SetPadding(CallUi.Dp(context, 8), 0, CallUi.Dp(context, 4), 0);
        topicName.SetMaxLines(1);
        topicName.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        topic.AddView(topicName, CallUi.Weighted(1));
        ImageView chevron = CallUi.Icon(context, Resource.Drawable.ic_rp_expand, CallUi.Muted);
        chevron.Rotation = topics ? 180 : 0;
        topic.AddView(chevron, new LinearLayout.LayoutParams(CallUi.Dp(context, 20), CallUi.Dp(context, 20)));
        topic.ContentDescription = topicName.Text;
        topic.Click += (_, _) => ShowMenu(topics: !topics);
        CallUi.Pressable(topic, 14);
        row.AddView(topic, CallUi.Weighted(1, CallUi.Dp(context, 44)));

        var open = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, ContentDescription = CallText.Get("MenuOpenApp") };
        open.SetGravity(GravityFlags.CenterVertical);
        open.SetPadding(CallUi.Dp(context, 10), 0, CallUi.Dp(context, 10), 0);
        TextView openLabel = CallUi.Label(context, CallText.Get("MenuOpenApp"), 13.5f, CallUi.Mint, "Bold");
        open.AddView(openLabel);
        open.AddView(CallUi.Icon(context, Resource.Drawable.ic_rp_open, CallUi.Mint), new LinearLayout.LayoutParams(CallUi.Dp(context, 16), CallUi.Dp(context, 16)) { LeftMargin = CallUi.Dp(context, 6) });
        open.Click += (_, _) =>
        {
            HideMenu();
            _assistant.OpenApp();
        };
        CallUi.Pressable(open, 14);
        row.AddView(open, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, CallUi.Dp(context, 44)) { LeftMargin = CallUi.Dp(context, 6) });

        var p = CallUi.Fill();
        p.TopMargin = CallUi.Dp(context, 8);
        row.LayoutParameters = p;
        return row;
    }

    /// <summary>The card goes back up and fades; <paramref name="animate"/> false removes it at once.</summary>
    public void HideMenu(bool animate = true)
    {
        _refreshMenu = null;
        MenuRoot? menu = _menu;
        IWindowManager? windows = _windows;
        _menu = null;
        _menuBody = null;
        _menuParams = null;
        if (menu == null)
        {
            return;
        }

        _menuClosedAt = DateTime.Now;
        if (!animate || menu.Height == 0)
        {
            RemoveFrom(windows, menu);
            return;
        }

        menu.Animate()?.Cancel();
        menu.Animate()?.TranslationY(-menu.Height * 0.4f).Alpha(0f).SetDuration(180).SetInterpolator(new AccelerateInterpolator(1.5f))
            ?.WithEndAction(new Java.Lang.Runnable(() => RemoveFrom(windows, menu)))?.Start();
    }

    private View BuildTopicList(Context context)
    {
        var list = new LinearLayout(context) { Orientation = Orientation.Vertical };
        list.SetPadding(0, CallUi.Dp(context, 10), 0, 0);
        foreach ((string name, bool selected, Action select) in _assistant.Topics())
        {
            var item = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, ContentDescription = name };
            item.SetGravity(GravityFlags.CenterVertical);
            item.SetPadding(CallUi.Dp(context, 14), 0, CallUi.Dp(context, 14), 0);
            item.Background = CallUi.Round(context, selected ? CallUi.Alt : CallUi.Surface, 14);
            TextView label = CallUi.Label(context, name, 15, selected ? CallUi.Mint : CallUi.Text, "Bold");
            label.SetMaxLines(1);
            label.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            item.AddView(label, CallUi.Weighted(1));
            if (selected)
            {
                item.AddView(CallUi.Icon(context, Resource.Drawable.ic_rp_check, CallUi.Mint), new LinearLayout.LayoutParams(CallUi.Dp(context, 20), CallUi.Dp(context, 20)));
            }

            item.Click += (_, _) =>
            {
                select();
                ShowMenu();
            };
            CallUi.Pressable(item, 14);
            var p = CallUi.Fill(CallUi.Dp(context, 50));
            p.BottomMargin = CallUi.Dp(context, 6);
            list.AddView(item, p);
        }

        var scroll = new ScrollView(context);
        scroll.AddView(list);
        int max = (int)(context.Resources!.DisplayMetrics!.HeightPixels * 0.32);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Min(max, CallUi.Dp(context, 56) * Math.Max(1, list.ChildCount) + CallUi.Dp(context, 10)));
        return scroll;
    }

    // ---------------------------------------------------------------- the hint card

    /// <summary>
    /// The card at the top of the screen: slides in with «Думаю…» pulsing, then the answer fades in. <paramref name="title"/>
    /// names it («ЧТО СКАЗАТЬ», «О ЧЁМ РЕЧЬ»); null keeps the current name. The card can be moved by its top row, pushed
    /// up to hide, copied, and its text made bigger.
    /// </summary>
    public void ShowHint(string text, bool thinking = false, string? title = null)
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        if (_hint == null || _hintText == null)
        {
            BuildHint(_host, title ?? CallText.Get("HintTitle"));
        }
        else if (title != null && _hintTitle != null)
        {
            _hintTitle.Text = title;
        }

        _hintError = false;
        _retry = null;
        _hintTitle?.SetTextColor(CallUi.Mint);
        SetHintButtons(error: false);
        SetHintText(text, thinking);
    }

    /// <summary>The hint did not come: the card stays, says why in one line and offers «Повторить».</summary>
    public void ShowHintError(string reason, Action? retry)
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        if (_hint == null || _hintText == null)
        {
            BuildHint(_host, CallText.Get("HintFailedTitle"));
        }

        _hintError = true;
        _retry = retry;
        if (_hintTitle != null)
        {
            _hintTitle.Text = CallText.Get("HintFailedTitle");
            _hintTitle.SetTextColor(CallUi.Amber);
        }

        SetHintButtons(error: true);
        SetHintText(reason, thinking: false);
    }

    /// <summary>A second «Что сказать» while one is on its way: the card nods and says so.</summary>
    public void PulseHint()
    {
        if (_hint == null)
        {
            ShowHint(CallText.Get("HintBusy"), thinking: true);
            return;
        }

        if (_hintCard is { } card)
        {
            card.Animate()?.ScaleX(1.03f).ScaleY(1.03f).SetDuration(110)
                ?.WithEndAction(new Java.Lang.Runnable(() => card.Animate()?.ScaleX(1f).ScaleY(1f).SetDuration(200).SetInterpolator(new OvershootInterpolator(2f))?.Start()))
                ?.Start();
        }
    }

    /// <summary>How many automatic hints wait behind the one on screen («Ещё 1»); 0 hides the mark.</summary>
    public void SetQueued(int count)
    {
        if (_hintMore == null)
        {
            return;
        }

        _hintMore.Text = CallText.Format("HintMore", count) + "  ›";
        _hintMore.Visibility = count > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private void BuildHint(Context context, string title)
    {
        var card = new LinearLayout(context) { Orientation = Orientation.Vertical };
        card.SetPadding(CallUi.Dp(context, 16), CallUi.Dp(context, 6), CallUi.Dp(context, 8), CallUi.Dp(context, 14));
        card.Background = CallUi.Round(context, CallUi.Bg, 22, CallUi.Border);
        card.Elevation = CallUi.Dp(context, 10);
        _hintCard = card;

        var head = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        _hintTitle = CallUi.Label(context, title, 12, CallUi.Mint, "ExtraBold");
        _hintTitle.SetMaxLines(2);
        head.AddView(_hintTitle, CallUi.Weighted(1));
        _hintMore = CallUi.Label(context, string.Empty, 12.5f, CallUi.Mint, "Bold");
        _hintMore.Visibility = ViewStates.Gone;
        _hintMore.Gravity = GravityFlags.Center;
        _hintMore.SetPadding(CallUi.Dp(context, 10), 0, CallUi.Dp(context, 10), 0);
        _hintMore.Background = CallUi.Round(context, CallUi.Alt, 14, CallUi.Mint);
        _hintMore.Clickable = true;
        _hintMore.Click += (_, _) => _assistant.ShowNextQueued();
        CallUi.Pressable(_hintMore, 14);
        head.AddView(_hintMore, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, CallUi.Dp(context, 30)) { RightMargin = CallUi.Dp(context, 4) });
        head.AddView(CallUi.IconButton(context, Resource.Drawable.ic_rp_text_size, CallUi.Muted, CallText.Get("HintTextSize"), NextTextSize),
            new LinearLayout.LayoutParams(CallUi.Dp(context, 44), CallUi.Dp(context, 44)));
        head.AddView(CallUi.IconButton(context, Resource.Drawable.ic_rp_copy, CallUi.Muted, CallText.Get("HintCopy"), CopyHint),
            new LinearLayout.LayoutParams(CallUi.Dp(context, 44), CallUi.Dp(context, 44)));
        head.AddView(CallUi.IconButton(context, Resource.Drawable.ic_rp_close, CallUi.Muted, CallText.Get("HintClose"), () => HideHint()),
            new LinearLayout.LayoutParams(CallUi.Dp(context, 44), CallUi.Dp(context, 44)));
        card.AddView(head, CallUi.Fill());

        _hintText = CallUi.Label(context, string.Empty, Prefs.GetFloat("hint_size", 17f), CallUi.Text, "SemiBold");
        _hintText.SetPadding(0, 0, CallUi.Dp(context, 8), 0);
        var scroll = new ScrollView(context);
        scroll.AddView(_hintText);
        _hintScroll = scroll;
        card.AddView(scroll, CallUi.Fill());

        var buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        TextView another = CallUi.Button(context, CallText.Get("HintAnother"), CallUi.Alt, CallUi.Text, 14);
        another.Click += (_, _) => _assistant.Another();
        CallUi.Pressable(another, 14);
        TextView shorter = CallUi.Button(context, CallText.Get("HintShorter"), CallUi.Alt, CallUi.Text, 14);
        shorter.Click += (_, _) => _assistant.Shorter();
        CallUi.Pressable(shorter, 14);
        buttons.AddView(another, CallUi.Weighted(1, CallUi.Dp(context, 48)));
        var shorterParams = CallUi.Weighted(1, CallUi.Dp(context, 48));
        shorterParams.LeftMargin = CallUi.Dp(context, 8);
        buttons.AddView(shorter, shorterParams);
        var buttonsParams = CallUi.Fill();
        buttonsParams.TopMargin = CallUi.Dp(context, 10);
        buttonsParams.RightMargin = CallUi.Dp(context, 8);
        card.AddView(buttons, buttonsParams);
        _hintButtons = buttons;

        TextView retry = CallUi.Button(context, CallText.Get("HintRetry"), CallUi.Mint, CallUi.Bg, 14);
        retry.Visibility = ViewStates.Gone;
        retry.Click += (_, _) => _retry?.Invoke();
        CallUi.Pressable(retry, 14, light: false);
        var retryParams = CallUi.Fill(CallUi.Dp(context, 48));
        retryParams.TopMargin = CallUi.Dp(context, 10);
        retryParams.RightMargin = CallUi.Dp(context, 8);
        card.AddView(retry, retryParams);
        _hintRetry = retry;

        WindowManagerLayoutParams p = NewParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotFocusable);
        p.Gravity = GravityFlags.Top;
        p.Y = Math.Max(0, Prefs.GetInt("hint_y", 0));
        int margin = CallUi.Dp(context, 12);
        var frame = new FrameLayout(context);
        frame.SetPadding(margin, CallUi.Dp(context, 8), margin, 0);
        frame.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _hint = frame;
        _hintParams = p;
        AttachHintDrag(head, frame, p);

        // Drops in from the top edge.
        frame.Alpha = 0f;
        frame.TranslationY = -CallUi.Dp(context, 56);
        Add(frame, p);
        frame.Animate()?.Alpha(1f).TranslationY(0).SetDuration(280).SetInterpolator(new DecelerateInterpolator(2f))?.Start();
    }

    /// <summary>The top row of the card moves it up or down (remembered); pushed above the screen edge, it hides.</summary>
    private void AttachHintDrag(View handle, FrameLayout frame, WindowManagerLayoutParams p)
    {
        float downY = 0;
        int startY = 0;
        bool moving = false;
        handle.Touch += (_, e) =>
        {
            if (e.Event == null || frame.Context == null)
            {
                return;
            }

            int slop = CallUi.Dp(frame.Context, 8);
            switch (e.Event.ActionMasked)
            {
                case MotionEventActions.Down:
                    downY = e.Event.RawY;
                    startY = p.Y;
                    moving = false;
                    break;
                case MotionEventActions.Move:
                    float dy = e.Event.RawY - downY;
                    if (!moving && Math.Abs(dy) > slop)
                    {
                        moving = true;
                    }

                    if (moving)
                    {
                        int y = startY + (int)dy;
                        p.Y = Math.Max(0, y);
                        frame.TranslationY = Math.Min(0, y);
                        frame.Alpha = y < 0 ? Math.Max(0.2f, 1f + y / (float)Math.Max(1, frame.Height)) : 1f;
                        Update(frame, p);
                    }

                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (moving)
                    {
                        if (frame.TranslationY < -CallUi.Dp(frame.Context, 40))
                        {
                            HideHint();
                        }
                        else
                        {
                            frame.Animate()?.TranslationY(0).Alpha(1f).SetDuration(180)?.Start();
                            Prefs.Edit()?.PutInt("hint_y", p.Y)?.Apply();
                        }
                    }

                    break;
            }

            e.Handled = moving || e.Event.ActionMasked == MotionEventActions.Down;
        };
    }

    private void SetHintButtons(bool error)
    {
        if (_hintButtons != null)
        {
            _hintButtons.Visibility = error ? ViewStates.Gone : ViewStates.Visible;
        }

        if (_hintRetry != null)
        {
            _hintRetry.Visibility = error && _retry != null ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    private void NextTextSize()
    {
        if (_hintText == null)
        {
            return;
        }

        float size = Prefs.GetFloat("hint_size", 17f);
        size = size < 19f ? 20f : size < 22f ? 23f : 17f;
        Prefs.Edit()?.PutFloat("hint_size", size)?.Apply();
        _hintText.TextSize = size;
        FitHintScroll();
    }

    private void CopyHint()
    {
        if (_hintText?.Context is not { } context || _hintThinking)
        {
            return;
        }

        try
        {
            var clipboard = (global::Android.Content.ClipboardManager?)context.GetSystemService(Context.ClipboardService);
            if (clipboard != null)
            {
                clipboard.PrimaryClip = ClipData.NewPlainText("Replio", _hintText.Text ?? string.Empty);
            }

            // Android 13+ says «Скопировано» by itself.
            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
            {
                CallAssistant.Toast(CallText.Get("HintCopied"));
            }
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Copy failed: " + ex.Message);
        }
    }

    private void SetHintText(string text, bool thinking)
    {
        TextView? label = _hintText;
        if (label == null)
        {
            return;
        }

        _hintThinking = thinking;
        _pulse?.Cancel();
        _pulse = null;
        bool changed = label.Text != text;
        label.Text = text;
        label.SetTextColor(thinking ? CallUi.Muted : CallUi.Text);
        if (thinking)
        {
            // «Думаю…» breathes until the answer comes.
            label.Alpha = 1f;
            ObjectAnimator? pulse = ObjectAnimator.OfFloat(label, "alpha", 1f, 0.35f);
            if (pulse != null)
            {
                pulse.SetDuration(650);
                pulse.RepeatCount = ValueAnimator.Infinite;
                pulse.RepeatMode = ValueAnimatorRepeatMode.Reverse;
                pulse.Start();
                _pulse = pulse;
            }
        }
        else if (changed)
        {
            label.Alpha = 0f;
            label.Animate()?.Alpha(1f).SetDuration(260)?.Start();
        }
        else
        {
            label.Alpha = 1f;
        }

        FitHintScroll();
    }

    /// <summary>A long answer scrolls inside the card instead of covering the call.</summary>
    private void FitHintScroll()
    {
        TextView? label = _hintText;
        ScrollView? scroll = _hintScroll;
        label?.Post(() =>
        {
            if (scroll?.LayoutParameters is not { } lp || label.Context?.Resources?.DisplayMetrics is not { } metrics)
            {
                return;
            }

            int maxText = (int)(metrics.HeightPixels * 0.3);
            int wanted = label.Height > maxText ? maxText : ViewGroup.LayoutParams.WrapContent;
            if (lp.Height != wanted)
            {
                lp.Height = wanted;
                scroll.LayoutParameters = lp;
            }
        });
    }

    /// <summary>The card slides back up; <paramref name="animate"/> false removes it at once.</summary>
    public void HideHint(bool animate = true)
    {
        _pulse?.Cancel();
        _pulse = null;
        _hintText = null;
        _hintTitle = null;
        _hintMore = null;
        _hintScroll = null;
        _hintButtons = null;
        _hintRetry = null;
        _hintCard = null;
        _hintThinking = false;
        _hintError = false;
        _retry = null;
        View? hint = _hint;
        IWindowManager? windows = _windows;
        _hint = null;
        _hintParams = null;
        _assistant.OnHintClosed();
        if (hint == null)
        {
            return;
        }

        if (!animate)
        {
            RemoveFrom(windows, hint);
            return;
        }

        hint.Animate()?.Alpha(0f).TranslationY(-CallUi.Dp(hint.Context!, 40)).SetDuration(180).SetInterpolator(new AccelerateInterpolator())
            ?.WithEndAction(new Java.Lang.Runnable(() => RemoveFrom(windows, hint)))?.Start();
    }

    // ---------------------------------------------------------------- the screenshot flash

    /// <summary>A white flash over the whole screen, like a camera, and the Replio button bounces.</summary>
    public void Flash()
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        var flash = new View(_host) { Alpha = 0.85f };
        flash.SetBackgroundColor(Color.White);
        WindowManagerLayoutParams p = NewParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits);
        IWindowManager? windows = _windows;
        Add(flash, p);
        flash.Animate()?.Alpha(0f).SetDuration(420).SetInterpolator(new DecelerateInterpolator(1.5f))
            ?.WithEndAction(new Java.Lang.Runnable(() => RemoveFrom(windows, flash)))?.Start();
        Bounce(_bubble);
    }

    private static void Bounce(View? view)
    {
        view?.Animate()?.ScaleX(1.18f).ScaleY(1.18f).SetDuration(140)
            ?.WithEndAction(new Java.Lang.Runnable(() =>
                view.Animate()?.ScaleX(1f).ScaleY(1f).SetDuration(280).SetInterpolator(new OvershootInterpolator(2f))?.Start()))
            ?.Start();
    }

    // ---------------------------------------------------------------- state

    /// <summary>Recording on or off, the timer, what Replio hears: called by the assistant whenever something changes.</summary>
    public void Refresh()
    {
        bool rec = _assistant.IsRecording;
        if (_bubbleTile != null && _bubblePill != null && _bubbleTimer != null && _host != null)
        {
            _bubbleTile.Visibility = rec ? ViewStates.Gone : ViewStates.Visible;
            _bubblePill.Visibility = rec ? ViewStates.Visible : ViewStates.Gone;
            if (_bubbleShowsPill != null && _bubbleShowsPill != rec)
            {
                // The logo turns into the timer pill (and back) with a short pop.
                View shown = rec ? _bubblePill : _bubbleTile;
                shown.Alpha = 0f;
                shown.ScaleX = shown.ScaleY = 0.8f;
                shown.Animate()?.Alpha(1f).ScaleX(1f).ScaleY(1f).SetDuration(240).SetInterpolator(new OvershootInterpolator(1.8f))?.Start();
            }

            _bubbleShowsPill = rec;
            _bubbleTimer.Text = _assistant.ElapsedText;
            _bubblePill.ContentDescription = CallText.Format("BubbleTimer", _assistant.ElapsedText);

            // Hearing only the user (no speakerphone): an amber ring and a crossed-out speaker instead of the level.
            _assistant.HearingLine(out bool problem, out _);
            var pill = (GradientDrawable)_bubblePill.Background!;
            pill.SetStroke(CallUi.Dp(_host, 2), problem ? CallUi.Amber : CallUi.Red);
            if (_bubbleLevel != null && _bubbleSpeakerOff != null)
            {
                _bubbleLevel.Visibility = problem ? ViewStates.Gone : ViewStates.Visible;
                _bubbleSpeakerOff.Visibility = problem ? ViewStates.Visible : ViewStates.Gone;
            }

            float l = _assistant.Level;
            _bubbleLevel?.SetHeights([0.3f + l * 0.4f, 0.4f + l * 0.6f, 0.25f + l * 0.7f, 0.35f + l * 0.5f]);
        }

        _refreshMenu?.Invoke();
        if (rec || _menu != null)
        {
            StartTicking();
        }
    }

    private void StartTicking()
    {
        _handler.RemoveCallbacks(_tick);
        _handler.PostDelayed(_tick, 500);
    }

    private void Tick()
    {
        if (_assistant.IsRecording || _menu != null)
        {
            Refresh();
        }
    }

    // ---------------------------------------------------------------- windows

    private WindowManagerLayoutParams NewParams(int width, int height, WindowManagerFlags flags)
    {
        var p = new WindowManagerLayoutParams(width, height, _type, flags, Format.Translucent);
        return p;
    }

    private void Add(View view, WindowManagerLayoutParams p)
    {
        try
        {
            _windows?.AddView(view, p);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Window could not be shown: " + ex.Message);
        }
    }

    private void Update(View view, WindowManagerLayoutParams p, IWindowManager? windows = null)
    {
        try
        {
            (windows ?? _windows)?.UpdateViewLayout(view, p);
        }
        catch (Exception)
        {
            // The window is already gone.
        }
    }

    /// <summary>Removes a window from the manager it was added to (an animation may end after the host changed).</summary>
    private static void RemoveFrom(IWindowManager? windows, View view)
    {
        try
        {
            windows?.RemoveViewImmediate(view);
        }
        catch (Exception)
        {
            // Already removed with its host.
        }
    }

    private void Remove<T>(ref T? view)
        where T : View
    {
        if (view == null)
        {
            return;
        }

        try
        {
            _windows?.RemoveViewImmediate(view);
        }
        catch (Exception)
        {
            // Already removed with its host.
        }

        view = null;
    }

    /// <summary>
    /// The menu card: a touch outside it (the call's buttons stay usable) or the back button closes it, and so does
    /// pushing it up by any spot (over the group list only when that list is at its top). A push that stops short
    /// glides back.
    /// </summary>
    private sealed class MenuRoot : LinearLayout
    {
        private const float FlickDpPerMs = 0.55f;

        private readonly Action _close;
        private readonly Action _settle;
        private readonly int _slop;
        private float _downX;
        private float _downY;
        private float _lastY;
        private long _lastTime;
        private float _speed;
        private bool _dragging;

        public MenuRoot(Context context, Action close, Action settle)
            : base(context)
        {
            _close = close;
            _settle = settle;
            _slop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? CallUi.Dp(context, 8);
            Focusable = true;
            FocusableInTouchMode = true;
        }

        public override bool OnInterceptTouchEvent(MotionEvent? e)
        {
            if (e == null)
            {
                return false;
            }

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    Down(e);
                    break;
                case MotionEventActions.Move:
                    return TryStart(e);
            }

            return false;
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null)
            {
                return false;
            }

            if (e.Action == MotionEventActions.Outside)
            {
                PhonePlatform.Log("[calls] menu closed by a touch outside");
                _close();
                return true;
            }

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    // On the card itself (the handle, the gaps): keep the touch so a push can follow.
                    Down(e);
                    return true;
                case MotionEventActions.Move:
                    if (_dragging || TryStart(e))
                    {
                        Drag(e);
                    }

                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_dragging)
                    {
                        if (e.ActionMasked == MotionEventActions.Up)
                        {
                            // The lift carries the last stretch of a quick flick (a slow phone can deliver few moves).
                            Drag(e);
                        }

                        _dragging = false;
                        float density = Resources?.DisplayMetrics?.Density ?? 1f;
                        if (-TranslationY > Height * 0.25f || -_speed / density > FlickDpPerMs)
                        {
                            PhonePlatform.Log("[calls] menu pushed up");
                            _close();
                        }
                        else
                        {
                            PhonePlatform.Log($"[calls] menu push too short ({-TranslationY:0} of {Height} px)");
                            _settle();
                        }
                    }

                    return true;
            }

            return base.OnTouchEvent(e);
        }

        private void Down(MotionEvent e)
        {
            PhonePlatform.Log($"[calls] menu touch at {e.RawX:0},{e.RawY:0}");
            _downX = e.RawX;
            _downY = e.RawY;
            _lastY = e.RawY;
            _lastTime = e.EventTime;
            _speed = 0;
            _dragging = false;
        }

        private bool TryStart(MotionEvent e)
        {
            if (_dragging)
            {
                return true;
            }

            float dy = _downY - e.RawY;
            float dx = e.RawX - _downX;
            if (dy <= _slop || dy <= Math.Abs(dx) * 1.2f || ScrolledUnder(this, _downX, _downY))
            {
                return false;
            }

            _dragging = true;
            PhonePlatform.Log("[calls] menu push started");
            _downY -= _slop; // no jump by the slop distance
            Animate()?.Cancel();
            return true;
        }

        private void Drag(MotionEvent e)
        {
            float offset = Math.Min(0, e.RawY - _downY);
            long dt = e.EventTime - _lastTime;
            if (dt > 0)
            {
                _speed = _speed * 0.4f + (e.RawY - _lastY) / dt * 0.6f;
            }

            _lastY = e.RawY;
            _lastTime = e.EventTime;
            TranslationY = offset;
            Alpha = 1f - Math.Min(0.7f, -offset / Math.Max(1, Height));
        }

        /// <summary>A list under the finger that can still scroll down: the push scrolls it instead.</summary>
        private static bool ScrolledUnder(ViewGroup group, float rawX, float rawY)
        {
            int[] spot = new int[2];
            for (int i = 0; i < group.ChildCount; i++)
            {
                View? child = group.GetChildAt(i);
                if (child == null || child.Visibility != ViewStates.Visible)
                {
                    continue;
                }

                child.GetLocationOnScreen(spot);
                if (rawX < spot[0] || rawX > spot[0] + child.Width || rawY < spot[1] || rawY > spot[1] + child.Height)
                {
                    continue;
                }

                if (child is ScrollView list && list.CanScrollVertically(1))
                {
                    return true;
                }

                if (child is ViewGroup inner && ScrolledUnder(inner, rawX, rawY))
                {
                    return true;
                }
            }

            return false;
        }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e?.KeyCode == Keycode.Back && e.Action == KeyEventActions.Up)
            {
                _close();
                return true;
            }

            return base.DispatchKeyEvent(e);
        }
    }
}
