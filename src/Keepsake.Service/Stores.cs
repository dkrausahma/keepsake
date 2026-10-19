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

        /// <summary>Other secrets (SFTP, share passwords), DPAPI-protected. Keys: see <see cref="SecretKeys"/>.</summary>
        public Dictionary<string, string> Secrets { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class SecretKeys
    {
        public const string SftpPassword = "sftp.password";
        public const string SftpKey = "sftp.key";
        public const string SftpPassphrase = "sftp.passphrase";
        public static string Share(string share) => "share:" + share.TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>Decrypted secrets of one job, used only while a run or a test is in progress.</summary>
    internal sealed class ResolvedSecrets
    {
        public string RepositoryPassword { get; set; }
        public string SftpPassword { get; set; }
        public string SftpKey { get; set; }
        public string SftpPassphrase { get; set; }
        public Dictionary<string, string> SharePasswords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Values in <paramref name="given"/> win over this (stored) set; null means "not given".</summary>
        public ResolvedSecrets Merge(JobSecrets given)
        {
            if (given == null)
                return this;
            var r = new ResolvedSecrets
            {
                RepositoryPassword = given.RepositoryPassword ?? RepositoryPassword,
                SftpPassword = given.SftpPassword ?? SftpPassword,
                SftpKey = given.SftpPrivateKey ?? SftpKey,
                SftpPassphrase = given.SftpKeyPassphrase ?? SftpPassphrase,
            };
            foreach (var kv in SharePasswords)
                r.SharePasswords[kv.Key] = kv.Value;
            foreach (var kv in given.SharePasswords ?? new Dictionary<string, string>())
            {
                if (kv.Value != null)
                    r.SharePasswords[kv.Key.TrimEnd('\\')] = kv.Value;
            }
            return r;
        }
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
            foreach (var j in _data.Jobs)
            {
                j.Secrets = new Dictionary<string, string>(j.Secrets ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                j.Job.ShareCredentials = j.Job.ShareCredentials ?? new List<ShareCredential>();
                j.Job.Destination.Volumes = j.Job.Destination.Volumes ?? new List<UsbVolume>();
            }
            if (UpgradeSecrets())
                Persist();
        }

        /// <summary>Converts secrets stored in the old machine-scope format (SEC-001). True if anything changed.</summary>
        private bool UpgradeSecrets()
        {
            bool changed = false;
            foreach (var j in _data.Jobs)
            {
                string pw = Secrets.Upgrade(j.RepositoryPassword);
                if (pw != j.RepositoryPassword)
                {
                    j.RepositoryPassword = pw;
                    changed = true;
                }
                foreach (string key in j.Secrets.Keys.ToList())
                {
                    string v = Secrets.Upgrade(j.Secrets[key]);
                    if (v != j.Secrets[key])
                    {
                        j.Secrets[key] = v;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        public bool HasSecret(string id, string key)
        {
            lock (_lock)
            {
                var stored = Find(id);
                return stored != null && stored.Secrets.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);
            }
        }

        /// <summary>All secrets of a job, decrypted. Empty (not null) for unknown jobs.</summary>
        public ResolvedSecrets GetSecrets(string id)
        {
            StoredJob stored;
            lock (_lock)
                stored = id == null ? null : Find(id);
            var r = new ResolvedSecrets();
            if (stored == null)
                return r;
            r.RepositoryPassword = Secrets.Unprotect(stored.RepositoryPassword);
            r.SftpPassword = GetSecret(stored, SecretKeys.SftpPassword);
            r.SftpKey = GetSecret(stored, SecretKeys.SftpKey);
            r.SftpPassphrase = GetSecret(stored, SecretKeys.SftpPassphrase);
            foreach (var c in stored.Job.ShareCredentials ?? new List<ShareCredential>())
            {
                string pw = GetSecret(stored, SecretKeys.Share(c.Share));
                if (pw != null)
                    r.SharePasswords[c.Share.TrimEnd('\\')] = pw;
            }
            return r;
        }

        private static void ApplySecret(StoredJob stored, string key, string value)
        {
            if (value == null)
                return;
            if (value.Length == 0)
                stored.Secrets.Remove(key);
            else
                stored.Secrets[key] = Secrets.Protect(value);
        }

        private static string GetSecret(StoredJob stored, string key) =>
            stored.Secrets.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? Secrets.Unprotect(v) : null;

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

        /// <summary>Creates or updates a job. Null secrets keep the stored values, empty strings remove them.</summary>
        public JobDefinition Save(JobDefinition job, string repositoryPassword, JobSecrets secrets = null)
        {
            repositoryPassword = repositoryPassword ?? secrets?.RepositoryPassword;
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
                    // The ID becomes a folder name under runs\: accept only the GUI's own format (SEC-011).
                    copy.Id = IsJobId(copy.Id) ? copy.Id : Guid.NewGuid().ToString("N");
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
                if (secrets != null)
                {
                    ApplySecret(stored, SecretKeys.SftpPassword, secrets.SftpPassword);
                    ApplySecret(stored, SecretKeys.SftpKey, secrets.SftpPrivateKey);
                    ApplySecret(stored, SecretKeys.SftpPassphrase, secrets.SftpKeyPassphrase);
                    foreach (var kv in secrets.SharePasswords ?? new Dictionary<string, string>())
                        ApplySecret(stored, SecretKeys.Share(kv.Key), kv.Value);
                }
                // Drop passwords of shares that are no longer configured.
                var keep = new HashSet<string>((copy.ShareCredentials ?? new List<ShareCredential>()).Select(c => SecretKeys.Share(c.Share)), StringComparer.OrdinalIgnoreCase);
                foreach (string key in stored.Secrets.Keys.Where(k => k.StartsWith("share:", StringComparison.OrdinalIgnoreCase) && !keep.Contains(k)).ToList())
                    stored.Secrets.Remove(key);
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

        internal static bool IsJobId(string id) =>
            id != null && id.Length == 32 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

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

        /// <summary>Write logs\debug.log with SFTP connection details (support; off by default).</summary>
        public bool DebugLog { get; set; }

        /// <summary>SMTP for notifications; the password is DPAPI-protected in <see cref="SmtpPassword"/>.</summary>
        public SmtpSettings Smtp { get; set; } = new SmtpSettings();

        public string SmtpPassword { get; set; }
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
            string smtp = Secrets.Upgrade(_settings.SmtpPassword);
            if (smtp != _settings.SmtpPassword)
                Update(s => s.SmtpPassword = smtp); // old machine-scope format (SEC-001)
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

        /// <summary>Last "no successful backup" alert per job, UTC.</summary>
        public Dictionary<string, DateTime> LastNoSuccessAlert { get; set; } = new Dictionary<string, DateTime>();
    }
}
