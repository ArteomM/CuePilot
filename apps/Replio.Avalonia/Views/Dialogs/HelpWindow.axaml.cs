using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReplioDesktop.ViewModels;

namespace Replio.Avalonia.Views.Dialogs;

/// <summary>«Справка»: a small window with short pages and a mini screenshot on each. One per app; not modal.</summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _open;
    private readonly MainViewModel? _viewModel;

    public HelpWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    private HelpWindow(MainViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = viewModel.L["HelpWindowTitle"];
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _open = null;
        };
        KeyDown += OnKeyDown;
        // The mini screenshot is small in the card: a click shows it over the whole window.
        ImageFrame.PointerReleased += (_, e) =>
        {
            if (TopicImage.Source != null)
            {
                ZoomImage.Source = TopicImage.Source;
                ZoomLayer.IsVisible = true;
                e.Handled = true;
            }
        };
        ZoomLayer.PointerReleased += (_, e) =>
        {
            HideZoom();
            e.Handled = true;
        };
        ShowTopicImage();
    }

    private void HideZoom()
    {
        ZoomLayer.IsVisible = false;
        ZoomImage.Source = null;
    }

    /// <summary>Opens the help window, or brings the open one forward.</summary>
    public static void ShowFor(Window? owner, MainViewModel viewModel)
    {
        if (_open == null)
        {
            _open = new HelpWindow(viewModel);
            if (owner is { IsVisible: true })
            {
                _open.Show(owner);
            }
            else
            {
                _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _open.Show();
            }
        }

        if (_open.WindowState == WindowState.Minimized)
        {
            _open.WindowState = WindowState.Normal;
        }

        _open.Activate();
        _open.TopicList.Focus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedHelpTopic) or nameof(MainViewModel.IsDarkTheme) or nameof(MainViewModel.SelectedThemeMode))
        {
            Dispatcher.UIThread.Post(ShowTopicImage);
        }
    }

    /// <summary>Assets/Help/&lt;name&gt;-dark.png or -light.png for the current theme; the frame hides when there is none.</summary>
    private void ShowTopicImage()
    {
        if (_viewModel == null)
        {
            return;
        }

        string name = _viewModel.SelectedHelpTopic?.Image ?? string.Empty;
        Bitmap? image = null;
        if (name.Length > 0)
        {
            string theme = _viewModel.IsDarkTheme ? "dark" : "light";
            try
            {
                using Stream stream = AssetLoader.Open(new Uri($"avares://Replio/Assets/Help/{name}-{theme}.png"));
                image = new Bitmap(stream);
            }
            catch (Exception)
            {
                // No picture for this page: the text alone is shown.
            }
        }

        HideZoom();
        var previous = TopicImage.Source as IDisposable;
        TopicImage.Source = image;
        previous?.Dispose();
        ImageFrame.IsVisible = image != null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (ZoomLayer.IsVisible)
            {
                HideZoom();
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.PageDown && _viewModel.NextHelpTopicCommand.CanExecute(null))
        {
            _viewModel.NextHelpTopicCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.PageUp && _viewModel.PreviousHelpTopicCommand.CanExecute(null))
        {
            _viewModel.PreviousHelpTopicCommand.Execute(null);
            e.Handled = true;
        }
    }
}
