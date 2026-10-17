using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Keepsake.Protocol
{
    /// <summary>Next-run calculation for schedules. All times are local times.</summary>
    public static class ScheduleMath
    {
        /// <summary>
        /// First occurrence strictly after <paramref name="after"/>. For interval schedules,
        /// <paramref name="after"/> is the time of the last run (or when the schedule was created).
        /// </summary>
        public static DateTime? Next(ScheduleEntry s, DateTime after)
        {
            if (s == null || !s.Enabled)
                return null;
            switch (s.Kind)
            {
                case ScheduleKind.Interval:
                    return s.IntervalMinutes > 0 ? after.AddMinutes(s.IntervalMinutes) : (DateTime?)null;

                case ScheduleKind.Daily:
                {
                    var times = ParseTimes(s.Times);
                    if (times.Count == 0)
                        return null;
                    var days = new HashSet<DayOfWeek>(s.Weekdays ?? new List<DayOfWeek>());
                    for (int d = 0; d <= 8; d++)
                    {
                        var day = after.Date.AddDays(d);
                        if (days.Count > 0 && !days.Contains(day.DayOfWeek))
                            continue;
                        foreach (var t in times)
                        {
                            var candidate = day + t;
                            if (candidate > after)
                                return candidate;
                        }
                    }
                    return null;
                }

                case ScheduleKind.Monthly:
                {
                    var times = ParseTimes(s.Times);
                    var daysOfMonth = (s.DaysOfMonth ?? new List<int>()).Where(d => d >= 1 && d <= 31).Distinct().OrderBy(d => d).ToList();
                    if (times.Count == 0 || daysOfMonth.Count == 0)
                        return null;
                    var month = new DateTime(after.Year, after.Month, 1);
                    for (int m = 0; m <= 13; m++, month = month.AddMonths(1))
                    {
                        int length = DateTime.DaysInMonth(month.Year, month.Month);
                        foreach (int day in daysOfMonth.Select(d => Math.Min(d, length)).Distinct())
                        {
                            foreach (var t in times)
                            {
                                var candidate = month.AddDays(day - 1) + t;
                                if (candidate > after)
                                    return candidate;
                            }
                        }
                    }
                    return null;
                }

                default:
                    return null;
            }
        }

        public static List<string> Validate(ScheduleEntry s)
        {
            var errors = new List<string>();
            switch (s.Kind)
            {
                case ScheduleKind.Interval:
                    if (s.IntervalMinutes < 5 || s.IntervalMinutes > 7 * 24 * 60)
                        errors.Add("Interval schedules must run every 5 minutes to 7 days.");
                    break;
                case ScheduleKind.Daily:
                case ScheduleKind.Monthly:
                    if (s.Times == null || s.Times.Count == 0)
                        errors.Add("A " + s.Kind.ToString().ToLowerInvariant() + " schedule needs at least one time.");
                    foreach (string t in s.Times ?? new List<string>())
                    {
                        if (!TryParseTime(t, out _))
                            errors.Add("'" + t + "' is not a valid time (use HH:mm).");
                    }
                    if (s.Kind == ScheduleKind.Monthly && (s.DaysOfMonth == null || s.DaysOfMonth.Count == 0 || s.DaysOfMonth.Any(d => d < 1 || d > 31)))
                        errors.Add("A monthly schedule needs days between 1 and 31.");
                    break;
            }
            return errors;
        }

        public static string Describe(ScheduleEntry s)
        {
            string mode = s.Mode == Core.Repo.BackupMode.Full ? "full" : "incremental";
            string text;
            switch (s.Kind)
            {
                case ScheduleKind.Interval:
                    text = s.IntervalMinutes % 60 == 0 ? "every " + (s.IntervalMinutes / 60) + " h" : "every " + s.IntervalMinutes + " min";
                    break;
                case ScheduleKind.Daily:
                    string days = s.Weekdays == null || s.Weekdays.Count == 0 || s.Weekdays.Count == 7
                        ? "daily"
                        : string.Join(", ", s.Weekdays.OrderBy(d => ((int)d + 6) % 7).Select(d => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(d)));
                    text = days + " at " + string.Join(", ", s.Times ?? new List<string>());
                    break;
                case ScheduleKind.Monthly:
                    text = "monthly on day " + string.Join(", ", s.DaysOfMonth ?? new List<int>()) + " at " + string.Join(", ", s.Times ?? new List<string>());
                    break;
                default:
                    text = s.Kind.ToString();
                    break;
            }
            return text + " (" + mode + ")" + (s.Enabled ? "" : " – disabled");
        }

        public static bool TryParseTime(string text, out TimeSpan time) =>
            TimeSpan.TryParseExact((text ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time)
            && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

        private static List<TimeSpan> ParseTimes(IEnumerable<string> times) =>
            (times ?? Enumerable.Empty<string>())
                .Select(t => TryParseTime(t, out var ts) ? ts : (TimeSpan?)null)
                .Where(t => t.HasValue)
                .Select(t => t.Value)
                .Distinct()
                .OrderBy(t => t)
                .ToList();
    }
}
