using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Keepsake.Core;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.Views;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>
    /// Browse a job's snapshots, tick files and folders, restore them (RST-01..06). Works through the service, or –
    /// in recovery mode – directly on a repository the GUI opened (RST-07); see <see cref="IRestoreBackend"/>.
    /// </summary>
    public sealed class RestoreViewModel : PageViewModel
    {
        private readonly IRestoreBackend _backend;
        private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private JobChoice _job;
        private SnapshotRow _snapshot;
        private string _pendingJobId;
        private bool _toOriginal;
        private string _targetFolder;
        private bool _keepFullPath;
        private ConflictChoice _conflict;
        private bool _dryRun;
        private string _runId;
        private string _runStatus;
        private bool _loadingSnapshots;
        private string _browseLocation;

        public RestoreViewModel(ShellViewModel shell) : this(shell, new ServiceRestoreBackend(shell.Connection), null)
        {
        }

        /// <summary>Recovery mode: no shell, no service.</summary>
        public RestoreViewModel(IRestoreBackend backend, string location) : this(null, backend, location)
        {
        }

        private RestoreViewModel(ShellViewModel shell, IRestoreBackend backend, string location) : base(shell)
        {
            _backend = backend;
            Hint = location == null
                ? "Pick a backup job and a point in time, tick what you need, choose where it goes."
                : "Backups in " + location + ". Pick a point in time, tick what you need, choose where it goes.";
            NoJobsText = location == null
                ? "There are no backup jobs yet. Create one under 'Backup jobs' first."
                : "This repository contains no backups yet.";
            _conflict = Conflicts[0];
            BrowseTargetCommand = new RelayCommand(() =>
            {
                string picked = Ui.PickFolder("Restore into which folder?", TargetFolder);
                if (picked != null)
                    TargetFolder = picked;
            });
            StartCommand = new AsyncCommand(Start, () => _snapshot != null && _checked.Count > 0 && (ToOriginal || !string.IsNullOrWhiteSpace(TargetFolder)) && !IsRunning);
            ShowLogCommand = new AsyncCommand(ShowLog, () => _runId != null && !IsRunning);
            ProtectCommand = new AsyncCommand(ToggleProtect, () => _backend.CanProtect && _snapshot != null && !IsRunning);
        }

        public ObservableCollection<JobChoice> Jobs { get; } = new ObservableCollection<JobChoice>();
        public ObservableCollection<SnapshotRow> Snapshots { get; } = new ObservableCollection<SnapshotRow>();
        public ObservableCollection<SnapshotNode> Nodes { get; } = new ObservableCollection<SnapshotNode>();

        public IReadOnlyList<ConflictChoice> Conflicts { get; } = new[]
        {
            new ConflictChoice(ConflictPolicy.Skip, "Keep the existing file"),
            new ConflictChoice(ConflictPolicy.OverwriteIfNewer, "Replace if the backup is newer"),
            new ConflictChoice(ConflictPolicy.Overwrite, "Always replace"),
            new ConflictChoice(ConflictPolicy.Rename, "Restore as a copy (name (restored 1).ext)"),
        };

        public ICommand BrowseTargetCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand ShowLogCommand { get; }
        public ICommand ProtectCommand { get; }
        public string ProtectLabel => _snapshot?.Info.Protected == true ? "Remove protection" : "Protect this backup";
        public bool CanProtect => _backend.CanProtect;
        public string Hint { get; }
        public string NoJobsText { get; }
        public override bool AutoRefresh => IsRunning;

        public JobChoice Job
        {
            get => _job;
            set
            {
                if (Set(ref _job, value))
                    _ = LoadSnapshots();
            }
        }

        public SnapshotRow Snapshot
        {
            get => _snapshot;
            set
            {
                if (Set(ref _snapshot, value))
                {
                    OnPropertyChanged(nameof(ProtectLabel));
                    CommandManager.InvalidateRequerySuggested();
                    _ = LoadRoot();
                }
            }
        }

        public bool ToOriginal { get => _toOriginal; set { if (Set(ref _toOriginal, value)) OnPropertyChanged(nameof(ToFolder)); } }

        public bool ToFolder
        {
            get => !_toOriginal;
            set => ToOriginal = !value;
        }

        public string TargetFolder { get => _targetFolder; set => Set(ref _targetFolder, value); }
        public bool KeepFullPath { get => _keepFullPath; set => Set(ref _keepFullPath, value); }
        public ConflictChoice Conflict { get => _conflict; set => Set(ref _conflict, value); }
        public bool DryRun { get => _dryRun; set => Set(ref _dryRun, value); }
        public string RunStatus { get => _runStatus; private set => Set(ref _runStatus, value); }
        public bool IsRunning { get; private set; }
        public bool NoJobs => Jobs.Count == 0;
        public string SelectionText => _checked.Count == 0 ? "Nothing selected – tick folders or files on the left." : _checked.Count + " item(s) selected";

        public void SelectJob(string jobId) => _pendingJobId = jobId;

        public override async Task Refresh()
        {
            if (Jobs.Count == 0 || _pendingJobId != null)
            {
                var jobs = await _backend.ListJobs();
                Jobs.Clear();
                foreach (var j in jobs)
                    Jobs.Add(j);
                OnPropertyChanged(nameof(NoJobs));
                var wanted = Jobs.FirstOrDefault(j => j.Id == _pendingJobId) ?? (Job != null ? Jobs.FirstOrDefault(j => j.Id == Job.Id) : null) ?? Jobs.FirstOrDefault();
                _pendingJobId = null;
                if (wanted != null && wanted.Id != Job?.Id)
                    Job = wanted;
            }

            if (IsRunning && _runId != null)
            {
                var poll = await _backend.Poll(_runId);
                var r = poll.Record;
                if (poll.Running)
                {
                    RunStatus = poll.Progress;
                }
                else
                {
                    IsRunning = false;
                    OnPropertyChanged(nameof(IsRunning));
                    var s = r.RestoreStats;
                    RunStatus = r.State.ToString().ToUpperInvariant() + (DryRun ? " (dry run)" : "") + ": " +
                                (s == null ? r.Message : Format.Count(s.FilesRestored) + " restored, " + Format.Count(s.FilesUnchanged) + " already up to date, " +
                                                         Format.Count(s.FilesSkipped) + " kept, " + Format.Count(s.FilesFailed) + " failed" +
                                                         (string.IsNullOrEmpty(r.Message) ? "" : " – " + r.Message));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        internal bool IsChecked(string path) => _checked.Contains(path);

        internal bool IsCoveredByParent(string path) =>
            _checked.Any(c => path.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase));

        internal void SetChecked(string path, bool value)
        {
            if (value)
            {
                _checked.RemoveWhere(c => c.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
                _checked.Add(path);
            }
            else
            {
                _checked.Remove(path);
            }
            foreach (var n in Nodes)
                n.RefreshState();
            OnPropertyChanged(nameof(SelectionText));
            CommandManager.InvalidateRequerySuggested();
        }

        internal Task<List<TreeEntry>> List(string path) =>
            _backend.ListTree(Job.Id, Snapshot.Id, path);

        private async Task LoadSnapshots()
        {
            Snapshots.Clear();
            Nodes.Clear();
            _checked.Clear();
            OnPropertyChanged(nameof(SelectionText));
            if (Job == null || _loadingSnapshots)
                return;
            _loadingSnapshots = true;
            try
            {
                foreach (var s in await _backend.ListSnapshots(Job.Id))
                    Snapshots.Add(new SnapshotRow(s));
                Snapshot = Snapshots.FirstOrDefault();
            }
            catch (KeepsakeException ex)
            {
                Ui.ShowError(ex);
            }
            finally
            {
                _loadingSnapshots = false;
            }
        }

        private async Task LoadRoot()
        {
            Nodes.Clear();
            _checked.Clear();
            OnPropertyChanged(nameof(SelectionText));
            if (Snapshot == null)
                return;
            try
            {
                // Skip the chain of parent folders above the backed-up sources (C → Users → ...):
                // the tree starts at the level that contains the sources.
                var sources = (Snapshot.Info.Paths ?? new List<string>())
                    .Select(p => Core.Backup.RepoPaths.Join(Core.Backup.RepoPaths.Split(p)))
                    .ToList();
                string path = "";
                var entries = await List(path);
                while (entries.Count == 1 && entries[0].IsFolder &&
                       sources.Any(s => s.StartsWith(entries[0].Path + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    path = entries[0].Path;
                    entries = await List(path);
                }
                BrowseLocation = path.Length == 0 ? null : "In " + ToWindowsPath(path);
                foreach (var e in entries.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    Nodes.Add(new SnapshotNode(this, e));
                if (Nodes.Count == 1 && Nodes[0].IsFolder)
                    await Nodes[0].Expand();
            }
            catch (KeepsakeException ex)
            {
                Ui.ShowError(ex);
            }
        }

        public string BrowseLocation { get => _browseLocation; private set => Set(ref _browseLocation, value); }

        private static string ToWindowsPath(string repoPath)
        {
            try
            {
                return Core.Backup.RepoPaths.ToWindows(Core.Backup.RepoPaths.Parse(repoPath));
            }
            catch (KeepsakeException)
            {
                return repoPath;
            }
        }

        private async Task Start()
        {
            if (ToOriginal && !DryRun && Conflict.Policy == ConflictPolicy.Overwrite &&
                !Ui.Confirm("Restore to the ORIGINAL location and overwrite existing files?\n\nCurrent versions of these files will be replaced.", "Restore"))
                return;
            var options = new RestoreOptions
            {
                Snapshot = Snapshot.Id,
                Includes = _checked.ToList(),
                Target = ToOriginal ? RestoreTargetMode.Original : RestoreTargetMode.Folder,
                TargetFolder = ToOriginal ? null : TargetFolder,
                KeepFullPath = KeepFullPath,
                Conflict = Conflict.Policy,
                DryRun = DryRun,
            };
            _runId = await _backend.StartRestore(Job.Id, options);
            IsRunning = true;
            OnPropertyChanged(nameof(IsRunning));
            RunStatus = "Starting restore…";
        }

        private async Task ToggleProtect()
        {
            bool protect = !_snapshot.Info.Protected;
            await _backend.Protect(Job.Id, _snapshot.Id, protect);
            string keepTime = _snapshot.Info.Time.ToString("o");
            await LoadSnapshots();
            Snapshot = Snapshots.FirstOrDefault(s => s.Info.Time.ToString("o") == keepTime) ?? Snapshots.FirstOrDefault();
        }

        private async Task ShowLog()
        {
            var detail = await _backend.Detail(_runId);
            RunDetailWindow.Open(new RunDetailViewModel(detail));
        }
    }

    public sealed class ConflictChoice
    {
        public ConflictChoice(ConflictPolicy policy, string label)
        {
            Policy = policy;
            Label = label;
        }

        public ConflictPolicy Policy { get; }
        public string Label { get; }
    }

    public sealed class SnapshotRow
    {
        public SnapshotRow(SnapshotInfo info)
        {
            Info = info;
        }

        public SnapshotInfo Info { get; }
        public string Id => Info.Id;
        public string Label => Format.LocalTime(Info.Time) + (Info.Protected ? "  ·  protected" : "") + "  ·  " + Info.Mode.ToString().ToLowerInvariant() +
                               (Info.Summary != null ? "  ·  " + Format.Count(Info.Summary.Files) + " files, " + Format.Bytes(Info.Summary.TotalBytes) : "");
    }

    public sealed class SnapshotNode : ObservableObject
    {
        private static readonly SnapshotNode Placeholder = new SnapshotNode();
        private readonly RestoreViewModel _owner;
        private bool _expanded;
        private bool _loaded;

        private SnapshotNode()
        {
        }

        public SnapshotNode(RestoreViewModel owner, TreeEntry entry)
        {
            _owner = owner;
            Entry = entry;
            if (entry.IsFolder)
                Children.Add(Placeholder);
        }

        public TreeEntry Entry { get; }
        public string Name => Entry?.Name;
        public string Details => Entry == null ? null : Entry.IsFolder ? Format.LocalTime(Entry.Modified) : Format.Bytes(Entry.Size) + " · " + Format.LocalTime(Entry.Modified);
        public bool IsFolder => Entry?.IsFolder ?? false;
        public ObservableCollection<SnapshotNode> Children { get; } = new ObservableCollection<SnapshotNode>();

        public bool IsChecked
        {
            get => Entry != null && (_owner.IsChecked(Entry.Path) || _owner.IsCoveredByParent(Entry.Path));
            set
            {
                if (Entry != null && !_owner.IsCoveredByParent(Entry.Path))
                    _owner.SetChecked(Entry.Path, value);
            }
        }

        public bool IsSelectable => Entry != null && !_owner.IsCoveredByParent(Entry.Path);

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (Set(ref _expanded, value) && value && !_loaded)
                    _ = Expand();
            }
        }

        public async Task Expand()
        {
            if (!_loaded && Entry != null && Entry.IsFolder)
            {
                _loaded = true;
                try
                {
                    var entries = await _owner.List(Entry.Path);
                    Children.Clear();
                    foreach (var e in entries.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                        Children.Add(new SnapshotNode(_owner, e));
                }
                catch (KeepsakeException ex)
                {
                    _loaded = false;
                    Ui.ShowError(ex);
                }
            }
            if (!_expanded)
            {
                _expanded = true;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        public void RefreshState()
        {
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(IsSelectable));
            if (!_loaded)
                return;
            foreach (var c in Children)
                c.RefreshState();
        }
    }
}
