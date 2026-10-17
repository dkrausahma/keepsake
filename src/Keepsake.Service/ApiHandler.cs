using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Keepsake.Core.Util;
using Keepsake.Core;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Storage;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>Dispatches API requests. Everything except <c>auth.status/setup/login</c> needs a valid session.</summary>
    internal sealed class ApiHandler
    {
        private static readonly TimeSpan BrowseCacheIdle = TimeSpan.FromMinutes(5);

        private readonly ServiceContext _ctx;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, CachedRepo> _repoCache = new Dictionary<string, CachedRepo>(StringComparer.Ordinal);

        public ApiHandler(ServiceContext ctx)
        {
            _ctx = ctx;
            // New snapshots change what can be browsed: drop the cached repository of that job.
            _ctx.Runner.RunFinished += r => Evict(r.JobId);
        }

        public Response Handle(Request request)
        {
            try
            {
                object result = Dispatch(request);
                return new Response
                {
                    Id = request.Id,
                    Ok = true,
                    Result = result == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(result, result.GetType(), Json.Compact),
                };
            }
            catch (ApiException ex)
            {
                return Error(request, ex.Code, ex.Message);
            }
            catch (WrongCredentialException ex)
            {
                return Error(request, ErrorCodes.Invalid, ex.Message);
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                return Error(request, ErrorCodes.Failed, ex.Message);
            }
            catch (Exception ex)
            {
                ServiceLog.Error("API call " + request.Method + " failed unexpectedly", ex);
                return Error(request, ErrorCodes.Failed, "Internal error: " + ex.Message);
            }
        }

        private object Dispatch(Request r)
        {
            switch (r.Method)
            {
                case Methods.AuthStatus:
                    return new AuthStatusResult { PasswordSet = _ctx.Auth.PasswordSet };
                case Methods.AuthSetup:
                    return _ctx.Auth.Setup(P<PasswordParams>(r).Password);
                case Methods.AuthLogin:
                    return _ctx.Auth.Login(P<PasswordParams>(r).Password);
            }

            _ctx.Auth.Validate(r.Token);
            switch (r.Method)
            {
                case Methods.AuthLogout:
                    _ctx.Auth.Logout(r.Token);
                    return null;
                case Methods.AuthChangePassword:
                {
                    var p = P<ChangePasswordParams>(r);
                    _ctx.Auth.ChangePassword(p.OldPassword, p.NewPassword);
                    return null;
                }
                case Methods.Status:
                    return Status();
                case Methods.SettingsGet:
                {
                    var s = _ctx.Settings.Get();
                    return new ServiceSettings { MaxConcurrentJobs = s.MaxConcurrentJobs, LogRetentionDays = s.LogRetentionDays, LogRetentionRuns = s.LogRetentionRuns };
                }
                case Methods.SettingsSave:
                {
                    var p = P<ServiceSettings>(r);
                    if (p.MaxConcurrentJobs < 1 || p.MaxConcurrentJobs > 8)
                        throw new ApiException(ErrorCodes.Invalid, "Parallel jobs must be between 1 and 8.");
                    if (p.LogRetentionDays < 1 || p.LogRetentionDays > 3650 || p.LogRetentionRuns < 1 || p.LogRetentionRuns > 10000)
                        throw new ApiException(ErrorCodes.Invalid, "Log retention must be 1–3650 days and 1–10000 runs.");
                    _ctx.Settings.Update(s =>
                    {
                        s.MaxConcurrentJobs = p.MaxConcurrentJobs;
                        s.LogRetentionDays = p.LogRetentionDays;
                        s.LogRetentionRuns = p.LogRetentionRuns;
                    });
                    ServiceLog.Info("Settings changed: " + p.MaxConcurrentJobs + " parallel job(s), logs kept " + p.LogRetentionDays + " days / " + p.LogRetentionRuns + " runs.");
                    return p;
                }
                case Methods.JobsList:
                    return _ctx.Jobs.List().Select(Summary).ToList();
                case Methods.JobsGet:
                    return Detail(Job(P<IdParams>(r).Id));
                case Methods.JobsSave:
                    return SaveJob(P<JobSaveParams>(r));
                case Methods.JobsDelete:
                    return DeleteJob(P<IdParams>(r).Id);
                case Methods.JobsRun:
                {
                    var p = P<RunJobParams>(r);
                    var job = Job(p.Id);
                    return new RunStarted { RunId = _ctx.Runner.Enqueue(job.Id, RunKind.Backup, p.Mode, RunTrigger.Manual).Id };
                }
                case Methods.JobsCancel:
                    _ctx.Runner.Cancel(Job(P<IdParams>(r).Id).Id);
                    return null;
                case Methods.RunsList:
                {
                    var p = P<RunsListParams>(r);
                    string jobId = string.IsNullOrEmpty(p.JobId) ? null : Job(p.JobId).Id;
                    return _ctx.History.List(jobId, p.Limit <= 0 ? 50 : p.Limit);
                }
                case Methods.RunsGet:
                    return _ctx.History.Get(P<IdParams>(r).Id) ?? throw new ApiException(ErrorCodes.NotFound, "Run not found.");
                case Methods.RepoInit:
                    return InitRepository(P<RepoParams>(r));
                case Methods.RepoTest:
                    return TestRepository(P<RepoParams>(r));
                case Methods.SnapshotsList:
                    return Snapshots(Job(P<IdParams>(r).Id));
                case Methods.TreeList:
                    return Tree(P<TreeParams>(r));
                case Methods.RestoreStart:
                {
                    var p = P<RestoreParams>(r);
                    var job = Job(p.JobId);
                    if (p.Options == null)
                        throw new ApiException(ErrorCodes.Invalid, "No restore options given.");
                    return new RunStarted { RunId = _ctx.Runner.Enqueue(job.Id, RunKind.Restore, BackupMode.Incremental, RunTrigger.Manual, 1, p.Options).Id };
                }
                default:
                    throw new ApiException(ErrorCodes.Invalid, "Unknown method '" + r.Method + "'.");
            }
        }

        // ---- Jobs ------------------------------------------------------------------------------

        private ServiceStatus Status() => new ServiceStatus
        {
            Version = ProgramInfo.Version,
            Host = ProgramInfo.Host,
            StartedAt = _ctx.StartedAt,
            DataFolder = _ctx.Paths.Root,
            MaxConcurrentJobs = _ctx.Settings.Get().MaxConcurrentJobs,
            Running = _ctx.Runner.Running(),
            Queued = _ctx.Runner.QueuedJobNames(),
        };

        private JobSummary Summary(JobDefinition job) => new JobSummary
        {
            Id = job.Id,
            Name = job.Name,
            Enabled = job.Enabled,
            Destination = job.Destination?.Path,
            ScheduleText = job.Schedules.Select(ScheduleMath.Describe).ToList(),
            NextRun = _ctx.Scheduler.NextRun(job),
            LastRun = _ctx.History.Last(job.Id),
            LastSuccess = _ctx.History.Last(job.Id, x => x.State == RunState.Success || x.State == RunState.Warning),
            Queued = _ctx.Runner.IsQueued(job.Id),
            Progress = _ctx.Runner.ProgressOf(job.Id),
        };

        private JobDetail Detail(JobDefinition job) => new JobDetail
        {
            Job = job,
            HasRepositoryPassword = _ctx.Jobs.HasPassword(job.Id),
            NextRun = _ctx.Scheduler.NextRun(job),
            ScheduleText = job.Schedules.Select(ScheduleMath.Describe).ToList(),
        };

        private JobDetail SaveJob(JobSaveParams p)
        {
            if (p?.Job == null)
                throw new ApiException(ErrorCodes.Invalid, "No job given.");
            var errors = JobValidation.Validate(p.Job);
            if (errors.Count > 0)
                throw new ApiException(ErrorCodes.Invalid, string.Join(" ", errors));
            p.Job.Name = p.Job.Name.Trim();
            p.Job.Sources = p.Job.Sources.Select(LongPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            p.Job.Destination.Path = LongPath.Normalize(p.Job.Destination.Path);
            p.Job.Excludes = (p.Job.Excludes ?? new List<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
            if (!string.IsNullOrEmpty(p.Job.Id) && _ctx.Jobs.Get(p.Job.Id) != null && _ctx.Runner.IsBusy(p.Job.Id))
                throw new ApiException(ErrorCodes.Conflict, "The job cannot be changed while it is running.");

            if (!string.IsNullOrEmpty(p.RepositoryPassword))
                VerifyPassword(p.Job.Destination.Path, p.RepositoryPassword);

            var saved = _ctx.Jobs.Save(p.Job, p.RepositoryPassword);
            Evict(saved.Id);
            ServiceLog.Info("Job '" + saved.Name + "' saved.");
            return Detail(saved);
        }

        private object DeleteJob(string id)
        {
            var job = Job(id);
            if (_ctx.Runner.IsBusy(job.Id))
                throw new ApiException(ErrorCodes.Conflict, "The job is running. Cancel it first.");
            _ctx.Jobs.Delete(job.Id);
            _ctx.History.DeleteJob(job.Id);
            Evict(job.Id);
            ServiceLog.Info("Job '" + job.Name + "' deleted (its repository was not touched).");
            return null;
        }

        private JobDefinition Job(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName))
                throw new ApiException(ErrorCodes.Invalid, "No job given.");
            return _ctx.Jobs.Resolve(idOrName) ?? throw new ApiException(ErrorCodes.NotFound, "Job '" + idOrName + "' not found.");
        }

        // ---- Repositories ----------------------------------------------------------------------

        private RepoInitResult InitRepository(RepoParams p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Path))
                throw new ApiException(ErrorCodes.Invalid, "No repository location given.");
            if (string.IsNullOrEmpty(p.Password) || p.Password.Length < 8)
                throw new ApiException(ErrorCodes.Invalid, "The repository password must have at least 8 characters.");
            var storage = StorageFactory.Open(p.Path);
            if (Repository.Exists(storage))
                throw new ApiException(ErrorCodes.Conflict, "A Keepsake repository already exists at " + storage.Location + ".");
            var init = Repository.Init(storage, p.Password);
            using (init.Repository)
            {
                ServiceLog.Info("Repository created at " + storage.Location + ".");
                return new RepoInitResult { RepositoryId = init.Repository.Header.Id, RecoveryKey = init.RecoveryKey };
            }
        }

        private RepoTestResult TestRepository(RepoParams p)
        {
            var storage = StorageFactory.Open(p.Path);
            if (!Repository.Exists(storage))
                return new RepoTestResult { Exists = false };
            using (var repo = Repository.Open(storage, RepoCredential.FromPassword(p.Password ?? "")))
                return new RepoTestResult { Exists = true, RepositoryId = repo.Header.Id, Snapshots = repo.ListSnapshots().Count };
        }

        private static void VerifyPassword(string path, string password)
        {
            var storage = StorageFactory.Open(path);
            if (!Repository.Exists(storage))
                return; // repository is created separately (repo.init)
            using (Repository.Open(storage, RepoCredential.FromPassword(password)))
            {
            }
        }

        // ---- Browsing --------------------------------------------------------------------------

        private List<SnapshotInfo> Snapshots(JobDefinition job)
        {
            var repo = Browse(job);
            lock (repo)
            {
                return repo.ListSnapshots()
                    .OrderByDescending(s => s.Snapshot.Time)
                    .Select(s => new SnapshotInfo
                    {
                        Id = s.Id,
                        Time = s.Snapshot.Time,
                        Job = s.Snapshot.Job,
                        Host = s.Snapshot.Host,
                        Mode = s.Snapshot.Mode,
                        Paths = s.Snapshot.Paths,
                        Summary = s.Snapshot.Summary,
                    })
                    .ToList();
            }
        }

        private List<TreeEntry> Tree(TreeParams p)
        {
            var repo = Browse(Job(p.JobId));
            lock (repo)
            {
                repo.LoadIndex();
                var snapshot = repo.FindSnapshot(p.SnapshotId);
                var tree = repo.LoadTree(snapshot.Snapshot.Tree);
                var path = string.IsNullOrEmpty(p.Path) ? new List<string>() : RepoPaths.Parse(p.Path);
                foreach (string part in path)
                {
                    var node = tree.Find(part);
                    if (node == null || node.Type != NodeType.Dir)
                        throw new ApiException(ErrorCodes.NotFound, "'" + RepoPaths.Join(path) + "' is not a folder in this snapshot.");
                    tree = repo.LoadTree(node.Subtree);
                }
                return tree.Nodes.Select(n => new TreeEntry
                {
                    Name = n.Name,
                    Path = RepoPaths.Join(path.Concat(new[] { n.Name })),
                    IsFolder = n.Type == NodeType.Dir,
                    Size = n.Size,
                    Modified = n.ModifiedUtc,
                }).ToList();
            }
        }

        private Repository Browse(JobDefinition job)
        {
            lock (_cacheLock)
            {
                foreach (var stale in _repoCache.Where(c => DateTime.UtcNow - c.Value.LastUse > BrowseCacheIdle).Select(c => c.Key).ToList())
                {
                    _repoCache[stale].Repository.Dispose();
                    _repoCache.Remove(stale);
                }
                if (_repoCache.TryGetValue(job.Id, out var cached))
                {
                    cached.LastUse = DateTime.UtcNow;
                    return cached.Repository;
                }
            }
            var repo = _ctx.Runner.OpenRepository(job); // Argon2: done outside the lock
            lock (_cacheLock)
            {
                _repoCache[job.Id] = new CachedRepo { Repository = repo, LastUse = DateTime.UtcNow };
            }
            return repo;
        }

        private void Evict(string jobId)
        {
            lock (_cacheLock)
            {
                if (jobId != null && _repoCache.TryGetValue(jobId, out var cached))
                {
                    cached.Repository.Dispose();
                    _repoCache.Remove(jobId);
                }
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------

        private static T P<T>(Request r) where T : new()
        {
            if (r.Params == null || r.Params.Value.ValueKind == JsonValueKind.Null)
                return new T();
            try
            {
                return r.Params.Value.Deserialize<T>(Json.Compact) ?? new T();
            }
            catch (JsonException ex)
            {
                throw new ApiException(ErrorCodes.Invalid, "Invalid parameters for " + r.Method + ": " + ex.Message);
            }
        }

        private static Response Error(Request r, string code, string message) =>
            new Response { Id = r?.Id ?? 0, Ok = false, Error = new ApiError { Code = code, Message = message } };

        private sealed class CachedRepo
        {
            public Repository Repository;
            public DateTime LastUse;
        }
    }
}
