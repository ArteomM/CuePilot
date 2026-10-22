using Avalonia.Controls;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views.Dialogs;

/// <summary>Adds or edits a text note of the topic knowledge base.</summary>
public partial class EchoNoteDialog : Window
{
    private bool _result;

    public EchoNoteDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(NoteTextBox.Text))
            {
                NoteTextBox.Focus();
                return;
            }

            _result = true;
            Close();
        };
        CancelButton.Click += (_, _) => Close();
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    private EchoNoteDialog(Window? owner, LocalizationService l, string title, string text)
        : this()
    {
        if (owner is not { IsVisible: true })
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        bool editing = text.Length > 0;
        Title = editing ? l["NoteDialogEditTitle"] : l["NoteDialogAddTitle"];
        HeaderText.Text = Title;
        HintText.Text = l["NoteDialogHint"];
        TitleBox.Text = title;
        ToolTip.SetTip(TitleBox, l["NoteDialogTitlePlaceholder"]);
        NoteTextBox.Text = text;
        ToolTip.SetTip(NoteTextBox, l["NoteDialogTextPlaceholder"]);
        OkButton.Content = editing ? l["NoteDialogSave"] : l["NoteDialogAdd"];
        CancelButton.Content = l["DialogCancel"];
        Opened += (_, _) => (editing ? NoteTextBox : TitleBox).Focus();
    }

    /// <summary>Returns the title and text, or null when cancelled.</summary>
    public static (string Title, string Text)? Show(Window? owner, LocalizationService l, string title, string text)
    {
        var dialog = new EchoNoteDialog(owner, l, title, text);
        ModalRunner.Run(dialog, owner);
        return dialog._result ? (dialog.TitleBox.Text ?? string.Empty, dialog.NoteTextBox.Text ?? string.Empty) : null;
    }
}
