using System;
using System.Collections.Generic;
using System.Linq;
using Keepsake.Core.Repo;

namespace Keepsake.Core.Maintenance
{
    /// <summary>
    /// Which snapshots to keep (RET-01). Rules are combined: a snapshot is kept if any rule keeps it.
    /// All zero = keep everything.
    /// </summary>
    public sealed class RetentionPolicy
    {
        public int KeepLast { get; set; }
        public int KeepDaily { get; set; }
        public int KeepWeekly { get; set; }
        public int KeepMonthly { get; set; }
        public int KeepYearly { get; set; }
        public int KeepWithinDays { get; set; }

        public bool IsEmpty => KeepLast + KeepDaily + KeepWeekly + KeepMonthly + KeepYearly + KeepWithinDays == 0;

        public string Describe()
        {
            if (IsEmpty)
                return "keep all snapshots";
            var parts = new List<string>();
            if (KeepLast > 0) parts.Add("last " + KeepLast);
            if (KeepWithinDays > 0) parts.Add("all within " + KeepWithinDays + " days");
            if (KeepDaily > 0) parts.Add(KeepDaily + " daily");
            if (KeepWeekly > 0) parts.Add(KeepWeekly + " weekly");
            if (KeepMonthly > 0) parts.Add(KeepMonthly + " monthly");
            if (KeepYearly > 0) parts.Add(KeepYearly + " yearly");
            return "keep " + string.Join(", ", parts);
        }

        public List<string> Validate()
        {
            var errors = new List<string>();
            foreach (var v in new[] { KeepLast, KeepDaily, KeepWeekly, KeepMonthly, KeepYearly, KeepWithinDays })
            {
                if (v < 0 || v > 10000)
                    errors.Add("Retention values must be between 0 and 10000.");
            }
            return errors.Distinct().ToList();
        }
    }

    public sealed class RetentionDecision
    {
        public RetentionDecision(SnapshotRef snapshot)
        {
            Snapshot = snapshot;
        }

        public SnapshotRef Snapshot { get; }
        public List<string> Reasons { get; } = new List<string>();
        public bool Keep => Reasons.Count > 0;
    }

    public static class Retention
    {
        /// <summary>Decides for every snapshot, newest first. Protected snapshots are always kept (RET-02).</summary>
        public static List<RetentionDecision> Apply(IEnumerable<SnapshotRef> snapshots, RetentionPolicy policy, DateTime nowUtc)
        {
            var decisions = snapshots.OrderByDescending(s => s.Snapshot.Time).Select(s => new RetentionDecision(s)).ToList();
            if (policy == null || policy.IsEmpty)
            {
                foreach (var d in decisions)
                    d.Reasons.Add("no retention rules");
                return decisions;
            }

            foreach (var d in decisions)
            {
                if (Repository.IsProtected(d.Snapshot.Snapshot))
                    d.Reasons.Add("protected");
            }
            for (int i = 0; i < Math.Min(policy.KeepLast, decisions.Count); i++)
                decisions[i].Reasons.Add("last " + policy.KeepLast);
            if (policy.KeepWithinDays > 0)
            {
                var cutoff = nowUtc.AddDays(-policy.KeepWithinDays);
                foreach (var d in decisions.Where(d => d.Snapshot.Snapshot.Time >= cutoff))
                    d.Reasons.Add("within " + policy.KeepWithinDays + " days");
            }
            Bucket(decisions, policy.KeepDaily, t => t.Date.ToString("yyyy-MM-dd"), "daily");
            Bucket(decisions, policy.KeepWeekly, t => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd"), "weekly");
            Bucket(decisions, policy.KeepMonthly, t => t.ToString("yyyy-MM"), "monthly");
            Bucket(decisions, policy.KeepYearly, t => t.ToString("yyyy"), "yearly");
            return decisions;
        }

        /// <summary>Keeps the newest snapshot of each of the <paramref name="count"/> most recent periods.</summary>
        private static void Bucket(List<RetentionDecision> newestFirst, int count, Func<DateTime, string> key, string label)
        {
            if (count <= 0)
                return;
            var seen = new HashSet<string>();
            foreach (var d in newestFirst)
            {
                string k = key(d.Snapshot.Snapshot.Time.ToLocalTime());
                if (seen.Contains(k))
                    continue;
                if (seen.Count >= count)
                    break;
                seen.Add(k);
                d.Reasons.Add(label + " " + k);
            }
        }
    }
}
