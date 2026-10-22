using System.Collections.ObjectModel;
using CuePilotDesktop.Models;

namespace CuePilotDesktop.ViewModels;

/// <summary>
/// What MainViewModel needs from the UI platform. WPF implements these in MainViewModel.Wpf.cs,
/// the Avalonia app in its own MainViewModel.Avalonia.cs.
/// </summary>
public partial class MainViewModel
{
    private partial IFilteredListView CreateFilteredListView(
        ObservableCollection<SessionSummary> source,
        Predicate<object> filter,
        string groupProperty);

    private partial void SetClipboardText(string text);

    /// <summary>Returns the chosen files, or an empty list when the dialog is cancelled. Patterns look like "*.pdf;*.docx".</summary>
    private partial Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, string patterns, bool multiple);

    private partial Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple);

    /// <summary>Recolors the app-wide palette resources (keys and colors in ThemePalette).</summary>
    private partial void ApplyPlatformPalette(bool dark);

    /// <summary>Checks that every hotkey text parses on this platform and that none repeat.</summary>
    private partial bool ValidateHotkeys(out List<string> errors);
}
