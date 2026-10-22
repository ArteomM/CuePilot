using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

/// <summary>
/// In-memory session repository. Sessions are loaded once; mutations mark a session dirty and a
/// background timer persists only dirty sessions. Returned objects are the live instances.
/// </summary>
public sealed class SessionLifecycleService : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly ISessionStore _sessionStore;
    private readonly object _sync = new();
    private readonly object _ioSync = new();
    private readonly List<SessionSummary> _sessions;
    private readonly HashSet<string> _dirty = [];
    private readonly HashSet<string> _deleted = [];
    private readonly Timer _flushTimer;
    private int _flushing;

    public SessionLifecycleService(ISessionStore sessionStore)
    {
        _sessionStore = sessionStore;
        _sessions = [.. _sessionStore.LoadSessions()];
        _flushTimer = new Timer(_ => FlushInBackground(), null, FlushInterval, FlushInterval);
    }

    public event Action<string>? PersistFailed;

    public IReadOnlyList<SessionSummary> LoadAll()
    {
        lock (_sync)
        {
            return _sessions.ToList();
        }
    }

    public SessionSummary StartNew(
        string title,
        string modeName = AppSettings.GeneralModeName,
        string modeRealtimePrompt = "",
        string modeNotesTemplate = "")
    {
        var session = new SessionSummary
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled session" : title,
            StartedAt = DateTime.Now,
            Status = SessionStatus.Active,
            ModeName = string.IsNullOrWhiteSpace(modeName) ? AppSettings.GeneralModeName : modeName,
            ModeRealtimePrompt = modeRealtimePrompt ?? string.Empty,
            ModeNotesTemplate = modeNotesTemplate ?? string.Empty
        };

        lock (_sync)
        {
            _sessions.Insert(0, session);
            MarkDirty(session.Id);
        }

        Flush();
        return session;
    }

    public SessionSummary? GetActive()
    {
        lock (_sync)
        {
            return _sessions.FirstOrDefault(s => s.Status == SessionStatus.Active);
        }
    }

    public SessionSummary? GetById(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_sync)
        {
            return Find(sessionId);
        }
    }

    public SessionTranscriptItem? AddTranscriptLine(string sessionId, SessionTranscriptItem item)
    {
        lock (_sync)
        {
            SessionSummary? session = Find(sessionId);
            if (session == null)
            {
                return null;
            }

            session.Transcript.Add(item);
            session.Usage.TranscriptItemsCount = session.Transcript.Count;
            session.Usage.SessionDurationMinutes = Math.Max(0.1, (DateTime.Now - session.StartedAt).TotalMinutes);
            MarkDirty(sessionId);
            return item;
        }
    }

    public void AddSuggestion(string sessionId)
    {
        Mutate(sessionId, session => session.Usage.SuggestionsCount++);
    }

    public void AddAssistMessage(string sessionId, SessionAssistMessage message)
    {
        Mutate(sessionId, session => session.AssistDialog.Add(message));
    }

    public void UpdateAssistUsage(string sessionId, string action, int attachedImages, AssistTokenUsage? tokenUsage)
    {
        Mutate(sessionId, session =>
        {
            session.Usage.AiRequestsCount++;
            session.Usage.LastAiRequestAt = DateTime.Now;
            session.Usage.LastAiAction = action ?? string.Empty;
            if (attachedImages > 0)
            {
                session.Usage.AiRequestsWithVisualContextCount++;
                session.Usage.AiImagesSentCount += attachedImages;
            }

            if (tokenUsage != null)
            {
                session.Usage.LastPromptTokens = tokenUsage.PromptTokens;
                session.Usage.LastCompletionTokens = tokenUsage.CompletionTokens;
                session.Usage.LastTotalTokens = tokenUsage.TotalTokens;
                session.Usage.LastCostRub = tokenUsage.CostRub;
                session.Usage.LlmCostRub += tokenUsage.CostRub ?? 0;
            }
        });
    }

    public void AddSttCost(string sessionId, double costRub)
    {
        if (costRub <= 0)
        {
            return;
        }

        Mutate(sessionId, session => session.Usage.SttCostRub += costRub);
    }

    /// <summary>Adds a frame and returns frames trimmed by the limit, so the caller can delete their files.</summary>
    public IReadOnlyList<SessionVisualFrame> AddVisualFrame(string sessionId, SessionVisualFrame frame, int maxFrames)
    {
        lock (_sync)
        {
            SessionSummary? session = Find(sessionId);
            if (session == null)
            {
                return [];
            }

            session.VisualFrames.Add(frame);
            int limit = Math.Clamp(maxFrames, 4, 120);
            List<SessionVisualFrame> removed = [];
            if (session.VisualFrames.Count > limit)
            {
                int removeCount = session.VisualFrames.Count - limit;
                removed = session.VisualFrames.GetRange(0, removeCount);
                session.VisualFrames.RemoveRange(0, removeCount);
            }

            MarkDirty(sessionId);
            return removed;
        }
    }

    public void UpdateSummary(string sessionId, string summaryText)
    {
        Mutate(sessionId, session => session.SummaryText = summaryText);
    }

    public void UpdateFollowUpEmail(string sessionId, string emailText)
    {
        Mutate(sessionId, session => session.FollowUpEmailText = emailText);
    }

    public void SetTopic(string sessionId, string topicId)
    {
        Mutate(sessionId, session => session.TopicId = topicId ?? string.Empty);
    }

    public void RenameSessionTitle(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        Mutate(sessionId, session => session.Title = title.Trim());
    }

    public bool DeleteSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_sync)
        {
            int removed = _sessions.RemoveAll(s => s.Id == sessionId);
            if (removed == 0)
            {
                return false;
            }

            _dirty.Remove(sessionId);
            _deleted.Add(sessionId);
        }

        Flush();
        return true;
    }

    public void Complete(string sessionId, string summaryText)
    {
        Mutate(sessionId, session => CompleteSession(session, summaryText));
        Flush();
    }

    public void CompleteActiveIfAny(string summaryText)
    {
        lock (_sync)
        {
            SessionSummary? active = _sessions.FirstOrDefault(s => s.Status == SessionStatus.Active);
            if (active == null)
            {
                return;
            }

            CompleteSession(active, summaryText);
            MarkDirty(active.Id);
        }

        Flush();
    }

    /// <summary>Synchronously persists pending changes.</summary>
    public void Flush()
    {
        List<(string Id, byte[]? Data)> pending;
        lock (_sync)
        {
            if (_dirty.Count == 0 && _deleted.Count == 0)
            {
                return;
            }

            pending = [];
            foreach (string id in _deleted)
            {
                pending.Add((id, null));
            }

            foreach (string id in _dirty)
            {
                SessionSummary? session = Find(id);
                if (session != null)
                {
                    pending.Add((id, _sessionStore.Serialize(session)));
                }
            }

            _dirty.Clear();
            _deleted.Clear();
        }

        lock (_ioSync)
        {
            foreach ((string id, byte[]? data) in pending)
            {
                try
                {
                    if (data == null)
                    {
                        _sessionStore.DeleteSession(id);
                    }
                    else
                    {
                        _sessionStore.WriteSession(id, data);
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        if (data != null && Find(id) != null)
                        {
                            _dirty.Add(id);
                        }
                    }

                    PersistFailed?.Invoke($"[session] Persist failed for {id}: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }

    private void FlushInBackground()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
        {
            return;
        }

        try
        {
            Flush();
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private void Mutate(string sessionId, Action<SessionSummary> mutation)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            SessionSummary? session = Find(sessionId);
            if (session == null)
            {
                return;
            }

            mutation(session);
            MarkDirty(sessionId);
        }
    }

    private SessionSummary? Find(string sessionId)
    {
        return _sessions.FirstOrDefault(s => s.Id == sessionId);
    }

    private void MarkDirty(string sessionId)
    {
        _dirty.Add(sessionId);
    }

    private static void CompleteSession(SessionSummary session, string summaryText)
    {
        session.EndedAt = DateTime.Now;
        session.Status = SessionStatus.Completed;
        session.SummaryText = summaryText;
        session.Usage.SessionDurationMinutes = Math.Max(0.1, (session.EndedAt.Value - session.StartedAt).TotalMinutes);
    }
}
