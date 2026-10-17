using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Keepsake.Core.Reporting;
using Keepsake.Core.Util;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    internal sealed class RunDetailFile
    {
        public List<PhaseTiming> Phases { get; set; } = new List<PhaseTiming>();
        public List<Issue> Issues { get; set; } = new List<Issue>();
    }

    /// <summary>
    /// Run history per job (LOG-01/05/06): <c>runs\&lt;jobId&gt;\&lt;runId&gt;.record.json</c> (summary),
    /// <c>.detail.json</c> (phases, every skipped item) and <c>.log</c> (readable log).
    /// </summary>
    internal sealed class RunHistory
    {
        private readonly string _root;
        private readonly object _lock = new object();
        private readonly Dictionary<string, List<RunRecord>> _cache = new Dictionary<string, List<RunRecord>>(StringComparer.Ordinal);

        public RunHistory(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            foreach (string dir in Directory.GetDirectories(root))
            {
                var records = new List<RunRecord>();
                foreach (string file in Directory.GetFiles(dir, "*.record.json"))
                {
                    try
                    {
                        records.Add(Json.FromBytes<RunRecord>(File.ReadAllBytes(file)));
                    }
                    catch (Exception ex) when (ex is IOException || ex is Core.KeepsakeException)
                    {
                        ServiceLog.Warn("Unreadable run record " + file + ": " + ex.Message);
                    }
                }
                _cache[Path.GetFileName(dir)] = records.OrderByDescending(r => r.Start).ToList();
            }
        }

        public static string NewRunId() => DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Rand.HexId(3);

        public void Save(RunRecord record, RunReport report = null, string log = null)
        {
            lock (_lock)
            {
                string dir = Path.Combine(_root, record.JobId);
                Directory.CreateDirectory(dir);
                string basePath = Path.Combine(dir, record.Id);
                AtomicFile.Write(basePath + ".record.json", Json.ToBytes(record, indented: true));
                if (report != null)
                    AtomicFile.Write(basePath + ".detail.json", Json.ToBytes(new RunDetailFile { Phases = report.Phases, Issues = report.Issues }));
                if (log != null)
                    File.WriteAllText(basePath + ".log", log, new UTF8Encoding(true));

                if (!_cache.TryGetValue(record.JobId, out var list))
                    _cache[record.JobId] = list = new List<RunRecord>();
                list.RemoveAll(r => r.Id == record.Id);
                list.Add(Clone(record));
                list.Sort((a, b) => b.Start.CompareTo(a.Start));
            }
        }

        public List<RunRecord> List(string jobId, int limit)
        {
            lock (_lock)
            {
                IEnumerable<RunRecord> all = jobId != null
                    ? (_cache.TryGetValue(jobId, out var list) ? list : new List<RunRecord>())
                    : _cache.Values.SelectMany(l => l).OrderByDescending(r => r.Start);
                return all.Take(Math.Max(1, limit)).Select(Clone).ToList();
            }
        }

        public RunRecord Last(string jobId, Func<RunRecord, bool> filter = null)
        {
            lock (_lock)
            {
                if (!_cache.TryGetValue(jobId, out var list))
                    return null;
                var r = list.FirstOrDefault(x => x.Kind == RunKind.Backup && x.State != RunState.Queued && (filter == null || filter(x)));
                return r == null ? null : Clone(r);
            }
        }

        public RunDetail Get(string runId)
        {
            lock (_lock)
            {
                foreach (var pair in _cache)
                {
                    var record = pair.Value.FirstOrDefault(r => r.Id == runId);
                    if (record == null)
                        continue;
                    string basePath = Path.Combine(_root, pair.Key, runId);
                    var detail = new RunDetail { Record = Clone(record) };
                    if (File.Exists(basePath + ".detail.json"))
                    {
                        var d = Json.FromBytes<RunDetailFile>(File.ReadAllBytes(basePath + ".detail.json"));
                        detail.Phases = d.Phases;
                        detail.Issues = d.Issues;
                    }
                    if (File.Exists(basePath + ".log"))
                        detail.Log = File.ReadAllText(basePath + ".log");
                    return detail;
                }
                return null;
            }
        }

        /// <summary>Runs still marked as running after a crash or power loss are closed as failed.</summary>
        public void MarkInterrupted()
        {
            List<RunRecord> open;
            lock (_lock)
                open = _cache.Values.SelectMany(l => l).Where(r => r.State == RunState.Running || r.State == RunState.Queued).Select(Clone).ToList();
            foreach (var r in open)
            {
                r.State = r.State == RunState.Queued ? RunState.Cancelled : RunState.Failed;
                r.End = r.End ?? DateTime.UtcNow;
                r.Message = "Interrupted: the service stopped unexpectedly.";
                Save(r);
                ServiceLog.Warn("Run " + r.Id + " of '" + r.JobName + "' was interrupted by a service stop.");
            }
        }

        /// <summary>Deletes runs older than <paramref name="days"/> and beyond <paramref name="maxRuns"/> per job.</summary>
        public int Prune(int days, int maxRuns)
        {
            int removed = 0;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, days));
            lock (_lock)
            {
                foreach (var pair in _cache)
                {
                    var doomed = pair.Value
                        .Where(r => r.State != RunState.Running && r.State != RunState.Queued)
                        .Select((r, i) => new { r, i })
                        .Where(x => x.i >= maxRuns || x.r.Start < cutoff)
                        .Select(x => x.r)
                        .ToList();
                    foreach (var r in doomed)
                    {
                        foreach (string ext in new[] { ".record.json", ".detail.json", ".log" })
                        {
                            string file = Path.Combine(_root, pair.Key, r.Id + ext);
                            if (File.Exists(file))
                                File.Delete(file);
                        }
                        pair.Value.Remove(r);
                        removed++;
                    }
                }
            }
            return removed;
        }

        public void DeleteJob(string jobId)
        {
            lock (_lock)
            {
                _cache.Remove(jobId);
                string dir = Path.Combine(_root, jobId);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        private static RunRecord Clone(RunRecord r) => Json.FromBytes<RunRecord>(Json.ToBytes(r));
    }
}
