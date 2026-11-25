using System.Collections.ObjectModel;
using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// The search box in the header: besides filtering the meeting list (as before), it shows a list of everything that
/// matches (Services/AppSearch.cs) grouped as topics, meetings, topic summaries, the knowledge base and questions.
/// A click opens the place where the words are: the meeting's summary or transcript tab, a topic tab, a source.
/// </summary>
public partial class MainViewModel
{
    private const int SearchGroupLimit = 20;
    private AppSearch? _appSearch;
    private CancellationTokenSource? _searchCancel;
    private bool _isSearchPopupOpen;
    private string _searchStatusText = string.Empty;

    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    public RelayCommand OpenSearchResultCommand { get; private set; } = null!;

    public RelayCommand CloseSearchCommand { get; private set; } = null!;

    public bool IsSearchPopupOpen
    {
        get => _isSearchPopupOpen;
        set
        {
            if (_isSearchPopupOpen == value)
            {
                return;
            }

            _isSearchPopupOpen = value;
            OnPropertyChanged();
        }
    }

    public string SearchStatusText => _searchStatusText;

    private void InitializeSearch()
    {
        _appSearch = new AppSearch(_knowledgeStore.GetSourcePath);
        OpenSearchResultCommand = new RelayCommand(parameter =>
        {
            if (parameter is SearchResultItem { Hit: { } hit })
            {
                OpenSearchHit(hit);
            }
        });
        CloseSearchCommand = new RelayCommand(() => IsSearchPopupOpen = false);
    }

