using System.Windows;
using System.Windows.Controls;

namespace ReplioDesktop.Views.Controls;

/// <summary>
/// Attached «Secret» for a PasswordBox: two-way binding of an API key, so keys in Settings show as dots
/// and never appear in screenshots or on a screen share.
/// </summary>
public static class SecretBox
{
    public static readonly DependencyProperty SecretProperty = DependencyProperty.RegisterAttached(
        "Secret",
        typeof(string),
        typeof(SecretBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSecretChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(SecretBox), new PropertyMetadata(false));

    public static string GetSecret(DependencyObject element) => (string)element.GetValue(SecretProperty);

    public static void SetSecret(DependencyObject element, string value) => element.SetValue(SecretProperty, value);

    private static void OnSecretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        if (!(bool)box.GetValue(HookedProperty))
        {
            box.SetValue(HookedProperty, true);
            box.PasswordChanged += (_, _) => SetSecret(box, box.Password);
        }

        string value = e.NewValue as string ?? string.Empty;
        if (box.Password != value)
        {
            box.Password = value;
        }
    }
}
