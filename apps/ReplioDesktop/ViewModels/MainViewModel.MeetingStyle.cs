using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// «Как помогать» per topic instead of the former «Режимы» (see Services/MeetingStyle.cs):
/// • the topic tab «Как помогать» edits the selected topic's variant (TopicMeetingStyle);
/// • Settings → «Встречи без темы» edits the variant for meetings without a topic (DefaultMeetingStyle);
/// • hints, the summary and the follow-up email use the variant of the session's topic (BuildAssistOptions).
/// At the first start after the change the old active mode's own texts become «Встречи без темы».
/// </summary>
public partial class MainViewModel
{
    public const string TopicTabMeetings = "Meetings";

    public MeetingStyleEditor TopicMeetingStyle { get; private set; } = null!;

    public MeetingStyleEditor DefaultMeetingStyle { get; private set; } = null!;

    public bool IsTopicTabMeetings { get => TopicTab == TopicTabMeetings; set { if (value) TopicTab = TopicTabMeetings; } }

    public bool ShowMeetingsPanel => IsTopicSelected && TopicTab == TopicTabMeetings;

    private void InitializeMeetingStyles()
    {
        MigrateModesToMeetingStyle();
        DefaultMeetingStyle = new MeetingStyleEditor(L, forTopic: false);
        DefaultMeetingStyle.Load(_settings.MeetingPreset, _settings.MeetingGuidance, _settings.SummaryFormat, SaveDefaultMeetingStyle);
        TopicMeetingStyle = new MeetingStyleEditor(L, forTopic: true, () => MeetingPresetName(_settings.MeetingPreset));
        LoadTopicMeetingStyle();
    }

    /// <summary>
    /// The former modes had two variants (General, Default) with a prompt and a notes template each, mostly the
    /// app's own «Пример: …» texts. The active one's own texts become «Встречи без темы» (Свой вариант); otherwise
    /// the ordinary-meeting preset is used. The old fields stay in settings.json untouched.
    /// </summary>
    private void MigrateModesToMeetingStyle()
    {
        if (_settings.MeetingPreset != null)
        {
            return;
        }

        bool defaultMode = string.Equals(_settings.SelectedModeName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase);
        string prompt = defaultMode ? _settings.DefaultRealtimePrompt : _settings.GeneralRealtimePrompt;
        string notes = defaultMode ? _settings.DefaultNotesTemplate : _settings.GeneralNotesTemplate;
        bool ownPrompt = !MeetingPresets.IsExampleText(prompt);
        bool ownNotes = !MeetingPresets.IsExampleText(notes);
        if (ownPrompt || ownNotes)
        {
            _settings.MeetingPreset = MeetingPresets.Custom;
            _settings.MeetingGuidance = ownPrompt ? prompt.Trim() : string.Empty;
            _settings.SummaryFormat = ownNotes ? notes.Trim() : string.Empty;
            AppendLog("[meetings] The active mode's own texts now apply to meetings without a topic.");
        }
        else
        {
            _settings.MeetingPreset = MeetingPresets.General;
            _settings.MeetingGuidance = L[MeetingPresets.GuidanceKey(MeetingPresets.General)];
            _settings.SummaryFormat = L[MeetingPresets.FormatKey(MeetingPresets.General)];
        }

        PersistSettingsQuietly();
    }

    private void SaveDefaultMeetingStyle(MeetingStyle style)
    {
        if (!string.Equals(_settings.SummaryFormat, style.SummaryFormat, StringComparison.Ordinal))
        {
            _settings.SummaryFormatUpdatedAt = DateTime.Now;
        }

        _settings.MeetingPreset = style.Preset;
        _settings.MeetingGuidance = style.Guidance;
        _settings.SummaryFormat = style.SummaryFormat;
        PersistSettingsQuietly();
        TopicMeetingStyle?.NotifyInheritedChanged();
        OnTopicActualizeStateChanged();
        OnSessionTopicActualizeChanged();
        AppendLog($"[meetings] Meetings without a topic: {style.Preset}.");
    }

    /// <summary>Selected topic changed: its own variant goes into the «Как помогать» tab.</summary>
    private void LoadTopicMeetingStyle()
    {
        if (TopicMeetingStyle == null)
        {
            return;
        }

        SessionTopic? topic = SelectedTopic;
        if (topic == null)
        {
            TopicMeetingStyle.Load(MeetingPresets.Inherit, string.Empty, string.Empty, _ => { });
            return;
        }

        TopicMeetingStyle.Load(topic.MeetingPreset, topic.MeetingGuidance, topic.SummaryFormat, style => SaveTopicMeetingStyle(topic, style));
    }

