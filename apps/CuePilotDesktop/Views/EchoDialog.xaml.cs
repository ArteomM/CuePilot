using System.Windows;
using System.Windows.Input;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.Views;

/// <summary>Replaces the system MessageBox for confirmations and errors.</summary>
public partial class EchoDialog : Window
{
    private EchoDialog(Window? owner, string title, string message)
    {
        InitializeComponent();
        if (owner != null && owner.IsVisible)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ScreenShareProtection.Track(this);
    }

    /// <summary>Asks a yes/no question; the confirm button is red for destructive actions.</summary>
    public static bool Confirm(Window? owner, LocalizationService l, string title, string message, bool destructive)
    {
        var dialog = new EchoDialog(owner, title, message);
        dialog.OkButton.Content = destructive ? l["DialogDelete"] : l["DialogYes"];
        dialog.CancelButton.Content = l["DialogCancel"];
        if (destructive)
        {
            dialog.OkButton.Style = (Style)dialog.FindResource("EchoButtonStyle");
            dialog.OkButton.SetResourceReference(BackgroundProperty, "EchoDangerSoftBrush");
            dialog.OkButton.SetResourceReference(BorderBrushProperty, "EchoDangerBorderBrush");
            dialog.OkButton.SetResourceReference(ForegroundProperty, "EchoDangerTextBrush");
            dialog.Marker.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "EchoDangerTextBrush");
        }

        return dialog.ShowDialog() == true;
    }

    public static void Notify(Window? owner, LocalizationService l, string title, string message, bool isError)
    {
        var dialog = new EchoDialog(owner, title, message);
        dialog.OkButton.Content = l["DialogOk"];
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        if (isError)
        {
            dialog.Marker.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "EchoDangerTextBrush");
        }

        dialog.ShowDialog();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
