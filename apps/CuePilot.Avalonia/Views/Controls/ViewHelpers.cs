using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace CuePilot.Avalonia.Views.Controls;

/// <summary>
/// ItemContainerStyle of WPF: <c>controls:ItemContainer.Classes="plain"</c> on a ListBox gives each of its rows
/// that style class (ListBoxItem.plain, .topic, .knowledge in Themes/Echo.axaml).
/// </summary>
public static class ItemContainer
{
    public static readonly AttachedProperty<string?> ClassesProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, string?>("Classes", typeof(ItemContainer));

    static ItemContainer()
    {
        ClassesProperty.Changed.AddClassHandler<ItemsControl>((list, e) =>
        {
            list.ContainerPrepared -= OnContainerPrepared;
            if (e.NewValue is string { Length: > 0 })
            {
                list.ContainerPrepared += OnContainerPrepared;
            }
        });
    }

    public static string? GetClasses(ItemsControl element) => element.GetValue(ClassesProperty);

    public static void SetClasses(ItemsControl element, string? value) => element.SetValue(ClassesProperty, value);

    private static void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (sender is ItemsControl list && GetClasses(list) is string classes)
        {
            foreach (string name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!e.Container.Classes.Contains(name))
                {
                    e.Container.Classes.Add(name);
                }
            }
        }
    }
}

/// <summary>True when the value's text equals the parameter (DataTrigger on a string or enum value).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString() ?? string.Empty, parameter?.ToString() ?? string.Empty, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter?.ToString() : global::Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>True when a number is zero (the «empty list» hints).</summary>
public sealed class IsZeroConverter : IValueConverter
{
    public static readonly IsZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int i => i == 0,
            long l => l == 0,
            string s => s.Trim() == "0",
            null => true,
            _ => false
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
