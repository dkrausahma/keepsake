using System;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;
using Xunit;

namespace Keepsake.Core.Tests
{
    public class BackupRestoreTests
    {
        [Fact]
        public void Round_trip_preserves_content_times_attributes_and_structure()
        {
            using (var t = new TestRepo())
            {
                var big = TestRepo.RandomBytes(700_000, 10);
                t.File("big.bin", big);
                t.File("text.txt", string.Concat(Enumerable.Repeat("line of text\r\n", 20000)));
                t.File(@"sub\empty.txt", new byte[0]);
                t.File(@"sub\Ünïcödé – файл.txt", "unicode");
                string readOnly = t.File(@"sub\readonly.txt", "ro");
                File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);
                Directory.CreateDirectory(Path.Combine(t.Source, "empty folder"));
                var mtime = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Path.Combine(t.Source, "big.bin"), mtime);

                var backup = t.Backup();
                Assert.Equal(RunStatus.Success, backup.Status);
                Assert.Equal(5, backup.Summary.FilesNew);
                Assert.True(backup.Summary.BytesStored < backup.Summary.BytesRead, "Text should compress.");

                string target = Path.Combine(t.Root, "restore");
                var restore = t.RestoreTo(target);
                Assert.Equal(RunStatus.Success, restore.Status);
                Assert.Equal(5, restore.Stats.FilesRestored);

