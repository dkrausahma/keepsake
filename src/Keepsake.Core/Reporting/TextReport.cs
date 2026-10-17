using System.Collections.Generic;
using System.Linq;
using System.Text;
using Keepsake.Core.Backup;
using Keepsake.Core.Check;
using Keepsake.Core.Restore;

namespace Keepsake.Core.Reporting
{
    /// <summary>Human-readable run logs, used for console output and log files.</summary>
    public static class TextReport
    {
        public static List<string> SummaryLines(BackupResult r)
        {
            var s = r.Summary;
            var lines = new List<string>();
            if (r.SnapshotId != null)
                lines.Add("Snapshot " + r.SnapshotId.Substring(0, 8) + " saved (" + r.Mode.ToString().ToLowerInvariant() +
                          (r.ParentId != null ? ", based on " + r.ParentId.Substring(0, 8) : ", first backup") + ")");
            lines.Add("Files:     " + Format.Count(s.FilesNew) + " new, " + Format.Count(s.FilesChanged) + " changed, " +
                      Format.Count(s.FilesUnchanged) + " unchanged, " + Format.Count(s.FilesSkipped) + " not backed up");
            lines.Add("Folders:   " + Format.Count(s.Dirs) + (r.FilesExcluded > 0 ? "   (" + Format.Count(r.FilesExcluded) + " items excluded)" : ""));
            lines.Add("Data:      " + Format.Bytes(s.TotalBytes) + " total, " + Format.Bytes(s.BytesRead) + " read, " +
                      Format.Bytes(s.BytesAdded) + " new, " + Format.Bytes(s.BytesStored) + " stored after compression");
            lines.Add(DurationLine(r));
            return lines;
        }

        public static List<string> SummaryLines(RestoreResult r)
        {
            var s = r.Stats;
            var lines = new List<string>();
            if (r.SnapshotId != null)
                lines.Add((r.DryRun ? "Dry run – nothing written. " : "") + "Snapshot " + r.SnapshotId.Substring(0, 8) + " from " + Format.LocalTime(r.SnapshotTime));
            lines.Add("Files:     " + Format.Count(s.FilesRestored) + (r.DryRun ? " would be restored, " : " restored, ") +
                      Format.Count(s.FilesUnchanged) + " already up to date, " + Format.Count(s.FilesSkipped) + " kept (conflict), " +
                      Format.Count(s.FilesFailed) + " failed");
            lines.Add("Data:      " + Format.Bytes(s.BytesWritten) + (r.DryRun ? " would be written" : " written"));
            lines.Add(DurationLine(r));
            return lines;
        }

        public static List<string> SummaryLines(CheckResult r)
        {
            var lines = new List<string>
            {
                "Checked:   " + r.Snapshots + " snapshots, " + Format.Count(r.Trees) + " folder listings, " + Format.Count(r.Packs) + " packs, " + r.IndexFiles + " index files",
            };
            if (r.PacksVerified > 0)
                lines.Add("Verified:  " + Format.Count(r.PacksVerified) + " packs, " + Format.Bytes(r.BytesVerified) + " read and decrypted");
            lines.Add("Problems:  " + r.Count(IssueKind.Error) + " errors, " + r.Count(IssueKind.Warning) + " warnings");
            lines.Add(DurationLine(r));
            return lines;
        }

        public static string DurationLine(RunReport r) =>
            "Duration:  " + Format.Duration(r.DurationSeconds) +
            (r.Phases.Count > 0 ? "  (" + string.Join(" · ", r.Phases.Select(p => p.Name + " " + Format.Duration(p.Seconds))) + ")" : "");

        public static string IssueLine(Issue i) => "[" + i.Kind.ToString().ToLowerInvariant() + "] " + i.Path + " – " + i.Message;

        /// <summary>Complete log file content.</summary>
        public static string Full(string title, RunReport report, IEnumerable<string> summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine(new string('=', title.Length));
            sb.AppendLine("Program:   " + ProgramInfo.Name + " on " + ProgramInfo.Host + " (" + ProgramInfo.User + ")");
            sb.AppendLine("Started:   " + Format.LocalTime(report.Start));
            sb.AppendLine("Finished:  " + Format.LocalTime(report.End));
            sb.AppendLine("Status:    " + report.Status.ToString().ToUpperInvariant() + (report.Error != null ? " – " + report.Error : ""));
            foreach (string line in summary)
                sb.AppendLine(line);
            sb.AppendLine();

            sb.AppendLine("Phases");
            sb.AppendLine("------");
            foreach (var p in report.Phases)
                sb.AppendLine("  " + p.Name.PadRight(20) + Format.Duration(p.Seconds).PadLeft(10) + "   started " + Format.LocalTime(p.Start));
            sb.AppendLine();

            foreach (var group in report.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key))
            {
                string heading = group.Key == IssueKind.Error ? "Errors (not processed)"
                    : group.Key == IssueKind.Warning ? "Warnings"
                    : "Skipped";
                sb.AppendLine(heading + " – " + group.Count());
                sb.AppendLine(new string('-', heading.Length));
                foreach (var i in group)
                    sb.AppendLine("  " + i.Path + "\r\n      " + i.Message);
                sb.AppendLine();
            }
            if (report.Issues.Count == 0)
                sb.AppendLine("No errors or warnings.");
            return sb.ToString();
        }
    }
}
