using System;
using System.Collections.Generic;
using System.Text.Json;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;

namespace Keepsake.Protocol
{
    /// <summary>API method names. Every method except <c>auth.*</c> requires a session token.</summary>
    public static class Methods
    {
        public const string AuthStatus = "auth.status";
        public const string AuthSetup = "auth.setup";
        public const string AuthLogin = "auth.login";
        public const string AuthLogout = "auth.logout";
        public const string AuthChangePassword = "auth.changePassword";

        public const string Status = "service.status";
        public const string SettingsGet = "settings.get";
        public const string SettingsSave = "settings.save";

        public const string JobsList = "jobs.list";
        public const string JobsGet = "jobs.get";
        public const string JobsSave = "jobs.save";
        public const string JobsDelete = "jobs.delete";
        public const string JobsRun = "jobs.run";
        public const string JobsCancel = "jobs.cancel";

        public const string RunsList = "runs.list";
        public const string RunsGet = "runs.get";

        public const string RepoInit = "repo.init";
        public const string RepoTest = "repo.test";
        public const string VolumesList = "volumes.list";

        public const string SnapshotsList = "snapshots.list";
        public const string TreeList = "tree.list";
        public const string RestoreStart = "restore.start";
        public const string SnapshotsProtect = "snapshots.protect";
        public const string RetentionPreview = "retention.preview";
        public const string SmtpGet = "smtp.get";
        public const string SmtpSave = "smtp.save";
        public const string SmtpTest = "smtp.test";
    }

    public static class ErrorCodes
    {
        public const string SetupRequired = "setup_required";
        public const string Unauthorized = "unauthorized";
        public const string LockedOut = "locked_out";
        public const string NotFound = "not_found";
        public const string Invalid = "invalid";
        public const string Conflict = "conflict";
        public const string Failed = "failed";
    }

    // ---- Wire envelope -------------------------------------------------------------------------

    public sealed class Request
    {
        public int Id { get; set; }
        public string Method { get; set; }
        public string Token { get; set; }
        public JsonElement? Params { get; set; }
    }

    public sealed class Response
    {
        public int Id { get; set; }
        public bool Ok { get; set; }
        public ApiError Error { get; set; }
        public JsonElement? Result { get; set; }
    }

