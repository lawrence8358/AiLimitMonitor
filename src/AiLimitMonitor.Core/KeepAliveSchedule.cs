using System.Globalization;
using AiLimitMonitor.Core.Config;

namespace AiLimitMonitor.Core;

/// <summary>
/// Answers whether a keep-alive call is currently allowed by the global schedule.
/// Rules are evaluated in the supplied time zone (the system local zone by default),
/// and a configured schedule with no valid rules fails closed.
/// </summary>
public sealed class KeepAliveSchedule
{
    private readonly TimeZoneInfo _timeZone;
    private State _state = new(false, []);

    public KeepAliveSchedule(TimeZoneInfo? timeZone = null)
    {
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>
    /// Replaces the active schedule. A null config means unrestricted for backward
    /// compatibility; an empty or invalid configured rule set allows nothing.
    /// </summary>
    public void Update(KeepAliveScheduleConfig? config)
    {
        var rules = config?.Rules is { } configuredRules
            ? configuredRules.Select(Parse).Where(static rule => rule is not null).Select(static rule => rule!).ToArray()
            : [];
        _state = new(config is not null, rules);
    }

    public bool IsAllowed(DateTimeOffset now)
    {
        var state = _state;
        if (!state.Restricted)
            return true;
        if (state.Rules.Length == 0)
            return false;

        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        return state.Rules.Any(rule => rule.IsAllowed(local.DayOfWeek, local.TimeOfDay));
    }

    private static Rule? Parse(KeepAliveScheduleRuleConfig? config)
    {
        if (config is null || config.Days is null || config.Days.Count == 0)
            return null;
        if (!TimeSpan.TryParseExact(config.Start, "hh\\:mm", CultureInfo.InvariantCulture,
                out var start) ||
            !TimeSpan.TryParseExact(config.End, "hh\\:mm", CultureInfo.InvariantCulture,
                out var end) ||
            start >= TimeSpan.FromDays(1) || end >= TimeSpan.FromDays(1) || start == end)
            return null;

        var days = config.Days
            .Where(static day => day is >= DayOfWeek.Sunday and <= DayOfWeek.Saturday)
            .Distinct()
            .ToArray();
        return days.Length == 0 ? null : new Rule(days, start, end);
    }

    private sealed class Rule(DayOfWeek[] days, TimeSpan start, TimeSpan end)
    {
        public bool IsAllowed(DayOfWeek day, TimeSpan time)
        {
            if (start < end)
                return days.Contains(day) && time >= start && time < end;

            // The day list names the start day. The second half therefore belongs to
            // the following calendar day (including Sunday -> Monday).
            if (days.Contains(day) && time >= start)
                return true;

            var previousDay = day == DayOfWeek.Sunday
                ? DayOfWeek.Saturday
                : (DayOfWeek)((int)day - 1);
            return days.Contains(previousDay) && time < end;
        }
    }

    private sealed record State(bool Restricted, Rule[] Rules);
}
