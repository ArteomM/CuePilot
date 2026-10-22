using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views;

public partial class MiniOverlayWindow : Window
{
    private const int MaxConversationItems = 100;
    private const double DefaultExpandedHeight = 520;

    private readonly OverlayPositionStore _positionStore = null!;
    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _thinkingTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private readonly LocalizationService _localizer = null!;
    private bool _captureActive;
    private bool _isCompact;
    private bool _placementLoaded;
    private double _expandedHeight = DefaultExpandedHeight;
    private string _lastStats = string.Empty;
    private Action? _suggestNextQuestionAction;
    private Action? _suggestReplyAction;
    private Action<string>? _whatShouldISayAction;
    private Action? _followUpQuestionAction;
    private Action? _recapAction;
    private Action? _openSessionAction;
    private Action<bool>? _smartToggleAction;

    public MiniOverlayWindow()
    {
        InitializeComponent();
    }

    public MiniOverlayWindow(OverlayPositionStore positionStore, LocalizationService localizer)
        : this()
    {
        _positionStore = positionStore;
        _localizer = localizer;
        DataContext = this;
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            SetCaptureStatus(_captureActive);
        };
        // Safety net: the dots never spin forever if an answer is lost.
        _thinkingTimer.Tick += (_, _) => HideThinking();
        Opened += OnOpened;
        PositionChanged += (_, _) => OnPlacementChanged();
        SizeChanged += (_, _) => OnPlacementChanged();
        PointerPressed += Window_OnPointerPressed;

