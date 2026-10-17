using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Keepsake.Core.Reporting
{
    public enum RunStatus
    {
        Success,
        Warning,
        Failed,
        Cancelled,
    }

    public enum IssueKind
    {
        /// <summary>Item could not be processed (not backed up / not restored).</summary>
        Error,

        /// <summary>Item was processed, but something needs attention.</summary>
        Warning,

        /// <summary>Item intentionally left out (exclusion pattern, conflict policy).</summary>
        Skipped,
    }

    public sealed class Issue
    {
        public IssueKind Kind { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
        public DateTime Time { get; set; }
    }

    public sealed class PhaseTiming
    {
        public string Name { get; set; }
        public DateTime Start { get; set; }
        public double Seconds { get; set; }

        [JsonIgnore] public TimeSpan Duration => TimeSpan.FromSeconds(Seconds);
    }

    /// <summary>Common result of every run (backup, restore, check): status, timings and per-item issues.</summary>
    public abstract class RunReport
    {
        public RunStatus Status { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public double DurationSeconds => (End - Start).TotalSeconds;
        public string Error { get; set; }
        public List<PhaseTiming> Phases { get; set; } = new List<PhaseTiming>();
        public List<Issue> Issues { get; set; } = new List<Issue>();

        public int Count(IssueKind kind) => Issues.Count(i => i.Kind == kind);
    }

    /// <summary>Thread-safe issue collector. Keeps at most <see cref="MaxIssues"/> entries but counts all.</summary>
    public sealed class IssueLog
    {
        public const int MaxIssues = 100000;
        private readonly List<Issue> _target;
        private readonly int[] _counts = new int[3];

        public IssueLog(List<Issue> target)
        {
            _target = target;
        }

        public void Error(string path, string message) => Add(IssueKind.Error, path, message);
        public void Warning(string path, string message) => Add(IssueKind.Warning, path, message);
        public void Skipped(string path, string message) => Add(IssueKind.Skipped, path, message);

        public int Count(IssueKind kind)
        {
            lock (_target)
                return _counts[(int)kind];
        }

        private void Add(IssueKind kind, string path, string message)
        {
            lock (_target)
            {
                _counts[(int)kind]++;
                if (_target.Count < MaxIssues)
                    _target.Add(new Issue { Kind = kind, Path = path, Message = message, Time = DateTime.UtcNow });
            }
        }
    }

    /// <summary>Records named phases: <c>using (phases.Begin("Scan")) { ... }</c>.</summary>
    public sealed class PhaseRecorder
    {
        private readonly List<PhaseTiming> _target;

        public PhaseRecorder(List<PhaseTiming> target)
        {
            _target = target;
        }

        public IDisposable Begin(string name) => new Scope(_target, name);

        private sealed class Scope : IDisposable
        {
            private readonly List<PhaseTiming> _target;
            private readonly PhaseTiming _phase;
            private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();

            public Scope(List<PhaseTiming> target, string name)
            {
                _target = target;
                _phase = new PhaseTiming { Name = name, Start = DateTime.UtcNow };
            }

            public void Dispose()
            {
                _phase.Seconds = _watch.Elapsed.TotalSeconds;
                lock (_target)
                    _target.Add(_phase);
            }
        }
    }
}
