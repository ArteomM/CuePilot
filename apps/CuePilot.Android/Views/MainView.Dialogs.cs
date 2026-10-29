using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CuePilot.Android.Views;

/// <summary>
/// Dialogs drawn over the screen. The view model asks for confirmations and notes synchronously (a modal window on
/// the desktop); a phone cannot block, so the first call is answered «no», the dialog opens, and on «yes» the same
/// action runs again with the answer ready (<see cref="RunConfirmable"/>, <see cref="RunWithNoteEditor"/>).
/// </summary>
public partial class MainView
{
    private Action? _pendingAction;
    private bool _preconfirmed;
    private (string Title, string Text)? _presetNote;
    private bool _noteAnswered;

    private void RunConfirmable(Action action)
    {
        _pendingAction = action;
        action();
        _pendingAction = null;
    }

    private bool OnConfirmationRequested(string title, string message)
    {
        if (_preconfirmed)
        {
            return true;
        }

        Action? retry = _pendingAction;
        _pendingAction = null;
        if (retry == null)
        {
            return false;
        }

        Dispatcher.UIThread.Post(() => ShowDialog(title, message,
        [
            (_viewModel.L["DialogCancel"], null, false),
            (_viewModel.L["DialogDelete"], () =>
            {
                _preconfirmed = true;
                try
                {
                    retry();
                }
                finally
                {
                    _preconfirmed = false;
                }
            }, true)
        ]));
        return false;
    }

    private void RunWithNoteEditor(Action action)
    {
        _pendingAction = action;
        _noteAnswered = false;
        action();
        _pendingAction = null;
    }

    private (string Title, string Text)? OnNoteEditorRequested(string title, string text)
    {
        if (_noteAnswered)
        {
            _noteAnswered = false;
            return _presetNote;
        }

        Action? retry = _pendingAction;
        _pendingAction = null;
        if (retry == null)
        {
            return null;
        }

        bool editing = !string.IsNullOrEmpty(text);
        Dispatcher.UIThread.Post(() =>
        {
            var titleBox = new TextBox { Text = title, Watermark = _viewModel.L["NoteDialogTitlePlaceholder"], Margin = new Thickness(0, 12, 0, 0) };
            var textBox = new TextBox
            {
                Text = text,
                Watermark = _viewModel.L["NoteDialogTextPlaceholder"],
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 140,
                MaxHeight = 280,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 0, 0)
            };
            ShowDialog(
                _viewModel.L[editing ? "NoteDialogEditTitle" : "NoteDialogAddTitle"],
                _viewModel.L["NoteDialogHint"],
                [
                    (_viewModel.L["DialogCancel"], null, false),
                    (_viewModel.L[editing ? "NoteDialogSave" : "NoteDialogAdd"], () =>
                    {
                        _presetNote = (titleBox.Text ?? string.Empty, textBox.Text ?? string.Empty);
                        _noteAnswered = true;
                        retry();
                        _noteAnswered = false;
                    }, false)
                ],
                [titleBox, textBox]);
            Dispatcher.UIThread.Post(() => (editing ? textBox : titleBox).Focus(), DispatcherPriority.Input);
        });
        return null;
    }

    private void ShowMessage(string title, string message) =>
        ShowDialog(title, message, [(_viewModel.L["DialogOk"], null, false)]);

    /// <summary>An action sheet: one full-width button per action, destructive ones in red.</summary>
    private void ShowActions(string title, IEnumerable<(string Label, Action Action, bool Destructive)> actions)
    {
        DialogContent.Children.Clear();
        DialogContent.Children.Add(TitleBlock(title));
        foreach ((string label, Action action, bool destructive) in actions)
        {
            Button button = DialogButton(label, destructive ? "dangerSoft" : null);
            button.Margin = new Thickness(0, 8, 0, 0);
            button.Click += (_, _) =>
            {
                CloseDialog();
                action();
            };
            DialogContent.Children.Add(button);
        }

        Button cancel = DialogButton(_viewModel.L["DialogCancel"], "ghost");
        cancel.Margin = new Thickness(0, 8, 0, 0);
        cancel.Click += (_, _) => CloseDialog();
        DialogContent.Children.Add(cancel);
        DialogLayer.IsVisible = true;
    }

    private void ShowDialog(string title, string message, IReadOnlyList<(string Label, Action? Action, bool Destructive)> buttons, IEnumerable<Control>? fields = null)
    {
        DialogContent.Children.Clear();
        DialogContent.Children.Add(TitleBlock(title));
        if (!string.IsNullOrWhiteSpace(message))
        {
            DialogContent.Children.Add(new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 14.5,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("EchoSoftTextBrush")
            });
        }

        foreach (Control field in fields ?? [])
        {
            DialogContent.Children.Add(field);
        }

        var row = new UniformGrid { Columns = buttons.Count, Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < buttons.Count; i++)
        {
            (string label, Action? action, bool destructive) = buttons[i];
            bool primary = i == buttons.Count - 1;
            Button button = DialogButton(label, destructive ? "dangerSoft" : primary ? "accent" : null);
            button.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Count - 1 ? 0 : 4, 0);
            button.Click += (_, _) =>
            {
                CloseDialog();
                action?.Invoke();
            };
            row.Children.Add(button);
        }

        DialogContent.Children.Add(row);
        DialogLayer.IsVisible = true;
    }

    private void CloseDialog()
    {
        DialogLayer.IsVisible = false;
        DialogContent.Children.Clear();
    }

    /// <summary>A tap outside the card closes it, like Cancel.</summary>
    private void DialogLayer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DialogLayer))
        {
            CloseDialog();
        }
    }

    private TextBlock TitleBlock(string title) => new()
    {
        Text = title,
        FontSize = 18,
        FontWeight = FontWeight.ExtraBold,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("EchoTextBrush")
    };

    private static Button DialogButton(string label, string? style)
    {
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 44
        };
        if (style != null)
        {
            button.Classes.Add(style);
        }

        return button;
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) ? value as IBrush : null;
}
