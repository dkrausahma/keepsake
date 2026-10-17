using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Keepsake.Core.Util;

namespace Keepsake.Core.Backup
{
    /// <summary>
    /// Maps Windows paths to the path inside a snapshot and back:
    /// <c>D:\Data\X</c> ↔ <c>D/Data/X</c>, <c>\\server\share\X</c> ↔ <c>UNC/server/share/X</c>.
    /// </summary>
    public static class RepoPaths
    {
        public const string UncRoot = "UNC";

        public static List<string> Split(string windowsPath)
        {
            string p = LongPath.Normalize(windowsPath);
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = p.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (parts.Count < 2)
                    throw new KeepsakeException("'" + windowsPath + "' is not a valid network path (\\\\server\\share\\...).");
                parts.Insert(0, UncRoot);
                return parts;
            }
            if (p.Length >= 2 && p[1] == ':')
            {
                var parts = p.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                parts.Insert(0, char.ToUpperInvariant(p[0]).ToString());
                return parts;
            }
            throw new KeepsakeException("'" + windowsPath + "' is not an absolute path.");
        }

        /// <summary>Accepts a snapshot path (<c>D/Data/X</c>, <c>/D/Data</c>) or a Windows path (<c>D:\Data\X</c>).</summary>
        public static List<string> Parse(string path)
        {
            if (path.Length >= 2 && path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal))
                return Split(path);
            return path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static string Join(IEnumerable<string> components) => string.Join("/", components);

        /// <summary>Original Windows location of a snapshot path. Volume roots return e.g. <c>D:\</c>.</summary>
        public static string ToWindows(IList<string> components)
        {
            if (components.Count == 0)
                throw new ArgumentException("Empty path.");
            if (components[0] == UncRoot)
            {
                if (components.Count < 3)
                    throw new KeepsakeException("'" + Join(components) + "' has no original location (incomplete network path).");
                return @"\\" + string.Join(@"\", components.Skip(1));
            }
            return components[0] + @":\" + string.Join(@"\", components.Skip(1));
        }

        /// <summary>Volume roots (<c>D</c>) and share roots (<c>UNC/server/share</c>) and anything above them.</summary>
        public static bool IsRootLevel(IList<string> components) =>
            components.Count == 0 || (components[0] == UncRoot ? components.Count <= 3 : components.Count <= 1);

        public static bool IsPrefix(IList<string> prefix, IList<string> path)
        {
            if (prefix.Count > path.Count)
                return false;
            for (int i = 0; i < prefix.Count; i++)
            {
                if (!string.Equals(prefix[i], path[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>Removes duplicates and sources contained in other sources.</summary>
        public static List<string> NormalizeSources(IEnumerable<string> sources)
        {
            var normalized = sources.Select(LongPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return normalized
                .Where(s => !normalized.Any(o => !string.Equals(o, s, StringComparison.OrdinalIgnoreCase) && LongPath.IsSameOrBelow(s, o)))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string Combine(string folder, IEnumerable<string> components) =>
            components.Aggregate(folder, Path.Combine);
    }
}
