using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace Keepsake.Core.Tests
{
    /// <summary>
    /// Integration tests against a real SFTP server. They run when one is reachable, e.g.:
    /// <c>docker run -d --name keepsake-sftp -p 2222:22 atmoz/sftp:alpine kuser:Sftp-Pass-1:1001:1001:backups</c>
    /// Override with KEEPSAKE_TEST_SFTP=host:port:user:password.
    /// </summary>
    public class SftpStorageTests
    {
        private readonly ITestOutputHelper _output;

        public SftpStorageTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private SftpOptions Options(string path)
        {
            string spec = Environment.GetEnvironmentVariable("KEEPSAKE_TEST_SFTP") ?? "localhost:2222:kuser:Sftp-Pass-1";
            var parts = spec.Split(':');
            var o = new SftpOptions { Host = parts[0], Port = int.Parse(parts[1]), User = parts[2], Password = parts[3], Path = path };
            try
            {
                using (var tcp = new TcpClient())
                {
                    if (!tcp.ConnectAsync(o.Host, o.Port).Wait(1000))
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
            return o;
        }

        private SftpOptions Trusted(string path)
        {
            var o = Options(path);
            if (o == null)
                return null;
            try
            {
                new SftpStorage(o).Connect();
            }
            catch (HostKeyException ex)
            {
                o.HostKeyFingerprint = ex.Fingerprint;
            }
            return o;
        }

        [Fact]
        public void Unknown_and_changed_host_keys_are_refused()
        {
            var o = Options("/backups/hostkey");
            if (o == null)
            {
                _output.WriteLine("No SFTP server reachable – skipped.");
                return;
            }
            var unknown = Assert.Throws<HostKeyException>(() => new SftpStorage(o).Connect());
            Assert.False(unknown.Mismatch);
            Assert.StartsWith("SHA256:", unknown.Fingerprint);

            o.HostKeyFingerprint = "SHA256:somethingelse";
            var changed = Assert.Throws<HostKeyException>(() => new SftpStorage(o).Connect());
            Assert.True(changed.Mismatch);

            o.HostKeyFingerprint = unknown.Fingerprint;
            using (var s = new SftpStorage(o))
                s.Connect();
        }

        [Fact]
        public void Storage_operations_and_probe()
        {
            var o = Trusted("/backups/ops-" + Rand.HexId(4));
            if (o == null)
            {
                _output.WriteLine("No SFTP server reachable – skipped.");
                return;
            }
            using (var s = new SftpStorage(o))
            {
                byte[] data = TestRepo.RandomBytes(100_000, 1);
                s.Write("data/ab/abc", data);
                s.Write("data/ab/abc", data); // overwrite
                Assert.True(s.Exists("data/ab/abc"));
                Assert.Equal(data.Length, s.GetSize("data/ab/abc"));
                Assert.Equal(data, s.Read("data/ab/abc"));
                Assert.Equal(data.Skip(500).Take(1000).ToArray(), s.ReadRange("data/ab/abc", 500, 1000));
                Assert.Equal(new[] { "data/ab/abc" }, s.List("data").Select(e => e.Path).ToArray());
                s.Delete("data/ab/abc");
                Assert.False(s.Exists("data/ab/abc"));
                Assert.Throws<StorageException>(() => s.Read("missing"));

                var probe = StorageProbe.Test(s);
                Assert.True(probe.Reachable && probe.CanWrite && probe.CanDelete, string.Join("; ", probe.Problems));
            }
        }

        [Fact]
        public void Backup_and_restore_over_sftp()
        {
            var o = Trusted("/backups/repo-" + Rand.HexId(4));
            if (o == null)
            {
                _output.WriteLine("No SFTP server reachable – skipped.");
                return;
            }
            using (var local = new TestRepo())
            {
                local.File(@"a\one.bin", TestRepo.RandomBytes(300_000, 5));
                local.File(@"a\two.txt", "two");
                var init = Repository.Init(new SftpStorage(o), TestRepo.Password, TestRepo.SmallChunks());
                init.Repository.Dispose();

                using (var repo = Repository.Open(new SftpStorage(o), RepoCredential.FromPassword(TestRepo.Password)))
                {
                    var backup = new BackupEngine(repo).Run(new BackupOptions { Sources = { local.Source } }, null, CancellationToken.None);
                    Assert.Equal(RunStatus.Success, backup.Status);
                }
                using (var repo = Repository.Open(new SftpStorage(o), RepoCredential.FromRecoveryKeyText(init.RecoveryKey)))
                {
                    string target = Path.Combine(local.Root, "from-sftp");
                    var restore = new RestoreEngine(repo).Run(new RestoreOptions { TargetFolder = target }, null, CancellationToken.None);
                    Assert.Equal(RunStatus.Success, restore.Status);
                    Assert.Equal("two", File.ReadAllText(local.RestoredPath(target, @"a\two.txt")));
                }
            }
        }
    }
}
