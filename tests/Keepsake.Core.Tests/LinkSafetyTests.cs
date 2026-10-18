using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;
using Xunit;

namespace Keepsake.Core.Tests
{
    /// <summary>Junctions must never redirect restores or repository maintenance (SEC-003, SEC-004).</summary>
    public class LinkSafetyTests
    {
        private static void Junction(string link, string target)
        {
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
        }

        [Fact]
        public void Restore_to_original_location_does_not_follow_a_junction_planted_in_the_source()
        {
            using (var t = new TestRepo())
            {
                t.File(@"user\data.txt", "user content");
                t.Backup();
                string elsewhere = Path.Combine(t.Root, "elsewhere");
                Directory.CreateDirectory(elsewhere);
                Directory.Delete(Path.Combine(t.Source, "user"), true);
                Junction(Path.Combine(t.Source, "user"), elsewhere);

                t.Reopen();
                var result = new RestoreEngine(t.Repo).Run(new RestoreOptions { Target = RestoreTargetMode.Original }, null, CancellationToken.None);

                Assert.NotEqual(RunStatus.Success, result.Status);
                Assert.Contains(result.Issues, i => i.Message.Contains("junction or symbolic link"));
                Assert.False(File.Exists(Path.Combine(elsewhere, "data.txt")));
                Directory.Delete(Path.Combine(t.Source, "user")); // the junction only
            }
        }

        [Fact]
        public void Restore_to_folder_does_not_follow_a_junction_inside_the_target()
        {
            using (var t = new TestRepo())
            {
                t.File(@"sub\file.txt", "content");
                t.Backup();
                string target = Path.Combine(t.Root, "target");
                string elsewhere = Path.Combine(t.Root, "elsewhere");
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(elsewhere);
                Junction(Path.Combine(target, "sub"), elsewhere);

                var result = t.RestoreTo(target, new RestoreOptions { KeepFullPath = false, Includes = { Path.Combine(t.Source, "sub") } });

                Assert.NotEqual(RunStatus.Success, result.Status);
                Assert.Contains(result.Issues, i => i.Message.Contains("junction or symbolic link"));
                Assert.False(File.Exists(Path.Combine(elsewhere, "file.txt")));
                Directory.Delete(Path.Combine(target, "sub"));
            }
        }

        [Fact]
        public void Folder_storage_does_not_list_or_delete_through_junctions()
        {
            string root = Path.Combine(Path.GetTempPath(), "keepsake-fs-" + Util.Rand.HexId(6));
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "cdvictim"), "must survive");
            try
            {
                var storage = new FileSystemStorage(Path.Combine(root, "repo"));
                storage.Write("data/ab/abc", new byte[] { 1 });
                Junction(Path.Combine(root, "repo", "data", "cd"), outside);

                Assert.Equal(new[] { "data/ab/abc" }, storage.List("data").Select(e => e.Path).ToArray());
                Assert.Throws<StorageException>(() => storage.Delete("data/cd/cdvictim"));
                Assert.Throws<StorageException>(() => storage.Write("data/cd/new", new byte[] { 2 }));
                Assert.Throws<StorageException>(() => storage.Delete("../outside/cdvictim"));
                Assert.True(File.Exists(Path.Combine(outside, "cdvictim")));
                Assert.False(File.Exists(Path.Combine(outside, "new")));

                Directory.Delete(Path.Combine(root, "repo", "data", "cd"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