                Assert.Equal(big, File.ReadAllBytes(t.RestoredPath(target, "big.bin")));
                Assert.Equal(mtime, File.GetLastWriteTimeUtc(t.RestoredPath(target, "big.bin")));
                Assert.Equal("unicode", File.ReadAllText(t.RestoredPath(target, @"sub\Ünïcödé – файл.txt")));
                Assert.Empty(File.ReadAllBytes(t.RestoredPath(target, @"sub\empty.txt")));
                var attrs = File.GetAttributes(t.RestoredPath(target, @"sub\readonly.txt"));
                Assert.True(attrs.HasFlag(FileAttributes.ReadOnly) && attrs.HasFlag(FileAttributes.Hidden));
                Assert.True(Directory.Exists(t.RestoredPath(target, "empty folder")));
            }
        }

        [Fact]
        public void Paths_longer_than_260_characters_are_backed_up_and_restored()
        {
            using (var t = new TestRepo())
            {
                string deep = string.Join(@"\", Enumerable.Range(0, 12).Select(i => "folder-with-a-long-name-" + i));
                string relative = deep + @"\file.txt";
                t.File(relative, "deep");
                Assert.True(Path.Combine(t.Source, relative).Length > 300);

                var backup = t.Backup();
                Assert.Equal(RunStatus.Success, backup.Status);
                string target = Path.Combine(t.Root, "r");
                Assert.Equal(RunStatus.Success, t.RestoreTo(target).Status);
                Assert.Equal("deep", File.ReadAllText(LongPath.ToExtended(t.RestoredPath(target, relative))));
            }
        }

        [Fact]
        public void Incremental_backup_reads_only_changed_files()
        {
            using (var t = new TestRepo())
            {
                t.File("a.bin", TestRepo.RandomBytes(300_000, 1));
                t.File("b.bin", TestRepo.RandomBytes(300_000, 2));
                var first = t.Backup();

                var second = t.Backup();
                Assert.NotNull(first.SnapshotId);
                Assert.Equal(first.SnapshotId, second.ParentId);
                Assert.Equal(2, second.Summary.FilesUnchanged);
                Assert.Equal(0, second.Summary.BytesRead);
                Assert.Equal(0, second.Summary.BytesAdded);

                t.File("a.bin", TestRepo.RandomBytes(300_000, 3));
                var third = t.Backup();
                Assert.Equal(1, third.Summary.FilesChanged);
                Assert.Equal(300_000, third.Summary.BytesRead);
            }
        }

        [Fact]
        public void Full_backup_rereads_everything_but_stores_nothing_new()
        {
            using (var t = new TestRepo())
            {
                t.File("a.bin", TestRepo.RandomBytes(400_000, 1));
                t.Backup();
                var full = t.Backup(BackupMode.Full);
                Assert.Equal(400_000, full.Summary.BytesRead);
                Assert.Equal(0, full.Summary.BytesAdded);
                Assert.Equal(BackupMode.Full, t.Repo.FindSnapshot(full.SnapshotId).Snapshot.Mode);
            }
        }

        [Fact]
        public void Identical_content_is_stored_once()
        {
            using (var t = new TestRepo())
            {
                var data = TestRepo.RandomBytes(500_000, 7);
                t.File("one.bin", data);
                t.File(@"copy\two.bin", data);
                var result = t.Backup();
                Assert.InRange(result.Summary.BytesAdded, 500_000, 510_000);
            }
        }

        [Fact]
        public void Excluded_items_are_not_backed_up()
        {
            using (var t = new TestRepo())
            {
                t.File("keep.txt", "k");
                t.File("drop.tmp", "d");
                t.File(@"cache\x.txt", "c");
                var result = t.Backup(BackupMode.Incremental, "*.tmp", @"cache\");
                Assert.Equal(1, result.Summary.FilesNew);
                Assert.Equal(2, result.FilesExcluded);
            }
        }

        [Fact]
        public void Restore_selected_folder_flat_into_target()
        {
            using (var t = new TestRepo())
            {
                t.File(@"Finance\2026\q1.txt", "q1");
                t.File(@"Other\x.txt", "x");
                t.Backup();

                string target = Path.Combine(t.Root, "flat");
                var result = t.RestoreTo(target, new RestoreOptions { Includes = { Path.Combine(t.Source, "Finance") }, KeepFullPath = false });
                Assert.Equal(RunStatus.Success, result.Status);
                Assert.Equal(1, result.Stats.FilesRestored);
                Assert.Equal("q1", File.ReadAllText(Path.Combine(target, @"Finance\2026\q1.txt")));
                Assert.False(Directory.Exists(Path.Combine(target, "Other")));
            }
        }

        [Fact]
        public void Restore_rejects_paths_that_are_not_in_the_snapshot()
        {
            using (var t = new TestRepo())
            {
                t.File("a.txt", "a");
                t.Backup();
                var result = t.RestoreTo(Path.Combine(t.Root, "x"), new RestoreOptions { Includes = { Path.Combine(t.Source, "missing") } });
                Assert.Equal(RunStatus.Failed, result.Status);
            }
        }

        [Theory]
        [InlineData(ConflictPolicy.Skip, "current", false)]
        [InlineData(ConflictPolicy.Overwrite, "backed up", false)]
        [InlineData(ConflictPolicy.OverwriteIfNewer, "current", false)]
        [InlineData(ConflictPolicy.Rename, "current", true)]
        public void Restore_to_original_location_honours_conflict_policy(ConflictPolicy policy, string expected, bool renamedCopy)
        {
            using (var t = new TestRepo())
            {
                string file = t.File("doc.txt", "backed up");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
                t.Backup();
                File.WriteAllText(file, "current");

                t.Reopen();
                var result = new RestoreEngine(t.Repo).Run(new RestoreOptions { Target = RestoreTargetMode.Original, Conflict = policy }, null, CancellationToken.None);
                Assert.NotEqual(RunStatus.Failed, result.Status);
                Assert.Equal(expected, File.ReadAllText(file));
                string copy = Path.Combine(t.Source, "doc (restored 1).txt");
                Assert.Equal(renamedCopy, File.Exists(copy));
                if (renamedCopy)
                    Assert.Equal("backed up", File.ReadAllText(copy));
            }
        }

        [Fact]
        public void Restore_recreates_deleted_files_at_original_location()
        {
            using (var t = new TestRepo())
            {
                string file = t.File(@"deleted\gone.txt", "please come back");
                t.Backup();
                Directory.Delete(Path.Combine(t.Source, "deleted"), true);

                t.Reopen();
                var result = new RestoreEngine(t.Repo).Run(new RestoreOptions { Target = RestoreTargetMode.Original }, null, CancellationToken.None);
                Assert.Equal(RunStatus.Success, result.Status);
                Assert.Equal("please come back", File.ReadAllText(file));
            }
        }

        [Fact]
        public void Dry_run_writes_nothing()
        {
            using (var t = new TestRepo())
            {
                t.File("a.txt", "a");
                t.Backup();
                string target = Path.Combine(t.Root, "dry");
                var result = t.RestoreTo(target, new RestoreOptions { DryRun = true });
                Assert.Equal(1, result.Stats.FilesRestored);
                Assert.False(Directory.Exists(target));
            }
        }

        [Fact]
        public void Locked_file_is_reported_and_the_rest_is_backed_up()
        {
            using (var t = new TestRepo())
            {
                t.File("ok.txt", "fine");
                string locked = t.File("locked.txt", "busy");
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var result = t.Backup();
                    Assert.Equal(RunStatus.Warning, result.Status);
                    Assert.Equal(1, result.Summary.FilesNew);
                    Assert.Equal(1, result.Summary.FilesSkipped);
                    var issue = Assert.Single(result.Issues, i => i.Kind == IssueKind.Error);
                    Assert.EndsWith("locked.txt", issue.Path);
                    Assert.Contains("locked", issue.Message);
                }
            }
        }

        [Fact]
        public void Missing_source_fails_without_snapshot()
        {
            using (var t = new TestRepo())
            {
                t.Reopen();
                var result = new BackupEngine(t.Repo).Run(new BackupOptions { Sources = { Path.Combine(t.Root, "does-not-exist") } }, null, CancellationToken.None);
                Assert.Equal(RunStatus.Failed, result.Status);
                Assert.Null(result.SnapshotId);
                Assert.Empty(t.Repo.ListSnapshots());
            }
        }

        [Fact]
        public void Cancelled_backup_keeps_uploaded_data_for_the_next_run()
        {
            using (var t = new TestRepo())
            {
                for (int i = 0; i < 20; i++)
                    t.File("file" + i + ".bin", TestRepo.RandomBytes(200_000, 100 + i));

                var cts = new CancellationTokenSource();
                // Cancel as soon as the third pack has been written.
                var storage = new CancellingStorage(new FileSystemStorage(t.RepoPath), cts, packsBeforeCancel: 3);
                var repo = Repository.Open(storage, RepoCredential.FromPassword(TestRepo.Password));
                var options = new BackupOptions { Sources = { t.Source }, Job = "test", Workers = 1 };
                var cancelled = new BackupEngine(repo).Run(options, null, cts.Token);
                Assert.Equal(RunStatus.Cancelled, cancelled.Status);
                Assert.Null(cancelled.SnapshotId);
                Assert.True(cancelled.Summary.BytesStored > 0);

                var resumed = t.Backup();
                Assert.Equal(RunStatus.Success, resumed.Status);
                Assert.True(resumed.Summary.BytesAdded < 20 * 200_000, "Data stored by the cancelled run must be reused.");

                string target = Path.Combine(t.Root, "after-resume");
                Assert.Equal(RunStatus.Success, t.RestoreTo(target).Status);
                Assert.Equal(TestRepo.RandomBytes(200_000, 119), File.ReadAllBytes(t.RestoredPath(target, "file19.bin")));
            }
        }

        private sealed class CancellingStorage : IStorage
        {
            private readonly IStorage _inner;
            private readonly CancellationTokenSource _cts;
            private int _remaining;

            public CancellingStorage(IStorage inner, CancellationTokenSource cts, int packsBeforeCancel)
            {
                _inner = inner;
                _cts = cts;
                _remaining = packsBeforeCancel;
            }

            public string Location => _inner.Location;
            public bool Exists(string path) => _inner.Exists(path);
            public long GetSize(string path) => _inner.GetSize(path);
            public byte[] Read(string path) => _inner.Read(path);
            public byte[] ReadRange(string path, long offset, int length) => _inner.ReadRange(path, offset, length);
            public void Delete(string path) => _inner.Delete(path);
            public System.Collections.Generic.IEnumerable<StorageEntry> List(string directory) => _inner.List(directory);
            public void Dispose() => _inner.Dispose();

            public void Write(string path, byte[] data)
            {
                _inner.Write(path, data);
                if (path.StartsWith(Layout.Data + "/", StringComparison.Ordinal) && Interlocked.Decrement(ref _remaining) == 0)
                    _cts.Cancel();
            }
        }
    }
}
