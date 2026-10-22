using System.Windows;
using System.Windows.Input;
using CuePilotDesktop.Services;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace CuePilotDesktop.Views;

public partial class MiniOverlayWindow : Window
{
    private const int MaxConversationItems = 100;
    private const double DefaultExpandedHeight = 520;

    private readonly OverlayPositionStore _positionStore;
    private readonly System.Windows.Threading.DispatcherTimer _placementSaveTimer;
    private readonly System.Windows.Threading.DispatcherTimer _noticeTimer;
    private readonly System.Windows.Threading.DispatcherTimer _thinkingTimer;
    private readonly LocalizationService _localizer;
    private bool _captureActive;
    private bool _isCompact;
    private double _expandedHeight = DefaultExpandedHeight;
    private string _lastStats = string.Empty;
    private Action? _suggestNextQuestionAction;
    private Action? _suggestReplyAction;
    private Action<string>? _whatShouldISayAction;
    private Action? _followUpQuestionAction;
    private Action? _recapAction;
    private Action? _openSessionAction;
    private Action<bool>? _smartToggleAction;

    public MiniOverlayWindow(OverlayPositionStore positionStore, LocalizationService localizer)
    {
        _positionStore = positionStore;
        _localizer = localizer;
        InitializeComponent();
        DataContext = this;
        // Hints over another app's window: the built-in tooltips only work in the active window.
        CuePilotDesktop.Views.Controls.FloatingHints.Attach(this);
        _placementSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            SetCaptureStatus(_captureActive);
        };
        // Safety net: the dots never spin forever if an answer is lost.
        _thinkingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _thinkingTimer.Tick += (_, _) => HideThinking();
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => FadeInIfShown();
        LocationChanged += OnPlacementChanged;
        SizeChanged += OnPlacementChanged;
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
        get => OverlayPromptText.Text;
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
        ThinkingDots.Visibility = Visibility.Collapsed;
    }

    public void SetSmartMode(bool enabled)
    {
        SmartToggle.IsChecked = enabled;
    }

    /// <summary>The overlay appears with a short fade rather than popping onto the screen.</summary>
    private void FadeInIfShown()
    {
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var placement = _positionStore.Load();
        if (placement == null)
        {
            return;
        }

        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height > 0 ? placement.Height : DefaultExpandedHeight;
        if (placement.Compact)
        {
            SetCompact(true);
        }
    }

    private void OnPlacementChanged(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        // Dragging fires LocationChanged per pixel: save once the window stops moving.
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacement()
    {
        _positionStore.Save(new OverlayWindowPlacement
        {
            Left = Left,
            Top = Top,
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
        Visibility expandedOnly = compact ? Visibility.Collapsed : Visibility.Visible;
        PromptRow.Visibility = expandedOnly;
        ConversationPanel.Visibility = expandedOnly;
        OverlayStatsText.Visibility = expandedOnly;
        CompactAnswerPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        if (compact)
        {
            _expandedHeight = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
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
        CompactToggleButton.ToolTip = _isCompact ? _localizer["OverlayExpandHint"] : _localizer["OverlayCollapseHint"];
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
        WhatToSayButton.ToolTip = _localizer["TipOverlayWhatToSay"];
        TopWhatShouldISayButton.ToolTip = _localizer["TipOverlayWhatToAsk"];
        FollowUpQuestionButton.ToolTip = _localizer["TipOverlayFollowUp"];
        RecapButton.ToolTip = _localizer["TipOverlayRecap"];
        OverlayPromptText.ToolTip = _localizer["TipAskBox"];
        SmartToggle.Content = _localizer["SmartToggleShort"];
        SmartToggle.ToolTip = _localizer["SmartModeToggle"];
        WhatShouldISayButton.Content = _localizer["AskPrefix"];
        WhatShouldISayButton.ToolTip = _localizer["OverlaySendHint"];
        OpenMainButton.ToolTip = _localizer["OverlayOpenMainHint"];
        SetCaptureStatus(_captureActive);
        OverlayStatsText.Text = _lastStats;
        if (ConversationItems.Count == 0)
        {
            AddPlaceholder();
        }
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void WhatShouldISayButton_OnClick(object sender, RoutedEventArgs e)
    {
        // The view model echoes the question back through AddUserQuestion, same as for the main screen.
        _whatShouldISayAction?.Invoke(NormalizePromptForUserMessage());
    }

    private void TopWhatShouldISayButton_OnClick(object sender, RoutedEventArgs e)
    {
        AppendConversation(isUser: true, ActionLabel("OverlayActionWhatShouldISay"));
        _suggestNextQuestionAction?.Invoke();
    }

    private void WhatToSayButton_OnClick(object sender, RoutedEventArgs e)
    {
        AppendConversation(isUser: true, ActionLabel("OverlayActionWhatToSay"));
        _suggestReplyAction?.Invoke();
    }

    private void OverlayPromptText_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        WhatShouldISayButton_OnClick(sender as object ?? this, new RoutedEventArgs());
    }

    private void SmartToggle_OnChecked(object sender, RoutedEventArgs e)
    {
        _smartToggleAction?.Invoke(true);
    }

    private void SmartToggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
        _smartToggleAction?.Invoke(false);
    }

    private void FollowUpQuestionButton_OnClick(object sender, RoutedEventArgs e)
    {
        AppendConversation(isUser: true, ActionLabel("OverlayActionFollowUpQuestions"));
        _followUpQuestionAction?.Invoke();
    }

    private void RecapButton_OnClick(object sender, RoutedEventArgs e)
    {
        AppendConversation(isUser: true, ActionLabel("OverlayActionRecap"));
        _recapAction?.Invoke();
    }

    private void CompactToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetCompact(!_isCompact);
    }

    private void OpenMainButton_OnClick(object sender, RoutedEventArgs e)
    {
        _openSessionAction?.Invoke();
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
            ThinkingDots.Visibility = Visibility.Visible;
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
            OverlayConversationList.ScrollIntoView(ConversationItems[^1]);
        }
    }

    private void OverlayPromptText_OnMouseEnter(object sender, MouseEventArgs e)
    {
        ClearPromptIfPlaceholder();
    }

    private void OverlayPromptText_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ClearPromptIfPlaceholder();
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
    private static readonly Brush UserBubble = Freeze("#243A33");
    private static readonly Brush UserBorder = Freeze("#2E4A41");
    private static readonly Brush UserRole = Freeze("#8FE3C6");
    private static readonly Brush UserText = Freeze("#DFFBF1");
    private static readonly Brush AssistantBubble = Freeze("#171B22");
    private static readonly Brush AssistantBorder = Freeze("#2A313C");
    private static readonly Brush AssistantRole = Freeze("#5EF0C1");
    private static readonly Brush AssistantText = Freeze("#E9EEF5");
    private static readonly Brush PlaceholderRole = Freeze("#9AA5B5");

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
    public Brush BubbleBackground { get; }
    public Brush BubbleBorder { get; }
    public CornerRadius BubbleCornerRadius { get; }
    public Brush RoleBrush { get; }
    public Brush MessageBrush { get; }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