    /// <summary>The query changed (typing pauses for a moment before the search runs).</summary>
    private async void ScheduleAppSearch()
    {
        _searchCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _searchCancel = cancel;
        string query = SessionQuery?.Trim() ?? string.Empty;
        if (query.Length < 2 || _appSearch == null)
        {
            SearchResults.Clear();
            IsSearchPopupOpen = false;
            return;
        }

        try
        {
            await Task.Delay(250, cancel.Token);
            List<SessionSummary> sessions = CompletedSessions.ToList();
            List<SessionTopic> topics = _topics.ToList();
            AppSearch search = _appSearch;
            List<SearchHit> hits = await Task.Run(() => search.Run(query, sessions, topics, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            ShowSearchResults(hits);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog("[search] Search failed: " + ex.Message);
        }
    }

    /// <summary>Enter in the search box opens the first result.</summary>
    public void OpenFirstSearchResult()
    {
        if (SearchResults.FirstOrDefault(r => r.IsResult)?.Hit is { } hit)
        {
            OpenSearchHit(hit);
        }
    }

    /// <summary>The search box got the focus again: show the last results.</summary>
    public void ReopenSearchResults()
    {
        if (SearchResults.Count > 0 && !string.IsNullOrWhiteSpace(SessionQuery))
        {
            IsSearchPopupOpen = true;
        }
    }

    private void ShowSearchResults(List<SearchHit> hits)
    {
        SearchResults.Clear();
        Dictionary<string, string> topicNames = _topics.ToDictionary(t => t.Id, t => t.Name);
        foreach (IGrouping<SearchHitKind, SearchHit> group in hits.GroupBy(h => h.Kind))
        {
            int count = group.Count();
            SearchResults.Add(SearchResultItem.Header(L[GroupKey(group.Key)] + " · " + count));
            foreach (SearchHit hit in group.Take(SearchGroupLimit))
            {
                SearchResults.Add(new SearchResultItem(hit, hit.Title, DescribeHit(hit, topicNames), hit.Snippet));
            }

            if (count > SearchGroupLimit)
            {
                SearchResults.Add(SearchResultItem.Header(string.Format(L["SearchMore"], count - SearchGroupLimit)));
            }
        }

        _searchStatusText = hits.Count == 0 ? L["SearchNothing"] : string.Empty;
        OnPropertyChanged(nameof(SearchStatusText));
        if (hits.Count == 0)
        {
            SearchResults.Add(SearchResultItem.Header(L["SearchNothing"]));
        }

        IsSearchPopupOpen = true;
        AppendLog($"[search] {hits.Count} result(s).");
    }

    private static string GroupKey(SearchHitKind kind) => kind switch
    {
        SearchHitKind.Topic => "SearchGroupTopics",
        SearchHitKind.Session => "SearchGroupSessions",
        SearchHitKind.TopicSummary => "SearchGroupTopicSummaries",
        SearchHitKind.KnowledgeSource => "SearchGroupKnowledge",
        _ => "SearchGroupQuestions"
    };

    private string DescribeHit(SearchHit hit, Dictionary<string, string> topicNames)
    {
        string topic = topicNames.TryGetValue(hit.TopicId, out string? name) ? name : string.Empty;
        string where = hit.Kind switch
        {
            SearchHitKind.Session => L["SearchIn" + hit.Field] + " · " + hit.When.ToString("dd.MM.yyyy HH:mm"),
            SearchHitKind.Topic => string.Empty,
            _ => string.Empty
        };

        if (hit.Kind != SearchHitKind.Topic && topic.Length > 0 && hit.Kind != SearchHitKind.TopicSummary)
        {
            where = where.Length > 0 ? where + " · " + topic : topic;
        }

        return where;
    }

    private void OpenSearchHit(SearchHit hit)
    {
        IsSearchPopupOpen = false;
        switch (hit.Kind)
        {
            case SearchHitKind.Session:
                SessionSummary? session = CompletedSessions.FirstOrDefault(s => s.Id == hit.SessionId);
                if (session == null)
                {
                    return;
                }

                OpenSession(session);
                SelectedSessionTab = hit.Field switch
                {
                    SearchField.Transcript => "Transcript",
                    SearchField.Email => "Email",
                    SearchField.Screen when session.Screenshots.Count > 0 => "Screenshots",
                    _ => "Summary"
                };
                break;
            case SearchHitKind.Topic:
                OpenTopicTab(hit.TopicId, TopicTabSessions);
                break;
            case SearchHitKind.TopicSummary:
                OpenTopicTab(hit.TopicId, TopicTabSummary);
                break;
            case SearchHitKind.KnowledgeSource:
                if (OpenTopicTab(hit.TopicId, TopicTabKnowledge))
                {
                    SelectedKnowledgeItem = KnowledgeItems.FirstOrDefault(item => item.Id == hit.SourceId) ?? SelectedKnowledgeItem;
                }

                break;
            case SearchHitKind.Question:
                if (OpenTopicTab(hit.TopicId, TopicTabQuestions))
                {
                    foreach (TopicQuestionItemViewModel item in QuestionItems)
                    {
                        item.IsExpanded = item.Entry.AskedAt == hit.AskedAt && item.Entry.Question == hit.Title;
                    }
                }

                break;
        }
    }

    private bool OpenTopicTab(string topicId, string tab)
    {
        TopicItemViewModel? item = TopicItems.FirstOrDefault(i => i.Topic?.Id == topicId);
        if (item == null)
        {
            return false;
        }

        // The list of meetings would be narrowed by the words just searched for: show the whole topic.
        _sessionQuery = string.Empty;
        OnPropertyChanged(nameof(SessionQuery));
        SelectedTopicItem = item;
        _completedSessionsView.Refresh();
        TopicTab = tab;
        SelectedMainPage = "Home";
        return true;
    }
}

/// <summary>A row of the search list: a group header or one result.</summary>
public sealed class SearchResultItem
{
    public SearchResultItem(SearchHit? hit, string title, string where, string snippet)
    {
        Hit = hit;
        Title = title;
        Where = where;
        Snippet = snippet;
    }

    public static SearchResultItem Header(string text) => new(null, text, string.Empty, string.Empty);

    public SearchHit? Hit { get; }

    public bool IsHeader => Hit == null;

    public bool IsResult => Hit != null;

    public string Title { get; }

    public string Where { get; }

    public bool HasWhere => Where.Length > 0;

    public string Snippet { get; }

    public bool HasSnippet => Snippet.Length > 0;
}