        CompactToggleButton.Click += (_, _) => SetCompact(!_isCompact);
        WhatToSayButton.Click += (_, _) =>
        {
            AppendConversation(isUser: true, ActionLabel("OverlayActionWhatToSay"));
            _suggestReplyAction?.Invoke();
        };
        TopWhatShouldISayButton.Click += (_, _) =>
        {
            AppendConversation(isUser: true, ActionLabel("OverlayActionWhatShouldISay"));
            _suggestNextQuestionAction?.Invoke();
        };
        FollowUpQuestionButton.Click += (_, _) =>
        {
            AppendConversation(isUser: true, ActionLabel("OverlayActionFollowUpQuestions"));
            _followUpQuestionAction?.Invoke();
        };
        RecapButton.Click += (_, _) =>
        {
            AppendConversation(isUser: true, ActionLabel("OverlayActionRecap"));
            _recapAction?.Invoke();
        };
        OpenMainButton.Click += (_, _) => _openSessionAction?.Invoke();
        // The view model echoes the question back through AddUserQuestion, same as for the main screen.
        WhatShouldISayButton.Click += (_, _) => _whatShouldISayAction?.Invoke(NormalizePromptForUserMessage());
        SmartToggle.IsCheckedChanged += (_, _) => _smartToggleAction?.Invoke(SmartToggle.IsChecked == true);
        OverlayPromptText.PointerEntered += (_, _) => ClearPromptIfPlaceholder();
        OverlayPromptText.GotFocus += (_, _) => ClearPromptIfPlaceholder();
        OverlayPromptText.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            _whatShouldISayAction?.Invoke(NormalizePromptForUserMessage());
        };

        _localizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                ApplyLocalization();
            }
        };
        ApplyLocalization();
    }

    public ObservableCollection<OverlayConversationItem> ConversationItems { get; } = [];

    /// <summary>Shown next to the title: the hotkey that shows and hides the overlay.</summary>
    public string HotkeyHint
    {
        get => HotkeyHintText.Text ?? string.Empty;
        set => HotkeyHintText.Text = value;
    }

    public void SetCaptureStatus(bool active)
    {
        _captureActive = active;
        ListeningBars.IsActive = active;
        if (_noticeTimer.IsEnabled)
        {
            return;
        }

        OverlayStatusText.Text = active ? _localizer["OverlayStatusRunning"] : _localizer["OverlayStatusIdle"];
    }

    /// <summary>Shows a short message in the status area, then returns to the capture status.</summary>
    public void ShowNotice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _noticeTimer.Stop();
        OverlayStatusText.Text = text;
        _noticeTimer.Start();
    }

    public void SetStats(string text)
    {
        _lastStats = text ?? string.Empty;
        OverlayStatsText.Text = _lastStats;
    }

    public void SetSuggestionPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (ConversationItems.Count == 0)
            {
                AddPlaceholder();
            }

            return;
        }

        AppendConversation(isUser: false, text.Trim());
    }

    public string OverlayPrompt
    {
        get => OverlayPromptText.Text ?? string.Empty;
        set => OverlayPromptText.Text = value;
    }

    public void ConfigureActions(
        Action openSessionAction,
        Action suggestNextQuestionAction,
        Action suggestReplyAction,
        Action<string> whatShouldISayAction,
        Action followUpQuestionAction,
        Action recapAction,
        Action<bool> smartToggleAction)
    {
        _openSessionAction = openSessionAction;
        _suggestNextQuestionAction = suggestNextQuestionAction;
        _suggestReplyAction = suggestReplyAction;
        _whatShouldISayAction = whatShouldISayAction;
        _followUpQuestionAction = followUpQuestionAction;
        _recapAction = recapAction;
        _smartToggleAction = smartToggleAction;
    }

    /// <summary>Adds a question asked elsewhere (main screen, hotkey) to the dialog, above its answer.</summary>
    public void AddUserQuestion(string text)
    {
        AppendConversation(isUser: true, text);
    }

    /// <summary>A new recording starts with an empty dialog instead of the previous session's answers.</summary>
    public void ResetConversation()
    {
        HideThinking();
        ConversationItems.Clear();
        AddPlaceholder();
        UpdateCompactAnswer();
    }

    public void HideThinking()
    {
        _thinkingTimer.Stop();
        ThinkingDots.IsVisible = false;
    }

    public void SetSmartMode(bool enabled)
    {
        SmartToggle.IsChecked = enabled;
    }

    /// <summary>The overlay appears with a short fade rather than popping onto the screen.</summary>
    private void FadeIn()
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }
        };
        _ = animation.RunAsync(RootBorder);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        FadeIn();
        if (_placementLoaded)
        {
            return;
        }

        _placementLoaded = true;
        var placement = _positionStore.Load();
        if (placement == null)
        {
            return;
        }

        Position = new PixelPoint((int)placement.Left, (int)placement.Top);
        Width = placement.Width;
        Height = placement.Height > 0 ? placement.Height : DefaultExpandedHeight;
        if (placement.Compact)
        {
            SetCompact(true);
        }
    }

    private void OnPlacementChanged()
    {
        if (!IsVisible)
        {
            return;
        }

        // Dragging fires PositionChanged per pixel: save once the window stops moving.
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacement()
    {
        _positionStore.Save(new OverlayWindowPlacement
        {
            Left = Position.X,
            Top = Position.Y,
            Width = Width,
            Height = _isCompact ? _expandedHeight : Height,
            Compact = _isCompact
        });
    }

    /// <summary>Compact mode keeps only the action bar and the latest answer.</summary>
    private void SetCompact(bool compact)
    {
        if (_isCompact == compact)
        {
            return;
        }

        _isCompact = compact;
        PromptRow.IsVisible = !compact;
        ConversationPanel.IsVisible = !compact;
        OverlayStatsText.IsVisible = !compact;
        CompactAnswerPanel.IsVisible = compact;

        if (compact)
        {
            _expandedHeight = !double.IsNaN(Height) && Height > 0 ? Height : Bounds.Height;
            UpdateCompactAnswer();
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Height = _expandedHeight > 0 ? _expandedHeight : DefaultExpandedHeight;
            if (ConversationItems.Count > 0)
            {
                OverlayConversationList.ScrollIntoView(ConversationItems[^1]);
            }
        }

        UpdateCompactToggleContent();
        SavePlacement();
    }

    private void UpdateCompactAnswer()
    {
        OverlayConversationItem? lastAnswer = ConversationItems.LastOrDefault(item => !item.IsUser);
        CompactAnswerText.Text = lastAnswer?.Message ?? _localizer["OverlaySuggestionPlaceholder"];
    }

    private void UpdateCompactToggleContent()
    {
        CompactToggleButton.Content = _localizer["OverlayTitle"] + (_isCompact ? " ▸" : " ▾");
        ToolTip.SetTip(CompactToggleButton, _isCompact ? _localizer["OverlayExpandHint"] : _localizer["OverlayCollapseHint"]);
    }

    private void ApplyLocalization()
    {
        Title = _localizer["OverlayWindowTitle"];
        if (string.IsNullOrWhiteSpace(OverlayPromptText.Text) || IsPlaceholderPrompt(OverlayPromptText.Text))
        {
            OverlayPromptText.Text = _localizer["OverlayPrompt"];
        }

        UpdateCompactToggleContent();
        WhatToSayButton.Content = ActionLabel("OverlayActionWhatToSay");
        TopWhatShouldISayButton.Content = ActionLabel("OverlayActionWhatShouldISay");
        FollowUpQuestionButton.Content = ActionLabel("OverlayActionFollowUpQuestions");
        RecapButton.Content = ActionLabel("OverlayActionRecap");
        ToolTip.SetTip(WhatToSayButton, _localizer["TipOverlayWhatToSay"]);
        ToolTip.SetTip(TopWhatShouldISayButton, _localizer["TipOverlayWhatToAsk"]);
        ToolTip.SetTip(FollowUpQuestionButton, _localizer["TipOverlayFollowUp"]);
        ToolTip.SetTip(RecapButton, _localizer["TipOverlayRecap"]);
        ToolTip.SetTip(OverlayPromptText, _localizer["TipAskBox"]);
        SmartToggle.Content = _localizer["SmartToggleShort"];
        ToolTip.SetTip(SmartToggle, _localizer["SmartModeToggle"]);
        WhatShouldISayButton.Content = _localizer["AskPrefix"];
        ToolTip.SetTip(WhatShouldISayButton, _localizer["OverlaySendHint"]);
        ToolTip.SetTip(OpenMainButton, _localizer["OverlayOpenMainHint"]);
        SetCaptureStatus(_captureActive);
        OverlayStatsText.Text = _lastStats;
        if (ConversationItems.Count == 0)
        {
            AddPlaceholder();
        }
    }

    private void Window_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Drag by any empty spot; buttons and the text box keep their own clicks.
        if (e.Source is Visual source && (source is TextBox || source.FindAncestorOfType<TextBox>() != null ||
                                          source.FindAncestorOfType<Button>() != null ||
                                          source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) != null ||
                                          source.FindAncestorOfType<global::Avalonia.Controls.Primitives.ScrollBar>() != null))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Button label without the leading bullet, used as the user's message in the chat.</summary>
    private string ActionLabel(string key)
    {
        string label = (_localizer[key] ?? string.Empty).Trim();
        return label.TrimStart('•').TrimStart();
    }

    private void AddPlaceholder()
    {
        ConversationItems.Add(new OverlayConversationItem(
            _localizer["OverlayRoleAssistant"],
            _localizer["OverlaySuggestionPlaceholder"],
            false,
            isPlaceholder: true));
    }

    private void AppendConversation(bool isUser, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string normalized = message.Trim();
        if (ConversationItems.Count > 0)
        {
            OverlayConversationItem last = ConversationItems[^1];
            if (last.IsUser == isUser && string.Equals(last.Message, normalized, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (ConversationItems.Count == 1 && ConversationItems[0].IsPlaceholder)
        {
            ConversationItems.Clear();
        }

        string role = isUser ? _localizer["OverlayRoleUser"] : _localizer["OverlayRoleAssistant"];
        ConversationItems.Add(new OverlayConversationItem(role, normalized, isUser));
        while (ConversationItems.Count > MaxConversationItems)
        {
            ConversationItems.RemoveAt(0);
        }

        if (isUser)
        {
            ThinkingDots.IsVisible = true;
            _thinkingTimer.Stop();
            _thinkingTimer.Start();
        }
        else
        {
            HideThinking();
        }

        if (_isCompact)
        {
            CompactAnswerText.Text = isUser ? _localizer["StateThinkingDesc"] : normalized;
        }
        else
        {
            OverlayConversationItem newest = ConversationItems[^1];
            Dispatcher.UIThread.Post(() => OverlayConversationList.ScrollIntoView(newest), DispatcherPriority.Background);
        }
    }

    private void ClearPromptIfPlaceholder()
    {
        if (IsPlaceholderPrompt(OverlayPromptText.Text))
        {
            OverlayPromptText.Clear();
        }
    }

    private bool IsPlaceholderPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return string.Equals(text.Trim(), _localizer["OverlayPrompt"], StringComparison.OrdinalIgnoreCase);
    }

    private string NormalizePromptForUserMessage()
    {
        if (IsPlaceholderPrompt(OverlayPromptText.Text))
        {
            return string.Empty;
        }

        return OverlayPromptText.Text?.Trim() ?? string.Empty;
    }
}

public class OverlayConversationItem
{
    private static readonly IBrush UserBubble = Brush.Parse("#243A33");
    private static readonly IBrush UserBorder = Brush.Parse("#2E4A41");
    private static readonly IBrush UserRole = Brush.Parse("#8FE3C6");
    private static readonly IBrush UserText = Brush.Parse("#DFFBF1");
    private static readonly IBrush AssistantBubble = Brush.Parse("#171B22");
    private static readonly IBrush AssistantBorder = Brush.Parse("#2A313C");
    private static readonly IBrush AssistantRole = Brush.Parse("#5EF0C1");
    private static readonly IBrush AssistantText = Brush.Parse("#E9EEF5");
    private static readonly IBrush PlaceholderRole = Brush.Parse("#9AA5B5");

    public OverlayConversationItem(string role, string message, bool isUser, bool isPlaceholder = false)
    {
        Role = role;
        Message = message;
        IsUser = isUser;
        IsPlaceholder = isPlaceholder;
        BubbleAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        BubbleBackground = isUser ? UserBubble : AssistantBubble;
        BubbleBorder = isUser ? UserBorder : AssistantBorder;
        BubbleCornerRadius = isUser ? new CornerRadius(16, 16, 4, 16) : new CornerRadius(16, 16, 16, 4);
        RoleBrush = isPlaceholder ? PlaceholderRole : isUser ? UserRole : AssistantRole;
        MessageBrush = isPlaceholder ? PlaceholderRole : isUser ? UserText : AssistantText;
    }

    public string Role { get; }
    public string Message { get; }
    public bool IsUser { get; }
    public bool IsPlaceholder { get; }
    public HorizontalAlignment BubbleAlignment { get; }
    public IBrush BubbleBackground { get; }
    public IBrush BubbleBorder { get; }
    public CornerRadius BubbleCornerRadius { get; }
    public IBrush RoleBrush { get; }
    public IBrush MessageBrush { get; }
}
