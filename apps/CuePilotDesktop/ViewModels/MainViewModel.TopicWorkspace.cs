using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>
/// Inside a topic: tabs «Сессии», «База знаний» (files, folders and notes parsed to Markdown),
/// «Вопросы» (a dialog about the topic) and «Граф» (entities and relations).
/// </summary>
public partial class MainViewModel
{
    public const string TopicTabSessions = "Sessions";
    public const string TopicTabSummary = "Summary";
    public const string TopicTabKnowledge = "Knowledge";
    public const string TopicTabQuestions = "Questions";
    public const string TopicTabGraph = "Graph";

    private const int MaxImagesPerFolder = 40;
    private const int MaxSavedQuestions = 100;
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(10);

    private readonly KnowledgeStore _knowledgeStore = new();
    private string _topicTab = TopicTabSessions;
    private KnowledgeItemViewModel? _selectedKnowledgeItem;
    private string _knowledgePreviewText = string.Empty;
    private string _topicQuestionText = string.Empty;
    private bool _isTopicQuestionBusy;
    private bool _isGraphBusy;
    private bool _isKnowledgeSelectionMode;
    private int _checkedKnowledgeCount;
    private bool _isQuestionSelectionMode;
    private int _checkedQuestionsCount;
    private int _previewVersion;
    private TopicGraph? _currentGraph;

    public ObservableCollection<KnowledgeItemViewModel> KnowledgeItems { get; } = [];
    public ObservableCollection<TopicQuestionItemViewModel> QuestionItems { get; } = [];

    public RelayCommand AddKnowledgeFilesCommand { get; private set; } = null!;
    public RelayCommand AddKnowledgeFolderCommand { get; private set; } = null!;
    public RelayCommand AddKnowledgeNoteCommand { get; private set; } = null!;
    public RelayCommand ParseKnowledgeItemCommand { get; private set; } = null!;
    public RelayCommand ParseAllKnowledgeCommand { get; private set; } = null!;
    public RelayCommand RemoveKnowledgeItemCommand { get; private set; } = null!;
    public RelayCommand RemoveCheckedKnowledgeCommand { get; private set; } = null!;
    public RelayCommand ToggleKnowledgeSelectionModeCommand { get; private set; } = null!;
    public RelayCommand SelectAllKnowledgeCommand { get; private set; } = null!;
    public RelayCommand EditKnowledgeNoteCommand { get; private set; } = null!;
    public RelayCommand OpenKnowledgeOriginalCommand { get; private set; } = null!;
    public RelayCommand OpenKnowledgeFolderCommand { get; private set; } = null!;
    public RelayCommand CopyKnowledgePreviewCommand { get; private set; } = null!;
    public RelayCommand AskTopicCommand { get; private set; } = null!;
    public RelayCommand AskTopicSuggestionCommand { get; private set; } = null!;
    public RelayCommand CopyTopicAnswerCommand { get; private set; } = null!;
    public RelayCommand ToggleQuestionCommand { get; private set; } = null!;
    public RelayCommand RemoveQuestionCommand { get; private set; } = null!;
    public RelayCommand RemoveCheckedQuestionsCommand { get; private set; } = null!;
    public RelayCommand ToggleQuestionSelectionModeCommand { get; private set; } = null!;
    public RelayCommand SelectAllQuestionsCommand { get; private set; } = null!;
    public RelayCommand BuildTopicGraphCommand { get; private set; } = null!;

    /// <summary>Opens the note editor (title, text) and returns the edited values, or null when cancelled. Set by the window.</summary>
    public Func<string, string, (string Title, string Text)?>? NoteEditor { get; set; }

    /// <summary>The graph of the selected topic changed (loaded, rebuilt or cleared).</summary>
    public event Action? TopicGraphChanged;

    public string TopicTab
    {
        get => _topicTab;
        set
        {
            if (_topicTab == value)
            {
                return;
            }

            _topicTab = value;
            OnPropertyChanged();
            OnTopicTabChanged();
        }
    }

    public bool IsTopicTabSessions { get => TopicTab == TopicTabSessions; set { if (value) TopicTab = TopicTabSessions; } }
    public bool IsTopicTabSummary { get => TopicTab == TopicTabSummary; set { if (value) TopicTab = TopicTabSummary; } }
    public bool IsTopicTabKnowledge { get => TopicTab == TopicTabKnowledge; set { if (value) TopicTab = TopicTabKnowledge; } }
    public bool IsTopicTabQuestions { get => TopicTab == TopicTabQuestions; set { if (value) TopicTab = TopicTabQuestions; } }
    public bool IsTopicTabGraph { get => TopicTab == TopicTabGraph; set { if (value) TopicTab = TopicTabGraph; } }

    /// <summary>«Все сессии» and «Без темы» only have the session list.</summary>
    public bool ShowSessionsPanel => !IsTopicSelected || TopicTab == TopicTabSessions;
    public bool ShowSummaryPanel => IsTopicSelected && TopicTab == TopicTabSummary;
    public bool ShowKnowledgePanel => IsTopicSelected && TopicTab == TopicTabKnowledge;
    public bool ShowQuestionsPanel => IsTopicSelected && TopicTab == TopicTabQuestions;
    public bool ShowGraphPanel => IsTopicSelected && TopicTab == TopicTabGraph;

    public int KnowledgeSourcesCount => SelectedTopic?.Sources.Count ?? 0;
    public int TopicQuestionsCount => SelectedTopic?.Questions.Count ?? 0;

    public KnowledgeItemViewModel? SelectedKnowledgeItem
    {
        get => _selectedKnowledgeItem;
        set
        {
            if (ReferenceEquals(_selectedKnowledgeItem, value))
            {
                return;
            }

            _selectedKnowledgeItem = value;
            OnPropertyChanged();
            RefreshKnowledgePreview();
        }
    }

    /// <summary>Markdown shown on the right of «База знаний»: the summary or the parsed text of the selected source.</summary>
    public string KnowledgePreviewText
    {
        get => _knowledgePreviewText;
        private set
        {
            _knowledgePreviewText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasKnowledgePreview));
            CopyKnowledgePreviewCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool HasKnowledgePreview => !string.IsNullOrWhiteSpace(KnowledgePreviewText);

    public bool IsKnowledgeSourceSelected => _selectedKnowledgeItem != null;

    /// <summary>Status under the title of the right pane.</summary>
    public string KnowledgeHeaderStatus => _selectedKnowledgeItem switch
    {
        null => string.Empty,
        { IsNote: true } item => item.StatusText,
        { } item => item.StatusText + " · " + item.Subtitle
    };

    /// <summary>What to do when the selected item has no text yet.</summary>
    public string KnowledgeEmptyText => _selectedKnowledgeItem switch
    {
        null => string.Empty,
        { IsBusy: true } => L["KnowledgeParsing"],
        { State: KnowledgeItemState.Failed } item => item.StatusText,
        _ => L["KnowledgeSourceEmpty"]
    };

    public bool HasKnowledgeSources => KnowledgeItems.Count > 0;

    public bool HasUnparsedKnowledge => KnowledgeItems.Any(item => item.NeedsParsing);

