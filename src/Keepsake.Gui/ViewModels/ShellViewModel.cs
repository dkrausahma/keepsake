using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Keepsake.Core;
using Keepsake.Gui.Infrastructure;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    public abstract class PageViewModel : ObservableObject
    {
        private bool _isActive;

        protected PageViewModel(ShellViewModel shell)
        {
            Shell = shell;
        }

        public ShellViewModel Shell { get; }
        protected ServiceConnection Connection => Shell.Connection;

        /// <summary>Bound two-way to the sidebar entry: selecting it (mouse, keyboard, automation) navigates.</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (value && !_isActive)
                    Shell.Navigate(this);
            }
        }

        internal void SetActive(bool value)
        {
            if (_isActive == value)
                return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
        }

        /// <summary>Called when the page is shown and then every few seconds while it is visible.</summary>
        public virtual Task Refresh() => Task.FromResult(0);

        /// <summary>Whether the periodic timer should call <see cref="Refresh"/>.</summary>
        public virtual bool AutoRefresh => false;
    }

    /// <summary>Main window content after login: sidebar navigation and the active page.</summary>
    public sealed class ShellViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly DispatcherTimer _timer;
        private PageViewModel _current;
        private string _footer;
        private bool _refreshing;

        public ShellViewModel(MainViewModel main)
        {
            _main = main;
            Jobs = new JobsViewModel(this);
            Restore = new RestoreViewModel(this);
            History = new HistoryViewModel(this);
            Settings = new SettingsViewModel(this);
            NavigateCommand = new RelayCommand(p => Navigate((PageViewModel)p));
            LogoutCommand = new RelayCommand(main.Logout);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (s, e) => await Tick();
            _timer.Start();
            Navigate(Jobs);
        }

        public ServiceConnection Connection => _main.Connection;
        public JobsViewModel Jobs { get; }
        public RestoreViewModel Restore { get; }
        public HistoryViewModel History { get; }
        public SettingsViewModel Settings { get; }
        public ICommand NavigateCommand { get; }
        public ICommand LogoutCommand { get; }

        public PageViewModel Current
        {
            get => _current;
            private set => Set(ref _current, value);
        }

        public string Footer
        {
            get => _footer;
            private set => Set(ref _footer, value);
        }

        public async void Navigate(PageViewModel page)
        {
            foreach (var p in new PageViewModel[] { Jobs, Restore, History, Settings })
                p.SetActive(p == page);
            Current = page;
            await SafeRefresh(page);
        }

        public void ShowHistory(string jobId)
        {
            History.SelectJob(jobId);
            Navigate(History);
        }

        public void ShowRestore(string jobId)
        {
            Restore.SelectJob(jobId);
            Navigate(Restore);
        }

        public void Stop() => _timer.Stop();

        private async Task Tick()
        {
            if (_refreshing || !Connection.LoggedIn)
                return;
            _refreshing = true;
            try
            {
                if (Current != null && Current.AutoRefresh)
                    await SafeRefresh(Current);
                var status = await Connection.Call<ServiceStatus>(Methods.Status);
                Footer = status.Running.Count == 0
                    ? "Service " + status.Version + " on " + status.Host + " · idle"
                    : "Running: " + string.Join(", ", status.Running.Select(r => r.JobName + " (" + r.Phase.ToLowerInvariant() + ")")) +
                      (status.Queued.Count > 0 ? " · waiting: " + string.Join(", ", status.Queued) : "");
            }
            catch (KeepsakeException ex)
            {
                Footer = ex.Message;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private static async Task SafeRefresh(PageViewModel page)
        {
            try
            {
                await page.Refresh();
            }
            catch (ApiException ex) when (ex.Code == ErrorCodes.Unauthorized)
            {
                // Login screen takes over.
            }
            catch (KeepsakeException ex)
            {
                Ui.ShowError(ex);
            }
        }
    }
}
