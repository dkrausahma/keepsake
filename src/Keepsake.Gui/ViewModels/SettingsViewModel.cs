using System.Collections.Generic;
using System.Linq;
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
        private SmtpSettings _smtp = new SmtpSettings();
        private string _smtpPassword;
        private string _smtpMessage;

        public SettingsViewModel(ShellViewModel shell) : base(shell)
        {
            SaveCommand = new AsyncCommand(Save);
            ChangePasswordCommand = new AsyncCommand(ChangePassword);
            SaveSmtpCommand = new AsyncCommand(SaveSmtp);
            TestSmtpCommand = new AsyncCommand(TestSmtp);
        }

        public ICommand SaveCommand { get; }
        public ICommand ChangePasswordCommand { get; }
        public ICommand SaveSmtpCommand { get; }
        public ICommand TestSmtpCommand { get; }

        public IReadOnlyList<string> SecurityOptions { get; } = new[] { "starttls", "ssl", "none" };
        public string SmtpHost { get => _smtp.Host; set { _smtp.Host = value; OnPropertyChanged(); } }
        public int SmtpPort { get => _smtp.Port; set { _smtp.Port = value; OnPropertyChanged(); } }
        public string SmtpSecurity { get => _smtp.Security; set { _smtp.Security = value; OnPropertyChanged(); } }
        public string SmtpUser { get => _smtp.User; set { _smtp.User = value; OnPropertyChanged(); } }
        public string SmtpFrom { get => _smtp.From; set { _smtp.From = value; OnPropertyChanged(); } }
        public string SmtpTo { get => string.Join("; ", _smtp.To ?? new List<string>()); set { _smtp.To = (value ?? "").Split(new[] { ';', ',' }, System.StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList(); OnPropertyChanged(); } }
        public string SmtpPassword { get => _smtpPassword; set => Set(ref _smtpPassword, value); }
        public string SmtpPasswordHint => _smtp.HasPassword ? "A password is stored; leave empty to keep it." : "";
        public string SmtpMessage { get => _smtpMessage; private set => Set(ref _smtpMessage, value); }

        private async Task SaveSmtp()
        {
            try
            {
                _smtp.Password = string.IsNullOrEmpty(SmtpPassword) ? null : SmtpPassword;
                await Connection.Call(Methods.SmtpSave, _smtp);
                SmtpPassword = null;
                _smtp = await Connection.Call<SmtpSettings>(Methods.SmtpGet);
                OnAllChanged();
                SmtpMessage = "Saved.";
            }
            catch (ApiException ex)
            {
                SmtpMessage = ex.Message;
            }
        }

        private async Task TestSmtp()
        {
            SmtpMessage = "Sending…";
            try
            {
                await Connection.Call(Methods.SmtpTest);
                SmtpMessage = "Test e-mail sent to " + SmtpTo + ".";
            }
            catch (Core.KeepsakeException ex)
            {
                SmtpMessage = ex.Message;
            }
        }

        public int MaxConcurrentJobs { get => _settings.MaxConcurrentJobs; set { _settings.MaxConcurrentJobs = value; OnPropertyChanged(); } }
        public int LogRetentionDays { get => _settings.LogRetentionDays; set { _settings.LogRetentionDays = value; OnPropertyChanged(); } }
        public int LogRetentionRuns { get => _settings.LogRetentionRuns; set { _settings.LogRetentionRuns = value; OnPropertyChanged(); } }
        public bool DebugLog { get => _settings?.DebugLog ?? false; set { _settings.DebugLog = value; OnPropertyChanged(); } }
        public string DebugLogText => _settings?.DebugLogPath == null ? null : "File: " + _settings.DebugLogPath + " (only readable by administrators; at most 5 MB, then the older part moves to .1)";

        public string ServiceInfo => _status == null ? "" :
            "Keepsake " + _status.Version + " on " + _status.Host + "\nRunning since " + Format.LocalTime(_status.StartedAt) + "\nData folder: " + _status.DataFolder;

        public string PasswordMessage { get => _passwordMessage; private set => Set(ref _passwordMessage, value); }
        public string SettingsMessage { get => _settingsMessage; private set => Set(ref _settingsMessage, value); }

        public override async Task Refresh()
        {
            _settings = await Connection.Call<ServiceSettings>(Methods.SettingsGet);
            _status = await Connection.Call<ServiceStatus>(Methods.Status);
            _smtp = await Connection.Call<SmtpSettings>(Methods.SmtpGet) ?? new SmtpSettings();
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
            OnPropertyChanged(nameof(DebugLog));
            OnPropertyChanged(nameof(DebugLogText));
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
