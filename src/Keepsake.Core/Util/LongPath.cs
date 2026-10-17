using System;
using System.IO;

namespace Keepsake.Core.Util
{
    /// <summary>
    /// Converts between normal and extended-length (<c>\\?\</c>) Windows paths. Windows Server 2008 R2
    /// has no opt-in for paths over 260 characters, so every file system call uses the extended form.
    /// </summary>
    public static class LongPath
    {
        private const string Prefix = @"\\?\";
        private const string UncPrefix = @"\\?\UNC\";

        public static string ToExtended(string path)
        {
            if (path.StartsWith(Prefix, StringComparison.Ordinal))
                return path;
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return UncPrefix + full.Substring(2);
            return Prefix + full;
        }

        public static string ToNormal(string path)
        {
            if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(UncPrefix.Length);
            if (path.StartsWith(Prefix, StringComparison.Ordinal))
                return path.Substring(Prefix.Length);
            return path;
        }

        /// <summary>Full, normal-form path without a trailing separator (except for drive roots like <c>D:\</c>).</summary>
        public static string Normalize(string path)
        {
            string full = Path.GetFullPath(ToNormal(path));
            if (full.Length > 3 && full.EndsWith(@"\", StringComparison.Ordinal))
                full = full.TrimEnd('\\');
            return full;
        }

        /// <summary>True when <paramref name="child"/> equals or lies below <paramref name="parent"/> (case-insensitive).</summary>
        public static bool IsSameOrBelow(string child, string parent)
        {
            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase))
                return true;
            string p = parent.EndsWith(@"\", StringComparison.Ordinal) ? parent : parent + @"\";
            return child.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
    }
}
