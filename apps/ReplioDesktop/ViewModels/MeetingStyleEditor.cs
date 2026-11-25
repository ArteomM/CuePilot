using System.Collections.ObjectModel;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

public sealed record MeetingPresetOption(string Id, string Name);

/// <summary>
/// The «Как помогать» block: a ready variant (Продажа, Собеседование…) and its two texts, «Как помогать на встречах»
/// and «Формат итогов». Picking a variant fills the texts; editing a text turns the variant into «Свой вариант»
/// (going back to it restores the user's own texts). Used by a topic's «Как помогать» tab and by Settings
/// («Встречи без темы»). Saving waits until typing pauses, so a topic file is not rewritten on every key.
/// </summary>
public sealed class MeetingStyleEditor : ObservableObject
{
    private readonly LocalizationService _l;
    private readonly bool _forTopic;
    private readonly Func<string> _inheritedName;
    private Action<MeetingStyle> _save = _ => { };
    private MeetingPresetOption? _selected;
    private string _guidance = string.Empty;
    private string _format = string.Empty;
    private string _customGuidance = string.Empty;
    private string _customFormat = string.Empty;
    private bool _loading;
    private bool _savePending;
    private CancellationTokenSource? _saveDelay;

    public MeetingStyleEditor(LocalizationService l, bool forTopic, Func<string>? inheritedName = null)
    {
        _l = l;
        _forTopic = forTopic;
        _inheritedName = inheritedName ?? (() => string.Empty);
        RebuildPresets();
    }

    public ObservableCollection<MeetingPresetOption> Presets { get; } = [];

    public MeetingPresetOption? SelectedPreset
    {
        get => _selected;
        set
        {
            // A rebuilt list (language switch) briefly sets null: keep the current variant.
            if (value == null || _loading || value.Id == _selected?.Id)
            {
                return;
            }

            if (_selected?.Id == MeetingPresets.Custom)
            {
                _customGuidance = _guidance;
                _customFormat = _format;
            }

            _selected = value;
            if (MeetingPresets.IsBuiltin(value.Id))
            {
                _guidance = _l[MeetingPresets.GuidanceKey(value.Id)];
                _format = _l[MeetingPresets.FormatKey(value.Id)];
            }
            else if (value.Id == MeetingPresets.Custom && (_customGuidance.Length > 0 || _customFormat.Length > 0))
            {
                _guidance = _customGuidance;
                _format = _customFormat;
            }
            else if (value.Id == MeetingPresets.Inherit)
            {
                _guidance = string.Empty;
                _format = string.Empty;
            }

            NotifyAll();
            SaveNow();
        }
    }

    public string Guidance
    {
        get => _guidance;
        set
        {
            value ??= string.Empty;
            if (_guidance == value)
            {
                return;
            }

            _guidance = value;
            OnPropertyChanged();
            OnTextEdited();
        }
    }

    public string SummaryFormat
    {
        get => _format;
        set
        {
            value ??= string.Empty;
            if (_format == value)
            {
                return;
            }

            _format = value;
            OnPropertyChanged();
            OnTextEdited();
        }
    }

    /// <summary>A topic on «Как для встреч без темы» has no texts of its own.</summary>
    public bool HasOwnTexts => _selected?.Id != MeetingPresets.Inherit;

    public bool IsInherited => !HasOwnTexts;

    public string InheritedText => string.Format(_l["MeetingStyleInheritedHint"], _inheritedName());

    /// <summary>Loads a topic's or the Settings variant; a pending save of the previous one is written first.</summary>
    public void Load(string? preset, string guidance, string format, Action<MeetingStyle> save)
    {
        Flush();
        _loading = true;
        _save = save;
        string id = string.IsNullOrWhiteSpace(preset) ? (_forTopic ? MeetingPresets.Inherit : MeetingPresets.General) : preset;
        _selected = Presets.FirstOrDefault(p => p.Id == id) ?? Presets.First(p => p.Id == MeetingPresets.Custom);
        _guidance = guidance ?? string.Empty;
        _format = format ?? string.Empty;
        _customGuidance = _selected.Id == MeetingPresets.Custom ? _guidance : string.Empty;
        _customFormat = _selected.Id == MeetingPresets.Custom ? _format : string.Empty;
        _loading = false;
        NotifyAll();
    }

    /// <summary>Interface language changed: variant names follow it; the stored texts stay as written.</summary>
    public void RebuildPresets()
    {
        string? id = _selected?.Id;
        _loading = true;
        Presets.Clear();
        if (_forTopic)
        {
            Presets.Add(new MeetingPresetOption(MeetingPresets.Inherit, _l["MeetingPresetName_inherit"]));
        }

        foreach (string builtin in MeetingPresets.Builtin)
        {
            Presets.Add(new MeetingPresetOption(builtin, _l[MeetingPresets.NameKey(builtin)]));
        }

        Presets.Add(new MeetingPresetOption(MeetingPresets.Custom, _l[MeetingPresets.NameKey(MeetingPresets.Custom)]));
        _selected = id == null ? null : Presets.FirstOrDefault(p => p.Id == id);
        _loading = false;
        NotifyAll();
    }

    public void NotifyInheritedChanged() => OnPropertyChanged(nameof(InheritedText));

    /// <summary>Writes a save that is still waiting for typing to pause.</summary>
    public void Flush()
    {
        _saveDelay?.Cancel();
        _saveDelay = null;
        if (_savePending)
        {
            SaveNow();
        }
    }

    private void OnTextEdited()
    {
        if (_loading)
        {
            return;
        }

        if (_selected != null && MeetingPresets.IsBuiltin(_selected.Id) &&
            (_guidance != _l[MeetingPresets.GuidanceKey(_selected.Id)] || _format != _l[MeetingPresets.FormatKey(_selected.Id)]))
        {
            _selected = Presets.FirstOrDefault(p => p.Id == MeetingPresets.Custom) ?? _selected;
            OnPropertyChanged(nameof(SelectedPreset));
        }

        _savePending = true;
        _saveDelay?.Cancel();
        var delay = new CancellationTokenSource();
        _saveDelay = delay;
        _ = SaveLaterAsync(delay.Token);
    }

    private async Task SaveLaterAsync(CancellationToken token)
    {
        try
        {
            // Resumes on the UI thread (its synchronization context), where the topics are saved.
            await Task.Delay(800, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        SaveNow();
    }

    private void SaveNow()
    {
        _savePending = false;
        if (_selected == null)
        {
            return;
        }

        _save(new MeetingStyle(_selected.Id, _guidance.Trim(), _format.Trim()));
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(Guidance));
        OnPropertyChanged(nameof(SummaryFormat));
        OnPropertyChanged(nameof(HasOwnTexts));
        OnPropertyChanged(nameof(IsInherited));
        OnPropertyChanged(nameof(InheritedText));
    }
}