    private void SaveTopicMeetingStyle(SessionTopic topic, MeetingStyle style)
    {
        bool inherit = style.Preset == MeetingPresets.Inherit;
        string formatBefore = ResolveMeetingStyle(topic).SummaryFormat;
        topic.MeetingPreset = inherit ? string.Empty : style.Preset;
        topic.MeetingGuidance = inherit ? string.Empty : style.Guidance;
        topic.SummaryFormat = inherit ? string.Empty : style.SummaryFormat;
        topic.MeetingStyleUpdatedAt = DateTime.Now;
        if (!string.Equals(formatBefore, ResolveMeetingStyle(topic).SummaryFormat, StringComparison.Ordinal))
        {
            // Summaries of the topic's meetings made before now lack the new sections: «Актуализировать тему» redoes them.
            topic.SummaryFormatUpdatedAt = DateTime.Now;
        }

        SaveTopics();
        OnTopicActualizeStateChanged();
        OnSessionTopicActualizeChanged();
        AppendLog($"[meetings] Topic «{topic.Name}»: {(inherit ? "as without a topic" : style.Preset)}.");
    }

    private MeetingStyle ResolveMeetingStyle(SessionSummary? session) =>
        ResolveMeetingStyle(session == null || string.IsNullOrWhiteSpace(session.TopicId)
            ? null
            : _topics.FirstOrDefault(t => t.Id == session.TopicId));

    /// <summary>The topic's own variant, or the one for meetings without a topic.</summary>
    private MeetingStyle ResolveMeetingStyle(SessionTopic? topic)
    {
        if (topic != null && !string.IsNullOrWhiteSpace(topic.MeetingPreset) && topic.MeetingPreset != MeetingPresets.Inherit)
        {
            return new MeetingStyle(topic.MeetingPreset, topic.MeetingGuidance, topic.SummaryFormat);
        }

        return new MeetingStyle(_settings.MeetingPreset ?? MeetingPresets.General, _settings.MeetingGuidance, _settings.SummaryFormat);
    }

    /// <summary>«Продажа», «Свой вариант»… for the interface.</summary>
    private string MeetingPresetName(string? preset) =>
        MeetingPresets.IsBuiltin(preset) || preset == MeetingPresets.Custom
            ? L[MeetingPresets.NameKey(preset!)]
            : L[MeetingPresets.NameKey(MeetingPresets.General)];

    /// <summary>For the model: only a ready meeting type says something («Продажа»); the user's own texts speak for themselves.</summary>
    private string MeetingTypeForPrompt(MeetingStyle style) =>
        MeetingPresets.IsBuiltin(style.Preset) && style.Preset != MeetingPresets.General
            ? L[MeetingPresets.NameKey(style.Preset)]
            : string.Empty;

    /// <summary>
    /// A meeting's summary is out of date when the AI never made one (it failed or was off), or when the summary format
    /// in effect for it changed after the summary was made. Summaries older than this version count as current.
    /// </summary>
    private bool IsSessionSummaryOutdated(SessionSummary session)
    {
        if (session.Status != SessionStatus.Completed || session.Transcript.Count == 0)
        {
            return false;
        }

        if (!SessionScreenshots.StripLinks(session.SummaryText ?? string.Empty).Contains("##", StringComparison.Ordinal))
        {
            return true;
        }

        SessionTopic? topic = string.IsNullOrWhiteSpace(session.TopicId) ? null : _topics.FirstOrDefault(t => t.Id == session.TopicId);
        bool ownStyle = topic != null && !string.IsNullOrWhiteSpace(topic.MeetingPreset) && topic.MeetingPreset != MeetingPresets.Inherit;
        DateTime? formatChanged = Later(topic?.SummaryFormatUpdatedAt, ownStyle ? null : _settings.SummaryFormatUpdatedAt);
        if (formatChanged == null)
        {
            return false;
        }

        DateTime made = session.SummaryUpdatedAt ?? session.EndedAt ?? session.StartedAt;
        return made < formatChanged.Value;
    }

    private static DateTime? Later(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : (a > b ? a : b);

    private List<SessionSummary> OutdatedSessionSummaries(SessionTopic topic) =>
        CompletedSessions.Where(s => s.TopicId == topic.Id && IsSessionSummaryOutdated(s)).OrderBy(s => s.StartedAt).ToList();

    private void OnLocalizationChangedForMeetingStyles()
    {
        TopicMeetingStyle?.RebuildPresets();
        DefaultMeetingStyle?.RebuildPresets();
    }
}
