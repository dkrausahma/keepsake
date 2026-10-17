using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Keepsake.Core.Util;
using Keepsake.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keepsake.Service.Tests
{
    public class DestinationTests
    {
        private readonly ITestOutputHelper _output;

        public DestinationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Usb_destination_is_found_by_volume_id()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                // Use the system volume as stand-in for a USB disk: what matters is the lookup by volume ID.
                var volumes = client.Call<List<VolumeInfo>>(Methods.VolumesList);
                var volume = volumes.First(v => t.Root.StartsWith(v.Drive, StringComparison.OrdinalIgnoreCase));
                string relative = Path.Combine(t.Root, "usb-repo").Substring(volume.Drive.Length);
                var job = t.NewJob("Usb");
                job.Destination = new Destination
                {
                    Type = DestinationTypes.Usb,
                    Path = relative,
                    Volumes = { new UsbVolume { VolumeId = "Volume{00000000-0000-0000-0000-000000000000}", Label = "OFFLINE" }, new UsbVolume { VolumeId = volume.VolumeId, Label = volume.Label } },
                };

                var p = new RepoParams { Destination = job.Destination, Secrets = new JobSecrets { RepositoryPassword = "usb password 1" } };
                var test = client.Call<RepoTestResult>(Methods.RepoTest, p);
                Assert.True(test.Reachable, string.Join("; ", test.Problems));
                Assert.False(test.Exists);
                Assert.StartsWith(volume.Drive, test.Location, StringComparison.OrdinalIgnoreCase);
                client.Call<RepoInitResult>(Methods.RepoInit, p);

                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, Secrets = new JobSecrets { RepositoryPassword = "usb password 1" } });
                var run = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                Assert.Equal(RunState.Success, run.State);
                Assert.True(File.Exists(Path.Combine(t.Root, "usb-repo", "keepsake.json")));

                // Only an unknown disk configured: clear error.
                job.Id = saved.Job.Id;
                job.Destination.Volumes.RemoveAt(1);
                client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job });
                var failed = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                Assert.Equal(RunState.Failed, failed.State);
                Assert.Contains("OFFLINE", failed.Message);
            }
        }

        [Fact]
        public void Secrets_are_kept_replaced_and_removed()
        {
            string root = Path.Combine(Path.GetTempPath(), "keepsake-store-" + Rand.HexId(4));
            Directory.CreateDirectory(root);
            try
            {
                var store = new JobStore(Path.Combine(root, "jobs.json"));
                var job = new JobDefinition
                {
                    Name = "S",
                    Sources = { @"C:\x" },
                    Destination = new Destination { Type = DestinationTypes.Sftp, Host = "h", User = "u", Path = "/p" },
                    ShareCredentials = { new ShareCredential { Share = @"\\srv\data", User = @"DOM\backup" } },
                };
                var saved = store.Save(job, null, new JobSecrets
                {
                    RepositoryPassword = "repo",
                    SftpPassword = "sftp",
                    SharePasswords = { [@"\\srv\data"] = "share" },
                });
                var s = store.GetSecrets(saved.Id);
                Assert.Equal("repo", s.RepositoryPassword);
                Assert.Equal("sftp", s.SftpPassword);
                Assert.Equal("share", s.SharePasswords[@"\\srv\data"]);

                // Null keeps, empty removes.
                store.Save(saved, null, new JobSecrets { SftpPassword = "" });
                s = store.GetSecrets(saved.Id);
                Assert.Equal("repo", s.RepositoryPassword);
                Assert.Null(s.SftpPassword);
                Assert.Equal("share", s.SharePasswords[@"\\srv\data"]);

                // Removing the share drops its password.
                saved.ShareCredentials.Clear();
                store.Save(saved, null, null);
                Assert.Empty(new JobStore(Path.Combine(root, "jobs.json")).GetSecrets(saved.Id).SharePasswords);
                Assert.DoesNotContain("share", File.ReadAllText(Path.Combine(root, "jobs.json")).Replace("shareCredentials", "").Replace("SharePasswords", ""));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void Sftp_job_through_the_service_with_host_key_confirmation()
        {
            if (!Reachable("localhost", 2222))
            {
                _output.WriteLine("No SFTP server on localhost:2222 – skipped.");
                return;
            }
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob("Sftp");
                job.Destination = new Destination { Type = DestinationTypes.Sftp, Host = "localhost", Port = 2222, User = "kuser", Path = "/backups/svc-" + Rand.HexId(4) };
                var secrets = new JobSecrets { RepositoryPassword = "sftp repo pw 1", SftpPassword = "Sftp-Pass-1" };

                var first = client.Call<RepoTestResult>(Methods.RepoTest, new RepoParams { Destination = job.Destination, Secrets = secrets });
                Assert.NotNull(first.UntrustedHostKey);
                Assert.False(first.HostKeyChanged);

                job.Destination.HostKeyFingerprint = first.UntrustedHostKey;
                var second = client.Call<RepoTestResult>(Methods.RepoTest, new RepoParams { Destination = job.Destination, Secrets = secrets });
                Assert.True(second.Reachable && second.CanWrite && second.CanDelete, string.Join("; ", second.Problems));
                client.Call<RepoInitResult>(Methods.RepoInit, new RepoParams { Destination = job.Destination, Secrets = secrets });

                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, Secrets = secrets });
                Assert.True(saved.HasSftpPassword);
                var run = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                Assert.Equal(RunState.Success, run.State);

                // Editing later without re-entering the SFTP password: test uses the stored one.
                var again = client.Call<RepoTestResult>(Methods.RepoTest, new RepoParams { JobId = saved.Job.Id, Destination = saved.Job.Destination });
                Assert.True(again.Exists);
                Assert.Equal(1, again.Snapshots);
            }
        }

        private static bool Reachable(string host, int port)
        {
            try
            {
                using (var tcp = new TcpClient())
                    return tcp.ConnectAsync(host, port).Wait(1000);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
