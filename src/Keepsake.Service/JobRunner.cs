using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Keepsake.Core;
using Keepsake.Core.Backup;
using Keepsake.Core.Check;
using Keepsake.Core.Maintenance;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>
    /// Queue and execution of runs. At most <see cref="Settings.MaxConcurrentJobs"/> run at the same time,
    /// and a job never runs twice at once (SCH-05).
    /// </summary>
    internal sealed class JobRunner
    {
        private readonly JobStore _jobs;
        private readonly SettingsStore _settings;
        private readonly RunHistory _history;
        private readonly object _lock = new object();
        private readonly List<Pending> _queue = new List<Pending>();
        private readonly Dictionary<string, Active> _active = new Dictionary<string, Active>(StringComparer.Ordinal);
        private readonly ShadowTracker _shadows;
        private bool _stopping;

        public JobRunner(JobStore jobs, SettingsStore settings, RunHistory history, ShadowTracker shadows = null)
        {
            _jobs = jobs;
            _settings = settings;
            _history = history;
            _shadows = shadows;
        }

        /// <summary>Raised on the run's thread after a run has ended and its record was saved.</summary>
        public event Action<RunRecord> RunFinished;

        /// <param name="followUp">Maintenance queued by the service after a backup: may wait behind the job's current run.</param>
        public RunRecord Enqueue(string jobId, RunKind kind, BackupMode mode, RunTrigger trigger, int attempt = 1, RestoreOptions restore = null,
            bool followUp = false, bool fullCheck = false)
        {
            var job = _jobs.Get(jobId) ?? throw new ApiException(ErrorCodes.NotFound, "Job not found.");
            lock (_lock)
            {
                if (_stopping)
                    throw new ApiException(ErrorCodes.Conflict, "The service is stopping.");
                if (followUp ? _queue.Any(q => q.Record.JobId == jobId && q.Record.Kind == kind)
                             : _active.ContainsKey(jobId) || _queue.Any(q => q.Record.JobId == jobId))
                    throw new ApiException(ErrorCodes.Conflict, "'" + job.Name + "' is already running or waiting to run.");

                var record = new RunRecord
                {
                    Id = RunHistory.NewRunId(),
                    JobId = jobId,
                    JobName = job.Name,
                    Kind = kind,
                    Trigger = trigger,
                    Mode = mode,
                    State = RunState.Queued,
                    Start = DateTime.UtcNow,
                    Attempt = attempt,
                };
                _history.Save(record);
                _queue.Add(new Pending { Record = record, Restore = restore, FullCheck = fullCheck });
                ServiceLog.Info("Queued " + kind.ToString().ToLowerInvariant() + " of '" + job.Name + "' (" + trigger.ToString().ToLowerInvariant() + ", " + mode.ToString().ToLowerInvariant() + ").");
                Pump();
                return Clone(record);
            }
        }

        public bool IsBusy(string jobId)
        {
            lock (_lock)
                return _active.ContainsKey(jobId) || _queue.Any(q => q.Record.JobId == jobId);
        }

        public bool IsQueued(string jobId)
        {
            lock (_lock)
                return _queue.Any(q => q.Record.JobId == jobId);
        }

        public void Cancel(string jobId)
        {
            RunRecord cancelled = null;
            lock (_lock)
            {
                if (_active.TryGetValue(jobId, out var active))
                {
                    active.Cancel.Cancel();
                    ServiceLog.Info("Cancel requested for '" + active.Record.JobName + "'.");
                    return;
                }
                var pending = _queue.FirstOrDefault(q => q.Record.JobId == jobId)
                              ?? throw new ApiException(ErrorCodes.NotFound, "The job is not running.");
                _queue.Remove(pending);
                cancelled = pending.Record;
            }
            cancelled.State = RunState.Cancelled;
            cancelled.End = DateTime.UtcNow;
            cancelled.Message = "Cancelled before it started.";
            _history.Save(cancelled);
        }

        public RunProgress ProgressOf(string jobId)
        {
            lock (_lock)
                return _active.TryGetValue(jobId, out var a) ? Copy(a.Progress) : null;
        }

        public List<RunProgress> Running()
        {
            lock (_lock)
                return _active.Values.Select(a => Copy(a.Progress)).ToList();
        }

        public List<string> QueuedJobNames()
        {
            lock (_lock)
                return _queue.Select(q => q.Record.JobName).ToList();
        }

        /// <summary>Cancels everything and waits up to <paramref name="timeout"/> for running jobs to end.</summary>
        public void StopAll(TimeSpan timeout)
        {
            List<Active> active;
            List<Pending> queued;
            lock (_lock)
            {
                _stopping = true;
                active = _active.Values.ToList();
                queued = _queue.ToList();
                _queue.Clear();
            }
            foreach (var q in queued)
            {
                q.Record.State = RunState.Cancelled;
                q.Record.End = DateTime.UtcNow;
                q.Record.Message = "Cancelled: the service was stopped.";
                _history.Save(q.Record);
            }
            foreach (var a in active)
                a.Cancel.Cancel();
            var deadline = DateTime.UtcNow + timeout;
            foreach (var a in active)
            {
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                    a.Thread.Join(left);
            }
        }

        private void Pump()
        {
            int max = Math.Max(1, _settings.Get().MaxConcurrentJobs);
            while (!_stopping && _active.Count < max)
            {
                // First waiting run whose job is not already running (a job never runs twice at once).
                var next = _queue.FirstOrDefault(q => !_active.ContainsKey(q.Record.JobId));
                if (next == null)
                    break;
                _queue.Remove(next);
                var active = new Active
                {
                    Record = next.Record,
                    Cancel = new CancellationTokenSource(),
                    Progress = new RunProgress
                    {
                        RunId = next.Record.Id,
                        JobId = next.Record.JobId,
                        JobName = next.Record.JobName,
                        Kind = next.Record.Kind,
                        Phase = "Starting",
                        Start = DateTime.UtcNow,
                    },
                };
                active.Thread = new Thread(() => Execute(active, next)) { IsBackground = true, Name = "Keepsake run " + next.Record.JobName };
                _active[next.Record.JobId] = active;
                active.Thread.Start();
            }
        }

        private void Execute(Active active, Pending pending)
        {
            var record = active.Record;
            record.State = RunState.Running;
            record.Start = DateTime.UtcNow;
            _history.Save(record);
            ServiceLog.Info("Started " + record.Kind.ToString().ToLowerInvariant() + " of '" + record.JobName + "' (run " + record.Id + ").");

            var log = new StringBuilder();
            RunReport report = null;
            try
            {
                var job = _jobs.Get(record.JobId) ?? throw new KeepsakeException("The job was deleted.");
                switch (record.Kind)
                {
                    case RunKind.Restore:
                        report = RunRestore(job, pending.Restore, active, log);
                        break;
                    case RunKind.Check:
                        report = RunCheck(job, pending.FullCheck, active, log);
                        break;
                    case RunKind.Prune:
                        report = RunPrune(job, active, log);
                        break;
                    default:
                        report = RunBackup(job, active, log);
                        break;
                }
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                record.State = active.Cancel.IsCancellationRequested ? RunState.Cancelled : RunState.Failed;
                record.Message = ex.Message;
                log.AppendLine().AppendLine((record.State == RunState.Cancelled ? "CANCELLED: " : "FAILED: ") + ex.Message);
            }
            catch (Exception ex)
            {
                record.State = RunState.Failed;
                record.Message = "Unexpected error: " + ex.Message;
                log.AppendLine().AppendLine("FAILED (unexpected): " + ex);
                ServiceLog.Error("Unexpected error in run " + record.Id, ex);
            }
            finally
            {
                record.End = DateTime.UtcNow;
                record.DurationSeconds = (record.End.Value - record.Start).TotalSeconds;
                if (report != null)
                {
                    record.Errors = report.Count(IssueKind.Error);
                    record.Warnings = report.Count(IssueKind.Warning);
                }
                try
                {
                    _history.Save(record, report, log.ToString());
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ServiceLog.Error("Could not save run " + record.Id, ex);
                }
                ServiceLog.Info("Finished '" + record.JobName + "': " + record.State + (record.Message != null ? " – " + record.Message : "") +
                                " (" + Format.Duration(record.DurationSeconds) + ").");
                lock (_lock)
                {
                    _active.Remove(record.JobId);
                    Pump();
                }
                ScheduleMaintenance(record);
                try
                {
                    RunFinished?.Invoke(Clone(record));
                }
                catch (Exception ex)
                {
                    ServiceLog.Error("Run-finished handler failed", ex);
                }
                active.Cancel.Dispose();
            }
        }

        private RunReport RunBackup(JobDefinition job, Active active, StringBuilder log)
        {
            var record = active.Record;
            var env = new Dictionary<string, string>
            {
                ["KEEPSAKE_JOB"] = job.Name,
                ["KEEPSAKE_RUN_ID"] = record.Id,
                ["KEEPSAKE_MODE"] = record.Mode.ToString().ToLowerInvariant(),
                ["KEEPSAKE_REPOSITORY"] = job.Destination.ToString(),
            };
            var timeout = TimeSpan.FromMinutes(Math.Max(1, job.CommandTimeoutMinutes));
            BackupResult result = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(job.PreCommand))
                {
                    SetPhase(active, "Pre-command");
                    log.AppendLine("Pre-command");
                    int exit = CommandRunner.Run(job.PreCommand, timeout, env, log, active.Cancel.Token);
                    log.AppendLine();
                    if (exit != 0)
                        throw new KeepsakeException("The pre-command failed with exit code " + exit + ". The backup was not started.");
                }

                VssSession vss = null;
                var initialIssues = new List<Issue>();
                if (job.UseVss)
                {
                    SetPhase(active, "Creating VSS snapshot");
                    vss = VssSession.Create(job.Sources, initialIssues, _shadows ?? ShadowTracker.None);
                }
                SetPhase(active, "Opening repository");
                using (vss)
                using (var opened = OpenRepository(job))
                {
                    var options = new BackupOptions
                    {
                        ReadPath = vss == null || vss.Count == 0 ? (Func<string, string>)null : vss.Map,
                        InitialIssues = initialIssues,
                        Sources = job.Sources.ToList(),
                        Excludes = job.Excludes.ToList(),
                        UseDefaultExcludes = job.UseDefaultExcludes,
                        Mode = record.Mode,
                        Job = job.Name,
                        Parent = ParentSnapshot(opened.Repository, job),
                    };
                    if (job.Workers > 0)
                        options.Workers = job.Workers;

                    SetPhase(active, "Backing up");
                    var progress = new Progress(p =>
                    {
                        var current = active.Progress;
                        active.Progress = new RunProgress
                        {
                            RunId = current.RunId, JobId = current.JobId, JobName = current.JobName, Kind = current.Kind, Start = current.Start,
                            Phase = "Backing up", Files = p.Files, BytesRead = p.BytesRead, BytesStored = p.BytesStored, CurrentPath = p.CurrentPath,
                        };
                    });
                    result = new BackupEngine(opened.Repository).Run(options, progress, active.Cancel.Token);
                    if (result.Status == RunStatus.Success || result.Status == RunStatus.Warning)
                    {
                        try
                        {
                            if (ToolCopy.Ensure(opened.Repository.Storage, AppDomain.CurrentDomain.BaseDirectory, ProgramInfo.Version))
                                log.AppendLine("Portable restore tool " + ProgramInfo.Version + " copied into the repository (tools folder).");
                        }
                        catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is UnauthorizedAccessException)
                        {
                            ServiceLog.Warn("Could not copy the restore tool into the repository: " + ex.Message);
                        }
                    }
                }

                record.SnapshotId = result.SnapshotId;
                record.Summary = result.Summary;
                record.State = Map(result.Status);
                record.Message = result.Error ?? (result.Status == RunStatus.Warning
                    ? result.Count(IssueKind.Error) + " item(s) not backed up, " + result.Count(IssueKind.Warning) + " warning(s)."
                    : null);
                log.Append(TextReport.Full("Keepsake backup – " + job.Name, result, TextReport.SummaryLines(result)));
                return result;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(job.PostCommand))
                {
                    SetPhase(active, "Post-command");
                    env["KEEPSAKE_STATUS"] = (result != null ? Map(result.Status) : RunState.Failed).ToString().ToLowerInvariant();
                    env["KEEPSAKE_SNAPSHOT"] = result?.SnapshotId ?? "";
                    log.AppendLine().AppendLine("Post-command");
                    try
                    {
                        int exit = CommandRunner.Run(job.PostCommand, timeout, env, log, CancellationToken.None);
                        if (exit != 0 && record.State == RunState.Success)
                        {
                            record.State = RunState.Warning;
                            record.Message = "The post-command failed with exit code " + exit + ".";
                        }
                    }
                    catch (Exception ex) when (ex is KeepsakeException || ex is System.ComponentModel.Win32Exception)
                    {
                        log.AppendLine("  " + ex.Message);
                    }
                }
            }
        }

        private RunReport RunRestore(JobDefinition job, RestoreOptions options, Active active, StringBuilder log)
        {
            if (options == null)
                throw new KeepsakeException("No restore options given.");
            SetPhase(active, "Opening repository");
            RestoreResult result;
            using (var opened = OpenRepository(job))
            {
                SetPhase(active, "Restoring");
                var progress = new RestoreProgressSink(p =>
                {
                    var current = active.Progress;
                    active.Progress = new RunProgress
                    {
                        RunId = current.RunId, JobId = current.JobId, JobName = current.JobName, Kind = current.Kind, Start = current.Start,
                        Phase = "Restoring", Files = p.Files, BytesRead = p.BytesWritten, CurrentPath = p.CurrentPath,
                    };
                });
                result = new RestoreEngine(opened.Repository).Run(options, progress, active.Cancel.Token);
            }
            var record = active.Record;
            record.SnapshotId = result.SnapshotId;
            record.RestoreStats = result.Stats;
            record.State = Map(result.Status);
            record.Message = result.Error;
            log.Append(TextReport.Full("Keepsake restore – " + job.Name, result, TextReport.SummaryLines(result)));
            return result;
        }

        private RunReport RunCheck(JobDefinition job, bool full, Active active, StringBuilder log)
        {
            SetPhase(active, "Opening repository");
            CheckResult result;
            int percent = full ? 100 : job.CheckReadDataPercent;
            using (var opened = OpenRepository(job))
            {
                SetPhase(active, "Checking");
                var progress = new TextProgress(text => SetPhase(active, text));
                result = new Checker(opened.Repository).Run(new CheckOptions { ReadData = percent > 0, ReadDataPercent = Math.Max(1, percent) }, progress, active.Cancel.Token);
            }
            var record = active.Record;
            record.State = Map(result.Status);
            record.Message = result.Error ?? (percent > 0 ? percent + " % of the data verified." : "Structure verified.");
            log.Append(TextReport.Full("Keepsake check – " + job.Name, result, TextReport.SummaryLines(result)));
            return result;
        }

        private RunReport RunPrune(JobDefinition job, Active active, StringBuilder log)
        {
            SetPhase(active, "Opening repository");
            PruneResult result;
            using (var opened = OpenRepository(job))
            {
                SetPhase(active, "Cleaning up");
                var progress = new TextProgress(text => SetPhase(active, text));
                result = new Pruner(opened.Repository).Run(new PruneOptions { Policy = job.Retention, Job = job.Name }, progress, active.Cancel.Token);
            }
            var record = active.Record;
            record.State = Map(result.Status);
            record.Message = result.Error ?? result.SnapshotsRemoved.Count + " snapshot(s) removed, " + Format.Bytes(result.BytesFreed) + " freed.";
            log.Append(TextReport.Full("Keepsake cleanup – " + job.Name, result, TextReport.SummaryLines(result)));
            return result;
        }

        /// <summary>After a good backup: queue cleanup and check if they are due (RET-03, VER-04).</summary>
        private void ScheduleMaintenance(RunRecord finished)
        {
            if (finished.Kind != RunKind.Backup || (finished.State != RunState.Success && finished.State != RunState.Warning))
                return;
            var job = _jobs.Get(finished.JobId);
            if (job == null)
                return;
            try
            {
                if (job.PruneEveryDays > 0 && job.Retention != null && !job.Retention.IsEmpty && Due(job.Id, RunKind.Prune, job.PruneEveryDays))
                    Enqueue(job.Id, RunKind.Prune, BackupMode.Incremental, RunTrigger.Schedule, followUp: true);
                if (job.CheckEveryDays > 0 && Due(job.Id, RunKind.Check, job.CheckEveryDays))
                    Enqueue(job.Id, RunKind.Check, BackupMode.Incremental, RunTrigger.Schedule, followUp: true);
            }
            catch (ApiException ex)
            {
                ServiceLog.Warn("Maintenance for '" + job.Name + "' not queued: " + ex.Message);
            }
        }

        private bool Due(string jobId, RunKind kind, int everyDays)
        {
            var last = _history.LastOfKind(jobId, kind);
            return last == null || DateTime.UtcNow - last.Start > TimeSpan.FromDays(everyDays) - TimeSpan.FromHours(1);
        }

        /// <summary>Opens the job's repository, connecting shares and finding the USB disk as needed.</summary>
        internal OpenedRepository OpenRepository(JobDefinition job)
        {
            var secrets = _jobs.GetSecrets(job.Id);
            if (string.IsNullOrEmpty(secrets.RepositoryPassword))
                throw new KeepsakeException("No repository password is stored for this job.");
            var destination = DestinationResolver.Open(job.Destination, job.ShareCredentials, secrets);
            try
            {
                if (!Repository.Exists(destination.Storage))
                    throw new KeepsakeException("No Keepsake repository found at " + destination.Storage.Location + ". Create it in the job settings first.");
                var repo = Repository.Open(destination.Storage, RepoCredential.FromPassword(secrets.RepositoryPassword));
                return new OpenedRepository(repo, destination);
            }
            catch
            {
                destination.Dispose();
                throw;
            }
        }

        /// <summary>Last snapshot written by this job – keeps incremental backups working after a job is renamed.</summary>
        private string ParentSnapshot(Repository repo, JobDefinition job)
        {
            var last = _history.Last(job.Id, r => r.SnapshotId != null && (r.State == RunState.Success || r.State == RunState.Warning));
            if (last == null)
                return null;
            try
            {
                return repo.FindSnapshot(last.SnapshotId).Id;
            }
            catch (KeepsakeException)
            {
                return null; // snapshot gone (e.g. repository replaced): fall back to automatic parent selection
            }
        }

        private static void SetPhase(Active active, string phase)
        {
            var p = Copy(active.Progress);
            p.Phase = phase;
            active.Progress = p;
        }

        private static RunState Map(RunStatus status)
        {
            switch (status)
            {
                case RunStatus.Success: return RunState.Success;
                case RunStatus.Warning: return RunState.Warning;
                case RunStatus.Cancelled: return RunState.Cancelled;
                default: return RunState.Failed;
            }
        }

        private static RunProgress Copy(RunProgress p) => Json.FromBytes<RunProgress>(Json.ToBytes(p));
        private static RunRecord Clone(RunRecord r) => Json.FromBytes<RunRecord>(Json.ToBytes(r));

        private sealed class Pending
        {
            public RunRecord Record;
            public RestoreOptions Restore;
            public bool FullCheck;
        }

        private sealed class Active
        {
            public RunRecord Record;
            public CancellationTokenSource Cancel;
            public Thread Thread;
            public volatile RunProgress Progress;
        }

        private sealed class Progress : IProgress<BackupProgress>
        {
            private readonly Action<BackupProgress> _action;
            public Progress(Action<BackupProgress> action) => _action = action;
            public void Report(BackupProgress value) => _action(value);
        }

        private sealed class TextProgress : IProgress<string>
        {
            private readonly Action<string> _action;
            public TextProgress(Action<string> action) => _action = action;
            public void Report(string value) => _action(value);
        }

        private sealed class RestoreProgressSink : IProgress<RestoreProgress>
        {
            private readonly Action<RestoreProgress> _action;
            public RestoreProgressSink(Action<RestoreProgress> action) => _action = action;
            public void Report(RestoreProgress value) => _action(value);
        }
    }
}
