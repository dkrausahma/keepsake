using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Keepsake.Core;
using Keepsake.Core.Repo;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.Views;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    public sealed class JobEditorViewModel : ObservableObject
    {
        private readonly ServiceConnection _connection;
        private readonly JobDefinition _original;
        private string _name;
        private bool _enabled = true;
        private string _patterns;
        private bool _useDefaultExcludes = true;
        private string _newPath;
        private ScheduleItemViewModel _selectedSchedule;
        private bool _runMissed = true;
        private int _retryCount = 2;
        private int _retryDelay = 15;
        private string _preCommand;
        private string _postCommand;
        private int _commandTimeout = 30;
        private string _error;

        public JobEditorViewModel(ServiceConnection connection, JobDetail detail)
        {
            _connection = connection;
            _original = detail?.Job;
            Selection.Changed += () =>
            {
                foreach (var root in Roots)
                    root.RefreshState();
                OnPropertyChanged(nameof(SelectedSources));
                OnPropertyChanged(nameof(SelectedExclusions));
            };
            foreach (var drive in FileTreeNode.Drives(Selection))
                Roots.Add(drive);
            Destination = new DestinationEditorViewModel(connection, detail, () => Name);

            if (_original != null)
            {
                _name = _original.Name;
                _enabled = _original.Enabled;
                _patterns = string.Join(Environment.NewLine, Selection.Load(_original.Sources, _original.Excludes));
                _useDefaultExcludes = _original.UseDefaultExcludes;
                foreach (var s in _original.Schedules)
                    Schedules.Add(new ScheduleItemViewModel(s));
                _runMissed = _original.RunMissed;
                _retryCount = _original.RetryCount;
                _retryDelay = _original.RetryDelayMinutes;
                _preCommand = _original.PreCommand;
                _postCommand = _original.PostCommand;
                _commandTimeout = _original.CommandTimeoutMinutes;
                var own = Destination.ShareCredential();
                foreach (var c in _original.ShareCredentials ?? new List<ShareCredential>())
                {
                    if (own == null || !string.Equals(own.Share, c.Share, StringComparison.OrdinalIgnoreCase))
                        ExtraShares.Add(new ShareCredentialRow(c, detail.SharesWithPassword.Any(s => string.Equals(s, c.Share, StringComparison.OrdinalIgnoreCase))));
                }
            }
            else
            {
                Schedules.Add(new ScheduleItemViewModel(new ScheduleEntry { Kind = ScheduleKind.Daily, Times = { "22:00" } }));
            }
            SelectedSchedule = Schedules.FirstOrDefault();

            AddPathCommand = new RelayCommand(AddPath, () => !string.IsNullOrWhiteSpace(NewPath));
            RemoveSourceCommand = new RelayCommand(p => Selection.Remove((string)p));
            AddScheduleCommand = new RelayCommand(() =>
            {
                var s = new ScheduleItemViewModel(new ScheduleEntry { Kind = ScheduleKind.Daily, Times = { "22:00" } });
                Schedules.Add(s);
                SelectedSchedule = s;
            });
            RemoveScheduleCommand = new RelayCommand(() =>
            {
                Schedules.Remove(SelectedSchedule);
                SelectedSchedule = Schedules.FirstOrDefault();
            }, () => SelectedSchedule != null);
            AddShareCommand = new RelayCommand(() => ExtraShares.Add(new ShareCredentialRow(new ShareCredential { Share = @"\\server\share" }, false)));
            RemoveShareCommand = new RelayCommand(p => ExtraShares.Remove((ShareCredentialRow)p));
        }

        public bool IsNew => _original == null;
        public string Title => IsNew ? "New backup job" : "Edit job – " + _original.Name;

        public SourceSelection Selection { get; } = new SourceSelection();
        public ObservableCollection<FileTreeNode> Roots { get; } = new ObservableCollection<FileTreeNode>();
        public List<string> SelectedSources => Selection.Sources;
        public List<string> SelectedExclusions => Selection.Exclusions;
        public ObservableCollection<ScheduleItemViewModel> Schedules { get; } = new ObservableCollection<ScheduleItemViewModel>();
        public ObservableCollection<ShareCredentialRow> ExtraShares { get; } = new ObservableCollection<ShareCredentialRow>();
        public IReadOnlyList<string> DefaultExcludes => Core.Backup.ExcludeMatcher.Defaults.ToList();
        public DestinationEditorViewModel Destination { get; }

        public string Name { get => _name; set => Set(ref _name, value); }
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public string Patterns { get => _patterns; set => Set(ref _patterns, value); }
        public bool UseDefaultExcludes { get => _useDefaultExcludes; set => Set(ref _useDefaultExcludes, value); }
        public string NewPath { get => _newPath; set => Set(ref _newPath, value); }
        public ScheduleItemViewModel SelectedSchedule { get => _selectedSchedule; set => Set(ref _selectedSchedule, value); }
        public bool RunMissed { get => _runMissed; set => Set(ref _runMissed, value); }
        public int RetryCount { get => _retryCount; set => Set(ref _retryCount, value); }
        public int RetryDelay { get => _retryDelay; set => Set(ref _retryDelay, value); }
        public string PreCommand { get => _preCommand; set => Set(ref _preCommand, value); }
        public string PostCommand { get => _postCommand; set => Set(ref _postCommand, value); }
        public int CommandTimeout { get => _commandTimeout; set => Set(ref _commandTimeout, value); }
        public string Error { get => _error; set => Set(ref _error, value); }

        public ICommand AddPathCommand { get; }
        public ICommand RemoveSourceCommand { get; }
        public ICommand AddScheduleCommand { get; }
        public ICommand RemoveScheduleCommand { get; }
        public ICommand AddShareCommand { get; }
        public ICommand RemoveShareCommand { get; }

        private void AddPath()
        {
            string path = NewPath.Trim().Trim('"');
            if (!(path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length >= 3 && path[1] == ':')))
            {
                Ui.Info("Enter an absolute path such as D:\\Data or \\\\server\\share\\folder.");
                return;
            }
            Selection.Include(path);
            NewPath = null;
        }

        private List<ShareCredential> ExtraShareCredentials() =>
            ExtraShares.Where(s => !string.IsNullOrWhiteSpace(s.Share))
                .Select(s => new ShareCredential { Share = s.Share.Trim().TrimEnd('\\'), User = s.User?.Trim() })
                .ToList();

        private JobSecrets ExtraSecrets()
        {
            var secrets = new JobSecrets();
            foreach (var s in ExtraShares.Where(s => !string.IsNullOrWhiteSpace(s.Share) && !string.IsNullOrEmpty(s.Password)))
                secrets.SharePasswords[s.Share.Trim().TrimEnd('\\')] = s.Password;
            return secrets;
        }

        /// <summary>Validates, creates the repository if needed and saves. Returns true when saved.</summary>
        public async Task<bool> Save()
        {
            Error = null;
            var job = Build();
            var errors = JobValidation.Validate(job);
            if (errors.Count > 0)
            {
                Error = string.Join(Environment.NewLine, errors);
                return false;
            }

            try
            {
                string problem = await Destination.EnsureRepository(ExtraShareCredentials(), ExtraSecrets());
                if (problem != null)
                {
                    Error = problem;
                    return false;
                }
                var secrets = ExtraSecrets();
                Destination.FillSecrets(secrets);
                await _connection.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, Secrets = secrets });
                return true;
            }
            catch (ApiException ex)
            {
                Error = ex.Message;
                return false;
            }
        }

        private JobDefinition Build()
        {
            var excludes = Selection.Exclusions.ToList();
            excludes.AddRange((Patterns ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)));
            var shares = ExtraShareCredentials();
            var own = Destination.ShareCredential();
            if (own != null && !shares.Any(s => string.Equals(s.Share, own.Share, StringComparison.OrdinalIgnoreCase)))
                shares.Add(own);
            return new JobDefinition
            {
                Id = _original?.Id,
                Name = Name?.Trim(),
                Enabled = Enabled,
                Sources = Selection.Sources,
                Excludes = excludes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                UseDefaultExcludes = UseDefaultExcludes,
                Destination = Destination.Build(),
                ShareCredentials = shares,
                Schedules = Schedules.Select(s => s.ToEntry()).ToList(),
                RunMissed = RunMissed,
                RetryCount = RetryCount,
                RetryDelayMinutes = RetryDelay,
                PreCommand = string.IsNullOrWhiteSpace(PreCommand) ? null : PreCommand.Trim(),
                PostCommand = string.IsNullOrWhiteSpace(PostCommand) ? null : PostCommand.Trim(),
                CommandTimeoutMinutes = CommandTimeout,
                Workers = _original?.Workers ?? 0,
            };
        }
    }

    public sealed class ScheduleItemViewModel : ObservableObject
    {
        private readonly string _id;
        private ScheduleKind _kind;
        private BackupMode _mode;
        private bool _enabled;
        private string _times;
        private string _days;
        private int _intervalMinutes;

        public ScheduleItemViewModel(ScheduleEntry entry)
        {
            _id = entry.Id;
            _kind = entry.Kind;
            _mode = entry.Mode;
            _enabled = entry.Enabled;
            _times = string.Join(", ", entry.Times ?? new List<string>());
            _days = string.Join(", ", entry.DaysOfMonth ?? new List<int>());
            _intervalMinutes = entry.IntervalMinutes > 0 ? entry.IntervalMinutes : 240;
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
                Weekdays.Add(new WeekdayOption(d, entry.Weekdays != null && entry.Weekdays.Contains(d), () => OnPropertyChanged(nameof(Summary))));
        }

        public static IReadOnlyList<ScheduleKind> Kinds { get; } = new[] { ScheduleKind.Daily, ScheduleKind.Interval, ScheduleKind.Monthly };
        public static IReadOnlyList<BackupMode> Modes { get; } = new[] { BackupMode.Incremental, BackupMode.Full };

        /// <summary>Monday first.</summary>
        public ObservableCollection<WeekdayOption> Weekdays { get; } = new ObservableCollection<WeekdayOption>();
        public IEnumerable<WeekdayOption> WeekdaysMondayFirst => Weekdays.OrderBy(w => ((int)w.Day + 6) % 7);

        public ScheduleKind Kind
        {
            get => _kind;
            set
            {
                if (Set(ref _kind, value))
                {
                    OnPropertyChanged(nameof(IsDaily));
                    OnPropertyChanged(nameof(IsInterval));
                    OnPropertyChanged(nameof(IsMonthly));
                    OnPropertyChanged(nameof(Summary));
                }
            }
        }

        public bool IsDaily => Kind == ScheduleKind.Daily;
        public bool IsInterval => Kind == ScheduleKind.Interval;
        public bool IsMonthly => Kind == ScheduleKind.Monthly;
        public bool HasTimes => !IsInterval;

        public BackupMode Mode { get => _mode; set { if (Set(ref _mode, value)) OnPropertyChanged(nameof(Summary)); } }
        public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) OnPropertyChanged(nameof(Summary)); } }
        public string Times { get => _times; set { if (Set(ref _times, value)) OnPropertyChanged(nameof(Summary)); } }
        public string Days { get => _days; set { if (Set(ref _days, value)) OnPropertyChanged(nameof(Summary)); } }
        public int IntervalMinutes { get => _intervalMinutes; set { if (Set(ref _intervalMinutes, value)) OnPropertyChanged(nameof(Summary)); } }

        public string Summary => ScheduleMath.Describe(ToEntry());

        public ScheduleEntry ToEntry() => new ScheduleEntry
        {
            Id = _id,
            Kind = Kind,
            Mode = Mode,
            Enabled = Enabled,
            IntervalMinutes = IsInterval ? IntervalMinutes : 0,
            Times = HasTimes ? Split(Times).ToList() : new List<string>(),
            Weekdays = IsDaily ? Weekdays.Where(w => w.IsSelected).Select(w => w.Day).ToList() : new List<DayOfWeek>(),
            DaysOfMonth = IsMonthly ? Split(Days).Select(d => int.TryParse(d, out int n) ? n : 0).ToList() : new List<int>(),
        };

        private static IEnumerable<string> Split(string text) =>
            (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim());
    }

    public sealed class WeekdayOption : ObservableObject
    {
        private readonly Action _changed;
        private bool _selected;

        public WeekdayOption(DayOfWeek day, bool selected, Action changed)
        {
            Day = day;
            _selected = selected;
            _changed = changed;
        }

        public DayOfWeek Day { get; }
        public string Label => System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(Day);

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value))
                    _changed();
            }
        }
    }
}