    public sealed class ApiError
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }

    // ---- Auth ----------------------------------------------------------------------------------

    public sealed class AuthStatusResult
    {
        public bool PasswordSet { get; set; }
    }

    public sealed class PasswordParams
    {
        public string Password { get; set; }
    }

    public sealed class ChangePasswordParams
    {
        public string OldPassword { get; set; }
        public string NewPassword { get; set; }
    }

    public sealed class LoginResult
    {
        public string Token { get; set; }
        public DateTime ExpiresIdle { get; set; }
    }

    // ---- Service status ------------------------------------------------------------------------

    public sealed class ServiceStatus
    {
        public string Version { get; set; }
        public string Host { get; set; }
        public DateTime StartedAt { get; set; }
        public string DataFolder { get; set; }
        public int MaxConcurrentJobs { get; set; }
        public List<RunProgress> Running { get; set; } = new List<RunProgress>();
        public List<string> Queued { get; set; } = new List<string>();
    }

    public sealed class ServiceSettings
    {
        public int MaxConcurrentJobs { get; set; } = 1;
        public int LogRetentionDays { get; set; } = 90;
        public int LogRetentionRuns { get; set; } = 200;

        /// <summary>Debug log with SFTP connection details (for support).</summary>
        public bool DebugLog { get; set; }

        /// <summary>Where the service writes the debug log (read-only).</summary>
        public string DebugLogPath { get; set; }
    }

    // ---- Jobs ----------------------------------------------------------------------------------

    public sealed class IdParams
    {
        public string Id { get; set; }
    }

    public sealed class JobSaveParams
    {
        public JobDefinition Job { get; set; }

        /// <summary>Repository password; null keeps the stored one. (Same as Secrets.RepositoryPassword.)</summary>
        public string RepositoryPassword { get; set; }

        /// <summary>Destination and share secrets; null fields keep the stored values.</summary>
        public JobSecrets Secrets { get; set; }
    }

    public sealed class JobDetail
    {
        public JobDefinition Job { get; set; }
        public bool HasRepositoryPassword { get; set; }
        public bool HasSftpPassword { get; set; }
        public bool HasSftpKey { get; set; }

        /// <summary>Shares that have a stored password.</summary>
        public List<string> SharesWithPassword { get; set; } = new List<string>();

        public DateTime? NextRun { get; set; }
        public List<string> ScheduleText { get; set; } = new List<string>();
    }

    public sealed class JobSummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public string Destination { get; set; }
        public List<string> ScheduleText { get; set; } = new List<string>();
        public DateTime? NextRun { get; set; }
        public RunRecord LastRun { get; set; }
        public RunRecord LastSuccess { get; set; }
        public bool Queued { get; set; }
        public RunProgress Progress { get; set; }
    }

    public sealed class RunJobParams
    {
        public string Id { get; set; }
        public RunKind Kind { get; set; } = RunKind.Backup;
        public BackupMode Mode { get; set; } = BackupMode.Incremental;

        /// <summary>Check runs: read and decrypt all data instead of the job's sample.</summary>
        public bool FullCheck { get; set; }
    }

    public sealed class ProtectParams
    {
        public string JobId { get; set; }
        public string SnapshotId { get; set; }
        public bool Protect { get; set; }
    }

    public sealed class RetentionPreviewParams
    {
        public string JobId { get; set; }
        public Core.Maintenance.RetentionPolicy Policy { get; set; }
    }

    public sealed class RetentionPreviewItem
    {
        public string SnapshotId { get; set; }
        public DateTime Time { get; set; }
        public bool Keep { get; set; }
        public List<string> Reasons { get; set; }
    }

    public sealed class SmtpSettings
    {
        public string Host { get; set; }
        public int Port { get; set; } = 587;

        /// <summary><c>none</c>, <c>starttls</c> or <c>ssl</c>.</summary>
        public string Security { get; set; } = "starttls";

        public string User { get; set; }
        public string From { get; set; }
        public List<string> To { get; set; } = new List<string>();

        /// <summary>Only sent to the service; never returned.</summary>
        public string Password { get; set; }

        public bool HasPassword { get; set; }
        public bool Enabled => !string.IsNullOrWhiteSpace(Host) && To != null && To.Count > 0;
    }

    public sealed class RunStarted
    {
        public string RunId { get; set; }
    }

    // ---- Runs ----------------------------------------------------------------------------------

    public enum RunKind
    {
        Backup,
        Restore,
        Check,

        /// <summary>Apply retention and free unused space.</summary>
        Prune,
    }

    public enum RunTrigger
    {
        Manual,
        Schedule,
        Missed,
        Retry,
    }

    public enum RunState
    {
        Queued,
        Running,
        Success,
        Warning,
        Failed,
        Cancelled,
    }

    /// <summary>One entry of a job's run history.</summary>
    public sealed class RunRecord
    {
        public string Id { get; set; }
        public string JobId { get; set; }
        public string JobName { get; set; }
        public RunKind Kind { get; set; }
        public RunTrigger Trigger { get; set; }
        public BackupMode Mode { get; set; }
        public RunState State { get; set; }
        public DateTime Start { get; set; }
        public DateTime? End { get; set; }
        public double DurationSeconds { get; set; }
        public string SnapshotId { get; set; }
        public SnapshotSummary Summary { get; set; }
        public RestoreStats RestoreStats { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public string Message { get; set; }
        public int Attempt { get; set; } = 1;
    }

    public sealed class RunDetail
    {
        public RunRecord Record { get; set; }
        public List<PhaseTiming> Phases { get; set; } = new List<PhaseTiming>();
        public List<Issue> Issues { get; set; } = new List<Issue>();
        public string Log { get; set; }
    }

    public sealed class RunProgress
    {
        public string RunId { get; set; }
        public string JobId { get; set; }
        public string JobName { get; set; }
        public RunKind Kind { get; set; }
        public string Phase { get; set; }
        public DateTime Start { get; set; }
        public long Files { get; set; }
        public long BytesRead { get; set; }
        public long BytesStored { get; set; }
        public string CurrentPath { get; set; }
    }

    public sealed class RunsListParams
    {
        public string JobId { get; set; }
        public int Limit { get; set; } = 50;
    }

    // ---- Repositories / restore ----------------------------------------------------------------

    /// <summary>
    /// A repository location for repo.init / repo.test. With <see cref="JobId"/>, secrets that are not given
    /// are taken from that job (editing an existing job without re-entering every password).
    /// </summary>
    public sealed class RepoParams
    {
        public string JobId { get; set; }
        public Destination Destination { get; set; }
        public List<ShareCredential> ShareCredentials { get; set; } = new List<ShareCredential>();
        public JobSecrets Secrets { get; set; } = new JobSecrets();

        /// <summary>USB: the disk to use (default: the first connected disk of the destination).</summary>
        public string VolumeId { get; set; }
    }

    public sealed class RepoInitResult
    {
        public string RepositoryId { get; set; }
        public string RecoveryKey { get; set; }
    }

    public sealed class RepoTestResult
    {
        /// <summary>Where the destination resolved to (e.g. the current drive of a USB disk).</summary>
        public string Location { get; set; }

        public bool Reachable { get; set; }
        public bool Exists { get; set; }
        public string RepositoryId { get; set; }
        public int Snapshots { get; set; }
        public bool CanWrite { get; set; }
        public bool CanDelete { get; set; }
        public long? FreeBytes { get; set; }
        public List<string> Problems { get; set; } = new List<string>();

        /// <summary>SFTP: negotiated encryption and its rating (e.g. "aes256-gcm@openssh.com (authenticated encryption), … – strong").</summary>
        public string Encryption { get; set; }

        /// <summary>SFTP: why the encryption is not the strongest possible, and what would help. Null when strong.</summary>
        public string EncryptionNote { get; set; }

        /// <summary>SFTP host key that is not trusted yet (or changed). The client asks the user and retries with it pinned.</summary>
        public string UntrustedHostKey { get; set; }
        public bool HostKeyChanged { get; set; }
    }

    public sealed class SnapshotInfo
    {
        public string Id { get; set; }
        public DateTime Time { get; set; }
        public string Job { get; set; }
        public string Host { get; set; }
        public BackupMode Mode { get; set; }
        public List<string> Paths { get; set; }
        public SnapshotSummary Summary { get; set; }
        public bool Protected { get; set; }
    }

    public sealed class TreeParams
    {
        public string JobId { get; set; }
        public string SnapshotId { get; set; }

        /// <summary>Path inside the snapshot (<c>D/Data</c>); empty for the root.</summary>
        public string Path { get; set; }
    }

    public sealed class TreeEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }

    public sealed class RestoreParams
    {
        public string JobId { get; set; }
        public RestoreOptions Options { get; set; }
    }
}
