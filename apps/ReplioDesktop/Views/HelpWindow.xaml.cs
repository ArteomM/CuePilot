using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ReplioDesktop.Services;
using ReplioDesktop.ViewModels;

namespace ReplioDesktop.Views;

/// <summary>«Справка»: a small window with short pages and a mini screenshot on each. One per app; not modal.</summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _open;
    private readonly MainViewModel _viewModel;

    private HelpWindow(Window? owner, MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (owner != null && owner.IsVisible)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Title = viewModel.L["HelpWindowTitle"];
        ScreenShareProtection.Track(this);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _open = null;
        };
        PreviewKeyDown += OnPreviewKeyDown;
        ShowTopicImage();
    }

    /// <summary>Opens the help window, or brings the open one forward.</summary>
    public static void ShowFor(Window? owner, MainViewModel viewModel)
    {
        if (_open == null)
        {
            _open = new HelpWindow(owner, viewModel);
            _open.Show();
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
            Dispatcher.BeginInvoke(new Action(ShowTopicImage));
        }
    }

    /// <summary>Assets/Help/&lt;name&gt;-dark.png or -light.png for the current theme; the frame hides when there is none.</summary>
    private void ShowTopicImage()
    {
        string name = _viewModel.SelectedHelpTopic?.Image ?? string.Empty;
        TopicImage.Source = null;
        if (name.Length > 0)
        {
            string theme = _viewModel.IsDarkTheme ? "dark" : "light";
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Assets/Help/{name}-{theme}.png", UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                TopicImage.Source = image;
            }
            catch (Exception)
            {
                // No picture for this page: the text alone is shown.
            }
        }

        ImageFrame.Visibility = TopicImage.Source == null ? Visibility.Collapsed : Visibility.Visible;
        TopicText.ScrollToHome();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
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

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
