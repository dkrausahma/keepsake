using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Keepsake.Core;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Gui.Infrastructure;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>
    /// Where the restore page gets its data from: the Keepsake service (normal operation) or a repository the GUI
    /// opened itself (recovery mode, RST-07 – no service, no configuration, any computer).
    /// </summary>
    public interface IRestoreBackend
    {
        /// <summary>Protecting snapshots changes the repository; only offered with the service.</summary>
        bool CanProtect { get; }

        Task<List<JobChoice>> ListJobs();
        Task<List<SnapshotInfo>> ListSnapshots(string jobId);
        Task<List<TreeEntry>> ListTree(string jobId, string snapshotId, string path);
        Task<string> StartRestore(string jobId, RestoreOptions options);
        Task<RestorePoll> Poll(string runId);
        Task Protect(string jobId, string snapshotId, bool protect);
        Task<RunDetail> Detail(string runId);
    }

    public sealed class RestorePoll
    {
        public bool Running { get; set; }
        public string Progress { get; set; }
        public RunRecord Record { get; set; }
    }

    /// <summary>Restore through the service: jobs, snapshots and runs as the service knows them.</summary>
    public sealed class ServiceRestoreBackend : IRestoreBackend
    {
        private readonly ServiceConnection _connection;

        public ServiceRestoreBackend(ServiceConnection connection)
        {
            _connection = connection;
        }

        public bool CanProtect => true;

        public async Task<List<JobChoice>> ListJobs() =>
            (await _connection.Call<List<JobSummary>>(Methods.JobsList)).Select(j => new JobChoice(j.Id, j.Name)).ToList();

        public Task<List<SnapshotInfo>> ListSnapshots(string jobId) =>
            _connection.Call<List<SnapshotInfo>>(Methods.SnapshotsList, new IdParams { Id = jobId });

        public Task<List<TreeEntry>> ListTree(string jobId, string snapshotId, string path) =>
            _connection.Call<List<TreeEntry>>(Methods.TreeList, new TreeParams { JobId = jobId, SnapshotId = snapshotId, Path = path });

        public async Task<string> StartRestore(string jobId, RestoreOptions options) =>
            (await _connection.Call<RunStarted>(Methods.RestoreStart, new RestoreParams { JobId = jobId, Options = options })).RunId;

        public async Task<RestorePoll> Poll(string runId)
        {
            var detail = await _connection.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = runId });
            var r = detail.Record;
            if (r.State != RunState.Running && r.State != RunState.Queued)
                return new RestorePoll { Record = r };
            var p = (await _connection.Call<ServiceStatus>(Methods.Status)).Running.FirstOrDefault(x => x.RunId == runId);
            return new RestorePoll
            {
                Running = true,
                Progress = p == null ? "Waiting…" : "Restoring: " + Format.Count(p.Files) + " files, " + Format.Bytes(p.BytesRead) + " written · " + p.CurrentPath,
            };
        }

        public Task Protect(string jobId, string snapshotId, bool protect) =>
            _connection.Call<IdParams>(Methods.SnapshotsProtect, new ProtectParams { JobId = jobId, SnapshotId = snapshotId, Protect = protect });

        public Task<RunDetail> Detail(string runId) => _connection.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = runId });
    }

    /// <summary>
    /// Restore straight from a repository opened in the GUI (recovery mode). Jobs are the job names found in the
    /// snapshots; the restore runs in this process with the rights of the logged-on administrator.
    /// </summary>
    public sealed class LocalRestoreBackend : IRestoreBackend, IDisposable
    {
        private const string NoName = "\u0001";
        private readonly Repository _repo;
        private readonly object _lock = new object();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private string _runId;
        private RunRecord _record;
        private RestoreResult _result;
        private RestoreProgress _progress;
        private string _log;

        public LocalRestoreBackend(Repository repo)
        {
            _repo = repo;
        }

        public string Location => _repo.Storage.Location;
        public bool CanProtect => false;
        public bool IsRunning { get; private set; }

        public Task<List<JobChoice>> ListJobs() => Task.Run(() =>
        {
            lock (_lock)
            {
                return _repo.ListSnapshots()
                    .Select(s => string.IsNullOrEmpty(s.Snapshot.Job) ? NoName : s.Snapshot.Job)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new JobChoice(n, n == NoName ? "(backups without job name)" : n))
                    .ToList();
            }
        });

        public Task<List<SnapshotInfo>> ListSnapshots(string jobId) => Task.Run(() =>
        {
            lock (_lock)
            {
                return _repo.ListSnapshots()
                    .Where(s => string.Equals(string.IsNullOrEmpty(s.Snapshot.Job) ? NoName : s.Snapshot.Job, jobId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Snapshot.Time)
                    .Select(s => new SnapshotInfo
                    {
                        Id = s.Id,
                        Time = s.Snapshot.Time,
                        Job = s.Snapshot.Job,
                        Host = s.Snapshot.Host,
                        Mode = s.Snapshot.Mode,
                        Paths = s.Snapshot.Paths,
                        Protected = Repository.IsProtected(s.Snapshot),
                        Summary = s.Snapshot.Summary,
                    })
                    .ToList();
            }
        });

        public Task<List<TreeEntry>> ListTree(string jobId, string snapshotId, string path) => Task.Run(() =>
        {
            lock (_lock)
            {
                _repo.LoadIndex();
                var snapshot = _repo.FindSnapshot(snapshotId);
                var tree = _repo.LoadTree(snapshot.Snapshot.Tree);
                var parts = string.IsNullOrEmpty(path) ? new List<string>() : RepoPaths.Parse(path);
                foreach (string part in parts)
                {
                    var node = tree.Find(part);
                    if (node == null || node.Type != NodeType.Dir)
                        throw new KeepsakeException("'" + RepoPaths.Join(parts) + "' is not a folder in this backup.");
                    tree = _repo.LoadTree(node.Subtree);
                }
                return tree.Nodes.Select(n => new TreeEntry
                {
                    Name = n.Name,
                    Path = RepoPaths.Join(parts.Concat(new[] { n.Name })),
                    IsFolder = n.Type == NodeType.Dir,
                    Size = n.Size,
                    Modified = n.ModifiedUtc,
                }).ToList();
            }
        });

        public Task<string> StartRestore(string jobId, RestoreOptions options)
        {
            if (IsRunning)
                throw new KeepsakeException("A restore is already running.");
            _runId = Guid.NewGuid().ToString("N");
            _record = new RunRecord { Id = _runId, JobName = jobId == NoName ? null : jobId, Kind = RunKind.Restore, Trigger = RunTrigger.Manual, State = RunState.Running, Start = DateTime.UtcNow };
            _result = null;
            _progress = null;
            _log = null;
            IsRunning = true;
            var progress = new Sink(p => _progress = p);
            Task.Run(() =>
            {
                try
                {
                    RestoreResult result;
                    lock (_lock)
                        result = new RestoreEngine(_repo).Run(options, progress, _cancel.Token);
                    _result = result;
                    _record.SnapshotId = result.SnapshotId;
                    _record.RestoreStats = result.Stats;
                    _record.Errors = result.Count(IssueKind.Error);
                    _record.Warnings = result.Count(IssueKind.Warning);
                    _record.Message = result.Error;
                    _record.State = Map(result.Status);
                    _log = TextReport.Full("Keepsake restore (recovery mode) – " + Location, result, TextReport.SummaryLines(result));
                }
                catch (Exception ex) when (ex is KeepsakeException || ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
                {
                    _record.State = ex is OperationCanceledException ? RunState.Cancelled : RunState.Failed;
                    _record.Message = ex.Message;
                    _log = "Restore failed: " + ex.Message;
                }
                finally
                {
                    _record.End = DateTime.UtcNow;
                    _record.DurationSeconds = (_record.End.Value - _record.Start).TotalSeconds;
                    IsRunning = false;
                }
            });
            return Task.FromResult(_runId);
        }

        public Task<RestorePoll> Poll(string runId)
        {
            if (IsRunning)
            {
                var p = _progress;
                return Task.FromResult(new RestorePoll
                {
                    Running = true,
                    Progress = p == null ? "Opening the backup…" : "Restoring: " + Format.Count(p.Files) + " files, " + Format.Bytes(p.BytesWritten) + " written · " + p.CurrentPath,
                });
            }
            return Task.FromResult(new RestorePoll { Record = _record });
        }

        public Task Protect(string jobId, string snapshotId, bool protect) =>
            throw new KeepsakeException("Protecting backups is only possible with the Keepsake service.");

        public Task<RunDetail> Detail(string runId) => Task.FromResult(new RunDetail
        {
            Record = _record,
            Phases = _result?.Phases ?? new List<PhaseTiming>(),
            Issues = _result?.Issues ?? new List<Issue>(),
            Log = _log,
        });

        public void Dispose()
        {
            _cancel.Cancel();
            lock (_lock)
                _repo.Dispose();
        }

        private static RunState Map(RunStatus status) =>
            status == RunStatus.Success ? RunState.Success
            : status == RunStatus.Warning ? RunState.Warning
            : status == RunStatus.Cancelled ? RunState.Cancelled
            : RunState.Failed;

        private sealed class Sink : IProgress<RestoreProgress>
        {
            private readonly Action<RestoreProgress> _report;

            public Sink(Action<RestoreProgress> report)
            {
                _report = report;
            }

            public void Report(RestoreProgress value) => _report(value);
        }
    }
}
