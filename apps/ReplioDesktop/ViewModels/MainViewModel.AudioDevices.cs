using System.Collections.ObjectModel;
using System.IO;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>One row of the device lists: empty id means «as in the system».</summary>
public sealed class AudioDeviceOption(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>
/// Settings → «Речь» → «Устройства»: the microphone and (Windows) the output device whose sound is recorded.
/// «Как в системе» follows the system default. A chosen device that is unplugged is shown as missing and the
/// recording uses the default one (DesktopCapture also falls back to any device that opens).
/// macOS records the sound of all apps at once, so there is no output choice there.
/// </summary>
public partial class MainViewModel
{
    private AudioDeviceOption? _selectedMicDevice;
    private AudioDeviceOption? _selectedOutputDevice;
    private bool _isLoadingAudioDevices;
    private DateTime _audioDevicesLoadedAt = DateTime.MinValue;
    private string _audioDevicesStatus = string.Empty;

    public ObservableCollection<AudioDeviceOption> MicDevices { get; } = [];
    public ObservableCollection<AudioDeviceOption> OutputDevices { get; } = [];

    public bool HasOutputDeviceChoice => OperatingSystem.IsWindows();

    public RelayCommand RefreshAudioDevicesCommand => _refreshAudioDevicesCommand ??= new RelayCommand(async () => await RefreshAudioDevicesAsync(force: true));
    private RelayCommand? _refreshAudioDevicesCommand;

    public string AudioDevicesStatus
    {
        get => _audioDevicesStatus;
        private set
        {
            _audioDevicesStatus = value;
            OnPropertyChanged();
        }
    }

    public AudioDeviceOption? SelectedMicDevice
    {
        get => _selectedMicDevice;
        set
        {
            if (value == null || ReferenceEquals(_selectedMicDevice, value))
            {
                return;
            }

            _selectedMicDevice = value;
            OnPropertyChanged();
            if (!_isLoadingAudioDevices && _settings.MicDeviceId != value.Id)
            {
                _settings.MicDeviceId = value.Id;
                PersistSettingsQuietly();
                AppendLog("[audio] Microphone set: " + (value.Id.Length == 0 ? "system default" : value.Name));
            }
        }
    }

    public AudioDeviceOption? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (value == null || ReferenceEquals(_selectedOutputDevice, value))
            {
                return;
            }

            _selectedOutputDevice = value;
            OnPropertyChanged();
            if (!_isLoadingAudioDevices && _settings.OutputDeviceId != value.Id)
            {
                _settings.OutputDeviceId = value.Id;
                PersistSettingsQuietly();
                AppendLog("[audio] Output device set: " + (value.Id.Length == 0 ? "system default" : value.Name));
            }
        }
    }

    private string CaptureWorkingDirectory()
    {
        string projectDir = Path.Combine(_projectRoot, "apps", "DesktopCapture");
        return Directory.Exists(projectDir) ? projectDir : _projectRoot;
    }

    /// <summary>Fills both lists; called when «Речь» opens (at most every 10 seconds) and by «Обновить список».</summary>
    private async Task RefreshAudioDevicesAsync(bool force)
    {
        if (_isLoadingAudioDevices || (!force && DateTime.UtcNow - _audioDevicesLoadedAt < TimeSpan.FromSeconds(10)))
        {
            return;
        }

        _isLoadingAudioDevices = true;
        AudioDevicesStatus = L["AudioDevicesLoading"];
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = await AudioDeviceCatalog.ListAsync(CaptureWorkingDirectory());
            _audioDevicesLoadedAt = DateTime.UtcNow;
            Fill(MicDevices, devices.Where(d => d.IsInput).ToList(), _settings.MicDeviceId, option => _selectedMicDevice = option);
            Fill(OutputDevices, devices.Where(d => !d.IsInput).ToList(), _settings.OutputDeviceId, option => _selectedOutputDevice = option);
            OnPropertyChanged(nameof(SelectedMicDevice));
            OnPropertyChanged(nameof(SelectedOutputDevice));
            AudioDevicesStatus = devices.Any(d => d.IsInput) ? string.Empty : L["AudioDevicesNoMic"];
        }
        catch (Exception ex)
        {
            AppendLog("[audio] Device list failed: " + ex.Message);
            AudioDevicesStatus = L["AudioDevicesFailed"];
            if (MicDevices.Count == 0)
            {
                Fill(MicDevices, [], _settings.MicDeviceId, option => _selectedMicDevice = option);
                Fill(OutputDevices, [], _settings.OutputDeviceId, option => _selectedOutputDevice = option);
                OnPropertyChanged(nameof(SelectedMicDevice));
                OnPropertyChanged(nameof(SelectedOutputDevice));
            }
        }
        finally
        {
            _isLoadingAudioDevices = false;
        }
    }

    private void Fill(ObservableCollection<AudioDeviceOption> list, List<AudioDeviceInfo> devices, string chosenId, Action<AudioDeviceOption> select)
    {
        list.Clear();
        AudioDeviceInfo? systemDefault = devices.FirstOrDefault(d => d.IsDefault);
        var fallback = new AudioDeviceOption(string.Empty, systemDefault == null
            ? L["AudioDeviceSystemDefault"]
            : string.Format(L["AudioDeviceSystemDefaultNamed"], systemDefault.Name));
        list.Add(fallback);
        foreach (AudioDeviceInfo device in devices)
        {
            list.Add(new AudioDeviceOption(device.Id, device.Name));
        }

        AudioDeviceOption? chosen = list.FirstOrDefault(o => o.Id == (chosenId ?? string.Empty));
        if (chosen == null)
        {
            // Chosen earlier but not connected now: kept in Settings, the recording uses the default meanwhile.
            chosen = new AudioDeviceOption(chosenId!, L["AudioDeviceMissing"]);
            list.Add(chosen);
        }

        select(chosen);
    }

    private void OnSettingsSectionChangedForAudio()
    {
        if (IsSettingsSpeech)
        {
            _ = RefreshAudioDevicesAsync(force: false);
        }
    }
}
