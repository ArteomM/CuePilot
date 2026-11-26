using System.Globalization;
using Avalonia.Data.Converters;
using Replio.Android.Platform;

namespace Replio.Android.Views;

/// <summary>
/// The line under a session's name on Home: «10:05 · 24 мин», «16:00 · 1 ч 12 мин» (whole minutes, no «24,0»).
/// Values: the start (DateTime) and the length in minutes (double).
/// </summary>
public sealed class SessionMetaConverter : IMultiValueConverter
{
    public static readonly SessionMetaConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        string time = values.Count > 0 && values[0] is DateTime at ? at.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        double minutes = values.Count > 1 && values[1] is double m ? m : 0;
        string length = Length(minutes);
        return length.Length == 0 ? time : time + " · " + length;
    }

    private static string Length(double minutes)
    {
        bool ru = MobileText.Language != "en";
        if (minutes <= 0)
        {
            return string.Empty;
        }

        if (minutes < 1)
        {
            return ru ? "< 1 мин" : "< 1 min";
        }

        int total = (int)Math.Round(minutes);
        if (total < 60)
        {
            return total + (ru ? " мин" : " min");
        }

        int hours = total / 60;
        int rest = total % 60;
        string h = hours + (ru ? " ч" : " h");
        return rest == 0 ? h : h + " " + rest + (ru ? " мин" : " min");
    }
}
