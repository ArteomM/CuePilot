using System.Globalization;
using System.Text.Json.Serialization;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;

namespace CuePilotDesktop.Models;

public enum SessionStatus
{
    Active,
    Completed
}

public enum SessionSource
{
    System,
    Mic
}

public class SessionTranscriptItem
{
    public DateTime Timestamp { get; set; }
    public SessionSource Source { get; set; }
    public string SpeakerName { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string FormattedText { get; set; } = string.Empty;
    public string SummaryReadyText { get; set; } = string.Empty;
}

public class SessionUsage
{
    public int TranscriptItemsCount { get; set; }
    public int SuggestionsCount { get; set; }
    public double SessionDurationMinutes { get; set; }
    public int AiRequestsCount { get; set; }
    public int AiRequestsWithVisualContextCount { get; set; }
    public int AiImagesSentCount { get; set; }
    public DateTime? LastAiRequestAt { get; set; }
    public string LastAiAction { get; set; } = string.Empty;
    public int LastPromptTokens { get; set; }
    public int LastCompletionTokens { get; set; }
    public int LastTotalTokens { get; set; }
    public double? LastCostRub { get; set; }

    /// <summary>LLM cost, RUB. Stored as "TotalCostRub" for compatibility with earlier session files.</summary>
    [JsonPropertyName("TotalCostRub")]
    public double LlmCostRub { get; set; }

    /// <summary>API speech recognition cost, RUB.</summary>
    public double SttCostRub { get; set; }

    [JsonIgnore]
    public double SessionCostRub => LlmCostRub + SttCostRub;

    /// <summary>Duration label for lists, in the interface language.</summary>
    [JsonIgnore]
    public string SessionDurationText => SessionDurationMinutes.ToString("0.0", CultureInfo.CurrentCulture) +
        (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? " мин" : " min");
}

public class SessionAssistMessage
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Action { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class SessionVisualFrame
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string FilePath { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ChangeReason { get; set; } = string.Empty;
    public int DifferenceScore { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public string OcrText { get; set; } = string.Empty;
}

public class SessionSummary : ObservableObject
{
    private bool _isChecked;
    private string _topicName = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled session";
    /// <summary>Topic the session is grouped into; empty when it has none.</summary>
    public string TopicId { get; set; } = string.Empty;
    /// <summary>Last generated follow-up email draft.</summary>
    public string FollowUpEmailText { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public string SummaryText { get; set; } = string.Empty;
    public string ModeName { get; set; } = AppSettings.GeneralModeName;
    public string ModeRealtimePrompt { get; set; } = string.Empty;
    public string ModeNotesTemplate { get; set; } = string.Empty;
    public List<SessionTranscriptItem> Transcript { get; set; } = [];
    public List<SessionAssistMessage> AssistDialog { get; set; } = [];
    public List<SessionVisualFrame> VisualFrames { get; set; } = [];
    public SessionUsage Usage { get; set; } = new();

    /// <summary>Topic name shown on the history card; filled from the topic list, not saved.</summary>
    [JsonIgnore]
    public string TopicName
    {
        get => _topicName;
        set
        {
            if (_topicName == value)
            {
                return;
            }

            _topicName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Checked in the history list for bulk actions; not saved.</summary>
    [JsonIgnore]
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

    public string DayGroupLabel
    {
        get
        {
            DateTime today = DateTime.Today;
            bool russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
            if (StartedAt.Date == today)
            {
                return russian ? "Сегодня" : "Today";
            }

            if (StartedAt.Date == today.AddDays(-1))
            {
                return russian ? "Вчера" : "Yesterday";
            }

            return russian
                ? StartedAt.ToString("ddd, d MMMM", CultureInfo.CurrentUICulture)
                : StartedAt.ToString("ddd, MMM dd", CultureInfo.CurrentUICulture);
        }
    }
}

/// <summary>A topic groups sessions and keeps a knowledge base built from all of them.</summary>
public class SessionTopic
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string KnowledgeText { get; set; } = string.Empty;
    public DateTime? KnowledgeUpdatedAt { get; set; }
    /// <summary>Sessions the knowledge base was built from: a mismatch means it needs refreshing.</summary>
    public List<string> KnowledgeSessionIds { get; set; } = [];
    /// <summary>Last question asked about the topic and its answer, kept so it survives a restart.</summary>
    public string LastQuestion { get; set; } = string.Empty;
    public string LastAnswer { get; set; } = string.Empty;
    public DateTime? LastAnswerAt { get; set; }
    /// <summary>Questions and answers of the «Вопросы» tab, oldest first.</summary>
    public List<TopicQuestionEntry> Questions { get; set; } = [];
    /// <summary>Files, folders and notes attached to the topic; their parsed Markdown lives in the knowledge folder.</summary>
    public List<KnowledgeSource> Sources { get; set; } = [];
    /// <summary>Fingerprint of the parsed sources the knowledge base was built from: a mismatch means it needs refreshing.</summary>
    public string KnowledgeSourcesStamp { get; set; } = string.Empty;
    public DateTime? GraphUpdatedAt { get; set; }
    /// <summary>Fingerprint of sessions and sources the graph was built from.</summary>
    public string GraphStamp { get; set; } = string.Empty;
}

public class TopicQuestionEntry
{
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public DateTime AskedAt { get; set; } = DateTime.Now;
    public double? CostRub { get; set; }
}

public enum KnowledgeSourceKind
{
    File,
    Folder,
    Note
}

/// <summary>Something attached to a topic's knowledge base. «Разобрать» turns it into Markdown stored by CuePilot.</summary>
public class KnowledgeSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public KnowledgeSourceKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Original file or folder path (empty for a note).</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>Text typed by the user (notes only).</summary>
    public string NoteText { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? ParsedAt { get; set; }
    /// <summary>Size and modification time of the original when it was parsed: a mismatch means it changed since.</summary>
    public string ParsedStamp { get; set; } = string.Empty;
    public int ParsedChars { get; set; }
    public int ParsedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public string Error { get; set; } = string.Empty;
}
