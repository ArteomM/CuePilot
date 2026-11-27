using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Replio.Android.Views;

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

    /// <summary>
    /// An action sheet from the bottom edge, as a Material list: one 56 dp row per action with its text on the left,
    /// no frames, destructive ones in red. No «Отмена»: a swipe down, «Назад» or a tap outside closes it.
    /// </summary>
    private void ShowActions(string title, IEnumerable<(string Label, Action Action, bool Destructive)> actions)
    {
        _dialogDismissed = null;
        DialogContent.Children.Clear();
        DialogContent.Children.Add(new Border
        {
            Width = 40,
            Height = 4,
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brush("EchoBorderBrush")
        });
        TextBlock heading = TitleBlock(title);
        heading.FontSize = 15;
        heading.Foreground = Brush("EchoMutedBrush");
        heading.MaxLines = 2;
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.Margin = new Thickness(4, 0, 4, 6);
        DialogContent.Children.Add(heading);
        foreach ((string label, Action action, bool destructive) in actions)
        {
            var row = new Button
            {
                Content = label,
                MinHeight = 56,
                Padding = new Thickness(4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 16.5,
                FontWeight = FontWeight.SemiBold
            };
            row.Classes.Add("ghost");
            if (destructive)
            {
                row.Foreground = new SolidColorBrush(Color.Parse("#F06A6F"));
            }

            row.Click += (_, _) =>
            {
                _dialogDismissed = null;
                CloseDialog();
                action();
            };
            DialogContent.Children.Add(row);
        }

        OpenDialogLayer(Motion.Kind.Sheet);
    }

    /// <summary>A dialog; <paramref name="onDismiss"/> runs when it is closed without a button (back, a tap outside).</summary>
    private void ShowDialog(string title, string message, IReadOnlyList<(string Label, Action? Action, bool Destructive)> buttons, IEnumerable<Control>? fields = null, Action? onDismiss = null)
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
                _dialogDismissed = null;
                CloseDialog();
                action?.Invoke();
            };
            row.Children.Add(button);
        }

        DialogContent.Children.Add(row);
        OpenDialogLayer(Motion.Kind.Pop);
        _dialogDismissed = onDismiss;
    }

    private Action? _dialogDismissed;
    private Motion.Kind _dialogKind = Motion.Kind.Pop;

    /// <summary>An action sheet sits on the bottom edge with round top corners, a dialog in the middle.</summary>
    private void OpenDialogLayer(Motion.Kind kind)
    {
        _dialogKind = kind;
        bool sheet = kind == Motion.Kind.Sheet;
        DialogCard.VerticalAlignment = sheet ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        DialogCard.Margin = sheet ? new Thickness(0) : new Thickness(16);
        DialogCard.MaxWidth = sheet ? double.PositiveInfinity : 460;
        DialogCard.CornerRadius = sheet ? new CornerRadius(24, 24, 0, 0) : new CornerRadius(20);
        DialogCard.BorderThickness = sheet ? new Thickness(0, 1, 0, 0) : new Thickness(1);
        DialogCard.Padding = sheet ? new Thickness(18, 10, 18, 24) : new Thickness(18, 16);
        Motion.Show(DialogLayer, DialogCard, kind);
    }

    private void CloseDialog()
    {
        Motion.Hide(DialogLayer, DialogCard, _dialogKind, () => DialogContent.Children.Clear());
        Action? dismissed = _dialogDismissed;
        _dialogDismissed = null;
        dismissed?.Invoke();
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
