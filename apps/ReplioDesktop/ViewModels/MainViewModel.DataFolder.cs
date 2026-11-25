using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Settings → «Папка данных». Pick a folder inside Яндекс Диск / OneDrive / iCloud Drive and the cloud client syncs
/// sessions, topics, the knowledge base and screenshots between computers; Replio's servers never see them.
/// Changing the folder copies this computer's data there (never overwriting what is already there), then Replio
/// restarts on the new folder. Different versions of a meeting left by the cloud are listed for a choice.
/// </summary>
public partial class MainViewModel
{
    private bool _isDataFolderBusy;
    private bool _isRestartNeeded;
    private string _dataFolderStatus = string.Empty;

    /// <summary>The window restarts the app (WPF: start a new copy with --restart, then quit).</summary>
    public event Action? RestartRequested;

    public RelayCommand ChooseDataFolderCommand { get; private set; } = null!;
    public RelayCommand ResetDataFolderCommand { get; private set; } = null!;
    public RelayCommand OpenDataFolderCommand { get; private set; } = null!;
    public RelayCommand RestartAppCommand { get; private set; } = null!;

    public ObservableCollection<SessionConflictItem> SessionConflicts { get; } = [];

    public string DataFolderPath => DataLocation.Root;

    public bool IsDataFolderCustom => DataLocation.IsCustom;

    public bool IsDataFolderBusy => _isDataFolderBusy;

    public bool IsRestartNeeded => _isRestartNeeded;

    public bool HasSessionConflicts => SessionConflicts.Count > 0;

    public string DataFolderStatus => _dataFolderStatus;

