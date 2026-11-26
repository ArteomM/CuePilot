using System.IO;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Settings → «Речь» → «Проверка звука», like in video call apps: «Проверить звук» plays a short melody and for
/// 12 seconds shows two live bars, the microphone («скажите что-нибудь») and the computer's sound (the melody itself
/// shows whether Replio hears what the computer plays). Nothing is recorded and no session is made; at the end a short
/// verdict says what works and what to check. During a recording the live levels of the recording are shown instead.
/// </summary>
public partial class MainViewModel
{
    private const int SoundCheckSeconds = 12;
    private const double SoundHeardLevel = 0.02;
    private const double VoiceHeardLevel = 0.05;

    private readonly SoundCheckService _soundCheck = new();
    private bool _soundCheckWired;
    private bool _isSoundCheckRunning;
    private double _soundCheckMicLevel;
    private double _soundCheckSystemLevel;
    private double _soundCheckMicMax;
    private double _soundCheckSystemMax;
    private string _soundCheckStatus = string.Empty;

    public bool IsSoundCheckRunning
    {
        get => _isSoundCheckRunning;
        private set
        {
            if (_isSoundCheckRunning == value)
            {
                return;
            }

            _isSoundCheckRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoundCheckButtonText));
            OnPropertyChanged(nameof(ShowSoundCheckLevels));
        }
    }

    /// <summary>Bars are shown while the check runs and after it (with the loudest levels heard).</summary>
    public bool ShowSoundCheckLevels => _isSoundCheckRunning || _soundCheckStatus.Length > 0;

    public double SoundCheckMicLevel
    {
        get => _soundCheckMicLevel;
        private set
        {
            _soundCheckMicLevel = value;
            OnPropertyChanged();
        }
    }

    public double SoundCheckSystemLevel
    {
        get => _soundCheckSystemLevel;
        private set
        {
            _soundCheckSystemLevel = value;
            OnPropertyChanged();
        }
    }

    public string SoundCheckStatus
    {
        get => _soundCheckStatus;
        private set
        {
            _soundCheckStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSoundCheckLevels));
        }
    }

    public string SoundCheckButtonText => L[_isSoundCheckRunning ? "SoundCheckStop" : "TestAudioCapture"];

    private void RunAudioCaptureTest()
    {
        if (IsCapturing)
        {
            // A recording already listens: its own levels say everything.
            AudioHealthMessage = $"{L["AudioTestRunning"]} {SystemStatsText} | {MicStatsText}";
            return;
        }

        if (_isSoundCheckRunning)
        {
            _soundCheck.Stop();
            return;
        }

        if (!_soundCheckWired)
        {
            _soundCheckWired = true;
            _soundCheck.LevelUpdated += (system, mic) => RunOnUi(() =>
            {
                // Bars fall back slowly, so a word stays visible for a moment.
                SoundCheckMicLevel = Math.Max(mic, _soundCheckMicLevel * 0.7);
                SoundCheckSystemLevel = Math.Max(system, _soundCheckSystemLevel * 0.7);
                _soundCheckMicMax = Math.Max(_soundCheckMicMax, mic);
                _soundCheckSystemMax = Math.Max(_soundCheckSystemMax, system);
            });
            _soundCheck.Finished += () => RunOnUi(FinishSoundCheck);
            _soundCheck.LogLine += line => RunOnUi(() => AppendLog(line));
        }

        _soundCheckMicMax = _soundCheckSystemMax = 0;
        SoundCheckMicLevel = SoundCheckSystemLevel = 0;
        AudioHealthMessage = string.Empty;
        SoundCheckStatus = L["SoundCheckRunningHint"];
        IsSoundCheckRunning = true;
        AppendLog("[sound-check] Started.");
        _soundCheck.Start(CaptureWorkingDirectory(), SoundCheckSeconds, _settings.MicDeviceId ?? string.Empty, _settings.OutputDeviceId ?? string.Empty);
    }

    private void FinishSoundCheck()
    {
        if (!_isSoundCheckRunning)
        {
            return;
        }

        IsSoundCheckRunning = false;
        bool computer = _soundCheckSystemMax >= SoundHeardLevel;
        bool voice = _soundCheckMicMax >= VoiceHeardLevel;
        SoundCheckStatus = string.Join(" ",
            L[voice ? "SoundCheckMicOk" : "SoundCheckMicSilent"],
            L[computer ? "SoundCheckSystemOk" : "SoundCheckSystemSilent"]);
        AppendLog($"[sound-check] Done: mic peak {_soundCheckMicMax:0.000}, computer peak {_soundCheckSystemMax:0.000}.");
    }

    /// <summary>A recording needs the devices: the check stops when one starts.</summary>
    private void StopSoundCheckForCapture()
    {
        if (_isSoundCheckRunning)
        {
            _soundCheck.Stop();
        }
    }
}
