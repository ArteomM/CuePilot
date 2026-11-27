using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Replio.Android.Platform;

namespace Replio.Android.Views;

/// <summary>
/// «Папка данных» in Settings → «О приложении»: a folder in the phone's storage that a sync app (FolderSync, Autosync)
/// keeps equal to the computer's cloud folder, so the phone sees the same meetings and groups. Picking it asks for
/// «Доступ ко всем файлам», copies the data there (shared MoveDataAsync) and offers a restart.
/// </summary>
public partial class MainView
{
    private void SetUpDataFolder() => _viewModel.RestartRequested += PhoneStorage.RestartApp;

    /// <summary>«Данные и синхронизация» opens and folds (most people never need it).</summary>
    private void DataFolderFold_OnClick(object? sender, RoutedEventArgs e)
    {
        DataFolderDetails.IsVisible = !DataFolderDetails.IsVisible;
        DataFolderChevron.RenderTransform = DataFolderDetails.IsVisible ? new global::Avalonia.Media.RotateTransform(180) : null;
    }

    private async void ChooseDataFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await PhoneStorage.RequestAllFilesAccessAsync())
            {
                _viewModel.ShowPhoneDataFolderStatus(MobileText.Get("PhoneDataNeedAccess"));
                return;
            }

            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            {
                return;
            }

            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = MobileText.Get("PhoneDataPick"),
                AllowMultiple = false
            });
            if (folders.Count == 0)
            {
                return;
            }

            string? path = PhoneStorage.TreeUriToPath(folders[0].Path.OriginalString);
            PhonePlatform.Log("[data] Picked " + folders[0].Path.OriginalString + " → " + (path ?? "(not in the phone's storage)"));
            if (path == null)
            {
                _viewModel.ShowPhoneDataFolderStatus(MobileText.Get("PhoneDataNotLocal"));
                return;
            }

            await _viewModel.MoveDataToPhoneFolderAsync(path);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[data] Choosing the data folder failed: " + ex);
            _viewModel.ShowPhoneDataFolderStatus(FriendlyError.Describe(FriendlyError.Classify(ex.Message)));
        }
    }

    private void DataFolderHowTo_OnClick(object? sender, RoutedEventArgs e) =>
        ShowMessage(MobileText.Get("PhoneDataHowTitle"), MobileText.Get("PhoneDataHowText"));
}