    /// <summary>Checkboxes on the sources for deleting several at once.</summary>
    public bool IsKnowledgeSelectionMode
    {
        get => _isKnowledgeSelectionMode;
        set
        {
            if (_isKnowledgeSelectionMode == value)
            {
                return;
            }

            _isKnowledgeSelectionMode = value;
            if (!value)
            {
                foreach (KnowledgeItemViewModel item in KnowledgeItems)
                {
                    item.IsChecked = false;
                }
            }

            OnPropertyChanged();
            UpdateCheckedKnowledgeCount();
        }
    }

    public int CheckedKnowledgeCount
    {
        get => _checkedKnowledgeCount;
        private set
        {
            if (_checkedKnowledgeCount == value)
            {
                return;
            }

            _checkedKnowledgeCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoveCheckedKnowledgeText));
            RemoveCheckedKnowledgeCommand?.RaiseCanExecuteChanged();
        }
    }

    public string RemoveCheckedKnowledgeText => string.Format(L["KnowledgeRemoveChecked"], CheckedKnowledgeCount);

    public string KnowledgeSourcesSummary => string.Format(L["KnowledgeSourcesSummary"], KnowledgeSourcesCount);

    public string TopicQuestionText
    {
        get => _topicQuestionText;
        set
        {
            if (_topicQuestionText == value)
            {
                return;
            }

            _topicQuestionText = value;
            OnPropertyChanged();
            AskTopicCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool IsTopicQuestionBusy
    {
        get => _isTopicQuestionBusy;
        private set
        {
            if (_isTopicQuestionBusy == value)
            {
                return;
            }

            _isTopicQuestionBusy = value;
            OnPropertyChanged();
            AskTopicCommand?.RaiseCanExecuteChanged();
            AskTopicSuggestionCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool HasTopicQuestions => QuestionItems.Count > 0;

    /// <summary>Checkboxes on the questions for deleting several at once, like the session list.</summary>
    public bool IsQuestionSelectionMode
    {
        get => _isQuestionSelectionMode;
        set
        {
            if (_isQuestionSelectionMode == value)
            {
                return;
            }

            _isQuestionSelectionMode = value;
            if (!value)
            {
                foreach (TopicQuestionItemViewModel item in QuestionItems)
                {
                    item.IsChecked = false;
                }
            }

            OnPropertyChanged();
        }
    }

    public int CheckedQuestionsCount
    {
        get => _checkedQuestionsCount;
        private set
        {
            if (_checkedQuestionsCount == value)
            {
                return;
            }

            _checkedQuestionsCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoveCheckedQuestionsText));
            RemoveCheckedQuestionsCommand?.RaiseCanExecuteChanged();
        }
    }

    public string RemoveCheckedQuestionsText => string.Format(L["QuestionsRemoveChecked"], CheckedQuestionsCount);

    public string TopicQuestionsSummary => string.Format(L["QuestionsSummary"], QuestionItems.Count(i => !i.IsPending));

    public bool IsGraphBusy
    {
        get => _isGraphBusy;
        private set
        {
            if (_isGraphBusy == value)
            {
                return;
            }

            _isGraphBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GraphStatusText));
            BuildTopicGraphCommand?.RaiseCanExecuteChanged();
        }
    }

    public TopicGraph? CurrentGraph => _currentGraph;

    public bool HasGraph => _currentGraph is { Nodes.Count: > 0 };

    public bool IsGraphStale
    {
        get
        {
            SessionTopic? topic = SelectedTopic;
            return topic != null && HasGraph && topic.GraphStamp != ComputeGraphStamp(topic);
        }
    }

    public string GraphStatusText
    {
        get
        {
            SessionTopic? topic = SelectedTopic;
            if (topic == null)
            {
                return string.Empty;
            }

            if (IsGraphBusy)
            {
                return L["GraphBusy"];
            }

            if (!HasGraph)
            {
                return L["GraphEmpty"];
            }

            string status = string.Format(L["GraphUpToDate"], _currentGraph!.Nodes.Count, _currentGraph.Edges.Count, topic.GraphUpdatedAt?.ToString("g") ?? string.Empty);
            return IsGraphStale ? status + " · " + L["GraphStale"] : status;
        }
    }

    public string BuildGraphButtonText => HasGraph ? L["GraphRebuild"] : L["GraphBuild"];

    private void InitializeTopicWorkspace()
    {
        AddKnowledgeFilesCommand = new RelayCommand(AddKnowledgeFiles, () => SelectedTopic != null);
        AddKnowledgeFolderCommand = new RelayCommand(AddKnowledgeFolder, () => SelectedTopic != null);
        AddKnowledgeNoteCommand = new RelayCommand(AddKnowledgeNote, () => SelectedTopic != null);
        ParseKnowledgeItemCommand = new RelayCommand(
            async parameter => await ParseKnowledgeItemAsync(parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem),
            parameter => (parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem) is { IsBusy: false });
        ParseAllKnowledgeCommand = new RelayCommand(async () => await ParseAllKnowledgeAsync(), () => HasUnparsedKnowledge);
        RemoveKnowledgeItemCommand = new RelayCommand(
            async parameter =>
            {
                if ((parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem) is { } item)
                {
                    await RemoveKnowledgeItemsAsync([item]);
                }
            },
            parameter => (parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem) is { IsBusy: false });
        RemoveCheckedKnowledgeCommand = new RelayCommand(
            async () => await RemoveKnowledgeItemsAsync(KnowledgeItems.Where(i => i.IsChecked).ToList()),
            () => CheckedKnowledgeCount > 0);
        ToggleKnowledgeSelectionModeCommand = new RelayCommand(() => IsKnowledgeSelectionMode = !IsKnowledgeSelectionMode);
        SelectAllKnowledgeCommand = new RelayCommand(() =>
        {
            bool check = KnowledgeItems.Any(i => !i.IsChecked);
            foreach (KnowledgeItemViewModel item in KnowledgeItems)
            {
                item.IsChecked = check;
            }
        });
        EditKnowledgeNoteCommand = new RelayCommand(
            parameter => EditKnowledgeNote(parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem),
            parameter => (parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem) is { IsNote: true, IsBusy: false });
        OpenKnowledgeOriginalCommand = new RelayCommand(
            parameter => OpenKnowledgeOriginal(parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem),
            parameter => (parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem) is { HasOriginal: true });
        OpenKnowledgeFolderCommand = new RelayCommand(OpenKnowledgeFolder, () => SelectedTopic != null);
        CopyKnowledgePreviewCommand = new RelayCommand(() => CopyToClipboard(KnowledgePreviewText), () => HasKnowledgePreview);
        AskTopicCommand = new RelayCommand(async () => await AskTopicAsync(TopicQuestionText), () => CanAskTopic && !string.IsNullOrWhiteSpace(TopicQuestionText));
        AskTopicSuggestionCommand = new RelayCommand(async parameter => await AskTopicAsync(parameter as string ?? string.Empty), _ => CanAskTopic);
        CopyTopicAnswerCommand = new RelayCommand(parameter => CopyToClipboard((parameter as TopicQuestionItemViewModel)?.Answer ?? string.Empty));
        ToggleQuestionCommand = new RelayCommand(parameter => ToggleQuestion(parameter as TopicQuestionItemViewModel));
        RemoveQuestionCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is TopicQuestionItemViewModel { IsPending: false } item)
                {
                    RemoveQuestions([item]);
                }
            },
            parameter => parameter is TopicQuestionItemViewModel { IsPending: false });
        RemoveCheckedQuestionsCommand = new RelayCommand(
            () => RemoveQuestions(QuestionItems.Where(i => i.IsChecked).ToList()),
            () => CheckedQuestionsCount > 0);
        ToggleQuestionSelectionModeCommand = new RelayCommand(() => IsQuestionSelectionMode = !IsQuestionSelectionMode);
        SelectAllQuestionsCommand = new RelayCommand(() =>
        {
            bool check = QuestionItems.Any(i => !i.IsPending && !i.IsChecked);
            foreach (TopicQuestionItemViewModel item in QuestionItems.Where(i => !i.IsPending))
            {
                item.IsChecked = check;
            }
        });
        BuildTopicGraphCommand = new RelayCommand(async () => await BuildTopicGraphAsync(), () => SelectedTopic != null && !IsGraphBusy && TopicHasMaterial(SelectedTopic));
    }

    /// <summary>1.0.1 kept only the last answer: carry it over into the dialog history once the topics are loaded.</summary>
    private void MigrateTopicQuestions()
    {
        foreach (SessionTopic topic in _topics)
        {
            topic.Questions ??= [];
            topic.Sources ??= [];
        }

        foreach (SessionTopic topic in _topics.Where(t => t.Questions.Count == 0 && !string.IsNullOrWhiteSpace(t.LastAnswer)))
        {
            topic.Questions.Add(new TopicQuestionEntry
            {
                Question = topic.LastQuestion,
                Answer = topic.LastAnswer,
                AskedAt = topic.LastAnswerAt ?? DateTime.Now
            });
        }
    }

    private bool CanAskTopic => SelectedTopic != null && !IsTopicQuestionBusy && TopicHasMaterial(SelectedTopic);

    private bool TopicHasMaterial(SessionTopic? topic) =>
        topic != null && ((_selectedTopicItem?.SessionCount ?? 0) > 0 || topic.Sources.Any(s => s.ParsedAt != null));

    private void OnTopicTabChanged()
    {
        OnPropertyChanged(nameof(IsTopicTabSessions));
        OnPropertyChanged(nameof(IsTopicTabSummary));
        OnPropertyChanged(nameof(IsTopicTabKnowledge));
        OnPropertyChanged(nameof(IsTopicTabQuestions));
        OnPropertyChanged(nameof(IsTopicTabGraph));
        OnPropertyChanged(nameof(ShowSessionsPanel));
        OnPropertyChanged(nameof(ShowSummaryPanel));
        OnPropertyChanged(nameof(ShowKnowledgePanel));
        OnPropertyChanged(nameof(ShowQuestionsPanel));
        OnPropertyChanged(nameof(ShowGraphPanel));
        if (TopicTab == TopicTabGraph)
        {
            OnPropertyChanged(nameof(GraphStatusText));
            OnPropertyChanged(nameof(IsGraphStale));
        }
    }

    /// <summary>Selected topic changed, or its sessions or sources did.</summary>
    private void OnTopicWorkspaceChanged()
    {
        if (_knowledgeItemsTopicId != SelectedTopic?.Id)
        {
            _knowledgeItemsTopicId = SelectedTopic?.Id;
            RebuildKnowledgeItems();
            RebuildQuestionItems();
            LoadGraph();
        }
        else
        {
            foreach (KnowledgeItemViewModel item in KnowledgeItems)
            {
                item.Refresh(L);
            }
        }

        OnKnowledgeHeaderChanged();
        OnTopicTabChanged();
        OnPropertyChanged(nameof(KnowledgeSourcesCount));
        OnPropertyChanged(nameof(KnowledgeSourcesSummary));
        OnPropertyChanged(nameof(TopicQuestionsCount));
        OnPropertyChanged(nameof(GraphStatusText));
        OnPropertyChanged(nameof(IsGraphStale));
        AddKnowledgeFilesCommand?.RaiseCanExecuteChanged();
        AddKnowledgeFolderCommand?.RaiseCanExecuteChanged();
        AddKnowledgeNoteCommand?.RaiseCanExecuteChanged();
        OpenKnowledgeFolderCommand?.RaiseCanExecuteChanged();
        AskTopicCommand?.RaiseCanExecuteChanged();
        AskTopicSuggestionCommand?.RaiseCanExecuteChanged();
        BuildTopicGraphCommand?.RaiseCanExecuteChanged();
    }

    private string? _knowledgeItemsTopicId;

    private void RebuildKnowledgeItems()
    {
        string? selectedId = _selectedKnowledgeItem?.Id;
        _isKnowledgeSelectionMode = false;
        OnPropertyChanged(nameof(IsKnowledgeSelectionMode));
        KnowledgeItems.Clear();
        SessionTopic? topic = SelectedTopic;
        if (topic != null)
        {
            foreach (KnowledgeSource source in topic.Sources)
            {
                KnowledgeItems.Add(NewKnowledgeItem(source));
            }
        }

        _selectedKnowledgeItem = KnowledgeItems.FirstOrDefault(i => i.Id == selectedId) ?? KnowledgeItems.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedKnowledgeItem));
        RefreshKnowledgePreview();
        OnKnowledgeItemsChanged();
        UpdateCheckedKnowledgeCount();
        _ = RefreshKnowledgeStampsAsync(KnowledgeItems.ToList());
    }

    private KnowledgeItemViewModel NewKnowledgeItem(KnowledgeSource source)
    {
        var item = new KnowledgeItemViewModel(source);
        item.Refresh(L);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KnowledgeItemViewModel.IsChecked))
            {
                UpdateCheckedKnowledgeCount();
            }
        };
        return item;
    }

    private void UpdateCheckedKnowledgeCount()
    {
        CheckedKnowledgeCount = KnowledgeItems.Count(i => i.IsChecked);
    }

    /// <summary>
    /// Checks the originals on disk for changes in the background: a folder on OneDrive or a network share
    /// can take seconds to list, and the window must not wait for it.
    /// </summary>
    private async Task RefreshKnowledgeStampsAsync(IReadOnlyList<KnowledgeItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        List<(KnowledgeItemViewModel Item, string Stamp)> stamps = await Task.Run(() => items
            .Where(i => i.Source != null)
            .Select(i => (i, KnowledgeStore.ComputeStamp(i.Source!)))
            .ToList());
        foreach ((KnowledgeItemViewModel item, string stamp) in stamps)
        {
            item.SetCurrentStamp(stamp);
            item.Refresh(L);
        }

        OnKnowledgeItemsChanged();
    }

    private void OnKnowledgeItemsChanged()
    {
        OnKnowledgeHeaderChanged();
        OnPropertyChanged(nameof(HasKnowledgeSources));
        OnPropertyChanged(nameof(HasUnparsedKnowledge));
        OnPropertyChanged(nameof(KnowledgeSourcesCount));
        OnPropertyChanged(nameof(KnowledgeSourcesSummary));
        ParseAllKnowledgeCommand?.RaiseCanExecuteChanged();
        ParseKnowledgeItemCommand?.RaiseCanExecuteChanged();
        RemoveKnowledgeItemCommand?.RaiseCanExecuteChanged();
        EditKnowledgeNoteCommand?.RaiseCanExecuteChanged();
        OpenKnowledgeOriginalCommand?.RaiseCanExecuteChanged();
        RefreshTopicKnowledgeCommand?.RaiseCanExecuteChanged();
        AskTopicCommand?.RaiseCanExecuteChanged();
        AskTopicSuggestionCommand?.RaiseCanExecuteChanged();
        BuildTopicGraphCommand?.RaiseCanExecuteChanged();
    }

    private void RefreshKnowledgePreview()
    {
        _previewVersion++;
        SessionTopic? topic = SelectedTopic;
        KnowledgeItemViewModel? item = _selectedKnowledgeItem;
        if (topic == null || item == null)
        {
            KnowledgePreviewText = string.Empty;
        }
        else if (item.Source.ParsedAt == null)
        {
            KnowledgePreviewText = string.Empty;
        }
        else
        {
            _ = LoadKnowledgePreviewAsync(topic.Id, item);
        }

        OnPropertyChanged(nameof(IsKnowledgeSourceSelected));
        OnKnowledgeHeaderChanged();
        ParseKnowledgeItemCommand?.RaiseCanExecuteChanged();
        RemoveKnowledgeItemCommand?.RaiseCanExecuteChanged();
        EditKnowledgeNoteCommand?.RaiseCanExecuteChanged();
        OpenKnowledgeOriginalCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Reads the parsed Markdown of a source off the UI thread; a newer selection wins.</summary>
    private async Task LoadKnowledgePreviewAsync(string topicId, KnowledgeItemViewModel item)
    {
        int version = _previewVersion;
        string text;
        try
        {
            text = await Task.Run(() => _knowledgeStore.ReadSource(topicId, item.Id));
        }
        catch (Exception ex)
        {
            text = string.Empty;
            AppendLog("[knowledge] Preview failed: " + ex.Message);
        }

        if (version == _previewVersion && ReferenceEquals(_selectedKnowledgeItem, item))
        {
            KnowledgePreviewText = text;
        }
    }

    private void OnKnowledgeHeaderChanged()
    {
        OnPropertyChanged(nameof(KnowledgeHeaderStatus));
        OnPropertyChanged(nameof(KnowledgeEmptyText));
    }

    private async void AddKnowledgeFiles()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null)
        {
            return;
        }

        IReadOnlyList<string> files = await PickFilesAsync(
            L["KnowledgeAddFilesTitle"], L["KnowledgeFileFilterName"], DocumentParser.FileDialogFilter.Split('|')[1], true);
        if (files.Count == 0)
        {
            return;
        }

        AddKnowledgeSources(topic, files.Select(FileSource));
    }

    private async void AddKnowledgeFolder()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null)
        {
            return;
        }

        IReadOnlyList<string> folders = await PickFoldersAsync(L["KnowledgeAddFolderTitle"], true);
        if (folders.Count == 0)
        {
            return;
        }

        AddKnowledgeSources(topic, folders.Select(FolderSource));
    }

    private static KnowledgeSource FolderSource(string path) => new()
    {
        Kind = KnowledgeSourceKind.Folder,
        Title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path,
        Path = path
    };

    private static KnowledgeSource FileSource(string path) => new()
    {
        Kind = KnowledgeSourceKind.File,
        Title = Path.GetFileName(path),
        Path = path
    };

    /// <summary>
    /// Files and folders dropped or pasted into «База знаний»: every folder and every supported file becomes its own
    /// source. The paths are checked off the UI thread, since a network folder can take a while to answer.
    /// </summary>
    public async Task AddKnowledgePathsAsync(IReadOnlyList<string> paths)
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || paths.Count == 0)
        {
            return;
        }

        (List<KnowledgeSource> sources, int skipped) = await Task.Run(() => ClassifyPaths(paths));
        if (!ReferenceEquals(topic, SelectedTopic))
        {
            return;
        }

        if (sources.Count == 0)
        {
            ShowFeedback(L["KnowledgeUnsupportedFeedback"]);
            return;
        }

        AddKnowledgeSources(topic, sources, skipped);
    }

    private static (List<KnowledgeSource> Sources, int Skipped) ClassifyPaths(IEnumerable<string> paths)
    {
        var sources = new List<KnowledgeSource>();
        int skipped = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                sources.Add(FolderSource(path));
            }
            else if (File.Exists(path) && DocumentParser.IsSupported(path))
            {
                sources.Add(FileSource(path));
            }
            else
            {
                skipped++;
            }
        }

        return (sources, skipped);
    }

    /// <summary>
    /// Text dropped or pasted into «База знаний» becomes a note named after its first line. Text that is only
    /// paths («Копировать как путь» in Explorer) adds those files and folders instead.
    /// </summary>
    public async Task AddKnowledgeTextAsync(string text)
    {
        SessionTopic? topic = SelectedTopic;
        string trimmed = text.Trim();
        if (topic == null || trimmed.Length == 0)
        {
            return;
        }

        string[] lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('"'))
            .ToArray();
        if (lines.Length <= 200 && lines.All(IsRootedPath))
        {
            bool allExist = await Task.Run(() => lines.All(line => Directory.Exists(line) || File.Exists(line)));
            if (allExist)
            {
                await AddKnowledgePathsAsync(lines);
                return;
            }
        }

        if (!ReferenceEquals(topic, SelectedTopic))
        {
            return;
        }

        string firstLine = trimmed.Split('\n', 2)[0].Trim().TrimStart('#', '-', '*', '>').Trim();
        KnowledgeItemViewModel? item = AddKnowledgeSources(topic, [new KnowledgeSource
        {
            Kind = KnowledgeSourceKind.Note,
            Title = firstLine.Length > 0 ? Shorten(firstLine, 60) : L["KnowledgeNoteDefaultTitle"],
            NoteText = trimmed
        }]);

        // Same as a note typed in: no recognition needed, so it is stored and used at once.
        if (item != null)
        {
            _ = ParseKnowledgeItemAsync(item);
        }
    }

    private static bool IsRootedPath(string line)
    {
        try
        {
            return line.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(line);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void AddKnowledgeNote()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || NoteEditor == null)
        {
            return;
        }

        (string Title, string Text)? note = NoteEditor(string.Empty, string.Empty);
        if (note == null || string.IsNullOrWhiteSpace(note.Value.Text))
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(note.Value.Title) ? L["KnowledgeNoteDefaultTitle"] : note.Value.Title.Trim();
        KnowledgeItemViewModel? item = AddKnowledgeSources(topic, [new KnowledgeSource
        {
            Kind = KnowledgeSourceKind.Note,
            Title = title,
            NoteText = note.Value.Text.Trim()
        }]);

        // A note needs no recognition: store it right away so it is used at once.
        if (item != null)
        {
            _ = ParseKnowledgeItemAsync(item);
        }
    }

    private void EditKnowledgeNote(KnowledgeItemViewModel? item)
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || item?.Source is not { Kind: KnowledgeSourceKind.Note } source || NoteEditor == null)
        {
            return;
        }

        (string Title, string Text)? note = NoteEditor(source.Title, source.NoteText);
        if (note == null || string.IsNullOrWhiteSpace(note.Value.Text))
        {
            return;
        }

        source.Title = string.IsNullOrWhiteSpace(note.Value.Title) ? source.Title : note.Value.Title.Trim();
        source.NoteText = note.Value.Text.Trim();
        SaveTopics();
        item.SetCurrentStamp(KnowledgeStore.ComputeStamp(source));
        item.Refresh(L);
        _ = ParseKnowledgeItemAsync(item);
    }

    /// <summary>
    /// Adds sources, skipping paths already attached to the topic; returns the last added item.
    /// <paramref name="unsupported"/> files were dropped but cannot be parsed: the feedback mentions them.
    /// </summary>
    private KnowledgeItemViewModel? AddKnowledgeSources(SessionTopic topic, IEnumerable<KnowledgeSource> sources, int unsupported = 0)
    {
        KnowledgeItemViewModel? last = null;
        int added = 0;
        foreach (KnowledgeSource source in sources)
        {
            if (source.Kind != KnowledgeSourceKind.Note &&
                topic.Sources.Any(s => s.Kind == source.Kind && string.Equals(s.Path, source.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            topic.Sources.Add(source);
            last = NewKnowledgeItem(source);
            KnowledgeItems.Add(last);
            added++;
        }

        if (added == 0)
        {
            ShowFeedback(L["KnowledgeAlreadyAdded"]);
            return null;
        }

        SaveTopics();
        SelectedKnowledgeItem = last;
        OnKnowledgeItemsChanged();
        _ = RefreshKnowledgeStampsAsync(KnowledgeItems.Where(i => i.Source != null && i.Source.ParsedAt == null).ToList());
        ShowFeedback(unsupported > 0
            ? string.Format(L["KnowledgeAddedSkippedFeedback"], added, unsupported)
            : string.Format(L["KnowledgeAddedFeedback"], added));
        AppendLog($"[knowledge] Added {added} source(s)" + (unsupported > 0 ? $", skipped {unsupported} unsupported." : "."));
        return last;
    }

    /// <summary>
    /// Removes sources from the topic: their parsed Markdown is deleted, the graph loses the entities that only
    /// came from them, and the knowledge base summary is rebuilt from what is left. Originals are never touched.
    /// </summary>
    private async Task RemoveKnowledgeItemsAsync(IReadOnlyList<KnowledgeItemViewModel> items)
    {
        SessionTopic? topic = SelectedTopic;
        List<KnowledgeItemViewModel> removable = items.Where(i => i.Source != null && !i.IsBusy).ToList();
        if (topic == null || removable.Count == 0)
        {
            return;
        }

        string message = removable.Count == 1
            ? string.Format(L["KnowledgeRemoveConfirmMessage"], removable[0].Title)
            : string.Format(L["KnowledgeRemoveManyConfirmMessage"], removable.Count);
        bool confirmed = ConfirmationRequested?.Invoke(L["KnowledgeRemoveConfirmTitle"], message) ?? false;
        if (!confirmed)
        {
            return;
        }

        bool graphWasCurrent = HasGraph && topic.GraphStamp == ComputeGraphStamp(topic);
        bool hadKnowledge = !string.IsNullOrWhiteSpace(topic.KnowledgeText);
        List<KnowledgeSource> sources = removable.Select(i => i.Source!).ToList();
        string topicId = topic.Id;

        // Names the graph may use for these sources: titles, file names and, for folders, every parsed file.
        HashSet<string> removedNames = await Task.Run(() =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KnowledgeSource source in sources)
            {
                names.Add(source.Title);
                if (!string.IsNullOrWhiteSpace(source.Path))
                {
                    names.Add(Path.GetFileName(source.Path.TrimEnd(Path.DirectorySeparatorChar)));
                }

                if (source.Kind == KnowledgeSourceKind.Folder && source.ParsedAt != null)
                {
                    try
                    {
                        foreach (string line in _knowledgeStore.ReadSource(topicId, source.Id).Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)))
                        {
                            names.Add(Path.GetFileName(line[3..].Trim()));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            foreach (KnowledgeSource source in sources)
            {
                try
                {
                    _knowledgeStore.DeleteSource(topicId, source.Id);
                }
                catch (Exception ex)
                {
                    AppendLog("[knowledge] Parsed file not removed: " + ex.Message);
                }
            }

            return names;
        });

        int index = KnowledgeItems.IndexOf(removable[0]);
        foreach (KnowledgeItemViewModel item in removable)
        {
            topic.Sources.Remove(item.Source!);
            KnowledgeItems.Remove(item);
        }

        if (!topic.Sources.Any())
        {
            IsKnowledgeSelectionMode = false;
        }

        // The graph drops what only these sources said; this needs no model and keeps the picture as it was.
        int removedNodes = 0;
        if (ReferenceEquals(SelectedTopic, topic) && _currentGraph != null)
        {
            var remainingNames = new HashSet<string>(
                topic.Sources.Select(s => s.Title).Concat(CompletedSessions.Where(s => s.TopicId == topic.Id).Select(s => s.Title)),
                StringComparer.OrdinalIgnoreCase);
            removedNodes = KnowledgeGraphPruner.Prune(_currentGraph, removedNames, remainingNames);
            TopicGraph graph = _currentGraph;
            try
            {
                await Task.Run(() => _knowledgeStore.WriteGraph(topicId, graph));
            }
            catch (Exception ex)
            {
                AppendLog("[graph] Not saved after removal: " + ex.Message);
            }

            if (graphWasCurrent)
            {
                topic.GraphStamp = ComputeGraphStamp(topic);
            }

            OnGraphChanged();
        }

        bool hasMaterial = TopicHasMaterial(topic);
        // Unparsed sources never reached the summary, so removing only them costs nothing.
        bool refreshSummary = hadKnowledge && hasMaterial && sources.Any(s => s.ParsedAt != null);
        if (hadKnowledge && !hasMaterial)
        {
            topic.KnowledgeText = string.Empty;
            topic.KnowledgeUpdatedAt = null;
            topic.KnowledgeSessionIds = [];
            topic.KnowledgeSourcesStamp = string.Empty;
        }

        SaveTopics();
        SelectedKnowledgeItem = KnowledgeItems.ElementAtOrDefault(Math.Clamp(index, 0, KnowledgeItems.Count - 1));
        OnKnowledgeItemsChanged();
        UpdateCheckedKnowledgeCount();
        RefreshTopicStats();
        AppendLog($"[knowledge] Removed {removable.Count} source(s), graph nodes removed: {removedNodes}.");
        ShowFeedback(string.Format(L[refreshSummary ? "KnowledgeRemovedUpdatingFeedback" : "KnowledgeRemovedFeedback"], removable.Count));

        // The summary still repeats what the removed documents said: rebuild it from what is left.
        if (refreshSummary)
        {
            await RefreshTopicKnowledgeAsync(topic, quiet: true);
        }
    }

    private async Task ParseAllKnowledgeAsync()
    {
        foreach (KnowledgeItemViewModel item in KnowledgeItems.Where(i => i.NeedsParsing).ToList())
        {
            await ParseKnowledgeItemAsync(item);
        }
    }

    /// <summary>«Разобрать»: reads the file, every supported file of the folder, or the note, and stores Markdown.</summary>
    private async Task ParseKnowledgeItemAsync(KnowledgeItemViewModel? item)
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || item?.Source == null || item.IsBusy)
        {
            return;
        }

        KnowledgeSource source = item.Source;
        bool needsModel = source.Kind == KnowledgeSourceKind.Folder ||
            (source.Kind == KnowledgeSourceKind.File && (DocumentParser.IsImage(source.Path) || source.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)));
        bool modelAvailable = TryValidateAssistSettings(out string settingsError);
        if (needsModel && !modelAvailable && DocumentParser.IsImage(source.Path))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], settingsError, true);
            return;
        }

        AssistOptions? options = modelAvailable ? BuildAssistOptions() : null;
        double costRub = 0;
        int recognized = 0;
        async Task<string> RecognizeAsync(string imagePath, CancellationToken token)
        {
            if (options == null)
            {
                throw new InvalidOperationException(settingsError);
            }

            if (++recognized > MaxImagesPerFolder)
            {
                throw new InvalidOperationException(L["KnowledgeTooManyImages"]);
            }

            AssistResult result = await _conversationAssistService.RecognizeImageAsync(imagePath, options, token);
            costRub += result.Usage?.CostRub ?? 0;
            return result.Text;
        }

        item.IsBusy = true;
        item.ProgressText = L["KnowledgeParsing"];
        OnKnowledgeItemsChanged();
        try
        {
            string stamp = await Task.Run(() => KnowledgeStore.ComputeStamp(source));
            var markdown = new StringBuilder();
            int parsedFiles = 0;
            int skippedFiles = 0;
            var errors = new List<string>();
            switch (source.Kind)
            {
                case KnowledgeSourceKind.Note:
                    markdown.Append("# ").AppendLine(source.Title).AppendLine().AppendLine(source.NoteText.Trim());
                    parsedFiles = 1;
                    break;
                case KnowledgeSourceKind.File:
                    string body = await Task.Run(() => DocumentParser.ParseFileAsync(source.Path, RecognizeAsync, CancellationToken.None))
                        .WaitAsync(ParseTimeout);
                    markdown.Append("# ").AppendLine(source.Title).AppendLine()
                        .Append("_").Append(L["KnowledgeSourceLabel"]).Append(": ").Append(source.Path).AppendLine("_").AppendLine()
                        .AppendLine(body);
                    parsedFiles = 1;
                    break;
                case KnowledgeSourceKind.Folder:
                    List<string> files = await Task.Run(() => DocumentParser.EnumerateFolder(source.Path).ToList());
                    markdown.Append("# ").AppendLine(source.Title).AppendLine()
                        .Append("_").Append(L["KnowledgeSourceLabel"]).Append(": ").Append(source.Path).AppendLine("_").AppendLine();
                    for (int i = 0; i < files.Count; i++)
                    {
                        string file = files[i];
                        string relative = Path.GetRelativePath(source.Path, file);
                        item.ProgressText = string.Format(L["KnowledgeParsingFile"], i + 1, files.Count, relative);
                        OnKnowledgeHeaderChanged();
                        try
                        {
                            string fileBody = await Task.Run(() => DocumentParser.ParseFileAsync(file, RecognizeAsync, CancellationToken.None))
                                .WaitAsync(ParseTimeout);
                            markdown.Append("## ").AppendLine(relative).AppendLine().AppendLine(fileBody).AppendLine();
                            parsedFiles++;
                        }
                        catch (Exception ex)
                        {
                            skippedFiles++;
                            errors.Add($"{relative}: {(ex is TimeoutException ? L["KnowledgeParseTimeout"] : ex.Message)}");
                        }
                    }

                    if (errors.Count > 0)
                    {
                        markdown.Append("## ").AppendLine(L["KnowledgeSkippedHeading"]).AppendLine();
                        foreach (string error in errors.Take(50))
                        {
                            markdown.Append("- ").AppendLine(error);
                        }
                    }

                    break;
            }

            string text = markdown.ToString().Trim() + "\n";
            await Task.Run(() => _knowledgeStore.WriteSource(topic.Id, source.Id, text));
            source.ParsedAt = DateTime.Now;
            source.ParsedStamp = stamp;
            source.ParsedChars = text.Length;
            source.ParsedFiles = parsedFiles;
            source.SkippedFiles = skippedFiles;
            source.Error = parsedFiles == 0 && errors.Count > 0 ? errors[0] : string.Empty;
            SaveTopics();
            string cost = costRub > 0 ? $", {costRub:0.##} ₽" : string.Empty;
            AppendLog($"[knowledge] Parsed {source.Kind}: {parsedFiles} file(s), {skippedFiles} skipped, {text.Length} chars{cost}.");
        }
        catch (Exception ex)
        {
            source.Error = ex is TimeoutException ? L["KnowledgeParseTimeout"] : ex.Message;
            SaveTopics();
            AppendLog("[knowledge] Parse failed: " + ex.Message);
        }
        finally
        {
            item.IsBusy = false;
            item.SetCurrentStamp(source.ParsedStamp);
            item.Refresh(L);
            if (ReferenceEquals(SelectedKnowledgeItem, item))
            {
                RefreshKnowledgePreview();
            }

            OnKnowledgeItemsChanged();
            RefreshTopicStats();
        }
    }

    private void OpenKnowledgeOriginal(KnowledgeItemViewModel? item)
    {
        if (item?.Source is not { } source || string.IsNullOrWhiteSpace(source.Path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(source.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
        }
    }

    private void OpenKnowledgeFolder()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_knowledgeStore.GetTopicFolder(topic.Id)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], ex.Message, true);
        }
    }

    /// <summary>Parsed Markdown of every source of the topic, in the order they were added, read off the UI thread.</summary>
    private Task<List<TopicDocument>> LoadTopicDocumentsAsync(SessionTopic topic)
    {
        List<KnowledgeSource> parsed = topic.Sources.Where(s => s.ParsedAt != null).ToList();
        string topicId = topic.Id;
        return Task.Run(() => LoadTopicDocuments(topicId, parsed));
    }

    private List<TopicDocument> LoadTopicDocuments(string topicId, IReadOnlyList<KnowledgeSource> sources)
    {
        var documents = new List<TopicDocument>();
        foreach (KnowledgeSource source in sources)
        {
            try
            {
                string markdown = _knowledgeStore.ReadSource(topicId, source.Id);
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    documents.Add(new TopicDocument(source.Title, markdown));
                }
            }
            catch (Exception ex)
            {
                AppendLog("[knowledge] Source not read: " + ex.Message);
            }
        }

        return documents;
    }

    private static string ComputeSourcesStamp(SessionTopic topic) =>
        KnowledgeStore.Hash(string.Join("|", topic.Sources
            .Where(s => s.ParsedAt != null)
            .Select(s => s.Id + ":" + s.ParsedAt!.Value.Ticks)));

    private string ComputeGraphStamp(SessionTopic topic) =>
        KnowledgeStore.Hash(string.Join(",", CompletedSessions.Where(s => s.TopicId == topic.Id).Select(s => s.Id).OrderBy(id => id)) +
            "|" + ComputeSourcesStamp(topic));

    /// <summary>Newest question on top, like the session list; only the newest answer starts open.</summary>
    private void RebuildQuestionItems()
    {
        _isQuestionSelectionMode = false;
        OnPropertyChanged(nameof(IsQuestionSelectionMode));
        QuestionItems.Clear();
        IEnumerable<TopicQuestionEntry> entries = SelectedTopic?.Questions ?? [];
        foreach (TopicQuestionEntry entry in entries.Reverse())
        {
            QuestionItems.Add(NewQuestionItem(entry, expanded: QuestionItems.Count == 0));
        }

        OnQuestionItemsChanged();
    }

    private TopicQuestionItemViewModel NewQuestionItem(TopicQuestionEntry entry, bool expanded)
    {
        var item = new TopicQuestionItemViewModel(entry, L) { IsExpanded = expanded };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TopicQuestionItemViewModel.IsChecked))
            {
                CheckedQuestionsCount = QuestionItems.Count(i => i.IsChecked);
            }
        };
        return item;
    }

    private void OnQuestionItemsChanged()
    {
        CheckedQuestionsCount = QuestionItems.Count(i => i.IsChecked);
        OnPropertyChanged(nameof(HasTopicQuestions));
        OnPropertyChanged(nameof(TopicQuestionsCount));
        OnPropertyChanged(nameof(TopicQuestionsSummary));
    }

    /// <summary>A click on a question opens or folds its answer; while selecting it checks the question instead.</summary>
    private void ToggleQuestion(TopicQuestionItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        if (IsQuestionSelectionMode)
        {
            item.IsChecked = !item.IsChecked && !item.IsPending;
            return;
        }

        item.IsExpanded = !item.IsExpanded;
    }

    private void RemoveQuestions(IReadOnlyList<TopicQuestionItemViewModel> items)
    {
        SessionTopic? topic = SelectedTopic;
        List<TopicQuestionItemViewModel> removable = items.Where(i => !i.IsPending).ToList();
        if (topic == null || removable.Count == 0)
        {
            return;
        }

        string message = removable.Count == 1
            ? string.Format(L["QuestionRemoveConfirmMessage"], Shorten(removable[0].Question, 120))
            : string.Format(L["QuestionsRemoveManyConfirmMessage"], removable.Count);
        bool confirmed = ConfirmationRequested?.Invoke(L["QuestionRemoveConfirmTitle"], message) ?? false;
        if (!confirmed)
        {
            return;
        }

        foreach (TopicQuestionItemViewModel item in removable)
        {
            topic.Questions.Remove(item.Entry);
            QuestionItems.Remove(item);
        }

        if (topic.Questions.Count == 0)
        {
            // 1.0.1 kept the last answer separately; it must not come back as a question.
            topic.LastQuestion = string.Empty;
            topic.LastAnswer = string.Empty;
            topic.LastAnswerAt = null;
            IsQuestionSelectionMode = false;
        }

        SaveTopics();
        OnQuestionItemsChanged();
        AppendLog($"[topics] Removed {removable.Count} question(s).");
        ShowFeedback(string.Format(L["QuestionsRemovedFeedback"], removable.Count));
    }

    private static string Shorten(string text, int max)
    {
        string line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Asks about the topic; the answer is built from the sessions and documents themselves, so it is current.</summary>
    private async Task AskTopicAsync(string questionText)
    {
        SessionTopic? topic = SelectedTopic;
        string question = questionText.Trim();
        if (topic == null || IsTopicQuestionBusy || question.Length == 0)
        {
            return;
        }

        if (!TryValidateAssistSettings(out string settingsError))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], settingsError, true);
            return;
        }

        List<SessionSummary> sessions = CompletedSessions.Where(s => s.TopicId == topic.Id).ToList();
        if (sessions.Count == 0 && !topic.Sources.Any(s => s.ParsedAt != null))
        {
            return;
        }

        var pending = NewQuestionItem(new TopicQuestionEntry { Question = question }, expanded: true);
        pending.IsPending = true;
        QuestionItems.Insert(0, pending);
        OnQuestionItemsChanged();
        TopicQuestionText = string.Empty;
        IsTopicQuestionBusy = true;
        try
        {
            List<TopicDocument> documents = await LoadTopicDocumentsAsync(topic);
            AssistOptions options = BuildAssistOptions();
            List<TopicQuestionEntry> history = topic.Questions.ToList();
            // Choosing passages of big documents is real work: it runs off the UI thread together with the request.
            AssistResult result = await Task.Run(() => _conversationAssistService.AskTopicAsync(
                topic.Name, topic.KnowledgeText, sessions, documents, history, question, options));
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                throw new InvalidOperationException(L["TopicKnowledgeEmptyAnswer"]);
            }

            pending.Entry.Answer = result.Text;
            pending.Entry.AskedAt = DateTime.Now;
            pending.Entry.CostRub = result.Usage?.CostRub;
            topic.Questions.Add(pending.Entry);
            if (topic.Questions.Count > MaxSavedQuestions)
            {
                topic.Questions.RemoveRange(0, topic.Questions.Count - MaxSavedQuestions);
            }

            SaveTopics();
            pending.IsPending = false;
            pending.Refresh();
            string cost = result.Usage?.CostRub is double rub ? $", {rub:0.##} ₽" : string.Empty;
            AppendLog($"[topics] Question answered: {sessions.Count} session(s), {documents.Count} document(s), tokens={result.Usage?.TotalTokens ?? 0}{cost}.");
        }
        catch (Exception ex)
        {
            QuestionItems.Remove(pending);
            if (ReferenceEquals(SelectedTopic, topic) && string.IsNullOrWhiteSpace(TopicQuestionText))
            {
                TopicQuestionText = question;
            }

            NotificationRequested?.Invoke(L["ErrorTitle"], $"{L["OpenAiRequestFailed"]}\n{ex.Message}", true);
            AppendLog("[topics] Question failed: " + ex.Message);
        }
        finally
        {
            IsTopicQuestionBusy = false;
            OnQuestionItemsChanged();
        }
    }

    private void LoadGraph()
    {
        SessionTopic? topic = SelectedTopic;
        _currentGraph = null;
        if (topic != null)
        {
            try
            {
                _currentGraph = _knowledgeStore.ReadGraph(topic.Id);
            }
            catch (Exception ex)
            {
                AppendLog("[graph] Not loaded: " + ex.Message);
            }
        }

        OnGraphChanged();
    }

    private void OnGraphChanged()
    {
        OnPropertyChanged(nameof(CurrentGraph));
        OnPropertyChanged(nameof(HasGraph));
        OnPropertyChanged(nameof(IsGraphStale));
        OnPropertyChanged(nameof(GraphStatusText));
        OnPropertyChanged(nameof(BuildGraphButtonText));
        TopicGraphChanged?.Invoke();
    }

    private async Task BuildTopicGraphAsync()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || IsGraphBusy)
        {
            return;
        }

        if (!TryValidateAssistSettings(out string settingsError))
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], settingsError, true);
            return;
        }

        List<SessionSummary> sessions = CompletedSessions.Where(s => s.TopicId == topic.Id).ToList();
        IsGraphBusy = true;
        try
        {
            List<TopicDocument> documents = await LoadTopicDocumentsAsync(topic);
            AssistOptions options = BuildAssistOptions();
            (TopicGraph graph, AssistResult result) = await Task.Run(() => _conversationAssistService.BuildTopicGraphAsync(
                topic.Name, topic.KnowledgeText, sessions, documents, options));
            if (graph.Nodes.Count == 0)
            {
                throw new InvalidOperationException(L["GraphEmptyAnswer"]);
            }

            // Keep the positions of nodes that survived, so a rebuild does not scatter the picture.
            if (_currentGraph != null)
            {
                Dictionary<string, GraphNodeData> previous = _currentGraph.Nodes
                    .GroupBy(n => n.Label.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First());
                foreach (GraphNodeData node in graph.Nodes)
                {
                    if (previous.TryGetValue(node.Label.ToLowerInvariant(), out GraphNodeData? old))
                    {
                        node.X = old.X;
                        node.Y = old.Y;
                    }
                }
            }

            await Task.Run(() => _knowledgeStore.WriteGraph(topic.Id, graph));
            topic.GraphUpdatedAt = DateTime.Now;
            topic.GraphStamp = ComputeGraphStamp(topic);
            SaveTopics();
            if (ReferenceEquals(SelectedTopic, topic))
            {
                _currentGraph = graph;
                OnGraphChanged();
            }

            string cost = result.Usage?.CostRub is double rub ? $", {rub:0.##} ₽" : string.Empty;
            AppendLog($"[graph] Built: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, tokens={result.Usage?.TotalTokens ?? 0}{cost}.");
        }
        catch (Exception ex)
        {
            NotificationRequested?.Invoke(L["ErrorTitle"], $"{L["OpenAiRequestFailed"]}\n{ex.Message}", true);
            AppendLog("[graph] Build failed: " + ex.Message);
        }
        finally
        {
            IsGraphBusy = false;
        }
    }

    /// <summary>Called by the graph view after the user dragged nodes.</summary>
    public void SaveGraphLayout()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null || _currentGraph == null)
        {
            return;
        }

        try
        {
            _knowledgeStore.WriteGraph(topic.Id, _currentGraph);
        }
        catch (Exception ex)
        {
            AppendLog("[graph] Layout not saved: " + ex.Message);
        }
    }
}

