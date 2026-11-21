using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Keepsake.Core;
using Keepsake.Core.Reporting;
using Keepsake.Gui.Infrastructure;
using Keepsake.Protocol;

namespace Keepsake.Gui.Views
{
    /// <summary>Checks a repository password or recovery key against a job's repository, without restoring anything.</summary>
    public partial class VerifyKeyWindow : Window
    {
        private readonly ServiceConnection _connection;
        private readonly string _jobId;

        private VerifyKeyWindow(ServiceConnection connection, string jobId, string jobName, string destination)
        {
            InitializeComponent();
            HelpFiles.Attach(this, "PASSWORDS-AND-KEYS.html");
            _connection = connection;
            _jobId = jobId;
            JobName.Text = "Check the keys of '" + jobName + "'";
            Location.Text = destination;
            Loaded += (s, e) => PasswordBox.Focus();
        }

        public static void Open(ServiceConnection connection, string jobId, string jobName, string destination) =>
            new VerifyKeyWindow(connection, jobId, jobName, destination) { Owner = Ui.Owner }.ShowDialog();

        private bool CheckingKey => UseRecoveryKey.IsChecked == true;

        private void OnKindChanged(object sender, RoutedEventArgs e)
        {
            if (KeyPanel == null)
                return; // during InitializeComponent
            PasswordBox.Visibility = CheckingKey ? Visibility.Collapsed : Visibility.Visible;
            KeyPanel.Visibility = CheckingKey ? Visibility.Visible : Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            if (CheckingKey)
                KeyBox.Focus();
            else
                PasswordBox.Focus();
        }

        private void OnLoadFile(object sender, RoutedEventArgs e)
        {
            string file = Ui.OpenFile("Recovery key file", "Text files (*.txt)|*.txt|All files|*.*");
            if (file == null)
                return;
            try
            {
                KeyBox.Text = File.ReadAllText(file).Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Show(false, ex.Message);
            }
        }

        private async void OnCheck(object sender, RoutedEventArgs e)
        {
            var p = new VerifyKeyParams { JobId = _jobId };
            if (CheckingKey)
                p.RecoveryKey = KeyBox.Text;
            else
                p.Password = PasswordBox.Password;
            if (string.IsNullOrWhiteSpace(p.RecoveryKey) && string.IsNullOrEmpty(p.Password))
            {
                Show(false, CheckingKey ? "Enter or load the recovery key." : "Enter the repository password.");
                return;
            }

            CheckButton.IsEnabled = false;
            Busy.Visibility = Visibility.Visible;
            ResultPanel.Visibility = Visibility.Collapsed;
            try
            {
                var result = await _connection.Call<VerifyKeyResult>(Methods.RepoVerifyKey, p);
                string what = CheckingKey ? "recovery key" : "password";
                if (result.Valid)
                    Show(true, "This " + what + " opens the repository" +
                               (result.KeyLabel != null ? " (key '" + result.KeyLabel + "'" + (result.KeyCreated.HasValue ? ", created " + Format.LocalTime(result.KeyCreated.Value) : "") + ")" : "") +
                               ". You can restore with it on any computer.");
                else
                    Show(false, "This " + what + " does NOT open the repository in " + result.Location + ". Check for typos, or whether it belongs to another job.");
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is IOException)
            {
                Show(false, ex.Message);
            }
            finally
            {
                CheckButton.IsEnabled = true;
                Busy.Visibility = Visibility.Collapsed;
            }
        }

        private void Show(bool ok, string message)
        {
            ResultText.Text = (ok ? "✓  " : "✗  ") + message;
            ResultText.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
            ResultPanel.Background = (Brush)FindResource(ok ? "SurfaceBrush" : "DangerSoftBrush");
            ResultPanel.BorderBrush = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
            ResultPanel.BorderThickness = new Thickness(1);
            ResultPanel.Visibility = Visibility.Visible;
        }
    }
}
