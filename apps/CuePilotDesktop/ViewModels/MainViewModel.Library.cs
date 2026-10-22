using System.Collections.ObjectModel;
using System.ComponentModel;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>
/// Session library: topics with a shared knowledge base, multi-select with bulk actions,
/// follow-up email, day/night palette and interface language.
/// </summary>
public partial class MainViewModel
{
    private readonly JsonTopicStore _topicStore = new();
    private readonly List<SessionTopic> _topics = [];
    private readonly UiTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private TopicItemViewModel? _selectedTopicItem;
    private bool _isSelectionMode;
    private int _checkedSessionsCount;
    private bool _isEditingTopicName;
    private string _editableTopicName = string.Empty;
    private bool _isTopicBusy;
    private bool _isFollowUpEmailBusy;
    private string _actionFeedbackText = string.Empty;

    public ObservableCollection<TopicItemViewModel> TopicItems { get; } = [];

    public RelayCommand CreateTopicCommand { get; private set; } = null!;
    public RelayCommand StartRenameTopicCommand { get; private set; } = null!;
    public RelayCommand DeleteTopicCommand { get; private set; } = null!;
    public RelayCommand RefreshTopicKnowledgeCommand { get; private set; } = null!;
    public RelayCommand CopyTopicKnowledgeCommand { get; private set; } = null!;
    public RelayCommand ToggleSelectionModeCommand { get; private set; } = null!;
    public RelayCommand SelectAllSessionsCommand { get; private set; } = null!;
    public RelayCommand DeleteCheckedSessionsCommand { get; private set; } = null!;
    public RelayCommand CopyFollowUpEmailCommand { get; private set; } = null!;
    public RelayCommand ToggleThemeCommand { get; private set; } = null!;

    public TopicItemViewModel? SelectedTopicItem
    {
        get => _selectedTopicItem;
        set
        {
            if (ReferenceEquals(_selectedTopicItem, value))
            {
                return;
            }

            _selectedTopicItem = value;
            IsEditingTopicName = false;
            OnPropertyChanged();
            OnTopicDetailsChanged();
            _completedSessionsView.Refresh();
            OnPropertyChanged(nameof(VisibleSessionsCount));
        }
    }

    public SessionTopic? SelectedTopic => _selectedTopicItem?.Topic;

    public bool IsTopicSelected => SelectedTopic != null;

    public string LibraryTitle => _selectedTopicItem?.Name ?? L["TopicAllSessions"];

    public int VisibleSessionsCount => CompletedSessions.Count(session => FilterByTopic(session));

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (_isSelectionMode == value)
            {
                return;
            }

            _isSelectionMode = value;
            if (!value)
            {
                foreach (SessionSummary session in CompletedSessions)
                {
                    session.IsChecked = false;
                }
            }

