using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

public class TranscriptItemViewModel
{
    public TranscriptItemViewModel(TranscriptLine line)
    {
        Source = line.Source;
        SourceLabel = string.IsNullOrWhiteSpace(line.SpeakerName)
            ? (line.Source == TranscriptSource.System ? "system" : "mic")
            : line.SpeakerName;
        SourceTagBackground = line.Source == TranscriptSource.System ? "#2563EB" : "#059669";
        TimeLabel = line.Timestamp.ToString("HH:mm:ss");
        Text = line.Text;
    }

    public TranscriptSource Source { get; }
    public string SourceLabel { get; }
    public string SourceTagBackground { get; }
    public string TimeLabel { get; }
    public string Text { get; }
}

public enum TranscriptFilter
{
    All,
    System,
    Mic
}
