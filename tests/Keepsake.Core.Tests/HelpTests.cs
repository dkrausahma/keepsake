using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Keepsake.Core.Tests
{
    /// <summary>
    /// The help: every page the GUI opens (F1, Help buttons) exists, and the generated site (src/Keepsake.Gui/help) is
    /// not older than docs/ – i.e. tools/help-docs/build.py was run after the last docs change.
    /// </summary>
    public class HelpTests
    {
        /// <summary>
        /// The repository: the first folder above the test binaries that holds Keepsake.slnx (release builds map source
        /// paths to /_/, so [CallerFilePath] cannot be used).
        /// </summary>
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Keepsake.slnx")))
                    return dir.FullName;
            }
            throw new DirectoryNotFoundException("Repository (Keepsake.slnx) not found above " + System.AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string HelpFolder => Path.Combine(RepoRoot(), "src", "Keepsake.Gui", "help");

        /// <summary>Help pages named in the GUI source: every string literal ending in ".html".</summary>
        private static List<string> ReferencedPages()
        {
            var literal = new Regex(@"""(?<page>[A-Za-z0-9/_-]+\.html)""");
            return Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Keepsake.Gui"), "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\help\"))
                .SelectMany(f => literal.Matches(File.ReadAllText(f)).Cast<Match>().Select(m => m.Groups["page"].Value))
                .Distinct().ToList();
        }

        [Fact]
        public void Every_page_the_gui_opens_exists()
        {
            var pages = ReferencedPages();
            Assert.True(pages.Count >= 10, "F1 pages of the GUI pages, job editor tabs and dialogs; found " + pages.Count);
            foreach (string page in pages)
                Assert.True(File.Exists(Path.Combine(HelpFolder, page.Replace('/', '\\'))), "missing help page " + page + ": run tools/help-docs/build.py");
        }

        [Fact]
        public void Generated_help_has_every_docs_page_and_is_up_to_date()
        {
            string docs = Path.Combine(RepoRoot(), "docs");
            foreach (string markdown in Directory.GetFiles(docs, "*.md", SearchOption.AllDirectories))
            {
                string relative = markdown.Substring(docs.Length + 1);
                if (relative == "REQUIREMENTS.md")
                    continue; // project document, excluded from the help (mkdocs.yml)
                string built = Path.Combine(HelpFolder, Path.ChangeExtension(relative, ".html"));
                Assert.True(File.Exists(built), relative + " has no help page: run tools/help-docs/build.py");
                Assert.True(File.GetLastWriteTimeUtc(built) >= File.GetLastWriteTimeUtc(markdown).AddSeconds(-2),
                    Path.ChangeExtension(relative, ".html") + " is older than " + relative + ": run tools/help-docs/build.py");
            }
        }

        [Fact]
        public void Help_loads_nothing_from_the_internet()
        {
            var external = new Regex(@"(?:src|href)=""https?://[^""]*(?:fonts\.googleapis|fonts\.gstatic|unpkg\.com|cdn\.jsdelivr)", RegexOptions.IgnoreCase);
            foreach (string file in Directory.GetFiles(HelpFolder, "*.html", SearchOption.AllDirectories))
                Assert.False(external.IsMatch(File.ReadAllText(file)), Path.GetFileName(file) + " loads from a CDN");
        }
    }
}
