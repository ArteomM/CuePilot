using System.Globalization;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// The limit bar (Arteom: «видеть полосу индикации, чтобы понимать, что я упираюсь в лимит»). It shows the tightest
/// remainder of the plan: today's limit (a fifth of the month's AI budget), the month's AI budget and, on plans with
/// hours, the minutes of meetings. Values come from GET /account (quota) and, between those reads, from the
/// X-Replio-*-Left headers of every AI answer. Mint above 20 %, yellow at 20 % and below, red at 5 % and below;
/// «Без лимита» for unlimited accounts, hidden on plans with own providers. A click opens the personal account on replio.ru.
/// The recording indicator shows a thin line of the same colour once the remainder is 20 % or less.
/// </summary>
public partial class MainViewModel
{
    public const double LimitTrackWidth = 120;
    private const int LimitWarnPercent = 20;
    private const int LimitCriticalPercent = 5;

    private int? _limitDay;
    private int? _limitMonth;
    private int? _limitMinutesLeft;
    private int? _limitMinutesTotal;
    private bool _limitUnlimited;
    private DateTime? _limitDayResetsAt;
    private DateTime? _limitPeriodEnd;

    /// <summary>The bar changed: the recording indicator (a window of its own) repaints its line.</summary>
    public event Action? LimitChanged;

    private enum LimitKind
    {
        Day,
        Month,
        Minutes
    }

    private void InitializeLimits()
    {
        ReplioApi.QuotaSeen += hint => RunOnUi(() => ApplyQuotaHint(hint));
        ApplyAccountLimits(_account);
    }

    /// <summary>A fresh /account answer (or none after signing out).</summary>
    private void ApplyAccountLimits(ReplioAccountInfo? account)
    {
        _limitDay = account?.DayLeftPercent;
        _limitMonth = account?.AiLeftPercent;
        _limitMinutesLeft = account?.MinutesLeft;
        _limitMinutesTotal = account?.MinutesTotal;
        _limitUnlimited = account?.Unlimited == true;
        _limitDayResetsAt = account?.DayResetsAt;
        _limitPeriodEnd = account?.PeriodEnd;
        OnLimitChanged();
    }

    private void ApplyQuotaHint(ReplioQuotaHint hint)
    {
        if (!IsManagedAi || !IsSignedIn)
        {
            return;
        }

        _limitUnlimited = hint.Unlimited;
        _limitDay = hint.DayLeft ?? _limitDay;
        _limitMonth = hint.MonthLeft ?? _limitMonth;
        _limitMinutesLeft = hint.MinutesLeft ?? _limitMinutesLeft;
        // After midnight in Moscow the day starts over: the next answer brings the new remainder.
        if (_limitDayResetsAt is DateTime resets && DateTime.Now >= resets)
        {
            _limitDayResetsAt = resets.AddDays(1);
        }

        OnLimitChanged();
    }

    private int? MinutesPercent =>
        _limitMinutesLeft is int left && _limitMinutesTotal is int total && total > 0
            ? (int)Math.Clamp(Math.Floor(100.0 * left / total), 0, 100)
            : null;

    /// <summary>The tightest of the known remainders and which one it is.</summary>
    private (int Percent, LimitKind Kind)? TightestLimit
    {
        get
        {
            var known = new List<(int Percent, LimitKind Kind)>();
            if (_limitDay is int day)
            {
                known.Add((Math.Clamp(day, 0, 100), LimitKind.Day));
            }

            if (_limitMonth is int month)
            {
                known.Add((Math.Clamp(month, 0, 100), LimitKind.Month));
            }

            if (MinutesPercent is int minutes)
            {
                known.Add((minutes, LimitKind.Minutes));
            }

            return known.Count == 0 ? null : known.MinBy(item => item.Percent);
        }
    }

    /// <summary>Unlimited accounts (the owner of the service) see «Без лимита» with a full bar.</summary>
    public bool ShowLimitBar => IsManagedAi && IsSignedIn && (_limitUnlimited || TightestLimit != null);

    public int LimitLeftPercent => _limitUnlimited ? 100 : TightestLimit?.Percent ?? 100;

    public bool IsLimitLow => ShowLimitBar && !_limitUnlimited && LimitLeftPercent <= LimitWarnPercent;

    public double LimitBarWidth => LimitLeftPercent <= 0 ? 0 : Math.Max(3, LimitTrackWidth * LimitLeftPercent / 100.0);

