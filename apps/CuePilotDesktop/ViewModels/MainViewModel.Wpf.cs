using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>
/// WPF side of MainViewModel: clipboard, file dialogs, app palette, collection view and global hotkeys.
/// The shared code calls these partial methods; the Avalonia app implements its own.
/// </summary>
public partial class MainViewModel
{
    private partial IFilteredListView CreateFilteredListView(
        ObservableCollection<SessionSummary> source,
        Predicate<object> filter,
        string groupProperty)
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(source);
        view.Filter = filter;
        view.GroupDescriptions.Add(new PropertyGroupDescription(groupProperty));
        return new WpfFilteredListView(view);
    }

    private partial void SetClipboardText(string text) => Clipboard.SetText(text);

    private partial Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, string patterns, bool multiple)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filterName + "|" + patterns,
            Multiselect = multiple
        };

        IReadOnlyList<string> files = dialog.ShowDialog() == true ? dialog.FileNames : [];
        return Task.FromResult(files);
    }

    private partial Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = multiple };
        IReadOnlyList<string> folders = dialog.ShowDialog() == true ? dialog.FolderNames : [];
        return Task.FromResult(folders);
    }

    /// <summary>Swaps the app-level palette brushes; overlay windows keep their own night palette.</summary>
    private partial void ApplyPlatformPalette(bool dark)
    {
        Application? app = Application.Current;
        if (app == null)
        {
            return;
        }

        foreach ((string key, string darkColor, string lightColor) in ThemePalette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private partial bool ValidateHotkeys(out List<string> errors) => TryBuildHotkeyMap(out _, out errors);

    public bool TryBuildHotkeyMap(out Dictionary<int, HotkeyGesture> map, out List<string> errors)
    {
        map = new Dictionary<int, HotkeyGesture>();
        errors = [];

        AddHotkey(map, errors, HotkeyIds.ToggleOverlay, "Overlay", ToggleOverlayHotkeyText);
        AddHotkey(map, errors, HotkeyIds.ToggleCapture, "Capture", ToggleCaptureHotkeyText);
        AddHotkey(map, errors, HotkeyIds.Ask, "Ask", AskHotkeyText);
        AddHotkey(map, errors, HotkeyIds.ClearChat, "ClearChat", ClearChatHotkeyText);
        AddHotkey(map, errors, HotkeyIds.MoveLeft, "MoveLeft", MoveLeftHotkeyText);
        AddHotkey(map, errors, HotkeyIds.MoveRight, "MoveRight", MoveRightHotkeyText);
        AddHotkey(map, errors, HotkeyIds.MoveUp, "MoveUp", MoveUpHotkeyText);
        AddHotkey(map, errors, HotkeyIds.MoveDown, "MoveDown", MoveDownHotkeyText);
        AddHotkey(map, errors, HotkeyIds.ScrollUp, "ScrollUp", ScrollUpHotkeyText);
        AddHotkey(map, errors, HotkeyIds.ScrollDown, "ScrollDown", ScrollDownHotkeyText);
        AddHotkey(map, errors, HotkeyIds.Snapshot, "Snapshot", SnapshotHotkeyText);

        var duplicates = map
            .GroupBy(kvp => $"{kvp.Value.Modifiers}:{kvp.Value.VirtualKey}")
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicates.Count > 0)
        {
            errors.Add("Some hotkeys are duplicated. Each action must have unique key combination.");
        }

        return errors.Count == 0;
    }

    private static void AddHotkey(
        IDictionary<int, HotkeyGesture> map,
        ICollection<string> errors,
        int id,
        string label,
        string text)
    {
        if (!HotkeyGesture.TryParse(text, out HotkeyGesture? gesture, out string error))
        {
            errors.Add($"{label}: {error}");
            return;
        }

        if (gesture != null)
        {
            map[id] = gesture;
        }
    }
    private sealed class WpfFilteredListView(ICollectionView view) : IFilteredListView
    {
        public object View => view;

        public IEnumerable<object> VisibleItems => view.Cast<object>();

        public void Refresh() => view.Refresh();
    }
}
