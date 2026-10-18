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
                    _ctx.Auth.ChangePassword(p.OldPassword, p.NewPassword, r.Token);
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
                    if (p.Kind == RunKind.Restore)
                        throw new ApiException(ErrorCodes.Invalid, "Use restore.start for restores.");
                    if (p.Kind == RunKind.Prune && (job.Retention == null || job.Retention.IsEmpty))
                        throw new ApiException(ErrorCodes.Invalid, "The job has no retention rules, so there is nothing to clean up.");
                    return new RunStarted { RunId = _ctx.Runner.Enqueue(job.Id, p.Kind, p.Mode, RunTrigger.Manual, fullCheck: p.FullCheck).Id };
                }
                case Methods.SmtpGet:
                {
                    var s = _ctx.Settings.Get();
                    var smtp = s.Smtp ?? new SmtpSettings();
                    smtp.Password = null;
                    smtp.HasPassword = !string.IsNullOrEmpty(s.SmtpPassword);
                    return smtp;
                }
                case Methods.SmtpSave:
                {
                    var p = P<SmtpSettings>(r);
                    if (!string.IsNullOrWhiteSpace(p.Host) && (p.Port < 1 || p.Port > 65535))
                        throw new ApiException(ErrorCodes.Invalid, "The SMTP port must be between 1 and 65535.");
                    if (p.Security != "none" && p.Security != "starttls" && p.Security != "ssl")
                        throw new ApiException(ErrorCodes.Invalid, "SMTP security must be none, starttls or ssl.");
                    p.To = (p.To ?? new List<string>()).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                    foreach (string address in p.To.Concat(string.IsNullOrWhiteSpace(p.From) ? new string[0] : new[] { p.From }))
                    {
                        if (!MimeKit.MailboxAddress.TryParse(address, out _))
                            throw new ApiException(ErrorCodes.Invalid, "'" + address + "' is not a valid e-mail address.");
                    }
                    string password = p.Password;
                    p.Password = null;
                    p.HasPassword = false;
                    _ctx.Settings.Update(s =>
                    {
                        s.Smtp = p;
                        if (password != null)
                            s.SmtpPassword = password.Length == 0 ? null : Secrets.Protect(password);
                    });
                    ServiceLog.Info("SMTP settings changed (" + (p.Enabled ? p.Host + ", " + p.To.Count + " recipient(s)" : "notifications off") + ").");
                    return null;
                }
                case Methods.SmtpTest:
                    _ctx.Notifier.Send("[Keepsake] Test message from " + ProgramInfo.Host,
                        "This is a test message from the Keepsake backup service on " + ProgramInfo.Host + ".\r\nIf you can read it, notifications work.");
                    return null;
                case Methods.SnapshotsProtect:
                {
                    var p = P<ProtectParams>(r);
                    var job = Job(p.JobId);
                    if (_ctx.Runner.IsBusy(job.Id))
                        throw new ApiException(ErrorCodes.Conflict, "The job is running; try again when it has finished.");
                    using (var opened = _ctx.Runner.OpenRepository(job))
                    {
                        var updated = opened.Repository.SetProtected(opened.Repository.FindSnapshot(p.SnapshotId), p.Protect);
                        Evict(job.Id);
                        ServiceLog.Info("Snapshot " + updated.ShortId + " of '" + job.Name + "' " + (p.Protect ? "protected." : "no longer protected."));
                        return new IdParams { Id = updated.Id };
                    }
                }
                case Methods.RetentionPreview:
                {
                    var p = P<RetentionPreviewParams>(r);
                    var job = Job(p.JobId);
                    var repo = Browse(job);
                    lock (repo)
                    {
                        var scoped = repo.ListSnapshots().Where(s => string.Equals(s.Snapshot.Job, job.Name, StringComparison.OrdinalIgnoreCase));
                        return Core.Maintenance.Retention.Apply(scoped, p.Policy, DateTime.UtcNow)
                            .Select(d => new RetentionPreviewItem { SnapshotId = d.Snapshot.Id, Time = d.Snapshot.Snapshot.Time, Keep = d.Keep, Reasons = d.Reasons })
                            .ToList();
                    }
                }
                case Methods.JobsCancel:
                    _ctx.Runner.Cancel(Job(P<IdParams>(r).Id).Id);
                    return null;
                case Methods.RunsList:
                {
                    var p = P<RunsListParams>(r);
                    string jobId = string.IsNullOrEmpty(p.JobId) ? null : Job(p.JobId).Id;
                    return _ctx.History.List(jobId, p.Limit <= 0 ? 50 : Math.Min(p.Limit, 10000)); // bounded response (SEC-015)
                }
                case Methods.RunsGet:
                    return _ctx.History.Get(P<IdParams>(r).Id) ?? throw new ApiException(ErrorCodes.NotFound, "Run not found.");
                case Methods.RepoInit:
                    return InitRepository(P<RepoParams>(r));
                case Methods.RepoTest:
                    return TestRepository(P<RepoParams>(r));
                case Methods.VolumesList:
                    return LocalVolumes.List();
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
            Destination = job.Destination?.ToString(),
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
            HasSftpPassword = _ctx.Jobs.HasSecret(job.Id, SecretKeys.SftpPassword),
            HasSftpKey = _ctx.Jobs.HasSecret(job.Id, SecretKeys.SftpKey),
            SharesWithPassword = job.ShareCredentials.Where(c => _ctx.Jobs.HasSecret(job.Id, SecretKeys.Share(c.Share))).Select(c => c.Share).ToList(),
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
            var d = p.Job.Destination;
            if (d.Type == DestinationTypes.Folder)
                d.Path = LongPath.Normalize(d.Path);
            else if (d.Type == DestinationTypes.Usb)
                d.Path = d.Path.Trim().Trim('\\');
            else if (d.Type == DestinationTypes.Sftp)
                d.Path = "/" + d.Path.Trim().Trim('/');
            p.Job.ShareCredentials = (p.Job.ShareCredentials ?? new List<ShareCredential>())
                .Select(c => new ShareCredential { Share = c.Share.Trim().TrimEnd('\\'), User = c.User.Trim() }).ToList();
            p.Job.Excludes = (p.Job.Excludes ?? new List<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
            var existing = string.IsNullOrEmpty(p.Job.Id) ? null : _ctx.Jobs.Get(p.Job.Id);
            if (existing != null && _ctx.Runner.IsBusy(p.Job.Id))
                throw new ApiException(ErrorCodes.Conflict, "The job cannot be changed while it is running.");

            var secrets = p.Secrets ?? new JobSecrets();
            if (!string.IsNullOrEmpty(p.RepositoryPassword))
                secrets.RepositoryPassword = p.RepositoryPassword;
            if (!string.IsNullOrEmpty(secrets.RepositoryPassword))
            {
                // A wrong password must never be stored: check it against the repository if it exists.
                var resolved = _ctx.Jobs.GetSecrets(existing?.Id).Merge(secrets);
                using (var opened = DestinationResolver.Open(p.Job.Destination, p.Job.ShareCredentials, resolved))
                {
                    if (Repository.Exists(opened.Storage))
                    {
                        using (Repository.Open(opened.Storage, RepoCredential.FromPassword(resolved.RepositoryPassword)))
                        {
                        }
                    }
                }
            }

            var saved = _ctx.Jobs.Save(p.Job, null, secrets);
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

        private OpenedDestination OpenFor(RepoParams p, out ResolvedSecrets secrets)
        {
            if (p?.Destination == null)
                throw new ApiException(ErrorCodes.Invalid, "No destination given.");
            var errors = JobValidation.ValidateDestination(p.Destination);
            if (errors.Count > 0)
                throw new ApiException(ErrorCodes.Invalid, string.Join(" ", errors));
            string jobId = string.IsNullOrEmpty(p.JobId) ? null : p.JobId;
            var stored = _ctx.Jobs.GetSecrets(jobId);
            // Stored SFTP credentials only go to the server they were saved for: a caller must not be able to send
            // them to another host by testing an existing job against a different destination (SEC-010).
            var job = jobId == null ? null : _ctx.Jobs.Get(jobId);
            if (job != null && !SameSftpServer(job.Destination, p.Destination))
            {
                stored.SftpPassword = null;
                stored.SftpKey = null;
                stored.SftpPassphrase = null;
            }
            secrets = stored.Merge(p.Secrets);
            return DestinationResolver.Open(p.Destination, p.ShareCredentials, secrets, p.VolumeId);
        }

        private static bool SameSftpServer(Destination saved, Destination requested) =>
            saved != null && requested != null &&
            saved.Type == DestinationTypes.Sftp && requested.Type == DestinationTypes.Sftp &&
            string.Equals(saved.Host, requested.Host, StringComparison.OrdinalIgnoreCase) &&
            saved.Port == requested.Port &&
            string.Equals(saved.User, requested.User, StringComparison.Ordinal);

        private RepoInitResult InitRepository(RepoParams p)
        {
            using (var opened = OpenFor(p, out var secrets))
            {
                if (string.IsNullOrEmpty(secrets.RepositoryPassword) || secrets.RepositoryPassword.Length < 8)
                    throw new ApiException(ErrorCodes.Invalid, "The repository password must have at least 8 characters.");
                if (Repository.Exists(opened.Storage))
                    throw new ApiException(ErrorCodes.Conflict, "A Keepsake repository already exists at " + opened.Storage.Location + ".");
                var init = Repository.Init(opened.Storage, secrets.RepositoryPassword);
                ServiceLog.Info("Repository created at " + opened.Storage.Location + ".");
                return new RepoInitResult { RepositoryId = init.Repository.Header.Id, RecoveryKey = init.RecoveryKey };
            }
        }

        /// <summary>Destination check (DST-06): reachable, writable, deletable, free space, repository and password.</summary>
        private RepoTestResult TestRepository(RepoParams p)
        {
            var result = new RepoTestResult();
            OpenedDestination opened;
            ResolvedSecrets secrets;
            try
            {
                opened = OpenFor(p, out secrets);
            }
            catch (HostKeyException ex)
            {
                result.UntrustedHostKey = ex.Fingerprint;
                result.HostKeyChanged = ex.Mismatch;
                result.Problems.Add(ex.Message);
                return result;
            }
            catch (KeepsakeException ex) when (!(ex is ApiException))
            {
                result.Problems.Add(ex.Message);
                return result;
            }

            using (opened)
            {
                result.Location = opened.Storage.Location;
                if (opened.Storage is SftpStorage sftp && sftp.Security != null)
                {
                    result.Encryption = sftp.Security.Summary;
                    result.EncryptionNote = sftp.Security.Note;
                }
                var probe = StorageProbe.Test(opened.Storage);
                result.Reachable = probe.Reachable;
                result.Exists = probe.RepositoryExists;
                result.CanWrite = probe.CanWrite;
                result.CanDelete = probe.CanDelete;
                result.FreeBytes = probe.FreeBytes;
                result.Problems.AddRange(probe.Problems);
                if (result.Exists && !string.IsNullOrEmpty(secrets.RepositoryPassword))
                {
                    try
                    {
                        using (var repo = Repository.Open(opened.Storage, RepoCredential.FromPassword(secrets.RepositoryPassword)))
                        {
                            result.RepositoryId = repo.Header.Id;
                            result.Snapshots = repo.ListSnapshots().Count;
                        }
                    }
                    catch (WrongCredentialException ex)
                    {
                        result.Problems.Add(ex.Message);
                    }
                }
            }
            return result;
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
                        Protected = Repository.IsProtected(s.Snapshot),
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
                    _repoCache[stale].Opened.Dispose();
                    _repoCache.Remove(stale);
                }
                if (_repoCache.TryGetValue(job.Id, out var cached))
                {
                    cached.LastUse = DateTime.UtcNow;
                    return cached.Opened.Repository;
                }
            }
            var opened = _ctx.Runner.OpenRepository(job); // Argon2 and network: done outside the lock
            lock (_cacheLock)
            {
                _repoCache[job.Id] = new CachedRepo { Opened = opened, LastUse = DateTime.UtcNow };
            }
            return opened.Repository;
        }

        private void Evict(string jobId)
        {
            lock (_cacheLock)
            {
                if (jobId != null && _repoCache.TryGetValue(jobId, out var cached))
                {
                    cached.Opened.Dispose();
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
            public OpenedRepository Opened;
            public DateTime LastUse;
        }
    }
}
