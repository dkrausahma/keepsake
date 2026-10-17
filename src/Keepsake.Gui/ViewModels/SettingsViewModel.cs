using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using Keepsake.Core.Reporting;
using Keepsake.Gui.Infrastructure;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    public sealed class SettingsViewModel : PageViewModel
    {
        private ServiceSettings _settings = new ServiceSettings();
        private ServiceStatus _status;
        private string _passwordMessage;
        private string _settingsMessage;

        public SettingsViewModel(ShellViewModel shell) : base(shell)
        {
            SaveCommand = new AsyncCommand(Save);
            ChangePasswordCommand = new AsyncCommand(ChangePassword);
        }

        public ICommand SaveCommand { get; }
        public ICommand ChangePasswordCommand { get; }

        public int MaxConcurrentJobs { get => _settings.MaxConcurrentJobs; set { _settings.MaxConcurrentJobs = value; OnPropertyChanged(); } }
        public int LogRetentionDays { get => _settings.LogRetentionDays; set { _settings.LogRetentionDays = value; OnPropertyChanged(); } }
        public int LogRetentionRuns { get => _settings.LogRetentionRuns; set { _settings.LogRetentionRuns = value; OnPropertyChanged(); } }

        public string ServiceInfo => _status == null ? "" :
            "Keepsake " + _status.Version + " on " + _status.Host + "\nRunning since " + Format.LocalTime(_status.StartedAt) + "\nData folder: " + _status.DataFolder;

        public string PasswordMessage { get => _passwordMessage; private set => Set(ref _passwordMessage, value); }
        public string SettingsMessage { get => _settingsMessage; private set => Set(ref _settingsMessage, value); }

        public override async Task Refresh()
        {
            _settings = await Connection.Call<ServiceSettings>(Methods.SettingsGet);
            _status = await Connection.Call<ServiceStatus>(Methods.Status);
            SettingsMessage = null;
            OnAllChanged();
        }

        private async Task Save()
        {
            try
            {
                _settings = await Connection.Call<ServiceSettings>(Methods.SettingsSave, _settings);
                SettingsMessage = "Saved.";
            }
            catch (ApiException ex)
            {
                SettingsMessage = ex.Message;
            }
            OnPropertyChanged(nameof(MaxConcurrentJobs));
        }

        private async Task ChangePassword(object parameter)
        {
            var boxes = (object[])parameter;
            var current = (PasswordBox)boxes[0];
            var next = (PasswordBox)boxes[1];
            var repeat = (PasswordBox)boxes[2];
            if (next.Password != repeat.Password)
            {
                PasswordMessage = "The new passwords do not match.";
                return;
            }
            try
            {
                await Connection.Call(Methods.AuthChangePassword, new ChangePasswordParams { OldPassword = current.Password, NewPassword = next.Password });
                current.Clear();
                next.Clear();
                repeat.Clear();
                PasswordMessage = "Application password changed.";
            }
            catch (ApiException ex)
            {
                PasswordMessage = ex.Message;
            }
        }
    }
}
