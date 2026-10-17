using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Check;
using Keepsake.Core.Maintenance;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Xunit;

namespace Keepsake.Core.Tests
{
    public class RetentionTests
    {
        private static SnapshotRef Snap(DateTime localTime, params string[] tags) =>
            new SnapshotRef(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                new Snapshot { Time = localTime.ToUniversalTime(), Tags = tags.ToList() });

        [Fact]
        public void Empty_policy_keeps_everything()
        {
            var snaps = Enumerable.Range(0, 5).Select(i => Snap(DateTime.Now.AddDays(-i))).ToList();
            Assert.All(Retention.Apply(snaps, new RetentionPolicy(), DateTime.UtcNow), d => Assert.True(d.Keep));
        }

        [Fact]
        public void Daily_keeps_newest_of_each_day()
        {
            var day = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Local);
            var snaps = new List<SnapshotRef>();
            for (int d = 0; d < 10; d++)
            {
                snaps.Add(Snap(day.AddDays(-d).AddHours(9)));
                snaps.Add(Snap(day.AddDays(-d).AddHours(21)));
            }
            var decisions = Retention.Apply(snaps, new RetentionPolicy { KeepDaily = 3 }, day.ToUniversalTime());
            var kept = decisions.Where(d => d.Keep).Select(d => d.Snapshot.Snapshot.Time.ToLocalTime()).ToList();
            Assert.Equal(new[] { day.AddHours(21), day.AddDays(-1).AddHours(21), day.AddDays(-2).AddHours(21) }, kept);
        }

        [Fact]
        public void Rules_combine_and_protected_is_always_kept()
        {
            var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Local);
            var snaps = new List<SnapshotRef>();
            for (int w = 0; w < 20; w++)
                snaps.Add(Snap(now.AddDays(-7 * w)));
            var oldProtected = Snap(now.AddYears(-3), Repository.ProtectedTag);
            snaps.Add(oldProtected);