            OnPropertyChanged();
        }
    }

    public int CheckedSessionsCount
    {
        get => _checkedSessionsCount;
        private set
        {
            if (_checkedSessionsCount == value)
            {
                return;
            }

            _checkedSessionsCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCheckedSessions));
            OnPropertyChanged(nameof(DeleteCheckedText));
            DeleteCheckedSessionsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasCheckedSessions => CheckedSessionsCount > 0;

    public string DeleteCheckedText => string.Format(L["DeleteCheckedSessions"], CheckedSessionsCount);

    public bool IsEditingTopicName
    {
        get => _isEditingTopicName;
        set
        {
            if (_isEditingTopicName == value)
            {
                return;
            }

            _isEditingTopicName = value;
            OnPropertyChanged();
        }
    }

    public string EditableTopicName
    {
        get => _editableTopicName;
        set
        {
            if (_editableTopicName == value)
            {
                return;
            }

            _editableTopicName = value;
            OnPropertyChanged();
        }
    }

    public bool IsTopicBusy
    {
        get => _isTopicBusy;
        private set
        {
            if (_isTopicBusy == value)
            {
                return;
            }

            _isTopicBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TopicKnowledgeStatusText));
            OnKnowledgeHeaderChanged();
            RefreshTopicKnowledgeCommand.RaiseCanExecuteChanged();
        }
    }

    public string TopicKnowledgeText
    {
        get
        {
            SessionTopic? topic = SelectedTopic;
            if (topic == null || string.IsNullOrWhiteSpace(topic.KnowledgeText))
            {
                return string.Empty;
            }

            return topic.KnowledgeText;
        }
    }

    public bool HasTopicKnowledge => !string.IsNullOrWhiteSpace(TopicKnowledgeText);

    public bool IsTopicKnowledgeStale => _selectedTopicItem?.IsStale == true;

    public string TopicKnowledgeStatusText
    {
        get
        {
            SessionTopic? topic = SelectedTopic;
            if (topic == null)
            {
                return string.Empty;
            }

            if (IsTopicBusy)
            {
                return L["TopicKnowledgeBusy"];
            }

            int count = _selectedTopicItem?.SessionCount ?? 0;
            if (count == 0 && !topic.Sources.Any(source => source.ParsedAt != null))
            {
                return L["TopicNoSessions"];
            }

            if (string.IsNullOrWhiteSpace(topic.KnowledgeText))
            {
                return L["TopicKnowledgeEmpty"];
            }

            if (IsTopicKnowledgeStale)
            {
                return L["TopicKnowledgeStale"];
            }

            return string.Format(
                L["TopicKnowledgeUpToDate"],
                topic.KnowledgeUpdatedAt?.ToString("g") ?? string.Empty,
                topic.KnowledgeSessionIds.Count,
                topic.Sources.Count(source => source.ParsedAt != null));
        }
    }

    public string CurrentFollowUpEmail => SelectedSession?.FollowUpEmailText ?? string.Empty;

    public bool HasFollowUpEmail => !string.IsNullOrWhiteSpace(CurrentFollowUpEmail);

    public bool IsFollowUpEmailBusy
    {
        get => _isFollowUpEmailBusy;
        private set
        {
            if (_isFollowUpEmailBusy == value)
            {
                return;
            }

            _isFollowUpEmailBusy = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Short confirmation such as «Скопировано», shown next to the action buttons for a moment.</summary>
    public string ActionFeedbackText
    {
        get => _actionFeedbackText;
        private set
        {
            if (_actionFeedbackText == value)
            {
                return;
            }

            _actionFeedbackText = value;
            OnPropertyChanged();
        }
    }

    public bool IsDarkTheme
    {
        get => SelectedThemeMode != "light";
        set
        {
            if (value)
            {
                SelectedThemeMode = "dark";
            }
        }
    }

    public bool IsLightTheme
    {
        get => SelectedThemeMode == "light";
        set
        {
            if (value)
            {
                SelectedThemeMode = "light";
            }
        }
    }

    public bool IsRussianUi
    {
        get => SelectedUiLanguage != "en";
        set
        {
            if (value)
            {
                SelectedUiLanguage = "ru";
            }
        }
    }

    public bool IsEnglishUi
    {
        get => SelectedUiLanguage == "en";
        set
        {
            if (value)
            {
                SelectedUiLanguage = "en";
            }
        }
    }

    private bool IsEnglishInterface => SelectedUiLanguage == "en";

    private void InitializeLibrary()
    {
        CreateTopicCommand = new RelayCommand(CreateTopic);
        StartRenameTopicCommand = new RelayCommand(StartRenameTopic, () => SelectedTopic != null);
        DeleteTopicCommand = new RelayCommand(DeleteSelectedTopic, () => SelectedTopic != null);
        RefreshTopicKnowledgeCommand = new RelayCommand(
            async () => await RefreshTopicKnowledgeAsync(),
            () => SelectedTopic != null && !IsTopicBusy && ((_selectedTopicItem?.SessionCount ?? 0) > 0 || SelectedTopic.Sources.Any(x => x.ParsedAt != null)));
        CopyTopicKnowledgeCommand = new RelayCommand(() => CopyToClipboard(TopicKnowledgeText), () => HasTopicKnowledge);
        InitializeTopicWorkspace();
        ToggleSelectionModeCommand = new RelayCommand(() => IsSelectionMode = !IsSelectionMode);
        SelectAllSessionsCommand = new RelayCommand(SelectAllVisibleSessions);
        DeleteCheckedSessionsCommand = new RelayCommand(DeleteCheckedSessions, () => CheckedSessionsCount > 0);
        CopyFollowUpEmailCommand = new RelayCommand(() => CopyToClipboard(CurrentFollowUpEmail), () => HasFollowUpEmail);
        ToggleThemeCommand = new RelayCommand(() => SelectedThemeMode = SelectedThemeMode == "light" ? "dark" : "light");
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ActionFeedbackText = string.Empty;
        };

        _topics.AddRange(_topicStore.Load());
        MigrateTopicQuestions();
        RebuildTopicItems();
    }

    /// <summary>Sidebar: «Все сессии», «Без темы», then topics by name.</summary>
    private void RebuildTopicItems()
    {
        string? selectedKey = _selectedTopicItem?.Key;
        TopicItems.Clear();
        TopicItems.Add(new TopicItemViewModel(TopicItemKind.All, null, L["TopicAllSessions"]));
        TopicItems.Add(new TopicItemViewModel(TopicItemKind.Unsorted, null, L["TopicUnsorted"]));
        foreach (SessionTopic topic in _topics.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            TopicItems.Add(new TopicItemViewModel(TopicItemKind.Topic, topic, topic.Name));
        }

        RefreshTopicStats();
        _selectedTopicItem = TopicItems.FirstOrDefault(item => item.Key == selectedKey) ?? TopicItems[0];
        OnPropertyChanged(nameof(SelectedTopicItem));
        OnTopicDetailsChanged();
    }

    /// <summary>Counts per topic and staleness of each knowledge base; call after sessions change.</summary>
    private void RefreshTopicStats()
    {
        var knownTopics = new HashSet<string>(_topics.Select(t => t.Id));
        foreach (TopicItemViewModel item in TopicItems)
        {
            List<SessionSummary> sessions = CompletedSessions.Where(s => BelongsTo(item, s, knownTopics)).ToList();
            item.SessionCount = sessions.Count;
            item.IsStale = item.Topic != null && IsKnowledgeStale(item.Topic, sessions);
        }

        Dictionary<string, string> topicNames = _topics.ToDictionary(t => t.Id, t => t.Name);
        foreach (SessionSummary session in CompletedSessions)
        {
            session.TopicName = topicNames.TryGetValue(session.TopicId, out string? name) ? name : string.Empty;
            session.PropertyChanged -= OnSessionCheckChanged;
            session.PropertyChanged += OnSessionCheckChanged;
        }

        CheckedSessionsCount = CompletedSessions.Count(s => s.IsChecked);
        OnPropertyChanged(nameof(VisibleSessionsCount));
        OnTopicDetailsChanged();
    }

    private static bool BelongsTo(TopicItemViewModel item, SessionSummary session, HashSet<string> knownTopics)
    {
        return item.Kind switch
        {
            TopicItemKind.All => true,
            TopicItemKind.Unsorted => string.IsNullOrEmpty(session.TopicId) || !knownTopics.Contains(session.TopicId),
            _ => session.TopicId == item.Topic!.Id
        };
    }

    private static bool IsKnowledgeStale(SessionTopic topic, IReadOnlyCollection<SessionSummary> sessions)
    {
        bool hasSources = topic.Sources.Any(source => source.ParsedAt != null);
        if (sessions.Count == 0 && !hasSources)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(topic.KnowledgeText))
        {
            return true;
        }

        return !new HashSet<string>(topic.KnowledgeSessionIds).SetEquals(sessions.Select(s => s.Id)) ||
            topic.KnowledgeSourcesStamp != ComputeSourcesStamp(topic);
    }

    private bool FilterByTopic(SessionSummary session)
    {
        if (_selectedTopicItem == null)
        {
            return true;
        }

        return BelongsTo(_selectedTopicItem, session, new HashSet<string>(_topics.Select(t => t.Id)));
    }

    private void OnSessionCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionSummary.IsChecked))
        {
            CheckedSessionsCount = CompletedSessions.Count(s => s.IsChecked);
        }
    }

    private void OnTopicDetailsChanged()
    {
        OnPropertyChanged(nameof(SelectedTopic));
        OnPropertyChanged(nameof(IsTopicSelected));
        OnPropertyChanged(nameof(LibraryTitle));
        OnPropertyChanged(nameof(TopicKnowledgeText));
        OnPropertyChanged(nameof(HasTopicKnowledge));
        OnPropertyChanged(nameof(IsTopicKnowledgeStale));
        OnPropertyChanged(nameof(TopicKnowledgeStatusText));
        StartRenameTopicCommand?.RaiseCanExecuteChanged();
        DeleteTopicCommand?.RaiseCanExecuteChanged();
        RefreshTopicKnowledgeCommand?.RaiseCanExecuteChanged();
        CopyTopicKnowledgeCommand?.RaiseCanExecuteChanged();
        OnTopicWorkspaceChanged();
    }

    private void SaveTopics()
    {
        try
        {
            _topicStore.Save(_topics);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
            AppendLog("[topics] Save failed: " + ex.Message);
        }
    }

    private void CreateTopic()
    {
        int number = 1;
        string name;
        do
        {
            name = number == 1 ? L["TopicNewName"] : $"{L["TopicNewName"]} {number}";
            number++;
        }
        while (_topics.Any(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)));

        var topic = new SessionTopic { Name = name };
        _topics.Add(topic);
        SaveTopics();
        RebuildTopicItems();
        SelectedTopicItem = TopicItems.FirstOrDefault(item => item.Topic == topic);
        StartRenameTopic();
        AppendLog("[topics] Topic created.");
    }

    private void StartRenameTopic()
    {
        if (SelectedTopic == null)
        {
            return;
        }

        EditableTopicName = SelectedTopic.Name;
        IsEditingTopicName = true;
    }

    public void CommitTopicRename()
    {
        SessionTopic? topic = SelectedTopic;
        if (!IsEditingTopicName || topic == null)
        {
            IsEditingTopicName = false;
            return;
        }

        IsEditingTopicName = false;
        string name = EditableTopicName.Trim();
        if (name.Length == 0 || name == topic.Name)
        {
            return;
        }

        topic.Name = name;
        SaveTopics();
        RebuildTopicItems();
        AppendLog("[topics] Topic renamed.");
    }

    public void CancelTopicRename()
    {
        IsEditingTopicName = false;
    }

    private void DeleteSelectedTopic()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null)
        {
            return;
        }

        bool confirmed = ConfirmationRequested?.Invoke(
            L["DeleteTopicConfirmTitle"],
            string.Format(L["DeleteTopicConfirmMessage"], topic.Name)) ?? false;
        if (!confirmed)
        {
            return;
        }

        // Sessions stay in the history: they only lose the topic.
        foreach (SessionSummary session in CompletedSessions.Where(s => s.TopicId == topic.Id))
        {
            _sessionLifecycleService.SetTopic(session.Id, string.Empty);
        }

        _sessionLifecycleService.Flush();
        try
        {
            _knowledgeStore.DeleteTopic(topic.Id);
        }
        catch (Exception ex)
        {
            AppendLog("[topics] Knowledge folder not removed: " + ex.Message);
        }

        _topics.Remove(topic);
        SaveTopics();
        _selectedTopicItem = null;
        RebuildTopicItems();
        _completedSessionsView.Refresh();
        AppendLog("[topics] Topic deleted.");
    }

    /// <summary>Moves sessions into a topic (null or the «Без темы» item removes them from their topic).</summary>
    public void MoveSessionsToTopic(IEnumerable<string> sessionIds, TopicItemViewModel? target)
    {
        if (target?.Kind == TopicItemKind.All)
        {
            return;
        }

        string topicId = target?.Topic?.Id ?? string.Empty;
        int moved = 0;
        foreach (string sessionId in sessionIds.Distinct())
        {
            SessionSummary? session = CompletedSessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null || session.TopicId == topicId)
            {
                continue;
            }

            _sessionLifecycleService.SetTopic(sessionId, topicId);
            moved++;
        }

        if (moved == 0)
        {
            return;
        }

        _sessionLifecycleService.Flush();
        RefreshTopicStats();
        _completedSessionsView.Refresh();
        ShowFeedback(string.Format(L["SessionsMovedFeedback"], moved, target?.Name ?? L["TopicUnsorted"]));
        AppendLog($"[topics] Moved {moved} session(s).");
    }

    /// <summary>Ids to act on for a session card: all checked sessions when the card is one of them.</summary>
    public IReadOnlyList<string> GetActionSessionIds(SessionSummary session)
    {
        if (IsSelectionMode && session.IsChecked)
        {
            return CompletedSessions.Where(s => s.IsChecked).Select(s => s.Id).ToList();
        }

        return [session.Id];
    }

    private void SelectAllVisibleSessions()
    {
        IsSelectionMode = true;
        List<SessionSummary> visible = _completedSessionsView.VisibleItems.Cast<SessionSummary>().ToList();
        bool allChecked = visible.Count > 0 && visible.All(s => s.IsChecked);
        foreach (SessionSummary session in visible)
        {
            session.IsChecked = !allChecked;
        }
    }

    private void DeleteCheckedSessions()
    {
        List<SessionSummary> sessions = CompletedSessions.Where(s => s.IsChecked).ToList();
        if (sessions.Count == 0)
        {
            return;
        }

        bool confirmed = ConfirmationRequested?.Invoke(
            L["DeleteCheckedConfirmTitle"],
            string.Format(L["DeleteCheckedConfirmMessage"], sessions.Count)) ?? false;
        if (!confirmed)
        {
            return;
        }

        int removed = sessions.Count(session => RemoveSessionData(session.Id));
        IsSelectionMode = false;
        RefreshSessionList();
        _completedSessionsView.Refresh();
        ShowFeedback(string.Format(L["SessionsDeletedFeedback"], removed));
        AppendLog($"[session] Bulk deleted: {removed}");
    }

    /// <summary>
    /// Rebuilds the knowledge base summary of a topic. <paramref name="quiet"/> is for automatic rebuilds
    /// (after sources were removed): a failure then shows a short note instead of an error window.
    /// </summary>
    private async Task RefreshTopicKnowledgeAsync(SessionTopic? topic = null, bool quiet = false)
    {
        topic ??= SelectedTopic;
        if (topic == null || IsTopicBusy)
        {
            return;
        }

        List<SessionSummary> sessions = CompletedSessions.Where(s => s.TopicId == topic.Id).ToList();
        if (sessions.Count == 0 && !topic.Sources.Any(s => s.ParsedAt != null))
        {
            return;
        }

        if (!TryValidateAssistSettings(out string settingsError))
        {
            if (!quiet)
            {
                NotificationRequested?.Invoke(L["ErrorTitle"], settingsError, true);
            }

            return;
        }

        IsTopicBusy = true;
        try
        {
            List<TopicDocument> documents = await LoadTopicDocumentsAsync(topic);
            AssistOptions options = BuildAssistOptions();
            AssistResult result = await Task.Run(() => _conversationAssistService.BuildTopicKnowledgeAsync(topic.Name, sessions, documents, options));
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                throw new InvalidOperationException(L["TopicKnowledgeEmptyAnswer"]);
            }

            topic.KnowledgeText = result.Text;
            topic.KnowledgeUpdatedAt = DateTime.Now;
            topic.KnowledgeSessionIds = sessions.Select(s => s.Id).ToList();
            topic.KnowledgeSourcesStamp = ComputeSourcesStamp(topic);
            SaveTopics();
            RefreshTopicStats();
            string cost = result.Usage?.CostRub is double rub ? $", {rub:0.##} ₽" : string.Empty;
            AppendLog($"[topics] Knowledge refreshed: {sessions.Count} session(s), {documents.Count} document(s), tokens={result.Usage?.TotalTokens ?? 0}{cost}.");
        }
        catch (Exception ex)
        {
            if (quiet)
            {
                ShowFeedback(L["KnowledgeAutoRefreshFailed"]);
            }
            else
            {
                NotificationRequested?.Invoke(L["ErrorTitle"], $"{L["OpenAiRequestFailed"]}\n{ex.Message}", true);
            }

            AppendLog("[topics] Knowledge refresh failed: " + ex.Message);
        }
        finally
        {
            IsTopicBusy = false;
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowFeedback(L["CopiedFeedback"]);
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
        }
    }

    internal void ShowFeedback(string text)
    {
        ActionFeedbackText = text;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void OnFollowUpEmailChanged()
    {
        OnPropertyChanged(nameof(CurrentFollowUpEmail));
        OnPropertyChanged(nameof(HasFollowUpEmail));
        CopyFollowUpEmailCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Interface language also sets the default speech and answer language: EN records and answers in English.
    /// Saved at once, like the theme, so the choice survives a restart without pressing «Сохранить».
    /// </summary>
    private void ApplyInterfaceLanguage(string language)
    {
        bool english = language == "en";
        _settings.UiLanguage = language;
        _settings.CaptureLanguage = english ? "en" : "ru";
        _settings.OutputLanguage = english ? "en" : "ru";
        _selectedOutputLanguage = _settings.OutputLanguage;
        OnPropertyChanged(nameof(SelectedOutputLanguage));
        if (english && _settings.MicParticipantName == "Я" && _settings.SystemParticipantName == "Собеседник")
        {
            _settings.MicParticipantName = "Me";
            _settings.SystemParticipantName = "Other";
        }
        else if (!english && _settings.MicParticipantName == "Me" && _settings.SystemParticipantName == "Other")
        {
            _settings.MicParticipantName = "Я";
            _settings.SystemParticipantName = "Собеседник";
        }

        PersistSettingsQuietly();
        OnPropertyChanged(nameof(IsRussianUi));
        OnPropertyChanged(nameof(IsEnglishUi));
        OnPropertyChanged(nameof(DeleteCheckedText));
        OnPropertyChanged(nameof(WhatsNewButtonText));
        OnPropertyChanged(nameof(WhatsNewTitle));
        OnPropertyChanged(nameof(TopicQuestionsSummary));
        OnPropertyChanged(nameof(RemoveCheckedQuestionsText));
        if (SelectedSession != null)
        {
            CurrentSessionSummary = BuildSummaryText(SelectedSession);
            UpdateUsageText(SelectedSession);
        }

        OnPropertyChanged(nameof(KnowledgeSourcesSummary));
        OnPropertyChanged(nameof(RemoveCheckedKnowledgeText));
        RebuildTopicItems();
        _completedSessionsView.Refresh();
        AppendLog($"[settings] Interface language: {language}, capture language: {_settings.CaptureLanguage}.");
    }

    private void PersistSettingsQuietly()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendLog("[settings] Save failed: " + ex.Message);
        }
    }

    /// <summary>Swaps the app-level palette brushes; overlay windows keep their own night palette.</summary>
    private void ApplyPalette(bool dark)
    {
        ApplyPlatformPalette(dark);
        ThemeApplied?.Invoke(dark);
    }

    /// <summary>Raised after the palette changes, with true for the night theme (the window repaints its title bar).</summary>
    public event Action<bool>? ThemeApplied;

    // Brush key, night color, day color. Night colors mirror Themes/EchoDarkPalette.xaml.
    private static readonly (string Key, string Dark, string Light)[] ThemePalette =
    [
        ("EchoBgBrush", "#0E1116", "#F3F6F5"),
        ("EchoSurfaceBrush", "#171B22", "#FFFFFF"),
        ("EchoSurfaceAltBrush", "#1E242D", "#E9F0EE"),
        ("EchoBorderBrush", "#2A313C", "#D3DCD9"),
        ("EchoTextBrush", "#E9EEF5", "#0E1116"),
        ("EchoMutedBrush", "#9AA5B5", "#4F5B67"),
        ("EchoSoftTextBrush", "#C9D2DE", "#2E3A45"),
        ("EchoAccentBrush", "#5EF0C1", "#3FD9A8"),
        ("EchoAccentTextBrush", "#5EF0C1", "#0B8A65"),
        ("EchoAccentInkBrush", "#0E1116", "#0E1116"),
        ("EchoAccentSoftBrush", "#14302A", "#DDF5EC"),
        ("EchoAccentSoftBorderBrush", "#245446", "#9ADFC6"),
        ("EchoBadgeTextBrush", "#BFF7E3", "#0B6B4F"),
        ("EchoHoverBrush", "#262D37", "#E3EAE8"),
        ("EchoHoverBorderBrush", "#3A4553", "#B9C6C2"),
        ("EchoHoverLayerBrush", "#FFFFFF", "#0E1116"),
        ("EchoUserBubbleBrush", "#243A33", "#D6F2E7"),
        ("EchoHeaderBrush", "#171B22", "#FFFFFF"),
        ("EchoHeroBrush", "#13171D", "#FFFFFF"),
        ("EchoInputBrush", "#12161C", "#FFFFFF"),
        ("EchoPopupBrush", "#F7171B22", "#FCFFFFFF"),
        ("EchoTooltipBrush", "#F21E242D", "#F7FFFFFF"),
        ("EchoCardBorderBrush", "#222831", "#DCE3E1"),
        ("EchoCheckBorderBrush", "#5C6878", "#9AA5B5"),
        ("EchoDangerSoftBrush", "#3A2230", "#FBE4EA"),
        ("EchoDangerBorderBrush", "#7B4C5F", "#E3A3B5"),
        ("EchoDangerTextBrush", "#FFD9E0", "#9B2C47"),
        ("EchoScrollThumbBrush", "#3A4553", "#C3CDCA"),
        ("EchoScrollThumbHoverBrush", "#55606F", "#9AA8A4"),
        ("EchoSelectedTabBrush", "#243A33", "#D6F2E7"),
        ("EchoDropTargetBrush", "#1A3A31", "#CDEFE2")
    ];
}

