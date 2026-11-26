using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Updates from replio.ru: a check about 10 seconds after start and then once a day. A newer version shows a banner
/// «Доступна версия X» with «Что нового» and «Обновить». «Обновить» is off while a recording runs; it downloads the
/// installer, checks it, starts it in update mode and closes the app. The installer keeps settings and the key.
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private UiTimer? _updateTimer;
    private ReplioUpdateInfo? _availableUpdate;
    private bool _isUpdateDismissed;
    private bool _isUpdateNotesOpen;
    private bool _isCheckingUpdates;
    private bool _isUpdating;
    private int _updateProgress;
    private string _updateStatus = string.Empty;
    private CancellationTokenSource? _updateCancellation;

    /// <summary>The window closes the app for the installer (WPF: the tray «Выход» path).</summary>
    public event Action? ExitForUpdateRequested;

    public RelayCommand InstallUpdateCommand { get; private set; } = null!;
    public RelayCommand DismissUpdateCommand { get; private set; } = null!;
    public RelayCommand ToggleUpdateNotesCommand { get; private set; } = null!;
    public RelayCommand CheckUpdatesCommand { get; private set; } = null!;

    public bool IsUpdateSupported => UpdateService.IsSupported;

    public bool IsUpdateBannerVisible => _availableUpdate != null && (!_isUpdateDismissed || _availableUpdate.Mandatory || _isUpdating);

    public bool CanDismissUpdate => _availableUpdate is { Mandatory: false } && !_isUpdating;

    public string UpdateBannerText => _availableUpdate == null
        ? string.Empty
        : string.Format(L[_availableUpdate.Mandatory ? "UpdateBannerMandatory" : "UpdateBanner"], _availableUpdate.VersionText);

    public string UpdateNotes => string.IsNullOrWhiteSpace(_availableUpdate?.Notes) ? L["UpdateNoNotes"] : _availableUpdate!.Notes;

    public bool IsUpdateNotesOpen
    {
        get => _isUpdateNotesOpen;
        set
        {
            if (_isUpdateNotesOpen == value)
            {
                return;
            }

            _isUpdateNotesOpen = value;
            OnPropertyChanged();
        }
    }

    public bool IsUpdating => _isUpdating;

    public int UpdateProgress => _updateProgress;

    /// <summary>Line under the banner and in Settings: why «Обновить» is off, progress, or the last check result.</summary>
    public string UpdateStatus => _updateStatus;

    public string InstallUpdateButtonText => _isUpdating ? L["UpdateDownloading"] : L[UpdateService.IsSelfInstall ? "UpdateInstall" : "UpdateDownload"];

    public string InstallUpdateToolTip => IsCapturing ? L["UpdateBlockedByRecording"] : L["UpdateInstallToolTip"];

    public string CurrentVersionText => string.Format(L["UpdateCurrentVersion"], UpdateService.CurrentVersion.ToString(3));

    private void InitializeUpdates()
    {
        InstallUpdateCommand = new RelayCommand(async () => await InstallUpdateAsync(), () => _availableUpdate != null && !_isUpdating && !IsCapturing);
        DismissUpdateCommand = new RelayCommand(() =>
        {
            _isUpdateDismissed = true;
            IsUpdateNotesOpen = false;
            OnUpdateChanged();
        }, () => CanDismissUpdate);
        ToggleUpdateNotesCommand = new RelayCommand(() => IsUpdateNotesOpen = !IsUpdateNotesOpen);
        CheckUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync(manual: true), () => IsUpdateSupported && !_isCheckingUpdates && !_isUpdating);

        if (!IsUpdateSupported)
        {
            return;
        }

        _updateTimer = new UiTimer { Interval = FirstUpdateCheckDelay };
        _updateTimer.Tick += async (_, _) =>
        {
            // First tick after 10 seconds, then once a day; the app often runs for days in the tray.
            _updateTimer.Stop();
            await CheckUpdatesAsync(manual: false);
            _updateTimer.Dispose();
            _updateTimer = new UiTimer { Interval = UpdateCheckInterval };
            _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(manual: false);
            _updateTimer.Start();
        };
        _updateTimer.Start();
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_isCheckingUpdates || _isUpdating)
        {
            return;
        }

        _isCheckingUpdates = true;
        if (manual)
        {
            SetUpdateStatus(L["UpdateChecking"]);
        }

        CheckUpdatesCommand.RaiseCanExecuteChanged();
        try
        {
            ReplioUpdateInfo? info = await Task.Run(() => UpdateService.CheckAsync(_settings.ReplioDeviceId, CancellationToken.None));
            if (info != null)
            {
                bool newer = _availableUpdate == null || info.Version > _availableUpdate.Version;
                _availableUpdate = info;
                if (newer || manual)
                {
                    // A newer announcement or a manual check shows the banner again after «Позже».
                    _isUpdateDismissed = false;
                }

                AppendLog($"[update] Version {info.VersionText} is available{(info.Mandatory ? " (mandatory)" : string.Empty)}.");
                SetUpdateStatus(IsCapturing ? L["UpdateBlockedByRecording"] : string.Empty);
            }
            else
            {
                _availableUpdate = null;
                SetUpdateStatus(manual ? L["UpdateUpToDate"] : string.Empty);
            }
        }
        catch (Exception ex)
        {
            AppendLog("[update] Check failed: " + ex.Message);
            if (manual)
            {
                SetUpdateStatus(string.Format(L["UpdateCheckFailed"], ex.Message));
            }
        }
        finally
        {
            _isCheckingUpdates = false;
            OnUpdateChanged();
        }
    }

    private async Task InstallUpdateAsync()
    {
        ReplioUpdateInfo? info = _availableUpdate;
        if (info == null || _isUpdating)
        {
            return;
        }

        if (IsCapturing)
        {
            // Never in the middle of a meeting: the recording and its session would be cut off.
            SetUpdateStatus(L["UpdateBlockedByRecording"]);
            return;
        }

        if (!UpdateService.IsSelfInstall)
        {
            // Mac: the browser downloads the .dmg; the user drags Replio to «Программы» over the old one.
            OpenUrl(info.Url);
            SetUpdateStatus(L["UpdateMacDownloadStarted"]);
            AppendLog($"[update] Opened the download of {info.VersionText} in the browser.");
            return;
        }

        _isUpdating = true;
        _updateProgress = 0;
        _updateCancellation = new CancellationTokenSource();
        SetUpdateStatus(string.Format(L["UpdateDownloadProgress"], 0));
        OnUpdateChanged();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                _updateProgress = percent;
                OnPropertyChanged(nameof(UpdateProgress));
                SetUpdateStatus(string.Format(L["UpdateDownloadProgress"], percent));
            });
            string installer = await Task.Run(() => UpdateService.DownloadAsync(info, progress, _updateCancellation.Token));

            if (IsCapturing)
            {
                // A recording started while the file was downloading: keep the file, install later.
                _isUpdating = false;
                SetUpdateStatus(L["UpdateBlockedByRecording"]);
                OnUpdateChanged();
                return;
            }

            SetUpdateStatus(L["UpdateStarting"]);
            AppendLog($"[update] Starting the installer for {info.VersionText}; the app closes.");
            UpdateService.StartInstaller(installer);
            ExitForUpdateRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            AppendLog("[update] Update failed: " + ex.Message);
            SetUpdateStatus(string.Format(L["UpdateFailed"], ex.Message));
            OnUpdateChanged();
        }
    }

    private void OnCaptureStateChangedForUpdate()
    {
        if (InstallUpdateCommand == null)
        {
            return;
        }

        if (_availableUpdate != null && !_isUpdating)
        {
            SetUpdateStatus(IsCapturing ? L["UpdateBlockedByRecording"] : string.Empty);
        }

        OnUpdateChanged();
    }

    private void SetUpdateStatus(string status)
    {
        _updateStatus = status;
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void OnUpdateChanged()
    {
        OnPropertyChanged(nameof(IsUpdateBannerVisible));
        OnPropertyChanged(nameof(CanDismissUpdate));
        OnPropertyChanged(nameof(UpdateBannerText));
        OnPropertyChanged(nameof(UpdateNotes));
        OnPropertyChanged(nameof(IsUpdating));
        OnPropertyChanged(nameof(UpdateProgress));
        OnPropertyChanged(nameof(InstallUpdateButtonText));
        OnPropertyChanged(nameof(InstallUpdateToolTip));
        InstallUpdateCommand.RaiseCanExecuteChanged();
        DismissUpdateCommand.RaiseCanExecuteChanged();
        CheckUpdatesCommand.RaiseCanExecuteChanged();
    }
}
