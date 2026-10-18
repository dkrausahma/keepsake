using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Keepsake.Core.Reporting;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.Views;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    public sealed class JobChoice
    {
        public JobChoice(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
    }

    /// <summary>Run history of all jobs or one job (LOG-05).</summary>
    public sealed class HistoryViewModel : PageViewModel
    {
        private JobChoice _job;
        private string _pendingJobId;
        private string _signature;
        private RunRowViewModel _selectedRun;

        public HistoryViewModel(ShellViewModel shell) : base(shell)
        {
            OpenCommand = new AsyncCommand(p => Open((RunRowViewModel)p), p => p is RunRowViewModel);
        }

        public ObservableCollection<JobChoice> Jobs { get; } = new ObservableCollection<JobChoice>();
        public ObservableCollection<RunRowViewModel> Runs { get; } = new ObservableCollection<RunRowViewModel>();
        public RunRowViewModel SelectedRun { get => _selectedRun; set => Set(ref _selectedRun, value); }
        public ICommand OpenCommand { get; }
        public override bool AutoRefresh => true;

        public JobChoice Job
        {
            get => _job;
            set
            {
                if (Set(ref _job, value))
                {
                    _signature = null;
                    _ = Refresh();
                }
            }
        }

        public void SelectJob(string jobId) => _pendingJobId = jobId;

        public override async Task Refresh()
        {
            var jobs = await Connection.Call<List<JobSummary>>(Methods.JobsList);
            var choices = new[] { new JobChoice(null, "All jobs") }.Concat(jobs.Select(j => new JobChoice(j.Id, j.Name))).ToList();
            if (Jobs.Count != choices.Count || Jobs.Zip(choices, (a, b) => a.Id != b.Id || a.Name != b.Name).Any(x => x))
            {
                string keep = _pendingJobId ?? _job?.Id;
                Jobs.Clear();
                foreach (var c in choices)
                    Jobs.Add(c);
                _job = Jobs.FirstOrDefault(j => j.Id == keep) ?? Jobs[0];
                OnPropertyChanged(nameof(Job));
            }
            if (_pendingJobId != null)
            {
                _job = Jobs.FirstOrDefault(j => j.Id == _pendingJobId) ?? Jobs[0];
                _pendingJobId = null;
                _signature = null;
                OnPropertyChanged(nameof(Job));
            }

            var runs = await Connection.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = _job?.Id, Limit = 200 });
            string signature = string.Join("|", runs.Select(r => r.Id + r.State));
            if (signature == _signature)
                return;
            _signature = signature;
            Runs.Clear();
            foreach (var r in runs)
                Runs.Add(new RunRowViewModel(r));
        }

        private async Task Open(RunRowViewModel row)
        {
            var detail = await Connection.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = row.Record.Id });
            RunDetailWindow.Open(new RunDetailViewModel(detail));
        }
    }

    public sealed class RunRowViewModel
    {
        public RunRowViewModel(RunRecord record)
        {
            Record = record;
        }

        public RunRecord Record { get; }
        public string State => Record.State.ToString();
        public string Job => Record.JobName;
        public string Kind => Record.Kind == RunKind.Backup ? (Record.Mode == Core.Repo.BackupMode.Full ? "Full backup" : "Backup")
            : Record.Kind == RunKind.Prune ? "Cleanup"
            : Record.Kind.ToString();
        public string Trigger => Record.Trigger == RunTrigger.Retry ? "Retry " + Record.Attempt : Record.Trigger.ToString();
        public string Start => Format.LocalTime(Record.Start);
        public string Duration => Record.State == RunState.Running || Record.State == RunState.Queued ? "…" : Format.Duration(Record.DurationSeconds);

        public string Files => Record.Summary != null
            ? Format.Count(Record.Summary.Files) + (Record.Summary.FilesSkipped > 0 ? " (" + Format.Count(Record.Summary.FilesSkipped) + " not backed up)" : "")
            : Record.RestoreStats != null ? Format.Count(Record.RestoreStats.FilesRestored) + " restored" : "";

        public string Data => Record.Summary != null
            ? Format.Bytes(Record.Summary.BytesAdded) + " new / " + Format.Bytes(Record.Summary.BytesStored) + " stored"
            : Record.RestoreStats != null ? Format.Bytes(Record.RestoreStats.BytesWritten) : "";

        public string Message => Record.Message;
    }

    /// <summary>One run: summary, phases, everything that was not backed up, full log (LOG-01..04).</summary>
    public sealed class RunDetailViewModel : ObservableObject
    {
        private readonly RunDetail _detail;
        private string _filter = "Problems";
        private string _search;

        public RunDetailViewModel(RunDetail detail)
        {
            _detail = detail;
            ExportCommand = new RelayCommand(Export, () => _detail.Issues.Count > 0);
        }

        public RunRecord Record => _detail.Record;
        public string Title => Record.JobName + " – " + Format.LocalTime(Record.Start);
        public string State => Record.State.ToString();
        public string Headline => Record.State.ToString().ToUpperInvariant() + (string.IsNullOrEmpty(Record.Message) ? "" : " – " + Record.Message);
        public List<PhaseTiming> Phases => _detail.Phases;
        public string Log => _detail.Log ?? "(no log)";

        public List<string> Summary
        {
            get
            {
                var lines = new List<string>
                {
                    "Run " + Record.Id + " · " + Record.Kind + " · triggered " + Record.Trigger.ToString().ToLowerInvariant(),
                    "Started " + Format.LocalTime(Record.Start) + (Record.End.HasValue ? ", finished " + Format.LocalTime(Record.End.Value) : "") + " · " + Format.Duration(Record.DurationSeconds),
                };
                var s = Record.Summary;
                if (s != null)
                {
                    lines.Add(Format.Count(s.FilesNew) + " new, " + Format.Count(s.FilesChanged) + " changed, " + Format.Count(s.FilesUnchanged) + " unchanged, " + Format.Count(s.FilesSkipped) + " not backed up · " + Format.Count(s.Dirs) + " folders");
                    lines.Add(Format.Bytes(s.TotalBytes) + " total, " + Format.Bytes(s.BytesRead) + " read, " + Format.Bytes(s.BytesAdded) + " new, " + Format.Bytes(s.BytesStored) + " stored after compression");
                }
                var r = Record.RestoreStats;
                if (r != null)
                    lines.Add(Format.Count(r.FilesRestored) + " restored, " + Format.Count(r.FilesUnchanged) + " already up to date, " + Format.Count(r.FilesSkipped) + " kept, " + Format.Count(r.FilesFailed) + " failed · " + Format.Bytes(r.BytesWritten));
                if (Record.SnapshotId != null)
                    lines.Add("Snapshot " + Record.SnapshotId.Substring(0, 8));
                return lines;
            }
        }

        public IReadOnlyList<string> Filters { get; } = new[] { "Problems", "Errors", "Warnings", "Skipped", "All" };

        public string Filter
        {
            get => _filter;
            set
            {
                if (Set(ref _filter, value))
                    OnPropertyChanged(nameof(Issues));
            }
        }

        public string Search
        {
            get => _search;
            set
            {
                if (Set(ref _search, value))
                    OnPropertyChanged(nameof(Issues));
            }
        }

        public string IssueCounts =>
            _detail.Issues.Count(i => i.Kind == IssueKind.Error) + " not processed · " +
            _detail.Issues.Count(i => i.Kind == IssueKind.Warning) + " warnings · " +
            _detail.Issues.Count(i => i.Kind == IssueKind.Skipped) + " skipped";

        public List<Issue> Issues => _detail.Issues
            .Where(i => Filter == "All"
                        || (Filter == "Problems" && i.Kind != IssueKind.Skipped)
                        || (Filter == "Errors" && i.Kind == IssueKind.Error)
                        || (Filter == "Warnings" && i.Kind == IssueKind.Warning)
                        || (Filter == "Skipped" && i.Kind == IssueKind.Skipped))
            .Where(i => string.IsNullOrWhiteSpace(Search)
                        || (i.Path ?? "").IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0
                        || (i.Message ?? "").IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(5000)
            .ToList();

        public ICommand ExportCommand { get; }

        private void Export()
        {
            string file = Ui.SaveFile("Export issues", "keepsake-" + Record.Id + ".csv", "CSV file (*.csv)|*.csv");
            if (file == null)
                return;
            var sb = new StringBuilder("Kind;Path;Message;Time\r\n");
            foreach (var i in _detail.Issues)
                sb.Append(i.Kind).Append(';').Append(Csv(i.Path)).Append(';').Append(Csv(i.Message)).Append(';').Append(Format.LocalTime(i.Time)).Append("\r\n");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>Quoted CSV cell; a leading = + - @ is neutralised so a spreadsheet never runs it as a formula.</summary>
        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0)
                s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
