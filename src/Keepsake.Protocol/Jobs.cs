using System;
using System.Collections.Generic;
using System.Linq;
using Keepsake.Core.Repo;

namespace Keepsake.Protocol
{
    public enum ScheduleKind
    {
        /// <summary>Every N minutes.</summary>
        Interval,

        /// <summary>At given times, on all days or on selected weekdays.</summary>
        Daily,

        /// <summary>At given times on given days of the month (31 = last day of shorter months).</summary>
        Monthly,
    }

    public sealed class ScheduleEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public bool Enabled { get; set; } = true;
        public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;
        public int IntervalMinutes { get; set; }

        /// <summary>Local times, <c>HH:mm</c>.</summary>
        public List<string> Times { get; set; } = new List<string>();

        /// <summary>Daily schedules: allowed weekdays. Empty = every day.</summary>
        public List<DayOfWeek> Weekdays { get; set; } = new List<DayOfWeek>();

        public List<int> DaysOfMonth { get; set; } = new List<int>();
        public BackupMode Mode { get; set; } = BackupMode.Incremental;
    }

    /// <summary>Where a job's repository lives. Milestone 4 adds share credentials, USB volumes and SFTP.</summary>
    public sealed class Destination
    {
        public string Type { get; set; } = DestinationTypes.Folder;

        /// <summary>
        /// folder: absolute local or UNC path. usb: folder on the disk, relative (<c>Keepsake\Fileserver</c>).
        /// sftp: remote folder (<c>/backups/fileserver</c>).
        /// </summary>
        public string Path { get; set; }

        /// <summary>USB disks that may hold this job's backups (rotation). Identified by volume ID, not drive letter.</summary>
        public List<UsbVolume> Volumes { get; set; } = new List<UsbVolume>();

        public string Host { get; set; }
        public int Port { get; set; } = 22;
        public string User { get; set; }

        /// <summary>SFTP: <c>password</c> or <c>key</c>.</summary>
        public string Auth { get; set; } = "password";

        /// <summary>SFTP: pinned host key, <c>SHA256:...</c>.</summary>
        public string HostKeyFingerprint { get; set; }

        public override string ToString()
        {
            switch (Type)
            {
                case DestinationTypes.Usb:
                    return "USB disk " + string.Join(" / ", (Volumes ?? new List<UsbVolume>()).Select(v => v.Label ?? v.VolumeId)) + " \\" + (Path ?? "").TrimStart('\\');
                case DestinationTypes.Sftp:
                    return "sftp://" + User + "@" + Host + (Port == 22 ? "" : ":" + Port) + "/" + (Path ?? "").TrimStart('/');
                default:
                    return Path;
            }
        }
    }

    public static class DestinationTypes
    {
        public const string Folder = "folder";
        public const string Usb = "usb";
        public const string Sftp = "sftp";
    }

    public sealed class UsbVolume
    {
        /// <summary><c>Volume{GUID}</c> as reported by Windows; survives drive letter changes.</summary>
        public string VolumeId { get; set; }
        public string Label { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>Credentials for one network share, used only for the connection (not the service account).</summary>
    public sealed class ShareCredential
    {
        /// <summary><c>\\server\share</c></summary>
        public string Share { get; set; }

        /// <summary><c>DOMAIN\user</c> or <c>server\user</c>.</summary>
        public string User { get; set; }
    }

    /// <summary>
    /// Secrets sent to the service (never returned). Null = keep the stored value, empty string = remove it.
    /// </summary>
    public sealed class JobSecrets
    {
        public string RepositoryPassword { get; set; }
        public string SftpPassword { get; set; }
        public string SftpPrivateKey { get; set; }
        public string SftpKeyPassphrase { get; set; }

        /// <summary>Share (<c>\\server\share</c>) → password.</summary>
        public Dictionary<string, string> SharePasswords { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A backup job as stored by the service. Secrets (repository password) are kept separately.</summary>
    public sealed class JobDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; } = true;
        public List<string> Sources { get; set; } = new List<string>();
        public List<string> Excludes { get; set; } = new List<string>();
        public bool UseDefaultExcludes { get; set; } = true;
        public Destination Destination { get; set; } = new Destination();

        /// <summary>Credentials for network shares used as source or destination (SRC-02, DST-02).</summary>
        public List<ShareCredential> ShareCredentials { get; set; } = new List<ShareCredential>();

        public List<ScheduleEntry> Schedules { get; set; } = new List<ScheduleEntry>();

        /// <summary>Run once after start-up if a scheduled run was missed while the service was stopped.</summary>
        public bool RunMissed { get; set; } = true;

        public int RetryCount { get; set; } = 2;
        public int RetryDelayMinutes { get; set; } = 15;

        /// <summary>Command run before the backup (cmd.exe). A non-zero exit code fails the run.</summary>
        public string PreCommand { get; set; }

        /// <summary>Command run after the backup, always. Gets <c>KEEPSAKE_STATUS</c> in its environment.</summary>
        public string PostCommand { get; set; }

        public int CommandTimeoutMinutes { get; set; } = 30;

        /// <summary>Parallel file workers; 0 = automatic.</summary>
        public int Workers { get; set; }

        /// <summary>Read from VSS snapshots so open files are backed up (SRC-08).</summary>
        public bool UseVss { get; set; } = true;

        /// <summary>Which snapshots to keep. Null or empty = keep all (RET-01).</summary>
        public Core.Maintenance.RetentionPolicy Retention { get; set; }

        /// <summary>After a backup: apply retention and free space if the last cleanup is older than this. 0 = never.</summary>
        public int PruneEveryDays { get; set; } = 7;

        /// <summary>After a backup: verify the repository if the last check is older than this. 0 = never (VER-04).</summary>
        public int CheckEveryDays { get; set; } = 7;

        /// <summary>Share of stored data read and decrypted by scheduled checks (0 = structure only).</summary>
        public int CheckReadDataPercent { get; set; } = 10;

        public bool NotifyOnFailure { get; set; } = true;
        public bool NotifyOnWarning { get; set; } = true;
        public bool NotifyOnSuccess { get; set; }

        /// <summary>Alert when there was no successful backup for this many hours. 0 = off (NOT-02).</summary>
        public int AlertNoSuccessHours { get; set; }

        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
    }

    public static class JobValidation
    {
        public static List<string> Validate(JobDefinition job)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(job.Name))
                errors.Add("The job needs a name.");
            if (job.Sources == null || job.Sources.Count == 0)
                errors.Add("Select at least one folder or file to back up.");
            else
            {
                foreach (string s in job.Sources)
                {
                    if (!IsAbsolute(s))
                        errors.Add("'" + s + "' is not an absolute path.");
                }
            }
            errors.AddRange(ValidateDestination(job.Destination));
            if (job.Retention != null)
                errors.AddRange(job.Retention.Validate());
            if (job.PruneEveryDays < 0 || job.PruneEveryDays > 365 || job.CheckEveryDays < 0 || job.CheckEveryDays > 365)
                errors.Add("Cleanup and check intervals must be between 0 and 365 days.");
            if (job.CheckReadDataPercent < 0 || job.CheckReadDataPercent > 100)
                errors.Add("The share of data read by checks must be between 0 and 100 %.");
            if (job.AlertNoSuccessHours < 0 || job.AlertNoSuccessHours > 24 * 90)
                errors.Add("The 'no successful backup' alert must be between 0 hours and 90 days.");
            foreach (var c in job.ShareCredentials ?? new List<ShareCredential>())
            {
                if (string.IsNullOrWhiteSpace(c.Share) || !c.Share.StartsWith(@"\\", StringComparison.Ordinal) || c.Share.Trim('\\').Split('\\').Length != 2)
                    errors.Add("'" + c.Share + "' is not a network share (use \\\\server\\share).");
                if (string.IsNullOrWhiteSpace(c.User))
                    errors.Add("Enter the user name for " + c.Share + ".");
            }
            if (job.RetryCount < 0 || job.RetryCount > 10)
                errors.Add("Retries must be between 0 and 10.");
            if (job.RetryDelayMinutes < 1 || job.RetryDelayMinutes > 24 * 60)
                errors.Add("The retry delay must be between 1 minute and 24 hours.");
            if (job.CommandTimeoutMinutes < 1 || job.CommandTimeoutMinutes > 24 * 60)
                errors.Add("The command timeout must be between 1 minute and 24 hours.");
            foreach (var s in job.Schedules ?? new List<ScheduleEntry>())
                errors.AddRange(ScheduleMath.Validate(s));
            return errors;
        }

        public static List<string> ValidateDestination(Destination d)
        {
            var errors = new List<string>();
            if (d == null)
            {
                errors.Add("Choose where the backups are stored.");
                return errors;
            }
            switch (d.Type)
            {
                case DestinationTypes.Folder:
                    if (string.IsNullOrWhiteSpace(d.Path))
                        errors.Add("Choose where the backups are stored.");
                    else if (!IsAbsolute(d.Path))
                        errors.Add("The backup destination must be an absolute path.");
                    break;
                case DestinationTypes.Usb:
                    if (d.Volumes == null || d.Volumes.Count == 0)
                        errors.Add("Add at least one USB disk.");
                    if (string.IsNullOrWhiteSpace(d.Path) || IsAbsolute(d.Path))
                        errors.Add("Enter the folder on the USB disk without drive letter, e.g. Keepsake\\Fileserver.");
                    break;
                case DestinationTypes.Sftp:
                    if (string.IsNullOrWhiteSpace(d.Host))
                        errors.Add("Enter the SFTP server.");
                    if (d.Port < 1 || d.Port > 65535)
                        errors.Add("The SFTP port must be between 1 and 65535.");
                    if (string.IsNullOrWhiteSpace(d.User))
                        errors.Add("Enter the SFTP user name.");
                    if (string.IsNullOrWhiteSpace(d.Path) || !d.Path.StartsWith("/", StringComparison.Ordinal))
                        errors.Add("Enter the remote folder as an absolute path, e.g. /backups/fileserver.");
                    if (d.Auth != "password" && d.Auth != "key")
                        errors.Add("SFTP authentication must be 'password' or 'key'.");
                    break;
                default:
                    errors.Add("Unknown destination type '" + d.Type + "'.");
                    break;
            }
            return errors;
        }

        private static bool IsAbsolute(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            ((path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) || path.StartsWith(@"\\", StringComparison.Ordinal));
    }
}
