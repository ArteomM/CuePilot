using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views.Controls;

/// <summary>
/// Model field of Settings: a text box that keeps whatever is typed, plus a list of the provider's models.
/// The ▾ button shows the whole list, typing filters it, ↑/↓ and Enter pick from the keyboard.
/// Opening the list asks <see cref="LoadCommand"/> to fetch the provider's models (parameter: false; «Обновить»: true).
/// </summary>
public partial class ModelPicker : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ModelPicker, string?>(nameof(Text), string.Empty, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable<ModelOption>?> OptionsProperty =
        AvaloniaProperty.Register<ModelPicker, IEnumerable<ModelOption>?>(nameof(Options));

    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<ModelPicker, string?>(nameof(Status), string.Empty);

    public static readonly StyledProperty<ICommand?> LoadCommandProperty =
        AvaloniaProperty.Register<ModelPicker, ICommand?>(nameof(LoadCommand));

    // Set while the box is updated from Text or from a picked row, so that does not count as typing.
    private bool _syncing;
    private string _filter = string.Empty;

    static ModelPicker()
    {
        TextProperty.Changed.AddClassHandler<ModelPicker>((picker, e) => picker.OnTextChanged(e.NewValue as string));
        OptionsProperty.Changed.AddClassHandler<ModelPicker>((picker, _) => picker.OnOptionsChanged());
        StatusProperty.Changed.AddClassHandler<ModelPicker>((picker, e) => picker.StatusLine.Text = e.NewValue as string ?? string.Empty);
    }

    public ModelPicker()
    {
        InitializeComponent();
        Box.TextChanged += Box_OnTextChanged;
        Box.AddHandler(KeyDownEvent, Box_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        OpenButton.PointerReleased += OpenButton_OnPointerReleased;
        DropDown.Opened += DropDown_OnOpened;
        DropDown.Closed += DropDown_OnClosed;
        RefreshButton.Click += Refresh_OnClick;
        List.ContainerPrepared += (_, e) => e.Container.Classes.Add("pickerRow");
        List.AddHandler(PointerReleasedEvent, List_OnPointerReleased, RoutingStrategies.Tunnel);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IEnumerable<ModelOption>? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public string? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public ICommand? LoadCommand
    {
        get => GetValue(LoadCommandProperty);
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

    private void Box_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        SetCurrentValue(TextProperty, Box.Text ?? string.Empty);
        _syncing = false;

        // Typing narrows the list; the list only opens while something in it matches.
        if (Box.IsKeyboardFocusWithin)
        {
            _filter = (Box.Text ?? string.Empty).Trim();
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
        EmptyLine.IsVisible = shown.Count == 0;

        string current = (Box.Text ?? string.Empty).Trim();
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
        Box.CaretIndex = option.Id.Length;
        DropDown.IsOpen = false;
        Box.Focus();
    }

    private void OpenButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
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

    private void Refresh_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LoadCommand?.CanExecute(true) == true)
        {
            LoadCommand.Execute(true);
        }

        Box.Focus();
    }

    private void List_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is Visual source &&
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ModelOption option })
        {
            e.Handled = true;
            Pick(option);
        }
    }

    private void Box_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when !DropDown.IsOpen:
                ShowAll();
                e.Handled = true;
                break;
            case Key.Down when DropDown.IsOpen:
            case Key.Up when DropDown.IsOpen:
                if (List.ItemCount > 0)
                {
                    int step = e.Key == Key.Down ? 1 : -1;
                    int index = List.SelectedIndex < 0 && step < 0 ? List.ItemCount - 1 : List.SelectedIndex + step;
                    List.SelectedIndex = Math.Clamp(index, 0, List.ItemCount - 1);
                    if (List.SelectedItem != null)
                    {
                        List.ScrollIntoView(List.SelectedItem);
                    }
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
