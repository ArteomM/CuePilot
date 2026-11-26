using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace Replio.Android.Platform.Calls;

/// <summary>
/// What Replio draws over a call: the draggable button (a timer pill while recording), the menu sheet at the bottom
/// and the hint card at the top. Native Android views, since the Avalonia screen only lives inside the app.
/// The windows belong to the «Слушать звонок» accessibility service when it is on (no overlay permission needed,
/// and Android then lets Replio record next to the call), otherwise to the app with the overlay permission.
/// </summary>
internal sealed class OverlayController
{
    private readonly CallAssistant _assistant;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly Action _tick;

    private Context? _host;
    private IWindowManager? _windows;
    private WindowManagerTypes _type;

    private FrameLayout? _bubble;
    private WindowManagerLayoutParams? _bubbleParams;
    private WaveView? _bubbleLogo;
    private LinearLayout? _bubblePill;
    private TextView? _bubbleTimer;
    private WaveView? _bubbleLevel;
    private TextView? _tip;

    private MenuRoot? _menu;
    private Action? _refreshMenu;
    private View? _hint;
    private TextView? _hintText;

    public OverlayController(CallAssistant assistant)
    {
        _assistant = assistant;
        _tick = Tick;
    }

    public bool BubbleVisible => _bubble != null;

