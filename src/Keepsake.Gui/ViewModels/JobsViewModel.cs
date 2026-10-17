using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.Views;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>Dashboard: every job with its state, last result and next run.</summary>
    public sealed class JobsViewModel : PageViewModel
    {
        private bool _loaded;

        public JobsViewModel(ShellViewModel shell) : base(shell)
        {
            NewJobCommand = new AsyncCommand(NewJob);
        }

        public ObservableCollection<JobItemViewModel> Items { get; } = new ObservableCollection<JobItemViewModel>();
        public ICommand NewJobCommand { get; }

        public bool IsEmpty => _loaded && Items.Count == 0;
        public override bool AutoRefresh => true;

        public override async Task Refresh()
        {
            var jobs = await Connection.Call<List<JobSummary>>(Methods.JobsList);
            foreach (var gone in Items.Where(i => jobs.All(j => j.Id != i.Id)).ToList())
                Items.Remove(gone);
            for (int i = 0; i < jobs.Count; i++)
            {
                var existing = Items.FirstOrDefault(x => x.Id == jobs[i].Id);
                if (existing == null)
                    Items.Insert(Math.Min(i, Items.Count), new JobItemViewModel(this, jobs[i]));
                else
                    existing.Update(jobs[i]);
            }
            _loaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }

        internal async Task NewJob()
        {
            if (JobEditorWindow.Open(Connection, null))
                await Refresh();
        }

        internal async Task Edit(string jobId)
        {
            var detail = await Connection.Call<JobDetail>(Methods.JobsGet, new IdParams { Id = jobId });
            if (JobEditorWindow.Open(Connection, detail))
                await Refresh();
        }
    }

    public sealed class JobItemViewModel : ObservableObject
    {
        private readonly JobsViewModel _page;
        private JobSummary _job;

        public JobItemViewModel(JobsViewModel page, JobSummary job)
        {
            _page = page;
            _job = job;
            RunCommand = new AsyncCommand(() => Run(BackupMode.Incremental), () => !IsBusy && _job.Enabled);
            RunFullCommand = new AsyncCommand(() => Run(BackupMode.Full), () => !IsBusy && _job.Enabled);
            CancelCommand = new AsyncCommand(Cancel, () => IsBusy);
            EditCommand = new AsyncCommand(() => _page.Edit(Id), () => !IsBusy);
            DeleteCommand = new AsyncCommand(Delete, () => !IsBusy);
            HistoryCommand = new RelayCommand(() => _page.Shell.ShowHistory(Id));
            CheckCommand = new AsyncCommand(() => RunKindNow(RunKind.Check), () => !IsBusy);
            PruneCommand = new AsyncCommand(() => RunKindNow(RunKind.Prune), () => !IsBusy);
            RestoreCommand = new RelayCommand(() => _page.Shell.ShowRestore(Id));
        }

        public string Id => _job.Id;
        public string Name => _job.Name;
        public string Destination => _job.Destination;
        public string Schedule => _job.ScheduleText.Count == 0 ? "Manual only" : string.Join(" · ", _job.ScheduleText);
        public bool IsBusy => _job.Progress != null || _job.Queued;
        public bool IsRunning => _job.Progress != null;

        public string StateKey => _job.Progress != null ? "Running"
            : _job.Queued ? "Queued"
            : _job.LastRun == null ? "None"
            : _job.LastRun.State.ToString();

        public string StateText
        {
            get
            {
                if (!_job.Enabled)
                    return "Disabled";
                if (_job.Progress != null)
                    return _job.Progress.Phase;
                if (_job.Queued)
                    return "Waiting for a free slot";
                if (_job.LastRun == null)
                    return "Never run";
                switch (_job.LastRun.State)
                {
                    case RunState.Success: return "Last backup OK";
                    case RunState.Warning: return "Last backup with warnings";
                    case RunState.Failed: return "Last backup FAILED";
                    case RunState.Cancelled: return "Last backup cancelled";
                    default: return _job.LastRun.State.ToString();
                }
            }
        }

        public string LastRunText => _job.LastRun == null
            ? "—"
            : Format.LocalTime(_job.LastRun.Start) + " · " + Format.Duration(_job.LastRun.DurationSeconds) +
              (_job.LastRun.Summary != null ? " · " + Format.Count(_job.LastRun.Summary.Files) + " files, " + Format.Bytes(_job.LastRun.Summary.BytesAdded) + " new" : "");

        public string LastMessage => _job.LastRun != null && _job.LastRun.State != RunState.Success ? _job.LastRun.Message : null;

        public string LastSuccessText => _job.LastSuccess == null ? "No successful backup yet" : "Last successful backup " + Ago(_job.LastSuccess.Start);

        public string NextRunText => !_job.Enabled ? "Disabled"
            : _job.NextRun.HasValue ? "Next run " + _job.NextRun.Value.ToString("ddd yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "No schedule";

        public string ProgressText => _job.Progress == null
            ? null
            : Format.Count(_job.Progress.Files) + " files · " + Format.Bytes(_job.Progress.BytesRead) + " read · " + Format.Bytes(_job.Progress.BytesStored) + " stored";

        public string CurrentPath => _job.Progress?.CurrentPath;

        public ICommand RunCommand { get; }
        public ICommand RunFullCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand HistoryCommand { get; }
        public ICommand CheckCommand { get; }
        public ICommand PruneCommand { get; }
        public ICommand RestoreCommand { get; }

        public void Update(JobSummary job)
        {
            _job = job;
            OnAllChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task Run(BackupMode mode)
        {
            await _page.Shell.Connection.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = Id, Mode = mode });
            await _page.Refresh();
        }

        private async Task RunKindNow(RunKind kind)
        {
            await _page.Shell.Connection.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = Id, Kind = kind });
            await _page.Refresh();
        }

        private async Task Cancel()
        {
            if (!Ui.Confirm("Cancel the running backup of '" + Name + "'?\n\nData stored so far is kept and reused by the next run."))
                return;
            await _page.Shell.Connection.Call(Methods.JobsCancel, new IdParams { Id = Id });
            await _page.Refresh();
        }

        private async Task Delete()
        {
            if (!Ui.Confirm("Delete the job '" + Name + "' and its run history?\n\nThe backups in " + Destination + " are NOT deleted.", "Delete job"))
                return;
            await _page.Shell.Connection.Call(Methods.JobsDelete, new IdParams { Id = Id });
            await _page.Refresh();
        }

        private static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + " h ago";
            return (int)span.TotalDays + " day(s) ago";
        }
    }
}