            var decisions = Retention.Apply(snaps, new RetentionPolicy { KeepLast = 2, KeepWeekly = 4, KeepMonthly = 3 }, now.ToUniversalTime());
            var keptIds = decisions.Where(d => d.Keep).Select(d => d.Snapshot.Id).ToList();
            Assert.Contains(oldProtected.Id, keptIds);
            // 4 weekly (covers the last 2) + monthly extends further back.
            Assert.True(keptIds.Count >= 6 && keptIds.Count < snaps.Count, "kept " + keptIds.Count);
            Assert.Contains(decisions, d => d.Reasons.Any(r => r.StartsWith("monthly")));
        }

        [Fact]
        public void Within_days_keeps_recent()
        {
            var now = DateTime.UtcNow;
            var snaps = Enumerable.Range(0, 10).Select(i => Snap(now.ToLocalTime().AddDays(-i))).ToList();
            var kept = Retention.Apply(snaps, new RetentionPolicy { KeepWithinDays = 3 }, now).Count(d => d.Keep);
            Assert.Equal(4, kept); // today and the 3 days before (inclusive cutoff)
        }
    }

    public class PrunerTests
    {
        private static void Snapshots(TestRepo t, int count)
        {
            for (int i = 0; i < count; i++)
            {
                t.File("rolling.bin", TestRepo.RandomBytes(150_000, 1000 + i));
                t.File("stable.bin", TestRepo.RandomBytes(150_000, 7));
                t.Backup();
            }
        }

        private static PruneResult Prune(TestRepo t, PruneOptions options)
        {
            t.Reopen();
            return new Pruner(t.Repo).Run(options, null, CancellationToken.None);
        }

        [Fact]
        public void Prune_removes_old_snapshots_and_their_data_and_keeps_repository_valid()
        {
            using (var t = new TestRepo())
            {
                Snapshots(t, 5);
                long before = Directory.EnumerateFiles(t.RepoPath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

                var result = Prune(t, new PruneOptions { Policy = new RetentionPolicy { KeepLast = 2 }, RepackThreshold = 0.01 });
                Assert.Equal(RunStatus.Success, result.Status);
                Assert.Equal(3, result.SnapshotsRemoved.Count);
                Assert.True(result.BytesFreed > 300_000, "freed " + result.BytesFreed);

                long after = Directory.EnumerateFiles(t.RepoPath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                Assert.True(after < before - 300_000);

                t.Reopen();
                var check = new Checker(t.Repo).Run(new CheckOptions { ReadData = true }, null, CancellationToken.None);
                Assert.Equal(RunStatus.Success, check.Status);
                Assert.Equal(2, check.Snapshots);

                string target = Path.Combine(t.Root, "after-prune");
                Assert.Equal(RunStatus.Success, t.RestoreTo(target).Status);
                Assert.Equal(TestRepo.RandomBytes(150_000, 1004), File.ReadAllBytes(t.RestoredPath(target, "rolling.bin")));
            }
        }

        [Fact]
        public void Dry_run_changes_nothing()
        {
            using (var t = new TestRepo())
            {
                Snapshots(t, 3);
                var files = Directory.GetFiles(t.RepoPath, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList();
                var result = Prune(t, new PruneOptions { Policy = new RetentionPolicy { KeepLast = 1 }, DryRun = true });
                Assert.Equal(2, result.SnapshotsRemoved.Count);
                Assert.Equal(files, Directory.GetFiles(t.RepoPath, "*", SearchOption.AllDirectories).Where(f => !f.Contains(@"\locks\")).OrderBy(f => f).ToList());
            }
        }

        [Fact]
        public void Protected_snapshot_survives_and_stays_restorable()
        {
            using (var t = new TestRepo())
            {
                Snapshots(t, 3);
                t.Reopen();
                var first = t.Repo.ListSnapshots().First();
                var protectedRef = t.Repo.SetProtected(first, true);

                var result = Prune(t, new PruneOptions { Policy = new RetentionPolicy { KeepLast = 1 }, RepackThreshold = 0.01 });
                Assert.Single(result.SnapshotsRemoved);

                t.Reopen();
                var remaining = t.Repo.ListSnapshots();
                Assert.Contains(remaining, s => s.Id == protectedRef.Id);
                string target = Path.Combine(t.Root, "protected");
                Assert.Equal(RunStatus.Success, t.RestoreTo(target, new RestoreOptions { Snapshot = protectedRef.Id }).Status);
                Assert.Equal(TestRepo.RandomBytes(150_000, 1000), File.ReadAllBytes(t.RestoredPath(target, "rolling.bin")));
            }
        }

        [Fact]
        public void Leftovers_of_interrupted_backups_are_removed()
        {
            using (var t = new TestRepo())
            {
                Snapshots(t, 1);
                string orphan = Path.Combine(t.RepoPath, "data", "ff", "ff" + new string('1', 62));
                Directory.CreateDirectory(Path.GetDirectoryName(orphan));
                File.WriteAllBytes(orphan, new byte[1000]);
                var result = Prune(t, new PruneOptions());
                Assert.Empty(result.SnapshotsRemoved);
                Assert.False(File.Exists(orphan));
            }
        }

        [Fact]
        public void Repair_rebuilds_a_lost_index()
        {
            using (var t = new TestRepo())
            {
                Snapshots(t, 3);
                foreach (var f in Directory.GetFiles(Path.Combine(t.RepoPath, "index")))
                    File.Delete(f);

                t.Reopen();
                var repair = new IndexRebuilder(t.Repo).Run(null, CancellationToken.None);
                Assert.Equal(RunStatus.Success, repair.Status);
                Assert.True(repair.BlobsIndexed > 0);

                t.Reopen();
                Assert.Equal(RunStatus.Success, new Checker(t.Repo).Run(new CheckOptions { ReadData = true }, null, CancellationToken.None).Status);
                string target = Path.Combine(t.Root, "after-repair");
                Assert.Equal(RunStatus.Success, t.RestoreTo(target).Status);
            }
        }

        [Fact]
        public void Restore_tool_is_copied_once_per_version()
        {
            using (var t = new TestRepo())
            {
                string install = Path.Combine(t.Root, "install");
                Directory.CreateDirectory(install);
                File.WriteAllText(Path.Combine(install, "keepsake.exe"), "exe");
                File.WriteAllText(Path.Combine(install, "keepsake.exe.config"), "config");
                File.WriteAllText(Path.Combine(install, "Keepsake.Core.dll"), "dll");

                Assert.True(ToolCopy.Ensure(t.Repo.Storage, install, "9.9.9"));
                Assert.False(ToolCopy.Ensure(t.Repo.Storage, install, "9.9.9"));
                Assert.Equal("exe", File.ReadAllText(Path.Combine(t.RepoPath, "tools", "keepsake-9.9.9", "keepsake.exe")));
                Assert.True(File.Exists(Path.Combine(t.RepoPath, "tools", "keepsake-9.9.9", "Keepsake.Core.dll")));
                Assert.False(ToolCopy.Ensure(t.Repo.Storage, Path.Combine(t.Root, "no-cli-here"), "1.0.0"));

                // The tools folder does not disturb checks.
                t.Reopen();
                Assert.Equal(RunStatus.Success, new Checker(t.Repo).Run(new CheckOptions(), null, CancellationToken.None).Status);
            }
        }

        [Fact]
        public void Prune_needs_exclusive_access()
        {
            using (var t = new TestRepo())
            {
                Snapshots(t, 1);
                t.Reopen();
                using (RepoLock.Acquire(t.Repo, exclusive: false, purpose: "backup"))
                {
                    var result = new Pruner(t.Repo).Run(new PruneOptions(), null, CancellationToken.None);
                    Assert.Equal(RunStatus.Failed, result.Status);
                    Assert.Contains("in use", result.Error);
                }
            }
        }
    }
}
