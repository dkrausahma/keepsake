using System;
using System.Globalization;

namespace Keepsake.Core.Reporting
{
    public static class Format
    {
        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0
                ? bytes.ToString("N0", CultureInfo.InvariantCulture) + " B"
                : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        public static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalSeconds < 10)
                return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            if (t.TotalMinutes < 1)
                return (int)t.TotalSeconds + "s";
            if (t.TotalHours < 1)
                return t.Minutes + "m " + t.Seconds.ToString("00") + "s";
            return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
        }

        public static string LocalTime(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
