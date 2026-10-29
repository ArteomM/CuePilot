using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

// Same namespace as the shared partial class, so this completes CuePilotDesktop.ViewModels.MainViewModel.
namespace CuePilotDesktop.ViewModels;

/// <summary>Phone side of MainViewModel: clipboard, file pickers (copied into the app), theme variant, the session list.</summary>
public partial class MainViewModel
{
    private static TopLevel? MainTopLevel =>
        TopLevel.GetTopLevel((Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView);

    /// <summary>Picked documents are copied here: Android hands out content links, the parsers need files.</summary>
    public static string ImportsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePilot", "imports");

    private partial IFilteredListView CreateFilteredListView(
        ObservableCollection<SessionSummary> source,
        Predicate<object> filter,
        string groupProperty) => new PhoneFilteredListView(source, filter, groupProperty);

    private partial void SetClipboardText(string text) => _ = MainTopLevel?.Clipboard?.SetTextAsync(text);

    private async partial Task<IReadOnlyList<string>> PickFilesAsync(string title, string filterName, string patterns, bool multiple)
    {
        if (MainTopLevel?.StorageProvider is not { } storage)
        {
            return [];
        }

        // No type filter: Android filters by MIME type and would hide .md, .docx and the like on some phones.
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple
        });

        var paths = new List<string>();
        foreach (IStorageFile file in files)
        {
            string? path = await CopyIntoAppAsync(file);
            if (path != null)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// A phone folder is a content tree, not a path: its supported documents (and those of its subfolders) are
    /// copied into the app with the same layout, and that copy is parsed like a folder on the computer. The copy
    /// runs off the UI thread, since a big folder on a memory card can take a while.
    /// </summary>
    private async partial Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool multiple)
    {
        if (MainTopLevel?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return [];
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple
        });
        if (folders.Count == 0)
        {
            return [];
        }

        ShowFeedback(CuePilot.Android.Platform.MobileText.Get("CopyingFolder"));
        var paths = new List<string>();
        foreach (IStorageFolder folder in folders)
        {
            string? path = await Task.Run(() => CopyFolderIntoAppAsync(folder));
            if (path != null)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private const int MaxFolderFiles = 400;

    private static async Task<string?> CopyFolderIntoAppAsync(IStorageFolder folder)
    {
        try
        {
            string root = Path.Combine(NewImportFolder(), SafeName(folder.Name, "folder"));
            Directory.CreateDirectory(root);
            int copied = 0;
            var pending = new Queue<(IStorageFolder Folder, string Target)>();
            pending.Enqueue((folder, root));
            while (pending.Count > 0 && copied < MaxFolderFiles)
            {
                (IStorageFolder current, string target) = pending.Dequeue();
                await foreach (IStorageItem item in current.GetItemsAsync())
                {
                    if (item.Name.StartsWith('.') || item.Name.StartsWith("~$", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (item is IStorageFolder child)
                    {
                        string childTarget = Path.Combine(target, SafeName(child.Name, "folder"));
                        Directory.CreateDirectory(childTarget);
                        pending.Enqueue((child, childTarget));
                    }
                    else if (item is IStorageFile file && DocumentParser.IsSupported(file.Name) && copied < MaxFolderFiles)
                    {
                        if (await CopyFileAsync(file, Path.Combine(target, SafeName(file.Name, "document"))))
                        {
                            copied++;
                        }
                    }
                }
            }

            return root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> CopyIntoAppAsync(IStorageFile file)
    {
        string target = Path.Combine(NewImportFolder(), SafeName(file.Name, "document"));
        return await CopyFileAsync(file, target) ? target : null;
    }

    private static async Task<bool> CopyFileAsync(IStorageFile file, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using Stream source = await file.OpenReadAsync();
            await using FileStream copy = File.Create(target);
            await source.CopyToAsync(copy);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string NewImportFolder() =>
        Path.Combine(ImportsDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);

    private static string SafeName(string name, string fallback)
    {
        string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    /// <summary>The Echo palette lives in theme dictionaries (Themes/EchoPalette.axaml), so switching the variant recolors it.</summary>
    private partial void ApplyPlatformPalette(bool dark)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    /// <summary>No global hotkeys on a phone.</summary>
    private partial bool ValidateHotkeys(out List<string> errors)
    {
        errors = [];
        return true;
    }

    /// <summary>Filtered copy of the source list with a day header before each day, like the grouped list on Windows.</summary>
    private sealed class PhoneFilteredListView : IFilteredListView
    {
        private readonly ObservableCollection<SessionSummary> _source;
        private readonly Predicate<object> _filter;
        private readonly Func<SessionSummary, string>? _groupOf;
        private readonly ObservableCollection<object> _view = [];
        private readonly List<SessionSummary> _visible = [];

        public PhoneFilteredListView(ObservableCollection<SessionSummary> source, Predicate<object> filter, string groupProperty)
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
            if (_groupOf == null)
            {
                items.AddRange(_visible);
            }
            else
            {
                var headers = new Dictionary<string, List<SessionSummary>>();
                var order = new List<string>();
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

/// <summary>Day header in the session list.</summary>
public sealed record SessionGroupHeader(string Name);
