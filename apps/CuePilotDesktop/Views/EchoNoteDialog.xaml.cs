using System.Windows;
using System.Windows.Input;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.Views;

/// <summary>Adds or edits a text note of the topic knowledge base.</summary>
public partial class EchoNoteDialog : Window
{
    private EchoNoteDialog(Window? owner, LocalizationService l, string title, string text)
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

        bool editing = text.Length > 0;
        Title = editing ? l["NoteDialogEditTitle"] : l["NoteDialogAddTitle"];
        HeaderText.Text = Title;
        HintText.Text = l["NoteDialogHint"];
        TitleBox.Text = title;
        TitleBox.ToolTip = l["NoteDialogTitlePlaceholder"];
        TextBox.Text = text;
        TextBox.ToolTip = l["NoteDialogTextPlaceholder"];
        OkButton.Content = editing ? l["NoteDialogSave"] : l["NoteDialogAdd"];
        CancelButton.Content = l["DialogCancel"];
        ScreenShareProtection.Track(this);
        Loaded += (_, _) => (editing ? TextBox : TitleBox).Focus();
    }

    /// <summary>Returns the title and text, or null when cancelled.</summary>
    public static (string Title, string Text)? Show(Window? owner, LocalizationService l, string title, string text)
    {
        var dialog = new EchoNoteDialog(owner, l, title, text);
        return dialog.ShowDialog() == true ? (dialog.TitleBox.Text, dialog.TextBox.Text) : null;
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextBox.Text))
        {
            TextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
