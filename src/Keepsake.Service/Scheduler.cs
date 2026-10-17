using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Repo;
using Keepsake.Core.Util;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>
    /// Starts scheduled runs (SCH-01..05). Checks every 20 seconds. A run is "missed" when its time passed more
    /// than 5 minutes ago (service stopped, machine off); missed runs start once after start-up if the job allows it.
    /// Failed scheduled runs are retried (SCH-04).
    /// </summary>
    internal sealed class Scheduler : IDisposable
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(5);

        private readonly JobStore _jobs;
        private readonly JobRunner _runner;
        private readonly RunHistory _history;
        private readonly SettingsStore _settings;
        private readonly string _stateFile;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Retry> _retries = new Dictionary<string, Retry>(StringComparer.Ordinal);
        private SchedulerState _state;
        private Timer _timer;
        private readonly Notifier _notifier;

        public Scheduler(JobStore jobs, JobRunner runner, RunHistory history, SettingsStore settings, string stateFile, Notifier notifier = null)
        {
            _notifier = notifier;
            _jobs = jobs;
            _runner = runner;
            _history = history;
            _settings = settings;
            _stateFile = stateFile;
            _state = File.Exists(stateFile) ? Json.FromBytes<SchedulerState>(File.ReadAllBytes(stateFile)) : new SchedulerState();
            _runner.RunFinished += OnRunFinished;
        }

        public void Start()
        {
            _timer = new Timer(_ => SafeTick(), null, TimeSpan.FromSeconds(2), Tick);
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _runner.RunFinished -= OnRunFinished;
        }

        /// <summary>Next automatic run of a job (schedule or pending retry), local time.</summary>
        public DateTime? NextRun(JobDefinition job)
        {
            if (!job.Enabled)
                return null;
            DateTime? next = null;
            lock (_lock)
            {
                foreach (var s in job.Schedules.Where(s => s.Enabled))
                {
                    var candidate = ScheduleMath.Next(s, LastFired(job, s));
                    if (candidate.HasValue && (!next.HasValue || candidate < next))
                        next = candidate;
                }
                if (_retries.TryGetValue(job.Id, out var retry) && (!next.HasValue || retry.At < next))
                    next = retry.At;
            }
            return next;
        }

        /// <summary>Runs one scheduling pass now (also used by tests).</summary>
        internal void RunOnce(DateTime now) => Evaluate(now);

        private void SafeTick()
        {
            try
            {
                Evaluate(DateTime.Now);
            }
            catch (Exception ex)
            {
                ServiceLog.Error("Scheduler pass failed", ex);
            }
        }

        private void Evaluate(DateTime now)
        {
            bool changed = false;
            foreach (var job in _jobs.List().Where(j => j.Enabled))
            {
                bool due = false, full = false, missed = false;
                lock (_lock)
                {
                    foreach (var s in job.Schedules.Where(s => s.Enabled))
                    {
                        string key = Key(job, s);
                        if (!_state.LastFired.ContainsKey(key))
                        {
                            // New schedule: start counting from now, never fire immediately.
                            _state.LastFired[key] = now.ToUniversalTime();
                            changed = true;
                            continue;
                        }
                        var next = ScheduleMath.Next(s, LastFired(job, s));
                        if (!next.HasValue || next.Value > now)
                            continue;
                        due = true;
                        full |= s.Mode == BackupMode.Full;
                        // Only missed if this occurrence and every later one up to now were skipped.
                        missed |= now - next.Value > MissedAfter;
                        _state.LastFired[key] = now.ToUniversalTime();
                        changed = true;
                    }
                }

                if (due)
                    Trigger(job, full ? BackupMode.Full : BackupMode.Incremental, missed ? RunTrigger.Missed : RunTrigger.Schedule, 1);

                Retry retry = null;
                lock (_lock)
                {
                    if (_retries.TryGetValue(job.Id, out var r) && r.At <= now)
                    {
                        retry = r;
                        _retries.Remove(job.Id);
                    }
                }
                if (retry != null && !due)
                    Trigger(job, retry.Mode, RunTrigger.Retry, retry.Attempt);
            }

            if (CheckNoSuccess(now))
                changed = true;
            if (changed)
                SaveState();
            PruneHistoryDaily(now);
        }

        /// <summary>"No successful backup for X hours" (NOT-02); repeated once per alert period while it lasts.</summary>
        private bool CheckNoSuccess(DateTime now)
        {
            bool changed = false;
            var nowUtc = now.ToUniversalTime();
            foreach (var job in _jobs.List().Where(j => j.Enabled && j.AlertNoSuccessHours > 0))
            {
                var last = _history.Last(job.Id, r => r.State == RunState.Success || r.State == RunState.Warning);
                var reference = last?.Start ?? job.Created;
                var period = TimeSpan.FromHours(job.AlertNoSuccessHours);
                if (nowUtc - reference < period || _runner.IsBusy(job.Id))
                    continue;
                lock (_lock)
                {
                    if (_state.LastNoSuccessAlert.TryGetValue(job.Id, out var sent) && sent > reference && nowUtc - sent < period)
                        continue;
                    _state.LastNoSuccessAlert[job.Id] = nowUtc;
                }
                changed = true;
                (_notifier ?? new Notifier(_settings, _jobs, _history)).NoSuccessAlert(job, last?.Start);
            }
            return changed;
        }

        private void Trigger(JobDefinition job, BackupMode mode, RunTrigger trigger, int attempt)
        {
            if (trigger == RunTrigger.Missed && !job.RunMissed)
            {
                ServiceLog.Info("Missed scheduled run of '" + job.Name + "' skipped (catch-up is disabled for this job).");
                EventLogWriter.Warning(1003, "Keepsake: a scheduled backup of '" + job.Name + "' was missed while the service was not running (catch-up is disabled).");
                return;
            }
            if (_runner.IsBusy(job.Id))
            {
                ServiceLog.Warn("Scheduled run of '" + job.Name + "' skipped: the job is still running.");
                EventLogWriter.Warning(1003, "Keepsake: scheduled backup of '" + job.Name + "' skipped because the previous run is still running.");
                return;
            }
            try
            {
                _runner.Enqueue(job.Id, RunKind.Backup, mode, trigger, attempt);
            }
            catch (ApiException ex)
            {
                ServiceLog.Warn("Scheduled run of '" + job.Name + "' not started: " + ex.Message);
            }
        }

        private void OnRunFinished(RunRecord record)
        {
            if (record.Kind != RunKind.Backup || record.State != RunState.Failed || record.Trigger == RunTrigger.Manual)
                return;
            var job = _jobs.Get(record.JobId);
            if (job == null || !job.Enabled || record.Attempt > job.RetryCount)
                return;
            var at = DateTime.Now.AddMinutes(job.RetryDelayMinutes);
            lock (_lock)
                _retries[job.Id] = new Retry { At = at, Attempt = record.Attempt + 1, Mode = record.Mode };
            ServiceLog.Info("'" + job.Name + "' failed; retry " + record.Attempt + " of " + job.RetryCount + " at " + at.ToString("HH:mm") + ".");
        }

        private void PruneHistoryDaily(DateTime now)
        {
            if (now.ToUniversalTime() - _state.LastHistoryPrune < TimeSpan.FromDays(1))
                return;
            var settings = _settings.Get();
            int removed = _history.Prune(settings.LogRetentionDays, settings.LogRetentionRuns);
            if (removed > 0)
                ServiceLog.Info("Removed " + removed + " old run log(s).");
            lock (_lock)
                _state.LastHistoryPrune = now.ToUniversalTime();
            SaveState();
        }

        private DateTime LastFired(JobDefinition job, ScheduleEntry s) =>
            _state.LastFired.TryGetValue(Key(job, s), out var utc) ? utc.ToLocalTime() : DateTime.Now;

        private static string Key(JobDefinition job, ScheduleEntry s) => job.Id + "/" + s.Id;

        private void SaveState()
        {
            byte[] data;
            lock (_lock)
                data = Json.ToBytes(_state, indented: true);
            AtomicFile.Write(_stateFile, data);
        }

        private sealed class Retry
        {
            public DateTime At;
            public int Attempt;
            public BackupMode Mode;
        }
    }
}
