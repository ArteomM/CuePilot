using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CuePilot.Avalonia.Platform;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

// Same namespace as the shared partial class, so this completes CuePilotDesktop.ViewModels.MainViewModel.
namespace CuePilotDesktop.ViewModels;

/// <summary>Avalonia side of MainViewModel: clipboard, file pickers, theme variant and the filtered session list.</summary>
public partial class MainViewModel
{
    private static TopLevel? MainTopLevel =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private partial IFilteredListView CreateFilteredListView(
        ObservableCollection<SessionSummary> source,
        Predicate<object> filter,
        string groupProperty) => new AvaloniaFilteredListView(source, filter, groupProperty);

    private partial void SetClipboardText(string text) => _ = MainTopLevel?.Clipboard?.SetTextAsync(text);

    private async partial Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, string patterns, bool multiple)
    {
        if (MainTopLevel?.StorageProvider is not { } storage)
        {
            return [];
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns.Split(';', StringSplitOptions.RemoveEmptyEntries) }]
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async partial Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple)
    {
        if (MainTopLevel?.StorageProvider is not { } storage)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple
        });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>The Echo palette lives in theme dictionaries (Themes/EchoPalette.axaml), so switching the variant recolors it.</summary>
    private partial void ApplyPlatformPalette(bool dark)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    private partial bool ValidateHotkeys(out List<string> errors) => TryBuildHotkeyMap(out _, out errors);

    /// <summary>The same checks as on Windows, with macOS key codes (see MacHotkeyGesture for the modifier mapping).</summary>
    public bool TryBuildHotkeyMap(out Dictionary<int, MacHotkeyGesture> map, out List<string> errors)
    {
        map = [];
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

        if (map.GroupBy(kvp => $"{kvp.Value.Modifiers}:{kvp.Value.KeyCode}").Any(g => g.Count() > 1))
        {
            errors.Add("Some hotkeys are duplicated. Each action must have unique key combination.");
        }

        return errors.Count == 0;
    }

    private static void AddHotkey(IDictionary<int, MacHotkeyGesture> map, ICollection<string> errors, int id, string label, string text)
    {
        if (!MacHotkeyGesture.TryParse(text, out MacHotkeyGesture? gesture, out string error))
        {
            errors.Add($"{label}: {error}");
            return;
        }

        if (gesture != null)
        {
            map[id] = gesture;
        }
    }

    /// <summary>
    /// Filtered copy of the source list with a day header before each day, like the grouped CollectionView on Windows.
    /// <see cref="View"/> holds headers and sessions for the list; <see cref="VisibleItems"/> only the sessions.
    /// </summary>
    private sealed class AvaloniaFilteredListView : IFilteredListView
    {
        private readonly ObservableCollection<SessionSummary> _source;
        private readonly Predicate<object> _filter;
        private readonly Func<SessionSummary, string>? _groupOf;
        private readonly ObservableCollection<object> _view = [];
        private readonly List<SessionSummary> _visible = [];

        public AvaloniaFilteredListView(ObservableCollection<SessionSummary> source, Predicate<object> filter, string groupProperty)
        {
            _source = source;
            _filter = filter;
            System.Reflection.PropertyInfo? property = typeof(SessionSummary).GetProperty(groupProperty);
            if (property != null)
            {
                _groupOf = item => property.GetValue(item)?.ToString() ?? string.Empty;
            }

            _source.CollectionChanged += (_, _) => Refresh();
            Refresh();
        }

        public object View => _view;

        public IEnumerable<object> VisibleItems => _visible;

        public void Refresh()
        {
            _visible.Clear();
            _visible.AddRange(_source.Where(i => _filter(i)));

            var items = new List<object>(_visible.Count + 8);
            var headers = new Dictionary<string, List<SessionSummary>>();
            var order = new List<string>();
            if (_groupOf == null)
            {
                items.AddRange(_visible);
            }
            else
            {
                // Groups keep the order of their first session, as PropertyGroupDescription does.
                foreach (SessionSummary item in _visible)
                {
                    string group = _groupOf(item);
                    if (!headers.TryGetValue(group, out List<SessionSummary>? members))
                    {
                        headers[group] = members = [];
                        order.Add(group);
                    }

                    members.Add(item);
                }

                foreach (string group in order)
                {
                    items.Add(new SessionGroupHeader(group));
                    items.AddRange(headers[group]);
                }
            }

            _view.Clear();
            foreach (object item in items)
            {
                _view.Add(item);
            }
        }
    }
}

/// <summary>Day header in the session list (Avalonia only).</summary>
public sealed record SessionGroupHeader(string Name);
