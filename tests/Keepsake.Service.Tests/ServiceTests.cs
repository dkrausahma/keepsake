using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Crypto;
using Keepsake.Core.Repo;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;
using Keepsake.Protocol;
using Xunit;

namespace Keepsake.Service.Tests
{
    /// <summary>A service host in console mode with its own data folder, pipe and test repository.</summary>
    public sealed class TestService : IDisposable
    {
        public const string AppPassword = "app password 1";
        public const string RepoPassword = "repo password 1";

        static TestService()
        {
            PasswordKdf.DefaultMemoryKiB = 1024;
            PasswordKdf.DefaultIterations = 1;
            PasswordKdf.DefaultParallelism = 1;
        }

        public TestService()
        {
            Root = Path.Combine(Path.GetTempPath(), "keepsake-svc-" + Rand.HexId(6));
            Source = Path.Combine(Root, "source");
            RepoPath = Path.Combine(Root, "repo");
            Directory.CreateDirectory(Source);
            for (int i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(Source, "file" + i + ".txt"), "content " + i);
            Repository.Init(new FileSystemStorage(RepoPath), RepoPassword).Repository.Dispose();

            PipeName = "keepsake-test-" + Rand.HexId(6);
            Host = new ServiceHost(Path.Combine(Root, "data"), PipeName, consoleMode: true);
            Host.Start();
        }

        public string Root { get; }
        public string Source { get; }
        public string RepoPath { get; }
        public string PipeName { get; }
        internal ServiceHost Host { get; }
        internal ServiceContext Ctx => Host.Context;

        public KeepsakeClient Client(bool login = true)
        {
            var client = KeepsakeClient.Connect(PipeName);
            if (login)
            {
                if (!client.Call<AuthStatusResult>(Methods.AuthStatus).PasswordSet)
                    client.Call<LoginResult>(Methods.AuthSetup, new PasswordParams { Password = AppPassword });
                client.Login(AppPassword);
            }
            return client;
        }

        public JobDefinition NewJob(string name = "Docs") => new JobDefinition
        {
            Name = name,
            Sources = { Source },
            Destination = new Destination { Path = RepoPath },
            UseVss = false, // VSS needs elevation; covered by its own test
        };

