using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Keepsake.Core.Crypto;
using Keepsake.Core.Util;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    internal sealed class StoredJob
    {
        public JobDefinition Job { get; set; }

        /// <summary>Repository password, DPAPI-protected.</summary>
        public string RepositoryPassword { get; set; }
    }

    internal sealed class JobsFile
    {
        public int Version { get; set; } = 1;
        public List<StoredJob> Jobs { get; set; } = new List<StoredJob>();
    }

    /// <summary>Job definitions (<c>config\jobs.json</c>). Thread-safe; every change is written immediately.</summary>
    internal sealed class JobStore
    {
        private readonly string _file;
        private readonly object _lock = new object();
        private JobsFile _data;

        public JobStore(string file)
        {
            _file = file;
            _data = File.Exists(file) ? Json.FromBytes<JobsFile>(File.ReadAllBytes(file)) : new JobsFile();
        }

        public List<JobDefinition> List()
        {
            lock (_lock)
                return _data.Jobs.Select(j => Clone(j.Job)).OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public JobDefinition Get(string id)
        {
            lock (_lock)
            {
                var stored = Find(id);
                return stored == null ? null : Clone(stored.Job);
            }
        }

        /// <summary>Finds a job by ID or (case-insensitive) name.</summary>
        public JobDefinition Resolve(string idOrName)
        {
            lock (_lock)
            {
                var stored = Find(idOrName) ?? _data.Jobs.FirstOrDefault(j => string.Equals(j.Job.Name, idOrName, StringComparison.OrdinalIgnoreCase));
                return stored == null ? null : Clone(stored.Job);
            }
        }

        public bool HasPassword(string id)
        {
            lock (_lock)
                return !string.IsNullOrEmpty(Find(id)?.RepositoryPassword);
        }

        public string GetPassword(string id)
        {
            string stored;
            lock (_lock)
                stored = Find(id)?.RepositoryPassword;
            return Secrets.Unprotect(stored);
        }

        /// <summary>Creates or updates a job. <paramref name="repositoryPassword"/> null keeps the stored password.</summary>
        public JobDefinition Save(JobDefinition job, string repositoryPassword)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var stored = string.IsNullOrEmpty(job.Id) ? null : Find(job.Id);
                if (_data.Jobs.Any(j => j != stored && string.Equals(j.Job.Name, job.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(ErrorCodes.Conflict, "A job named '" + job.Name + "' already exists.");

                var copy = Clone(job);
                if (stored == null)
                {
                    if (string.IsNullOrEmpty(repositoryPassword))
                        throw new ApiException(ErrorCodes.Invalid, "A new job needs the repository password.");
                    copy.Id = string.IsNullOrEmpty(copy.Id) ? Guid.NewGuid().ToString("N") : copy.Id;
                    copy.Created = now;
                    stored = new StoredJob();
                    _data.Jobs.Add(stored);
                }
                else
                {
                    copy.Created = stored.Job.Created;
                }
                copy.Modified = now;
                foreach (var s in copy.Schedules.Where(s => string.IsNullOrEmpty(s.Id)))
                    s.Id = Guid.NewGuid().ToString("N");
                stored.Job = copy;
                if (!string.IsNullOrEmpty(repositoryPassword))
                    stored.RepositoryPassword = Secrets.Protect(repositoryPassword);
                Persist();
                return Clone(copy);
            }
        }

        public bool Delete(string id)
        {
            lock (_lock)
            {
                var stored = Find(id);
                if (stored == null)
                    return false;
                _data.Jobs.Remove(stored);
                Persist();
                return true;
            }
        }

        private StoredJob Find(string id) => _data.Jobs.FirstOrDefault(j => j.Job.Id == id);

        private void Persist() => AtomicFile.Write(_file, Json.ToBytes(_data, indented: true));

        private static JobDefinition Clone(JobDefinition job) => Json.FromBytes<JobDefinition>(Json.ToBytes(job));
    }

    internal sealed class PasswordHash
    {
        public KdfParams Kdf { get; set; }
        public string Hash { get; set; }
    }

    internal sealed class Settings
    {
        public int Version { get; set; } = 1;
        public PasswordHash AppPassword { get; set; }
        public int MaxConcurrentJobs { get; set; } = 1;
        public int LogRetentionDays { get; set; } = 90;
        public int LogRetentionRuns { get; set; } = 200;
    }

    /// <summary>Service settings (<c>config\settings.json</c>).</summary>
    internal sealed class SettingsStore
    {
        private readonly string _file;
        private readonly object _lock = new object();
        private Settings _settings;

        public SettingsStore(string file)
        {
            _file = file;
            _settings = File.Exists(file) ? Json.FromBytes<Settings>(File.ReadAllBytes(file)) : new Settings();
        }

        public Settings Get()
        {
            lock (_lock)
                return Json.FromBytes<Settings>(Json.ToBytes(_settings));
        }

        public void Update(Action<Settings> change)
        {
            lock (_lock)
            {
                var copy = Get();
                change(copy);
                AtomicFile.Write(_file, Json.ToBytes(copy, indented: true));
                _settings = copy;
            }
        }
    }

    internal sealed class SchedulerState
    {
        /// <summary>Last time each schedule fired (key: jobId/scheduleId), UTC.</summary>
        public Dictionary<string, DateTime> LastFired { get; set; } = new Dictionary<string, DateTime>();

        public DateTime LastHistoryPrune { get; set; }
    }
}
