using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Keepsake.Gui.Infrastructure;

namespace Keepsake.Gui.Views
{
    /// <summary>Shows a new recovery key and only closes after the user confirmed storing it (KEY-03).</summary>
    public partial class RecoveryKeyWindow : Window
    {
        private readonly string _key;
        private readonly string _location;

        private RecoveryKeyWindow(string key, string location)
        {
            InitializeComponent();
            _key = key;
            _location = location;
            KeyBox.Text = key;
            Location.Text = location;
        }

        public static void ShowKey(string key, string location)
        {
            new RecoveryKeyWindow(key, location) { Owner = Ui.Owner }.ShowDialog();
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(_key);
            Feedback.Text = "Copied. Paste it into KeePass, then clear the clipboard.";
        }

        private void OnSaveFile(object sender, RoutedEventArgs e)
        {
            string file = Ui.SaveFile("Save recovery key", "keepsake-recovery-key.txt", "Text file (*.txt)|*.txt");
            if (file == null)
                return;
            File.WriteAllText(file, "Keepsake recovery key\r\nRepository: " + _location + "\r\nCreated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n\r\n" + _key + "\r\n");
            Feedback.Text = "Saved. Move the file off this server.";
        }

        private void OnConfirmChanged(object sender, RoutedEventArgs e) => CloseButton.IsEnabled = Confirmed.IsChecked == true;

        private void OnDone(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (Confirmed.IsChecked != true &&
                !Ui.Confirm("You have not confirmed that the recovery key is stored.\nWithout it a lost password means lost backups.\n\nClose anyway?", "Recovery key"))
                e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
