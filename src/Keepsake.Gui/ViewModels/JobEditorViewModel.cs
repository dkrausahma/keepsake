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
        private string _destination;
        private string _destinationStatus;
        private bool _destinationOk;
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
            HasStoredPassword = detail?.HasRepositoryPassword ?? false;
            Selection.Changed += () =>
            {
                foreach (var root in Roots)
                    root.RefreshState();
                OnPropertyChanged(nameof(SelectedSources));
                OnPropertyChanged(nameof(SelectedExclusions));
            };
            foreach (var drive in FileTreeNode.Drives(Selection))
                Roots.Add(drive);

            if (_original != null)
            {
                _name = _original.Name;
                _enabled = _original.Enabled;
                _patterns = string.Join(Environment.NewLine, Selection.Load(_original.Sources, _original.Excludes));
                _useDefaultExcludes = _original.UseDefaultExcludes;
                _destination = _original.Destination?.Path;
                foreach (var s in _original.Schedules)
                    Schedules.Add(new ScheduleItemViewModel(s));
                _runMissed = _original.RunMissed;
                _retryCount = _original.RetryCount;
                _retryDelay = _original.RetryDelayMinutes;
                _preCommand = _original.PreCommand;
                _postCommand = _original.PostCommand;
                _commandTimeout = _original.CommandTimeoutMinutes;
                _destinationStatus = HasStoredPassword ? "Repository password is stored. Leave the password empty to keep it." : "No password stored – enter the repository password.";
                _destinationOk = HasStoredPassword;
            }
            else
            {
                Schedules.Add(new ScheduleItemViewModel(new ScheduleEntry { Kind = ScheduleKind.Daily, Times = { "22:00" } }));
                _destinationStatus = "Choose a folder, network share or USB disk. A new repository is created there when you save.";
            }
            SelectedSchedule = Schedules.FirstOrDefault();

            AddPathCommand = new RelayCommand(AddPath, () => !string.IsNullOrWhiteSpace(NewPath));
            RemoveSourceCommand = new RelayCommand(p => Selection.Remove((string)p));
            BrowseDestinationCommand = new RelayCommand(() =>
            {
                string picked = Ui.PickFolder("Where should the backups be stored?", Destination);
                if (picked != null)
                    Destination = picked;
            });
            CheckDestinationCommand = new AsyncCommand(p => CheckDestination(((System.Windows.Controls.PasswordBox)p).Password), _ => !string.IsNullOrWhiteSpace(Destination));
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
        }

        public bool IsNew => _original == null;
        public string Title => IsNew ? "New backup job" : "Edit job – " + _original.Name;
        public bool HasStoredPassword { get; }

        public SourceSelection Selection { get; } = new SourceSelection();
        public ObservableCollection<FileTreeNode> Roots { get; } = new ObservableCollection<FileTreeNode>();
        public List<string> SelectedSources => Selection.Sources;
        public List<string> SelectedExclusions => Selection.Exclusions;
        public ObservableCollection<ScheduleItemViewModel> Schedules { get; } = new ObservableCollection<ScheduleItemViewModel>();
        public IReadOnlyList<string> DefaultExcludes => Core.Backup.ExcludeMatcher.Defaults.ToList();

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

        public string Destination
        {
            get => _destination;
            set
            {
                if (Set(ref _destination, value))
                {
                    DestinationOk = false;
                    DestinationStatus = "Not checked yet.";
                }
            }
        }

        public string DestinationStatus { get => _destinationStatus; private set => Set(ref _destinationStatus, value); }
        public bool DestinationOk { get => _destinationOk; private set => Set(ref _destinationOk, value); }

        private bool DestinationChanged => _original == null ||
                                           !string.Equals(_original.Destination?.Path, Destination, StringComparison.OrdinalIgnoreCase);

        public ICommand AddPathCommand { get; }
        public ICommand RemoveSourceCommand { get; }
        public ICommand BrowseDestinationCommand { get; }
        public ICommand CheckDestinationCommand { get; }
        public ICommand AddScheduleCommand { get; }
        public ICommand RemoveScheduleCommand { get; }

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

        private async Task<RepoTestResult> CheckDestination(string password)
        {
            Error = null;
            if (string.IsNullOrEmpty(password) && HasStoredPassword && !DestinationChanged)
            {
                DestinationStatus = "Repository password is stored. Enter it here to check it again.";
                return null;
            }
            try
            {
                var result = await _connection.Call<RepoTestResult>(Methods.RepoTest, new RepoParams { Path = Destination, Password = password });
                if (result.Exists)
                {
                    DestinationOk = true;
                    DestinationStatus = "Repository found, password correct – " + result.Snapshots + " snapshot(s).";
                }
                else
                {
                    DestinationOk = false;
                    DestinationStatus = "No repository here yet. A new encrypted repository will be created when you save.";
                }
                return result;
            }
            catch (ApiException ex)
            {
                DestinationOk = false;
                DestinationStatus = ex.Message;
                throw;
            }
        }

        /// <summary>Validates, creates the repository if needed and saves. Returns true when saved.</summary>
        public async Task<bool> Save(string password, string confirm)
        {
            Error = null;
            var job = Build();
            var errors = JobValidation.Validate(job);
            if (errors.Count > 0)
            {
                Error = string.Join(Environment.NewLine, errors);
                return false;
            }

            string passwordToSend = string.IsNullOrEmpty(password) ? null : password;
            if (DestinationChanged || !HasStoredPassword)
            {
                if (passwordToSend == null)
                {
                    Error = "Enter the repository password (Destination tab).";
                    return false;
                }
                RepoTestResult test;
                try
                {
                    test = await CheckDestination(password);
                }
                catch (ApiException ex)
                {
                    Error = ex.Message;
                    return false;
                }
                if (test != null && !test.Exists)
                {
                    if (password.Length < 8)
                    {
                        Error = "The repository password must have at least 8 characters.";
                        return false;
                    }
                    if (password != confirm)
                    {
                        Error = "The repository passwords do not match.";
                        return false;
                    }
                    if (!Ui.Confirm("Create a new encrypted repository in\n" + Destination + " ?"))
                        return false;
                    var init = await _connection.Call<RepoInitResult>(Methods.RepoInit, new RepoParams { Path = Destination, Password = password });
                    RecoveryKeyWindow.ShowKey(init.RecoveryKey, Destination);
                }
            }

            try
            {
                await _connection.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = passwordToSend });
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
            return new JobDefinition
            {
                Id = _original?.Id,
                Name = Name?.Trim(),
                Enabled = Enabled,
                Sources = Selection.Sources,
                Excludes = excludes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                UseDefaultExcludes = UseDefaultExcludes,
                Destination = new Destination { Type = "folder", Path = Destination?.Trim() },
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
