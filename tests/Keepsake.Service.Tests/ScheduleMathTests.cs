using System;
using System.Collections.Generic;
using Keepsake.Core.Repo;
using Keepsake.Protocol;
using Xunit;

namespace Keepsake.Service.Tests
{
    public class ScheduleMathTests
    {
        private static ScheduleEntry Daily(params string[] times) => new ScheduleEntry { Kind = ScheduleKind.Daily, Times = new List<string>(times) };

        [Fact]
        public void Daily_picks_next_time_today_or_tomorrow()
        {
            var s = Daily("08:00", "22:00");
            Assert.Equal(new DateTime(2026, 10, 17, 22, 0, 0), ScheduleMath.Next(s, new DateTime(2026, 10, 17, 14, 0, 0)));
            Assert.Equal(new DateTime(2026, 10, 18, 8, 0, 0), ScheduleMath.Next(s, new DateTime(2026, 10, 17, 22, 0, 0)));
        }

        [Fact]
        public void Weekdays_are_respected()
        {
            var s = Daily("23:30");
            s.Weekdays = new List<DayOfWeek> { DayOfWeek.Sunday };
            // 2026-10-17 is a Saturday.
            Assert.Equal(new DateTime(2026, 10, 18, 23, 30, 0), ScheduleMath.Next(s, new DateTime(2026, 10, 17, 12, 0, 0)));
            Assert.Equal(new DateTime(2026, 10, 25, 23, 30, 0), ScheduleMath.Next(s, new DateTime(2026, 10, 18, 23, 30, 0)));
        }

        [Fact]
        public void Monthly_day_31_falls_back_to_last_day()
        {
            var s = new ScheduleEntry { Kind = ScheduleKind.Monthly, Times = { "01:00" }, DaysOfMonth = { 31 } };
            Assert.Equal(new DateTime(2026, 11, 30, 1, 0, 0), ScheduleMath.Next(s, new DateTime(2026, 11, 1)));
            Assert.Equal(new DateTime(2027, 2, 28, 1, 0, 0), ScheduleMath.Next(s, new DateTime(2027, 2, 1)));
            Assert.Equal(new DateTime(2026, 12, 31, 1, 0, 0), ScheduleMath.Next(s, new DateTime(2026, 11, 30, 1, 0, 0)));
        }

        [Fact]
        public void Interval_adds_minutes_and_disabled_never_runs()
        {
            var s = new ScheduleEntry { Kind = ScheduleKind.Interval, IntervalMinutes = 90 };
            var t = new DateTime(2026, 10, 17, 10, 0, 0);
            Assert.Equal(t.AddMinutes(90), ScheduleMath.Next(s, t));
            s.Enabled = false;
            Assert.Null(ScheduleMath.Next(s, t));
        }

        [Fact]
        public void Validation_and_description()
        {
            Assert.NotEmpty(ScheduleMath.Validate(Daily("25:00")));
            Assert.NotEmpty(ScheduleMath.Validate(new ScheduleEntry { Kind = ScheduleKind.Interval, IntervalMinutes = 1 }));
            Assert.Empty(ScheduleMath.Validate(Daily("7:05")));

            var s = Daily("22:00");
            s.Weekdays = new List<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Monday };
            s.Mode = BackupMode.Full;
            Assert.Equal("Mon, Sun at 22:00 (full)", ScheduleMath.Describe(s));
        }
    }
}
