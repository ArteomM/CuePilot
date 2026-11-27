using System.Globalization;
using Avalonia.Data.Converters;

namespace Replio.Android.Views;

/// <summary>
/// The first lines of a meeting's summary for its card inside a group: headings and list marks dropped, at most
/// about 160 characters (the card shows two lines).
/// </summary>
public sealed class SummaryPreviewConverter : IValueConverter
{
    public static readonly SummaryPreviewConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string summary || string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        int length = 0;
        foreach (string raw in summary.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith('|'))
            {
                continue;
            }

            line = line.TrimStart('*', '-', '•', '>', ' ').Replace("**", string.Empty, StringComparison.Ordinal).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            parts.Add(line);
            length += line.Length;
            if (length >= 160)
            {
                break;
            }
        }

        string text = string.Join(" ", parts);
        return text.Length > 170 ? text[..167] + "…" : text;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