    public string LimitBarColor
    {
        get
        {
            bool dark = SelectedThemeMode != "light";
            int percent = LimitLeftPercent;
            if (percent <= LimitCriticalPercent)
            {
                return dark ? "#FF6B7A" : "#D33A4F";
            }

            if (percent <= LimitWarnPercent)
            {
                return dark ? "#F5C451" : "#B7791F";
            }

            return dark ? "#5EF0C1" : "#0B8A65";
        }
    }

    /// <summary>The label: ordinary text, the bar's colour once the remainder is low.</summary>
    public string LimitTextColor => IsLimitLow ? LimitBarColor : SelectedThemeMode != "light" ? "#C9D2DE" : "#2E3A45";

    /// <summary>The recording indicator is always dark.</summary>
    public string LimitBarColorOnDark => LimitLeftPercent <= LimitCriticalPercent ? "#FF6B7A" : LimitLeftPercent <= LimitWarnPercent ? "#F5C451" : "#5EF0C1";

    /// <summary>«Лимит · 64%», «На сегодня лимит исчерпан».</summary>
    public string LimitBarText
    {
        get
        {
            if (_limitUnlimited)
            {
                return L["LimitUnlimited"];
            }

            if (TightestLimit is not { } tight)
            {
                return string.Empty;
            }

            if (tight.Percent <= 0)
            {
                return L[tight.Kind == LimitKind.Day ? "LimitDayOut" : tight.Kind == LimitKind.Month ? "LimitMonthOut" : "LimitMinutesOut"];
            }

            return string.Format(L["LimitShort"], tight.Percent);
        }
    }

    /// <summary>Every remainder on its own line, when it starts over, and what a click does.</summary>
    public string LimitBarTooltip
    {
        get
        {
            var lines = new List<string>();
            if (_limitUnlimited)
            {
                return L["LimitTipUnlimited"] + "\n" + L["LimitTipClick"];
            }

            if (_limitDay is int day)
            {
                lines.Add(string.Format(L["LimitTipDay"], Math.Clamp(day, 0, 100), DescribeReset(_limitDayResetsAt)));
            }

            if (_limitMonth is int month)
            {
                lines.Add(_limitPeriodEnd is DateTime end
                    ? string.Format(L["LimitTipMonthUntil"], Math.Clamp(month, 0, 100), end.ToString("d", CultureInfo.CurrentCulture))
                    : string.Format(L["LimitTipMonth"], Math.Clamp(month, 0, 100)));
            }

            if (_limitMinutesLeft is int left)
            {
                lines.Add(_limitMinutesTotal is int total
                    ? string.Format(L["LimitTipMinutesOf"], FormatHours(left), FormatHours(total))
                    : string.Format(L["LimitTipMinutes"], FormatHours(left)));
            }

            lines.Add(L["LimitTipDayRule"]);
            lines.Add(L["LimitTipClick"]);
            return string.Join("\n", lines);
        }
    }

    /// <summary>«в 00:00» today/tomorrow, otherwise with the date.</summary>
    private string DescribeReset(DateTime? resets)
    {
        if (resets is not DateTime at)
        {
            return L["LimitResetMidnight"];
        }

        return at.Date <= DateTime.Today.AddDays(1)
            ? string.Format(L["LimitResetAt"], at.ToString("HH:mm", CultureInfo.CurrentCulture))
            : string.Format(L["LimitResetOn"], at.ToString("g", CultureInfo.CurrentCulture));
    }

    /// <summary>«5 ч 20 мин», «45 мин».</summary>
    private string FormatHours(int minutes)
    {
        minutes = Math.Max(0, minutes);
        return minutes >= 60
            ? string.Format(L[minutes % 60 == 0 ? "LimitHours" : "LimitHoursMinutes"], minutes / 60, minutes % 60)
            : string.Format(L["LimitMinutesOnly"], minutes);
    }

    private void OnLimitChanged()
    {
        foreach (string name in new[]
        {
            nameof(ShowLimitBar), nameof(LimitLeftPercent), nameof(IsLimitLow), nameof(LimitBarWidth), nameof(LimitBarColor),
            nameof(LimitBarColorOnDark), nameof(LimitTextColor), nameof(LimitBarText), nameof(LimitBarTooltip)
        })
        {
            OnPropertyChanged(name);
        }

        LimitChanged?.Invoke();
    }
}
