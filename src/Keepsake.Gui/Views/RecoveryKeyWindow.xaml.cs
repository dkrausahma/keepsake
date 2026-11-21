using System.ComponentModel;
using System.Windows;
using Keepsake.Gui.Infrastructure;

namespace Keepsake.Gui.Views
{
    /// <summary>Shows a new recovery key and only closes after the user confirmed storing it (KEY-03).</summary>
    public partial class RecoveryKeyWindow : Window
    {
        private readonly string _key;
        private readonly string _entry;

        private RecoveryKeyWindow(string key, string location, string keePassEntry)
        {
            InitializeComponent();
            HelpFiles.Attach(this, "PASSWORDS-AND-KEYS.html");
            _key = key;
            _entry = keePassEntry;
            KeyBox.Text = key;
            Location.Text = location;
        }

        /// <param name="keePassEntry">The job's KeePass text block including this key (<see cref="KeePassEntry"/>).</param>
        public static void ShowKey(string key, string location, string keePassEntry)
        {
            new RecoveryKeyWindow(key, location, keePassEntry) { Owner = Ui.Owner }.ShowDialog();
        }

        private void OnCopyEntry(object sender, RoutedEventArgs e)
        {
            Ui.CopyToClipboard(_entry, secret: true);
            Feedback.Text = "Copied: location, host key, login hints and the recovery key. Paste it into the job's KeePass entry; the clipboard is cleared in two minutes.";
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            Ui.CopyToClipboard(_key, secret: true);
            Feedback.Text = "Copied. Paste it into KeePass; the clipboard is cleared in two minutes.";
        }

        private void OnSaveFile(object sender, RoutedEventArgs e)
        {
            string file = Ui.SaveFile("Save recovery key", "keepsake-recovery-key.txt", "Text file (*.txt)|*.txt");
            if (file == null)
                return;
            // The KeePass block holds the key on a line of its own, so the file also works with "Load from file…" and --recovery-key-file.
            PrivateFile.WriteAllText(file, _entry);
            Feedback.Text = "Saved (readable only by you). Move the file off this server, e.g. as attachment of the KeePass entry.";
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
