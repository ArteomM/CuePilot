using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.Views.Controls;

/// <summary>
/// Model field of Settings: a text box that keeps whatever is typed, plus a list of the provider's models.
/// The ▾ button shows the whole list, typing filters it, ↑/↓ and Enter pick from the keyboard.
/// Opening the list asks <see cref="LoadCommand"/> to fetch the provider's models (parameter: false; «Обновить»: true).
/// </summary>
public partial class ModelPicker : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(ModelPicker),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ModelPicker)d).OnTextChanged(e.NewValue as string))
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });

    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options),
        typeof(IEnumerable<ModelOption>),
        typeof(ModelPicker),
        new PropertyMetadata(null, (d, _) => ((ModelPicker)d).OnOptionsChanged()));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(string),
        typeof(ModelPicker),
        new PropertyMetadata(string.Empty, (d, e) => ((ModelPicker)d).StatusLine.Text = e.NewValue as string ?? string.Empty));

    public static readonly DependencyProperty LoadCommandProperty = DependencyProperty.Register(
        nameof(LoadCommand),
        typeof(ICommand),
        typeof(ModelPicker));

    // Set while the box is updated from Text or from a picked row, so that does not count as typing.
    private bool _syncing;
    private string _filter = string.Empty;

    public ModelPicker()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable<ModelOption>? Options
    {
        get => (IEnumerable<ModelOption>?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public ICommand? LoadCommand
    {
        get => (ICommand?)GetValue(LoadCommandProperty);
        set => SetValue(LoadCommandProperty, value);
    }

    private void OnTextChanged(string? value)
    {
        if (_syncing || Box.Text == (value ?? string.Empty))
        {
            return;
        }

        _syncing = true;
        Box.Text = value ?? string.Empty;
        _syncing = false;
    }

    private void Box_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        SetCurrentValue(TextProperty, Box.Text);
        _syncing = false;

        // Typing narrows the list; the list only opens while something in it matches.
        if (Box.IsKeyboardFocused)
        {
            _filter = Box.Text.Trim();
            if (ApplyFilter() > 0)
            {
                DropDown.IsOpen = true;
            }
            else if (_filter.Length > 0)
            {
                DropDown.IsOpen = false;
            }
        }
    }

    private void OnOptionsChanged()
    {
        if (DropDown.IsOpen)
        {
            ApplyFilter();
        }
    }

    /// <summary>Fills the list with the options that contain the filter and highlights the current value.</summary>
    private int ApplyFilter()
    {
        IEnumerable<ModelOption> all = Options ?? [];
        List<ModelOption> shown = _filter.Length == 0
            ? all.ToList()
            : all.Where(option => option.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                                  option.Note.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = shown;
        EmptyLine.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        string current = Box.Text.Trim();
        ModelOption? selected = shown.FirstOrDefault(option => string.Equals(option.Id, current, StringComparison.OrdinalIgnoreCase));
        List.SelectedItem = selected;
        if (selected != null)
        {
            List.ScrollIntoView(selected);
        }

        return shown.Count;
    }

    private void ShowAll()
    {
        _filter = string.Empty;
        ApplyFilter();
        DropDown.IsOpen = true;
        Box.Focus();
    }

    private void Pick(ModelOption option)
    {
        _syncing = true;
        Box.Text = option.Id;
        SetCurrentValue(TextProperty, option.Id);
        _syncing = false;
        Box.CaretIndex = Box.Text.Length;
        DropDown.IsOpen = false;
        Box.Focus();
    }

    private void OpenButton_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowAll();
    }

    private void DropDown_OnOpened(object? sender, EventArgs e)
    {
        // While open, a click on ▾ falls through to the box and just closes the list instead of reopening it.
        OpenButton.IsHitTestVisible = false;
        if (LoadCommand?.CanExecute(false) == true)
        {
            LoadCommand.Execute(false);
        }
    }

    private void DropDown_OnClosed(object? sender, EventArgs e)
    {
        OpenButton.IsHitTestVisible = true;
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (LoadCommand?.CanExecute(true) == true)
        {
            LoadCommand.Execute(true);
        }

        Box.Focus();
    }

    private void List_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(List, source) is ListBoxItem { DataContext: ModelOption option })
        {
            e.Handled = true;
            Pick(option);
        }
    }

    private void Box_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when !DropDown.IsOpen:
                ShowAll();
                e.Handled = true;
                break;
            case Key.Down when DropDown.IsOpen:
            case Key.Up when DropDown.IsOpen:
                if (List.Items.Count > 0)
                {
                    int step = e.Key == Key.Down ? 1 : -1;
                    int index = List.SelectedIndex < 0 && step < 0 ? List.Items.Count - 1 : List.SelectedIndex + step;
                    List.SelectedIndex = Math.Clamp(index, 0, List.Items.Count - 1);
                    List.ScrollIntoView(List.SelectedItem);
                }

                e.Handled = true;
                break;
            case Key.Enter when DropDown.IsOpen && List.SelectedItem is ModelOption option:
                Pick(option);
                e.Handled = true;
                break;
            case Key.Escape when DropDown.IsOpen:
                DropDown.IsOpen = false;
                e.Handled = true;
                break;
            case Key.Tab:
                DropDown.IsOpen = false;
                break;
        }
    }
}