    private void InitializeDataFolder()
    {
        ChooseDataFolderCommand = new RelayCommand(async () => await ChooseDataFolderAsync(), () => !_isDataFolderBusy && !IsCapturing);
        ResetDataFolderCommand = new RelayCommand(async () => await MoveDataAsync(DataLocation.DefaultRoot), () => !_isDataFolderBusy && !IsCapturing && DataLocation.IsCustom);
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(DataLocation.Root) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                SetDataFolderStatus(ex.Message);
            }
        });
        RestartAppCommand = new RelayCommand(() => RestartRequested?.Invoke(), () => !IsCapturing);

        if (DataLocation.UnavailableChosenFolder.Length > 0)
        {
            SetDataFolderStatus(string.Format(L["DataFolderUnavailable"], DataLocation.UnavailableChosenFolder));
        }

        _ = RefreshSessionConflictsAsync();
    }

    private async Task ChooseDataFolderAsync()
    {
        IReadOnlyList<string> picked = await PickFoldersAsync(L["DataFolderPickTitle"], false);
        if (picked.Count == 0)
        {
            return;
        }

        string target = picked[0];
        // A folder named Replio inside the cloud is clearer for the user and leaves the rest of the cloud alone.
        if (!string.Equals(Path.GetFileName(target.TrimEnd('\\', '/')), "Replio", StringComparison.OrdinalIgnoreCase))
        {
            target = Path.Combine(target, "Replio");
        }

        await MoveDataAsync(target);
    }

    private async Task MoveDataAsync(string target)
    {
        if (IsCapturing)
        {
            SetDataFolderStatus(L["DataFolderBlockedByRecording"]);
            return;
        }

        string? problem = DataMove.Validate(target, DataLocation.Root);
        if (problem != null)
        {
            SetDataFolderStatus(L[problem == "same" ? "DataFolderSame" : "DataFolderInvalid"]);
            return;
        }

        SetDataFolderBusy(true);
        try
        {
            string source = DataLocation.Root;
            var progress = new Progress<int>(p => SetDataFolderStatus(string.Format(L["DataFolderCopying"], p)));
            (int copied, int versions) = await Task.Run(() => DataMove.Copy(source, target, progress));
            DataLocation.SaveChoice(target);
            AppendLog($"[data] Data folder set to {target}: {copied} file(s) copied, {versions} second version(s).");
            _isRestartNeeded = true;
            OnPropertyChanged(nameof(IsRestartNeeded));
            SetDataFolderStatus(string.Format(L[versions > 0 ? "DataFolderMovedWithVersions" : "DataFolderMoved"], target, copied, versions));
        }
        catch (Exception ex)
        {
            AppendLog("[data] Moving the data failed: " + ex.Message);
            SetDataFolderStatus(string.Format(L["DataFolderFailed"], ex.Message));
        }
        finally
        {
            SetDataFolderBusy(false);
        }
    }

    private async Task RefreshSessionConflictsAsync()
    {
        List<SessionConflict> conflicts = await Task.Run(DataConflicts.ListSessionConflicts);
        SessionConflicts.Clear();
        foreach (SessionConflict conflict in conflicts)
        {
            SessionConflicts.Add(new SessionConflictItem(this, conflict, string.Format(L["DataConflictCurrent"], DescribeVersion(conflict.Main)), string.Format(L["DataConflictOther"], DescribeVersion(conflict.Copy))));
        }

        OnPropertyChanged(nameof(HasSessionConflicts));
    }

    internal async Task ResolveConflictAsync(SessionConflictItem item, bool keepCopy)
    {
        try
        {
            await Task.Run(() => DataConflicts.Resolve(item.Conflict, keepCopy));
            AppendLog($"[data] Session {item.Conflict.SessionId}: kept the {(keepCopy ? "other" : "current")} version.");
            if (keepCopy)
            {
                // The list in memory holds the old version until the next start.
                _isRestartNeeded = true;
                OnPropertyChanged(nameof(IsRestartNeeded));
            }
        }
        catch (Exception ex)
        {
            SetDataFolderStatus(string.Format(L["DataFolderFailed"], ex.Message));
        }

        await RefreshSessionConflictsAsync();
    }

    private string DescribeVersion(SessionVersionInfo info) =>
        string.Format(L["DataConflictVersion"], info.Changed.ToString("dd.MM.yyyy HH:mm"), info.Lines,
            L[info.HasSummary ? "DataConflictWithSummary" : "DataConflictNoSummary"]);

    private void SetDataFolderBusy(bool busy)
    {
        _isDataFolderBusy = busy;
        OnPropertyChanged(nameof(IsDataFolderBusy));
        ChooseDataFolderCommand.RaiseCanExecuteChanged();
        ResetDataFolderCommand.RaiseCanExecuteChanged();
    }

    private void SetDataFolderStatus(string status)
    {
        _dataFolderStatus = status;
        OnPropertyChanged(nameof(DataFolderStatus));
    }

    private void OnCaptureStateChangedForDataFolder()
    {
        ChooseDataFolderCommand?.RaiseCanExecuteChanged();
        ResetDataFolderCommand?.RaiseCanExecuteChanged();
        RestartAppCommand?.RaiseCanExecuteChanged();
    }
}

/// <summary>One meeting with two versions after a sync, with a button for each side.</summary>
public sealed class SessionConflictItem
{
    public SessionConflictItem(MainViewModel owner, SessionConflict conflict, string mainInfo, string copyInfo)
    {
        Conflict = conflict;
        MainInfo = mainInfo;
        CopyInfo = copyInfo;
        KeepMainCommand = new RelayCommand(async () => await owner.ResolveConflictAsync(this, keepCopy: false));
        KeepCopyCommand = new RelayCommand(async () => await owner.ResolveConflictAsync(this, keepCopy: true));
    }

    public SessionConflict Conflict { get; }

    public string Title => string.IsNullOrWhiteSpace(Conflict.Title) ? Conflict.SessionId : Conflict.Title;

    public string MainInfo { get; }

    public string CopyInfo { get; }

    public RelayCommand KeepMainCommand { get; }

    public RelayCommand KeepCopyCommand { get; }
}
