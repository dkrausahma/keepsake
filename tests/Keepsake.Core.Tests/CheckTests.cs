using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Check;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Xunit;

namespace Keepsake.Core.Tests
{
    public class CheckTests
    {
        private static CheckResult Check(TestRepo t, bool readData)
        {
            t.Reopen();
            return new Checker(t.Repo).Run(new CheckOptions { ReadData = readData }, null, CancellationToken.None);
        }

        private static TestRepo WithBackup()
        {
            var t = new TestRepo();
            for (int i = 0; i < 5; i++)
                t.File("f" + i + ".bin", TestRepo.RandomBytes(150_000, i));
            t.Backup();
            return t;
        }

        private static string[] Packs(TestRepo t) =>
            Directory.GetFiles(Path.Combine(t.RepoPath, Layout.Data), "*", SearchOption.AllDirectories);

        [Fact]
        public void Healthy_repository_passes_full_check()
        {
            using (var t = WithBackup())
            {
                var result = Check(t, readData: true);
                Assert.Equal(RunStatus.Success, result.Status);
                Assert.Equal(Packs(t).Length, result.PacksVerified);
            }
        }

        [Fact]
        public void Missing_pack_is_an_error()
        {
            using (var t = WithBackup())
            {
                File.Delete(Packs(t).First());
                var result = Check(t, readData: false);
                Assert.Equal(RunStatus.Failed, result.Status);
                Assert.Contains(result.Issues, i => i.Message.Contains("missing"));
            }
        }

        [Fact]
        public void Flipped_bit_is_found_by_read_data()
        {
            using (var t = WithBackup())
            {
                string pack = Packs(t).OrderByDescending(p => new FileInfo(p).Length).First();
                byte[] data = File.ReadAllBytes(pack);
                data[data.Length / 2] ^= 0x01;
                File.WriteAllBytes(pack, data);

                Assert.Equal(RunStatus.Success, Check(t, readData: false).Status);
                var deep = Check(t, readData: true);
                Assert.Equal(RunStatus.Failed, deep.Status);
                Assert.Contains(deep.Issues, i => i.Message.Contains("damaged"));
            }
        }

        [Fact]
        public void Corrupted_data_fails_restore_of_that_file_only()
        {
            using (var t = new TestRepo())
            {
                t.File("a.bin", TestRepo.RandomBytes(100_000, 1));
                t.Backup();
                t.File("b.bin", TestRepo.RandomBytes(100_000, 2));
                t.Backup();

                t.Reopen();
                t.Repo.LoadIndex();
                var snapshot = t.Repo.FindSnapshot("latest");
                // Corrupt the pack that holds b.bin's first block.
                var tree = t.Repo.LoadTree(snapshot.Snapshot.Tree);
                var node = FindFile(t.Repo, tree, "b.bin");
                t.Repo.Index.TryGet(node.Content[0], out var loc);
                string packFile = Path.Combine(t.RepoPath, Layout.PackPath(loc.PackId).Replace('/', '\\'));
                byte[] data = File.ReadAllBytes(packFile);
                data[loc.Offset + 20] ^= 0xFF;
                File.WriteAllBytes(packFile, data);

                string target = Path.Combine(t.Root, "r");
                var result = t.RestoreTo(target);
                Assert.Equal(RunStatus.Warning, result.Status);
                Assert.Equal(1, result.Stats.FilesFailed);
                Assert.True(File.Exists(t.RestoredPath(target, "a.bin")));
                Assert.False(File.Exists(t.RestoredPath(target, "b.bin")));
            }
        }

        [Fact]
        public void Unreferenced_pack_is_a_warning()
        {
            using (var t = WithBackup())
            {
                string orphan = Path.Combine(t.RepoPath, "data", "ab", "ab" + new string('0', 62));
                Directory.CreateDirectory(Path.GetDirectoryName(orphan));
                File.WriteAllBytes(orphan, new byte[] { 1, 2, 3 });
                var result = Check(t, readData: false);
                Assert.Equal(RunStatus.Warning, result.Status);
            }
        }

        private static Node FindFile(Repository repo, Tree tree, string name)
        {
            foreach (var n in tree.Nodes)
            {
                if (n.Type == NodeType.File && n.Name == name)
                    return n;
                if (n.Type == NodeType.Dir)
                {
                    var found = FindFile(repo, repo.LoadTree(n.Subtree), name);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }
    }
}
