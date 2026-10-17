using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Keepsake.Core.Maintenance;
using Keepsake.Core.Reporting;
using Keepsake.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Keepsake.Service.Tests
{
    public class MaintenanceServiceTests
    {
        private readonly ITestOutputHelper _output;

        public MaintenanceServiceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Cleanup_and_check_are_queued_after_a_backup_and_only_when_due()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.Retention = new RetentionPolicy { KeepLast = 1 };
                job.UseVss = false;
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });

                var first = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                Assert.Equal(RunState.Success, first.State);
                var runs = WaitIdle(t, client, saved.Job.Id);
                Assert.Contains(runs, r => r.Kind == RunKind.Prune && r.State == RunState.Success);
                Assert.Contains(runs, r => r.Kind == RunKind.Check && r.State == RunState.Success);

                // Second backup the same day: maintenance is not due again.
                File.WriteAllText(Path.Combine(t.Source, "new.txt"), "new");
                t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                var after = WaitIdle(t, client, saved.Job.Id);
                Assert.Equal(1, after.Count(r => r.Kind == RunKind.Prune));
                Assert.Equal(1, after.Count(r => r.Kind == RunKind.Check));

                // Manual cleanup applies KeepLast = 1.
                var prune = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id, Kind = RunKind.Prune }).RunId);
                Assert.Equal(RunState.Success, prune.State);
                Assert.Single(client.Call<List<SnapshotInfo>>(Methods.SnapshotsList, new IdParams { Id = saved.Job.Id }));
            }
        }

        [Fact]
        public void Retention_preview_and_protect()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.UseVss = false;
                job.CheckEveryDays = 0;
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });
                for (int i = 0; i < 3; i++)
                {
                    File.WriteAllText(Path.Combine(t.Source, "v.txt"), "version " + i);
                    t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                }
                var snapshots = client.Call<List<SnapshotInfo>>(Methods.SnapshotsList, new IdParams { Id = saved.Job.Id });
                var oldest = snapshots.OrderBy(s => s.Time).First();
                client.Call<IdParams>(Methods.SnapshotsProtect, new ProtectParams { JobId = saved.Job.Id, SnapshotId = oldest.Id, Protect = true });

                var preview = client.Call<List<RetentionPreviewItem>>(Methods.RetentionPreview,
                    new RetentionPreviewParams { JobId = saved.Job.Id, Policy = new RetentionPolicy { KeepLast = 1 } });
                Assert.Equal(3, preview.Count);
                Assert.Equal(1, preview.Count(p => !p.Keep));
                Assert.Contains(preview, p => p.Keep && p.Reasons.Contains("protected"));
            }
        }

        [Fact]
        public void Vss_without_elevation_falls_back_with_a_warning()
        {
            if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                _output.WriteLine("Elevated: VSS would really be used – fallback test skipped.");
                return;
            }
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.UseVss = true;
                job.CheckEveryDays = 0;
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });
                var run = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);
                Assert.Equal(RunState.Warning, run.State);
                Assert.Equal(5, run.Summary.FilesNew);
                var detail = client.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = run.Id });
                Assert.Contains(detail.Issues, i => i.Kind == IssueKind.Warning && i.Message.Contains("VSS"));
            }
        }

        [Fact]
        public void Failure_mail_is_sent_through_smtp()
        {
            // Needs: docker run -d --name keepsake-mail -p 1025:1025 -p 8025:8025 axllent/mailpit
            if (!Reachable(1025))
            {
                _output.WriteLine("No SMTP test server on localhost:1025 – skipped.");
                return;
            }
            using (var web = new WebClient())
                web.UploadString("http://localhost:8025/api/v1/messages", "DELETE", "");

            using (var t = new TestService())
            using (var client = t.Client())
            {
                client.Call(Methods.SmtpSave, new SmtpSettings { Host = "localhost", Port = 1025, Security = "none", From = "keepsake@test.local", To = { "admin@test.local" } });
                client.Call(Methods.SmtpTest);

                var job = t.NewJob("Mail job");
                job.PreCommand = "exit 3";
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });
                Assert.Equal(RunState.Failed, t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId).State);
                Thread.Sleep(1500);

                string inbox;
                using (var web = new WebClient())
                    inbox = web.DownloadString("http://localhost:8025/api/v1/messages");
                Assert.Contains("Test message", inbox);
                Assert.Contains("FAILED: Mail job", inbox);
            }
        }

        private static List<RunRecord> WaitIdle(TestService t, KeepsakeClient client, string jobId)
        {
            for (int i = 0; i < 100; i++)
            {
                if (!t.Ctx.Runner.IsBusy(jobId))
                    break;
                Thread.Sleep(100);
            }
            Thread.Sleep(200);
            return client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = jobId, Limit = 50 });
        }

        private static bool Reachable(int port)
        {
            try
            {
                using (var tcp = new TcpClient())
                    return tcp.ConnectAsync("localhost", port).Wait(1000);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