/// <summary>A row of «База знаний»: one attached file, folder or note.</summary>
public sealed class KnowledgeItemViewModel : ObservableObject
{
    private bool _isBusy;
    private string _progressText = string.Empty;
    private string _statusText = string.Empty;
    private string _subtitle = string.Empty;
    private KnowledgeItemState _state;
    private string _currentStamp = string.Empty;
    private bool _isChecked;

    public KnowledgeItemViewModel(KnowledgeSource source)
    {
        Source = source;
    }

    public KnowledgeSource Source { get; }
    public bool IsNote => Source.Kind == KnowledgeSourceKind.Note;
    public bool HasOriginal => Source.Kind != KnowledgeSourceKind.Note;
    public string Id => Source.Id;
    public string Title => Source.Title;

    /// <summary>Kind glyph for the list: file, folder or note.</summary>
    public string KindGlyph => Source.Kind switch
    {
        KnowledgeSourceKind.Folder => "▣",
        KnowledgeSourceKind.Note => "✎",
        _ => "▤"
    };

    public string Subtitle
    {
        get => _subtitle;
        private set
        {
            _subtitle = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _isBusy ? _progressText : _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public KnowledgeItemState State
    {
        get => _isBusy ? KnowledgeItemState.Busy : _state;
        private set
        {
            _state = value;
            OnPropertyChanged();
        }
    }

    public bool NeedsParsing => !_isBusy && _state is KnowledgeItemState.New or KnowledgeItemState.Changed or KnowledgeItemState.Failed;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(NeedsParsing));
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            _progressText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string ParseButtonText { get; private set; } = string.Empty;

    /// <summary>Checked for removal in selection mode.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The state of the original on disk, computed in the background (see <see cref="KnowledgeStore.ComputeStamp"/>).</summary>
    public void SetCurrentStamp(string stamp)
    {
        _currentStamp = stamp;
    }

    /// <summary>Re-reads the status from the source and the last known state of the original.</summary>
    public void Refresh(LocalizationService l)
    {
        Subtitle = Source.Kind switch
        {
            KnowledgeSourceKind.Note => Shorten(Source.NoteText.ReplaceLineEndings(" "), 70),
            _ => Source.Path
        };

        if (_currentStamp == "missing")
        {
            State = KnowledgeItemState.Failed;
            StatusText = l["KnowledgeStatusMissing"];
        }
        else if (Source.ParsedAt == null)
        {
            State = string.IsNullOrEmpty(Source.Error) ? KnowledgeItemState.New : KnowledgeItemState.Failed;
            StatusText = string.IsNullOrEmpty(Source.Error) ? l["KnowledgeStatusNew"] : l["KnowledgeStatusFailed"] + ": " + Shorten(Source.Error, 90);
        }
        else if (_currentStamp.Length > 0 && Source.ParsedStamp != _currentStamp)
        {
            State = KnowledgeItemState.Changed;
            StatusText = l["KnowledgeStatusChanged"];
        }
        else
        {
            State = KnowledgeItemState.Parsed;
            string size = Source.ParsedChars >= 10000 ? $"{Source.ParsedChars / 1000} тыс." : Source.ParsedChars.ToString();
            StatusText = Source.Kind == KnowledgeSourceKind.Folder
                ? string.Format(l["KnowledgeStatusParsedFolder"], Source.ParsedAt.Value.ToString("g"), Source.ParsedFiles, Source.SkippedFiles)
                : string.Format(l["KnowledgeStatusParsed"], Source.ParsedAt.Value.ToString("g"), size);
        }

        ParseButtonText = Source.ParsedAt == null ? l["KnowledgeParse"] : l["KnowledgeReparse"];
        OnPropertyChanged(nameof(ParseButtonText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NeedsParsing));
    }

    private static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}

public enum KnowledgeItemState
{
    New,
    Busy,
    Parsed,
    Changed,
    Failed
}

/// <summary>One question of the «Вопросы» list: the answer opens and folds on a click.</summary>
public sealed class TopicQuestionItemViewModel : ObservableObject
{
    private readonly LocalizationService _l;
    private bool _isPending;
    private bool _isExpanded;
    private bool _isChecked;

    public TopicQuestionItemViewModel(TopicQuestionEntry entry, LocalizationService l)
    {
        Entry = entry;
        _l = l;
    }

    public TopicQuestionEntry Entry { get; }
    public string Question => Entry.Question;
    public string Answer => Entry.Answer;
    public string MetaText => Entry.AskedAt.ToString("g");

    /// <summary>The first lines of the answer without Markdown marks, shown while the answer is folded.</summary>
    public string AnswerPreview
    {
        get
        {
            string text = System.Text.RegularExpressions.Regex.Replace(Entry.Answer, @"[#*_`>|]+", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= 220 ? text : text[..219].TrimEnd() + "…";
        }
    }

    /// <summary>The price of the answer, shown on hover so the list itself stays free of money.</summary>
    public string? CostToolTip => Entry.CostRub is double rub ? string.Format(_l["TopicAnswerCost"], $"{rub:0.##} ₽") : null;

    public bool IsPending
    {
        get => _isPending;
        set
        {
            _isPending = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Checked for removal in selection mode.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value || (value && _isPending))
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(AnswerPreview));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(CostToolTip));
    }
}
