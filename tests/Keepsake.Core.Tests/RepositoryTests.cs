using System.IO;
using System.Linq;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;
using Xunit;

namespace Keepsake.Core.Tests
{
    public class RepositoryTests
    {
        [Fact]
        public void Opens_with_password_and_recovery_key_but_not_wrong_password()
        {
            using (var t = new TestRepo())
            {
                Assert.Equal(t.Repo.Keys.Master, t.Reopen().Keys.Master);
                Assert.Equal(t.Repo.Keys.Master, t.Reopen(RepoCredential.FromRecoveryKeyText(t.RecoveryKey)).Keys.Master);
                Assert.Throws<WrongCredentialException>(() => t.Reopen(RepoCredential.FromPassword("wrong password")));
            }
        }

        [Fact]
        public void Only_plain_metadata_is_readable_on_storage()
        {
            using (var t = new TestRepo())
            {
                t.File("secret-name.txt", "top secret content");
                t.Backup();
                foreach (var file in Directory.EnumerateFiles(t.RepoPath, "*", SearchOption.AllDirectories))
                {
                    string text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file));
                    Assert.DoesNotContain("secret-name", text);
                    Assert.DoesNotContain("top secret", text);
                }
            }
        }

        [Fact]
        public void Keys_can_be_added_and_removed_but_not_the_last()
        {
            using (var t = new TestRepo())
            {
                string id = t.Repo.AddPasswordKey("second password", "Second");
                Assert.Equal(3, t.Repo.ListKeys().Count);
                Assert.NotNull(t.Reopen(RepoCredential.FromPassword("second password")));

                foreach (var k in t.Repo.ListKeys().Where(k => k.Id != id))
                    t.Repo.RemoveKey(k.Id);
                Assert.Throws<KeepsakeException>(() => t.Repo.RemoveKey(id));
                Assert.Throws<WrongCredentialException>(() => t.Reopen(RepoCredential.FromPassword(TestRepo.Password)));
            }
        }

        [Fact]
        public void Init_refuses_existing_repository()
        {
            using (var t = new TestRepo())
                Assert.Throws<KeepsakeException>(() => Repository.Init(new FileSystemStorage(t.RepoPath), "x"));
        }

        [Fact]
        public void Newer_format_is_rejected_with_clear_message()
        {
            using (var t = new TestRepo())
            {
                string header = Path.Combine(t.RepoPath, Layout.Header);
                var h = Json.FromBytes<RepoHeader>(File.ReadAllBytes(header));
                h.Version = Repository.FormatVersion + 1;
                File.WriteAllBytes(header, Json.ToBytes(h, true));
                var ex = Assert.Throws<RepositoryFormatException>(() => t.Reopen());
                Assert.Contains("newer", ex.Message);
            }
        }

        [Fact]
        public void Exclusive_lock_blocks_others_and_stale_locks_are_ignored()
        {
            using (var t = new TestRepo())
            {
                using (RepoLock.Acquire(t.Repo, exclusive: false, purpose: "a"))
                using (RepoLock.Acquire(t.Repo, exclusive: false, purpose: "b"))
                {
                    Assert.Throws<RepositoryLockedException>(() => RepoLock.Acquire(t.Repo, exclusive: true, purpose: "prune"));
                }
                using (RepoLock.Acquire(t.Repo, exclusive: true, purpose: "prune"))
                    Assert.Throws<RepositoryLockedException>(() => RepoLock.Acquire(t.Repo, exclusive: false, purpose: "backup"));

                // Locks are removed on dispose.
                Assert.Empty(RepoLock.List(t.Repo));

                // A stale exclusive lock does not block.
                var stale = new LockInfo { Host = "old", Exclusive = true, Purpose = "crashed", Time = System.DateTime.UtcNow.AddHours(-2) };
                t.Repo.Storage.Write(Layout.LockPath(Keepsake.Core.Util.Rand.HexId(16)), t.Repo.EncryptObject(stale, "lock"));
                using (RepoLock.Acquire(t.Repo, exclusive: false, purpose: "backup"))
                {
                }
                Assert.Equal(1, RepoLock.RemoveLocks(t.Repo, all: false));
            }
        }
    }

    public class ExcludeTests
    {
        [Theory]
        [InlineData("*.tmp", @"D:\Data\a.tmp", "a.tmp", false, true)]
        [InlineData("*.tmp", @"D:\Data\a.tmp.txt", "a.tmp.txt", false, false)]
        [InlineData("~$*", @"D:\Data\~$report.docx", "~$report.docx", false, true)]
        [InlineData(@"cache\", @"D:\Data\cache", "cache", true, true)]
        [InlineData(@"cache\", @"D:\Data\cache", "cache", false, false)]
        [InlineData(@"D:\Data\Temp", @"D:\Data\Temp", "Temp", true, true)]
        [InlineData(@"D:\Data\Temp", @"D:\Data\Temp2", "Temp2", true, false)]
        [InlineData(@"D:\Data\*.log", @"D:\Data\x.log", "x.log", false, true)]
        [InlineData(@"D:\Data\*.log", @"D:\Data\sub\x.log", "x.log", false, false)]
        [InlineData(@"D:\Data\**\obj\", @"D:\Data\a\b\obj", "obj", true, true)]
        [InlineData(@"D:\Data\**\obj\", @"D:\Data\obj", "obj", true, true)]
        [InlineData(@"bin\Debug", @"D:\Src\App\bin\Debug", "Debug", true, true)]
        [InlineData(@"bin\Debug", @"D:\Src\App\bin\Debugger", "Debugger", true, false)]
        [InlineData("THUMBS.DB", @"D:\Pics\Thumbs.db", "Thumbs.db", false, true)]
        public void Patterns(string pattern, string path, string name, bool isDir, bool excluded)
        {
            var m = new ExcludeMatcher(new[] { pattern });
            Assert.Equal(excluded, m.Match(path, name, isDir) != null);
        }

        [Fact]
        public void Defaults_exclude_system_folders()
        {
            var m = new ExcludeMatcher(ExcludeMatcher.Defaults);
            Assert.NotNull(m.Match(@"D:\System Volume Information", "System Volume Information", true));
            Assert.NotNull(m.Match(@"D:\$RECYCLE.BIN", "$RECYCLE.BIN", true));
            Assert.Null(m.Match(@"D:\Data\report.docx", "report.docx", false));
        }
    }
}
