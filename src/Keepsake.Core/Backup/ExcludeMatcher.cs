using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Keepsake.Core.Backup
{
    /// <summary>
    /// Exclusion patterns, case-insensitive:
    /// <list type="bullet">
    /// <item><c>*.tmp</c>, <c>~$*</c>, <c>Thumbs.db</c> – no separator: matches the file or folder name.</item>
    /// <item><c>cache\</c> – trailing separator: folders only.</item>
    /// <item><c>D:\Data\Temp</c>, <c>D:\Data\**\obj\</c> – with separator: matches the full path. <c>*</c> stays within one
    /// folder level, <c>**</c> spans levels. Relative path patterns like <c>bin\Debug</c> match at any depth.</item>
    /// </list>
    /// </summary>
    public sealed class ExcludeMatcher
    {
        public static readonly IReadOnlyList<string> Defaults = new[]
        {
            @"$RECYCLE.BIN\",
            @"System Volume Information\",
            "pagefile.sys",
            "hiberfil.sys",
            "swapfile.sys",
            "~$*",
        };

        private readonly List<Rule> _rules;

        public ExcludeMatcher(IEnumerable<string> patterns)
        {
            _rules = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Compile).ToList();
        }

        public IReadOnlyList<string> Patterns => _rules.Select(r => r.Pattern).ToList();

        /// <summary>Returns the first matching pattern, or null.</summary>
        public string Match(string fullPath, string name, bool isDirectory)
        {
            foreach (var r in _rules)
            {
                if (r.DirectoriesOnly && !isDirectory)
                    continue;
                if (r.Regex.IsMatch(r.FullPath ? fullPath : name))
                    return r.Pattern;
            }
            return null;
        }

        private static Rule Compile(string pattern)
        {
            string p = pattern.Trim().Replace('/', '\\');
            bool dirOnly = p.EndsWith(@"\", StringComparison.Ordinal);
            p = p.TrimEnd('\\');
            if (p.Length == 0)
                throw new KeepsakeException("Invalid exclusion pattern '" + pattern + "'.");
            bool fullPath = p.IndexOf('\\') >= 0;

            string regex = GlobToRegex(p, fullPath);
            if (fullPath)
            {
                bool anchored = (p.Length >= 2 && p[1] == ':') || p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("**", StringComparison.Ordinal);
                if (!anchored)
                    regex = @"(?:.*\\)?" + regex;
            }
            return new Rule
            {
                Pattern = pattern.Trim(),
                DirectoriesOnly = dirOnly,
                FullPath = fullPath,
                Regex = new Regex("^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            };
        }

        private static string GlobToRegex(string glob, bool fullPath)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    if (i + 2 < glob.Length && glob[i + 2] == '\\')
                    {
                        sb.Append(@"(?:.*\\)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                }
                else if (c == '*')
                {
                    sb.Append(fullPath ? @"[^\\]*" : ".*");
                }
                else if (c == '?')
                {
                    sb.Append(fullPath ? @"[^\\]" : ".");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            return sb.ToString();
        }

        private sealed class Rule
        {
            public string Pattern;
            public bool DirectoriesOnly;
            public bool FullPath;
            public Regex Regex;
        }
    }
}
