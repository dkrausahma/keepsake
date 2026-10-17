using System;
using System.Collections.Generic;
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
        public string Type { get; set; } = "folder";
        public string Path { get; set; }

        public override string ToString() => Path;
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
            if (job.Destination == null || string.IsNullOrWhiteSpace(job.Destination.Path))
                errors.Add("Choose where the backups are stored.");
            else if (job.Destination.Type != "folder")
                errors.Add("Destination type '" + job.Destination.Type + "' is not supported yet.");
            else if (!IsAbsolute(job.Destination.Path))
                errors.Add("The backup destination must be an absolute path.");
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

        private static bool IsAbsolute(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            ((path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) || path.StartsWith(@"\\", StringComparison.Ordinal));
    }
}