public enum TopicItemKind
{
    All,
    Unsorted,
    Topic
}

/// <summary>A row in the library sidebar: all sessions, sessions without a topic, or one topic.</summary>
public sealed class TopicItemViewModel : ObservableObject
{
    private int _sessionCount;
    private bool _isStale;
    private bool _isDropTarget;

    public TopicItemViewModel(TopicItemKind kind, SessionTopic? topic, string name)
    {
        Kind = kind;
        Topic = topic;
        Name = name;
    }

    public TopicItemKind Kind { get; }
    public SessionTopic? Topic { get; }
    public string Name { get; }
    public string Key => Topic?.Id ?? Kind.ToString();
    public bool IsTopic => Kind == TopicItemKind.Topic;
    public bool AcceptsDrop => Kind != TopicItemKind.All;

    public int SessionCount
    {
        get => _sessionCount;
        set
        {
            if (_sessionCount == value)
            {
                return;
            }

            _sessionCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>New sessions were added (or removed) since the knowledge base was built.</summary>
    public bool IsStale
    {
        get => _isStale;
        set
        {
            if (_isStale == value)
            {
                return;
            }

            _isStale = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Highlighted while sessions are dragged over the row.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value)
            {
                return;
            }

            _isDropTarget = value;
            OnPropertyChanged();
        }
    }
}