    public bool MenuVisible => _menu != null;

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
        _menu = null;
        _hint = null;
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
            ShowTip(CallText.Get("BubbleTip"));
        }
    }

    public void HideBubble()
    {
        HideTip();
        Remove(ref _bubble);
        _handler.RemoveCallbacks(_tick);
    }

    public void HideAll()
    {
        HideMenu();
        HideHint();
        HideBubble();
    }

    private void BuildBubble(Context context)
    {
        int size = CallUi.Dp(context, 60);
        var root = new FrameLayout(context);

        // The logo tile: mint square with the dark wave, like the app icon.
        var tile = new FrameLayout(context) { Background = CallUi.Round(context, CallUi.Mint, 20) };
        tile.Elevation = CallUi.Dp(context, 8);
        _bubbleLogo = new WaveView(context, CallUi.Bg);
        tile.AddView(_bubbleLogo, new FrameLayout.LayoutParams(CallUi.Dp(context, 30), CallUi.Dp(context, 30), GravityFlags.Center));
        root.AddView(tile, new FrameLayout.LayoutParams(size, size));

        // While recording: a pill with a red dot, the time and the level.
        _bubblePill = new LinearLayout(context) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        _bubblePill.SetGravity(GravityFlags.CenterVertical);
        _bubblePill.SetPadding(CallUi.Dp(context, 14), 0, CallUi.Dp(context, 14), 0);
        _bubblePill.Background = CallUi.Round(context, CallUi.Bg, 26, CallUi.Red);
        ((GradientDrawable)_bubblePill.Background).SetStroke(CallUi.Dp(context, 2), CallUi.Red);
        _bubblePill.Elevation = CallUi.Dp(context, 8);
        var dot = new View(context) { Background = CallUi.Round(context, CallUi.Red, 6) };
        _bubblePill.AddView(dot, new LinearLayout.LayoutParams(CallUi.Dp(context, 12), CallUi.Dp(context, 12)));
        _bubbleTimer = CallUi.Label(context, "00:00", 16, CallUi.Text, "ExtraBold");
        _bubbleTimer.SetPadding(CallUi.Dp(context, 8), 0, CallUi.Dp(context, 8), 0);
        _bubblePill.AddView(_bubbleTimer);
        _bubbleLevel = new WaveView(context, CallUi.Mint, 4);
        _bubblePill.AddView(_bubbleLevel, new LinearLayout.LayoutParams(CallUi.Dp(context, 18), CallUi.Dp(context, 18)));
        root.AddView(_bubblePill, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, CallUi.Dp(context, 52)));

        root.ContentDescription = CallText.Get("MenuOpenApp");
        AttachDrag(root, tile);

        var metrics = context.Resources!.DisplayMetrics!;
        _bubbleParams = NewParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits);
        _bubbleParams.Gravity = GravityFlags.Top | GravityFlags.Left;
        _bubbleParams.X = metrics.WidthPixels - size - CallUi.Dp(context, 12);
        _bubbleParams.Y = (int)(metrics.HeightPixels * 0.5);
        _bubble = root;
        Add(root, _bubbleParams);
    }

    /// <summary>Drag anywhere, let go: the button sticks to the nearer side. A tap without moving opens the menu.</summary>
    private void AttachDrag(View view, View tile)
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
            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    downX = ev.RawX;
                    downY = ev.RawY;
                    startX = _bubbleParams.X;
                    startY = _bubbleParams.Y;
                    moved = false;
                    tile.ScaleX = tile.ScaleY = 0.94f;
                    break;
                case MotionEventActions.Move:
                    if (!moved && (Math.Abs(ev.RawX - downX) > slop || Math.Abs(ev.RawY - downY) > slop))
                    {
                        moved = true;
                        HideTip();
                    }

                    if (moved)
                    {
                        _bubbleParams.X = startX + (int)(ev.RawX - downX);
                        _bubbleParams.Y = startY + (int)(ev.RawY - downY);
                        Update(view, _bubbleParams);
                    }

                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    tile.ScaleX = tile.ScaleY = 1f;
                    if (moved)
                    {
                        int width = _host.Resources!.DisplayMetrics!.WidthPixels;
                        int margin = CallUi.Dp(_host, 8);
                        bool left = _bubbleParams.X + view.Width / 2 < width / 2;
                        _bubbleParams.X = left ? margin : width - view.Width - margin;
                        _bubbleParams.Y = Math.Max(margin, _bubbleParams.Y);
                        Update(view, _bubbleParams);
                    }
                    else if (ev.Action == MotionEventActions.Up)
                    {
                        HideTip();
                        ToggleMenu();
                    }

                    break;
            }

            e.Handled = true;
        };
    }

    private void ShowTip(string text)
    {
        if (_host == null || _bubbleParams == null)
        {
            return;
        }

        HideTip();
        Context context = _host;
        _tip = CallUi.Label(context, text, 14, CallUi.Text, "SemiBold");
        _tip.SetPadding(CallUi.Dp(context, 14), CallUi.Dp(context, 12), CallUi.Dp(context, 14), CallUi.Dp(context, 12));
        _tip.Background = CallUi.Round(context, CallUi.Bg, 16, CallUi.Border);
        _tip.Elevation = CallUi.Dp(context, 8);
        _tip.SetMaxWidth(CallUi.Dp(context, 220));
        _tip.Click += (_, _) =>
        {
            HideTip();
            ToggleMenu();
        };
        int width = context.Resources!.DisplayMetrics!.WidthPixels;
        bool bubbleOnRight = _bubbleParams.X > width / 2;
        WindowManagerLayoutParams p = NewParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotFocusable);
        p.Gravity = GravityFlags.Top | (bubbleOnRight ? GravityFlags.Right : GravityFlags.Left);
        p.X = CallUi.Dp(context, 84);
        p.Y = Math.Max(0, _bubbleParams.Y - CallUi.Dp(context, 30));
        Add(_tip, p);
        TextView shown = _tip;
        _handler.PostDelayed(() =>
        {
            if (ReferenceEquals(_tip, shown))
            {
                HideTip();
            }
        }, 7000);
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

    public void ShowMenu(bool topics = false)
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        HideMenu();
        Context context = _host;
        var root = new MenuRoot(context, HideMenu);
        root.Orientation = Orientation.Vertical;
        int pad = CallUi.Dp(context, 16);
        root.SetPadding(pad, CallUi.Dp(context, 10), pad, CallUi.Dp(context, 20));
        var sheet = new GradientDrawable();
        sheet.SetColor(CallUi.Bg);
        float r = CallUi.Dp(context, 28);
        sheet.SetCornerRadii([r, r, r, r, 0, 0, 0, 0]);
        root.Background = sheet;

        var handle = new View(context) { Background = CallUi.Round(context, CallUi.Border, 2) };
        var handleParams = new LinearLayout.LayoutParams(CallUi.Dp(context, 40), CallUi.Dp(context, 4)) { Gravity = GravityFlags.CenterHorizontal };
        handleParams.BottomMargin = CallUi.Dp(context, 14);
        root.AddView(handle, handleParams);

        // Topic and «collapse».
        var top = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        TextView topic = CallUi.Button(context, CallText.Format("MenuTopic", _assistant.CurrentTopicName), CallUi.Surface, CallUi.Text, 14, CallUi.Border);
        topic.Gravity = GravityFlags.CenterVertical | GravityFlags.Start;
        topic.Click += (_, _) => ShowMenu(topics: !topics);
        top.AddView(topic, CallUi.Weighted(1, CallUi.Dp(context, 48)));
        TextView collapse = CallUi.Button(context, "⌄", CallUi.Surface, CallUi.Text, 14);
        collapse.TextSize = 22;
        collapse.ContentDescription = "Свернуть";
        collapse.Click += (_, _) => HideMenu();
        var collapseParams = new LinearLayout.LayoutParams(CallUi.Dp(context, 48), CallUi.Dp(context, 48)) { LeftMargin = CallUi.Dp(context, 10) };
        top.AddView(collapse, collapseParams);
        root.AddView(top, CallUi.Fill());

        if (topics)
        {
            root.AddView(BuildTopicList(context), CallUi.Fill());
        }
        else
        {
            root.AddView(BuildMainRow(context), CallUi.Fill());
            root.AddView(BuildTiles(context), CallUi.Fill());
            root.AddView(BuildAsk(context), CallUi.Fill());
        }

        TextView open = CallUi.Label(context, CallText.Get("MenuOpenApp"), 14, CallUi.Mint, "Bold");
        open.Gravity = GravityFlags.Center;
        open.SetMinHeight(CallUi.Dp(context, 44));
        open.Click += (_, _) =>
        {
            HideMenu();
            _assistant.OpenApp();
        };
        root.AddView(open, CallUi.Fill());

        WindowManagerLayoutParams p = NewParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.DimBehind | WindowManagerFlags.WatchOutsideTouch);
        p.Gravity = GravityFlags.Bottom;
        p.DimAmount = 0.45f;
        p.SoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden;
        _menu = root;
        Add(root, p);
        Refresh();
        StartTicking();
    }

    public void HideMenu()
    {
        _refreshMenu = null;
        if (_menu != null && _host?.GetSystemService(Context.InputMethodService) is InputMethodManager keyboard)
        {
            keyboard.HideSoftInputFromWindow(_menu.WindowToken, HideSoftInputFlags.None);
        }

        View? menu = _menu;
        _menu = null;
        Remove(ref menu);
    }

    private View BuildMainRow(Context context)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(CallUi.Dp(context, 4), CallUi.Dp(context, 18), CallUi.Dp(context, 4), CallUi.Dp(context, 18));

        int big = CallUi.Dp(context, 92);
        var main = new FrameLayout(context) { Clickable = true };
        var stopSquare = new View(context) { Background = CallUi.Round(context, Color.White, 8) };
        var mic = new WaveView(context, CallUi.Bg);
        main.AddView(mic, new FrameLayout.LayoutParams(CallUi.Dp(context, 40), CallUi.Dp(context, 40), GravityFlags.Center));
        main.AddView(stopSquare, new FrameLayout.LayoutParams(CallUi.Dp(context, 30), CallUi.Dp(context, 30), GravityFlags.Center));
        main.Click += (_, _) => _assistant.ToggleRecording();
        row.AddView(main, new LinearLayout.LayoutParams(big, big));

        var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
        texts.SetPadding(CallUi.Dp(context, 18), 0, 0, 0);
        TextView title = CallUi.Label(context, "", 24, CallUi.Text, "ExtraBold");
        TextView sub = CallUi.Label(context, "", 14, CallUi.Muted, "Medium");
        var level = new WaveView(context, CallUi.Mint, 12);
        texts.AddView(title);
        texts.AddView(sub);
        var levelParams = new LinearLayout.LayoutParams(CallUi.Dp(context, 96), CallUi.Dp(context, 18)) { TopMargin = CallUi.Dp(context, 8) };
        texts.AddView(level, levelParams);
        row.AddView(texts, CallUi.Weighted(1));

        var levels = new float[12];
        _refreshMenu = () =>
        {
            bool rec = _assistant.IsRecording;
            main.Background = CallUi.Round(context, rec ? CallUi.Red : CallUi.Mint, 46);
            main.ContentDescription = rec ? CallText.Get("ActionStop") : CallText.Get("MenuStart");
            mic.Visibility = rec ? ViewStates.Gone : ViewStates.Visible;
            stopSquare.Visibility = rec ? ViewStates.Visible : ViewStates.Gone;
            title.Text = rec ? _assistant.ElapsedText : CallText.Get("MenuStart");
            sub.Text = _assistant.HearingText;
            Array.Copy(levels, 1, levels, 0, levels.Length - 1);
            levels[^1] = rec ? _assistant.Level : 0.05f;
            level.BarColor = rec ? CallUi.Mint : CallUi.Border;
            level.SetHeights((float[])levels.Clone());
        };
        return row;
    }

    private View BuildTiles(Context context)
    {
        var grid = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var first = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        var second = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        int h = CallUi.Dp(context, 84);
        int gap = CallUi.Dp(context, 10);

        View say = Tile(context, "❝", CallText.Get("MenuWhatToSay"), primary: true, () =>
        {
            HideMenu();
            _assistant.WhatToSay();
        });
        View auto = Tile(context, _assistant.AutoHints ? "●" : "○", CallText.Get("MenuAutoHints"), primary: false, () =>
        {
            _assistant.AutoHints = !_assistant.AutoHints;
            ShowMenu();
        });
        View shot = Tile(context, "▣", CallText.Get("MenuSnapshot"), primary: false, () =>
        {
            HideMenu();
            _assistant.Snapshot();
        });
        View recap = Tile(context, "≡", CallText.Get("MenuRecap"), primary: false, () =>
        {
            HideMenu();
            _assistant.Recap();
        });

        first.AddView(say, CallUi.Weighted(1, h));
        first.AddView(auto, CallUi.Weighted(1, h));
        second.AddView(shot, CallUi.Weighted(1, h));
        second.AddView(recap, CallUi.Weighted(1, h));
        ((LinearLayout.LayoutParams)auto.LayoutParameters!).LeftMargin = gap;
        ((LinearLayout.LayoutParams)recap.LayoutParameters!).LeftMargin = gap;
        grid.AddView(first, CallUi.Fill());
        var secondParams = CallUi.Fill();
        secondParams.TopMargin = gap;
        grid.AddView(second, secondParams);
        return grid;
    }

    private static View Tile(Context context, string glyph, string text, bool primary, Action click)
    {
        var tile = new LinearLayout(context) { Orientation = Orientation.Vertical, Clickable = true };
        tile.SetPadding(CallUi.Dp(context, 14), CallUi.Dp(context, 12), CallUi.Dp(context, 14), CallUi.Dp(context, 12));
        tile.Background = primary
            ? CallUi.Round(context, CallUi.Mint, 20)
            : CallUi.Round(context, CallUi.Surface, 20, CallUi.Border);
        Color ink = primary ? CallUi.Bg : CallUi.Text;
        TextView icon = CallUi.Label(context, glyph, 18, primary ? CallUi.Bg : CallUi.Mint, "Bold");
        tile.AddView(icon, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, 0, 1));
        tile.AddView(CallUi.Label(context, text, 16, ink, primary ? "ExtraBold" : "Bold"));
        tile.Click += (_, _) => click();
        return tile;
    }

    private View BuildAsk(Context context)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(CallUi.Dp(context, 14), 0, CallUi.Dp(context, 6), 0);
        row.Background = CallUi.Round(context, CallUi.Surface, 16, CallUi.Border);
        var input = new EditText(context)
        {
            Hint = CallText.Get("MenuAsk"),
            Background = null,
            TextSize = 15,
            ImeOptions = ImeAction.Send
        };
        input.SetSingleLine(true);
        input.SetTextColor(CallUi.Text);
        input.SetHintTextColor(CallUi.Muted);
        input.SetTypeface(CallUi.Font(context, "Medium"), TypefaceStyle.Normal);
        row.AddView(input, CallUi.Weighted(1, CallUi.Dp(context, 52)));
        TextView send = CallUi.Button(context, "➤", CallUi.Mint, CallUi.Bg, 12);
        send.ContentDescription = "Спросить";
        row.AddView(send, new LinearLayout.LayoutParams(CallUi.Dp(context, 44), CallUi.Dp(context, 44)));

        void Submit()
        {
            string question = input.Text?.Trim() ?? string.Empty;
            if (question.Length == 0)
            {
                return;
            }

            HideMenu();
            _assistant.Ask(question);
        }

        send.Click += (_, _) => Submit();
        input.EditorAction += (_, e) =>
        {
            if (e.ActionId == ImeAction.Send)
            {
                Submit();
                e.Handled = true;
            }
        };
        var p = CallUi.Fill();
        p.TopMargin = CallUi.Dp(context, 12);
        row.LayoutParameters = p;
        return row;
    }

    private View BuildTopicList(Context context)
    {
        var list = new LinearLayout(context) { Orientation = Orientation.Vertical };
        list.SetPadding(0, CallUi.Dp(context, 10), 0, 0);
        foreach ((string name, bool selected, Action select) in _assistant.Topics())
        {
            TextView item = CallUi.Button(context, name, selected ? CallUi.Alt : CallUi.Surface, selected ? CallUi.Mint : CallUi.Text, 14);
            item.Gravity = GravityFlags.CenterVertical | GravityFlags.Start;
            item.Click += (_, _) =>
            {
                select();
                ShowMenu();
            };
            var p = CallUi.Fill(CallUi.Dp(context, 52));
            p.BottomMargin = CallUi.Dp(context, 6);
            list.AddView(item, p);
        }

        var scroll = new ScrollView(context);
        scroll.AddView(list);
        int max = (int)(context.Resources!.DisplayMetrics!.HeightPixels * 0.45);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Min(max, CallUi.Dp(context, 58) * Math.Max(1, list.ChildCount) + CallUi.Dp(context, 10)));
        return scroll;
    }

    // ---------------------------------------------------------------- the hint card

    public void ShowHint(string text, bool thinking = false)
    {
        if (!EnsureHost() || _host == null)
        {
            return;
        }

        if (_hint != null && _hintText != null)
        {
            _hintText.Text = text;
            _hintText.SetTextColor(thinking ? CallUi.Muted : CallUi.Text);
            return;
        }

        Context context = _host;
        var card = new LinearLayout(context) { Orientation = Orientation.Vertical };
        card.SetPadding(CallUi.Dp(context, 16), CallUi.Dp(context, 12), CallUi.Dp(context, 16), CallUi.Dp(context, 14));
        card.Background = CallUi.Round(context, CallUi.Bg, 22, CallUi.Border);
        card.Elevation = CallUi.Dp(context, 10);

        var head = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.AddView(CallUi.Label(context, CallText.Get("HintTitle"), 12, CallUi.Mint, "ExtraBold"), CallUi.Weighted(1));
        TextView close = CallUi.Label(context, "✕", 18, CallUi.Muted, "Bold");
        close.Gravity = GravityFlags.Center;
        close.ContentDescription = "Скрыть подсказку";
        close.Click += (_, _) => HideHint();
        head.AddView(close, new LinearLayout.LayoutParams(CallUi.Dp(context, 44), CallUi.Dp(context, 40)));
        card.AddView(head, CallUi.Fill());

        _hintText = CallUi.Label(context, text, 17, thinking ? CallUi.Muted : CallUi.Text, "SemiBold");
        var scroll = new ScrollView(context);
        scroll.AddView(_hintText);
        int maxText = (int)(context.Resources!.DisplayMetrics!.HeightPixels * 0.3);
        _hintText.Post(() =>
        {
            if (scroll.LayoutParameters is { } lp && _hintText != null && _hintText.Height > maxText)
            {
                lp.Height = maxText;
                scroll.LayoutParameters = lp;
            }
        });
        card.AddView(scroll, CallUi.Fill());

        var buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        TextView another = CallUi.Button(context, CallText.Get("HintAnother"), CallUi.Alt, CallUi.Text, 14);
        another.Click += (_, _) => _assistant.Another();
        TextView shorter = CallUi.Button(context, CallText.Get("HintShorter"), CallUi.Alt, CallUi.Text, 14);
        shorter.Click += (_, _) => _assistant.Shorter();
        buttons.AddView(another, CallUi.Weighted(1, CallUi.Dp(context, 46)));
        var shorterParams = CallUi.Weighted(1, CallUi.Dp(context, 46));
        shorterParams.LeftMargin = CallUi.Dp(context, 8);
        buttons.AddView(shorter, shorterParams);
        var buttonsParams = CallUi.Fill();
        buttonsParams.TopMargin = CallUi.Dp(context, 10);
        card.AddView(buttons, buttonsParams);

        WindowManagerLayoutParams p = NewParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerFlags.NotFocusable);
        p.Gravity = GravityFlags.Top;
        int margin = CallUi.Dp(context, 12);
        p.HorizontalMargin = 0;
        card.SetMinimumWidth(context.Resources.DisplayMetrics.WidthPixels - 2 * margin);
        var frame = new FrameLayout(context);
        frame.SetPadding(margin, CallUi.Dp(context, 36), margin, 0);
        frame.AddView(card, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _hint = frame;
        Add(frame, p);
    }

    public void HideHint()
    {
        _hintText = null;
        View? hint = _hint;
        _hint = null;
        Remove(ref hint);
    }

    // ---------------------------------------------------------------- state

    /// <summary>Recording on or off, the timer, the topic: called by the assistant whenever something changes.</summary>
    public void Refresh()
    {
        bool rec = _assistant.IsRecording;
        if (_bubbleLogo?.Parent is View tile && _bubblePill != null && _bubbleTimer != null)
        {
            tile.Visibility = rec ? ViewStates.Gone : ViewStates.Visible;
            _bubblePill.Visibility = rec ? ViewStates.Visible : ViewStates.Gone;
            _bubbleTimer.Text = _assistant.ElapsedText;
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

    private void Update(View view, WindowManagerLayoutParams p)
    {
        try
        {
            _windows?.UpdateViewLayout(view, p);
        }
        catch (Exception)
        {
            // The window is already gone.
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

    /// <summary>The menu sheet: a tap outside it or the back button closes it.</summary>
    private sealed class MenuRoot : LinearLayout
    {
        private readonly Action _close;

        public MenuRoot(Context context, Action close)
            : base(context)
        {
            _close = close;
            Focusable = true;
            FocusableInTouchMode = true;
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e?.Action == MotionEventActions.Outside)
            {
                _close();
                return true;
            }

            return base.OnTouchEvent(e);
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
