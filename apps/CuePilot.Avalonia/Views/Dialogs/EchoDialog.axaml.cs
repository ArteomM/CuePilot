using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views.Dialogs;

/// <summary>Replaces the system message box for confirmations and errors.</summary>
public partial class EchoDialog : Window
{
    private bool _result;

    public EchoDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            _result = true;
            Close();
        };
        CancelButton.Click += (_, _) => Close();
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    private EchoDialog(Window? owner, string title, string message)
        : this()
    {
        if (owner is not { IsVisible: true })
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    /// <summary>Asks a yes/no question; the confirm button is red for destructive actions.</summary>
    public static bool Confirm(Window? owner, LocalizationService l, string title, string message, bool destructive)
    {
        var dialog = new EchoDialog(owner, title, message);
        dialog.OkButton.Content = destructive ? l["DialogDelete"] : l["DialogYes"];
        dialog.CancelButton.Content = l["DialogCancel"];
        if (destructive)
        {
            dialog.OkButton.Classes.Remove("accent");
            dialog.OkButton.Bind(BackgroundProperty, dialog.GetResourceObservable("EchoDangerSoftBrush"));
            dialog.OkButton.Bind(Button.BorderBrushProperty, dialog.GetResourceObservable("EchoDangerBorderBrush"));
            dialog.OkButton.Bind(ForegroundProperty, dialog.GetResourceObservable("EchoDangerTextBrush"));
            dialog.Marker.Bind(Shape.FillProperty, dialog.GetResourceObservable("EchoDangerTextBrush"));
        }

        ModalRunner.Run(dialog, owner);
        return dialog._result;
    }

    public static void Notify(Window? owner, LocalizationService l, string title, string message, bool isError)
    {
        var dialog = new EchoDialog(owner, title, message);
        dialog.OkButton.Content = l["DialogOk"];
        dialog.CancelButton.IsVisible = false;
        if (isError)
        {
            dialog.Marker.Bind(Shape.FillProperty, dialog.GetResourceObservable("EchoDangerTextBrush"));
        }

        ModalRunner.Run(dialog, owner);
    }
}