        public RunRecord WaitForRun(KeepsakeClient client, string runId, int timeoutSeconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var detail = client.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = runId });
                if (detail.Record.State != RunState.Queued && detail.Record.State != RunState.Running)
                    return detail.Record;
                Thread.Sleep(100);
            }
            throw new TimeoutException("Run " + runId + " did not finish.");
        }

        public void Dispose()
        {
            Host.Stop();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public class ServiceTests
    {
        [Fact]
        public void Api_requires_setup_then_login()
        {
            using (var t = new TestService())
            using (var client = t.Client(login: false))
            {
                Assert.False(client.Call<AuthStatusResult>(Methods.AuthStatus).PasswordSet);
                var ex = Assert.Throws<ApiException>(() => client.Call<List<JobSummary>>(Methods.JobsList));
                Assert.Equal(ErrorCodes.Unauthorized, ex.Code);

                Assert.Throws<ApiException>(() => client.Call<LoginResult>(Methods.AuthSetup, new PasswordParams { Password = "short" }));
                client.Call<LoginResult>(Methods.AuthSetup, new PasswordParams { Password = TestService.AppPassword });
                Assert.Throws<ApiException>(() => client.Call<LoginResult>(Methods.AuthSetup, new PasswordParams { Password = "another password" }));

                Assert.Throws<ApiException>(() => client.Login("wrong password"));
                client.Login(TestService.AppPassword);
                Assert.Empty(client.Call<List<JobSummary>>(Methods.JobsList));
            }
        }

        [Fact]
        public void Repeated_wrong_passwords_lock_out()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                for (int i = 0; i < 5; i++)
                    Assert.Equal(ErrorCodes.Unauthorized, Assert.Throws<ApiException>(() => client.Login("nope")).Code);
                // Now even the right password is refused for a while.
                Assert.Equal(ErrorCodes.LockedOut, Assert.Throws<ApiException>(() => client.Login(TestService.AppPassword)).Code);
            }
        }

        [Fact]
        public void Job_is_saved_with_encrypted_password_and_runs()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = t.NewJob(), RepositoryPassword = TestService.RepoPassword });
                Assert.True(saved.HasRepositoryPassword);
                string stored = File.ReadAllText(Path.Combine(t.Root, "data", "config", "jobs.json"));
                Assert.DoesNotContain(TestService.RepoPassword, stored);
                Assert.Contains("dpapi:", stored);

                var run = client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id });
                var record = t.WaitForRun(client, run.RunId);
                Assert.Equal(RunState.Success, record.State);
                Assert.Equal(5, record.Summary.FilesNew);

                var detail = client.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = run.RunId });
                Assert.Contains("Keepsake backup", detail.Log);

                var snapshots = client.Call<List<SnapshotInfo>>(Methods.SnapshotsList, new IdParams { Id = saved.Job.Id });
                Assert.Single(snapshots);
                Assert.Equal(record.SnapshotId, snapshots[0].Id);
            }
        }

        [Fact]
        public void Wrong_repository_password_is_rejected_when_saving()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var ex = Assert.Throws<ApiException>(() => client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = t.NewJob(), RepositoryPassword = "wrong" }));
                Assert.Equal(ErrorCodes.Invalid, ex.Code);
            }
        }

        [Fact]
        public void Invalid_jobs_and_duplicate_names_are_rejected()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.Sources.Clear();
                Assert.Equal(ErrorCodes.Invalid, Assert.Throws<ApiException>(() => client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword })).Code);

                client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = t.NewJob("Same"), RepositoryPassword = TestService.RepoPassword });
                Assert.Equal(ErrorCodes.Conflict, Assert.Throws<ApiException>(() => client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = t.NewJob("same"), RepositoryPassword = TestService.RepoPassword })).Code);
            }
        }

        [Fact]
        public void Failing_pre_command_fails_the_run_and_post_command_still_runs()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.PreCommand = "exit 7";
                job.PostCommand = "echo post status %KEEPSAKE_STATUS%";
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });
                var run = client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id });
                var record = t.WaitForRun(client, run.RunId);
                Assert.Equal(RunState.Failed, record.State);
                Assert.Contains("exit code 7", record.Message);
                Assert.Contains("post status failed", client.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = run.RunId }).Log);
            }
        }

        [Fact]
        public void A_job_cannot_run_twice_at_once_and_can_be_cancelled()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.PreCommand = "ping -n 30 127.0.0.1 >nul";
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });

                var run = client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id });
                var ex = Assert.Throws<ApiException>(() => client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }));
                Assert.Equal(ErrorCodes.Conflict, ex.Code);

                Thread.Sleep(500);
                client.Call(Methods.JobsCancel, new IdParams { Id = saved.Job.Id });
                var record = t.WaitForRun(client, run.RunId, 20);
                Assert.Equal(RunState.Cancelled, record.State);
            }
        }

        [Fact]
        public void Missed_schedule_runs_once_after_start_up()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.Schedules.Add(new ScheduleEntry { Kind = ScheduleKind.Daily, Times = { "03:00" } });
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });

                // First pass only registers the schedule (no immediate run).
                var now = DateTime.Now;
                t.Ctx.Scheduler.RunOnce(now);
                Assert.Empty(client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = saved.Job.Id }));

                // Two days later (service was "off"): one catch-up run, marked as missed.
                t.Ctx.Scheduler.RunOnce(now.AddDays(2));
                var runs = client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = saved.Job.Id });
                var run = Assert.Single(runs);
                Assert.Equal(RunTrigger.Missed, run.Trigger);
                Assert.Equal(RunState.Success, t.WaitForRun(client, run.Id).State);

                // Nothing more is due right after.
                t.Ctx.Scheduler.RunOnce(now.AddDays(2).AddMinutes(1));
                Assert.Single(client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = saved.Job.Id }), r => r.Kind == RunKind.Backup);
            }
        }

        [Fact]
        public void Failed_scheduled_run_is_retried()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var job = t.NewJob();
                job.Schedules.Add(new ScheduleEntry { Kind = ScheduleKind.Interval, IntervalMinutes = 60 });
                job.RetryCount = 1;
                job.RetryDelayMinutes = 1;
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = TestService.RepoPassword });
                var now = DateTime.Now;
                t.Ctx.Scheduler.RunOnce(now);

                // Make the run fail: the source disappears.
                Directory.Delete(t.Source, true);
                t.Ctx.Scheduler.RunOnce(now.AddMinutes(61));
                var first = Assert.Single(client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = saved.Job.Id }));
                Assert.Equal(RunState.Failed, t.WaitForRun(client, first.Id).State);

                Thread.Sleep(300); // RunFinished handler schedules the retry
                Assert.NotNull(t.Ctx.Scheduler.NextRun(t.Ctx.Jobs.Get(saved.Job.Id)));
                Directory.CreateDirectory(t.Source);
                File.WriteAllText(Path.Combine(t.Source, "back.txt"), "back");
                t.Ctx.Scheduler.RunOnce(DateTime.Now.AddMinutes(2));
                var runs = client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = saved.Job.Id });
                var retry = runs.First();
                Assert.Equal(RunTrigger.Retry, retry.Trigger);
                Assert.Equal(2, retry.Attempt);
                Assert.Equal(RunState.Success, t.WaitForRun(client, retry.Id).State);
            }
        }

        [Fact]
        public void Restore_through_the_service_and_browse_the_snapshot()
        {
            using (var t = new TestService())
            using (var client = t.Client())
            {
                var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = t.NewJob(), RepositoryPassword = TestService.RepoPassword });
                var backup = t.WaitForRun(client, client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = saved.Job.Id }).RunId);

                var root = client.Call<List<TreeEntry>>(Methods.TreeList, new TreeParams { JobId = saved.Job.Id, SnapshotId = backup.SnapshotId });
                Assert.Single(root);
                var sourceFolder = client.Call<List<TreeEntry>>(Methods.TreeList, new TreeParams { JobId = saved.Job.Id, SnapshotId = backup.SnapshotId, Path = t.Source });
                Assert.Equal(5, sourceFolder.Count);

                string target = Path.Combine(t.Root, "restored");
                var started = client.Call<RunStarted>(Methods.RestoreStart, new RestoreParams
                {
                    JobId = saved.Job.Id,
                    Options = new Core.Restore.RestoreOptions { Snapshot = backup.SnapshotId, TargetFolder = target, Includes = { t.Source }, KeepFullPath = false },
                });
                var restore = t.WaitForRun(client, started.RunId);
                Assert.Equal(RunState.Success, restore.State);
                Assert.Equal(5, restore.RestoreStats.FilesRestored);
                Assert.Equal("content 3", File.ReadAllText(Path.Combine(target, "source", "file3.txt")));
            }
        }

        [Fact]
        public void Interrupted_runs_are_closed_on_start()
        {
            string root = Path.Combine(Path.GetTempPath(), "keepsake-hist-" + Rand.HexId(6));
            try
            {
                var history = new RunHistory(root);
                history.Save(new RunRecord { Id = "r1", JobId = "j1", JobName = "J", State = RunState.Running, Start = DateTime.UtcNow });
                var reloaded = new RunHistory(root);
                reloaded.MarkInterrupted();
                var r = reloaded.List("j1", 10).Single();
                Assert.Equal(RunState.Failed, r.State);
                Assert.Contains("Interrupted", r.Message);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void History_pruning_keeps_newest_runs()
        {
            string root = Path.Combine(Path.GetTempPath(), "keepsake-hist-" + Rand.HexId(6));
            try
            {
                var history = new RunHistory(root);
                for (int i = 0; i < 10; i++)
                    history.Save(new RunRecord { Id = "r" + i, JobId = "j", State = RunState.Success, Start = DateTime.UtcNow.AddDays(-i * 20) });
                int removed = history.Prune(days: 90, maxRuns: 3);
                Assert.Equal(7, removed);
                Assert.Equal(new[] { "r0", "r1", "r2" }, history.List("j", 100).Select(r => r.Id).ToArray());
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
