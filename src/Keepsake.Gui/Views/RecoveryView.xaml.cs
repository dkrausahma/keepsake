using System.Windows;
using System.Windows.Controls;
using Keepsake.Gui.ViewModels;

namespace Keepsake.Gui.Views
{
    public partial class RecoveryView : UserControl
    {
        public RecoveryView()
        {
            InitializeComponent();
            Loaded += (s, e) => LocationBox.Focus();
        }

        private async void OnOpen(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is RecoveryViewModel vm) || vm.Busy)
                return;
            OpenButton.IsEnabled = false;
            try
            {
                await vm.Open(PasswordBox.Password, SftpPasswordBox.Password, SftpPassphraseBox.Password);
            }
            finally
            {
                OpenButton.IsEnabled = true;
                if (vm.IsOpen)
                {
                    // Passwords are not needed any more once the repository is open.
                    PasswordBox.Clear();
                    SftpPasswordBox.Clear();
                    SftpPassphraseBox.Clear();
                }
            }
        }

        private void OnAbout(object sender, RoutedEventArgs e) => AboutWindow.ShowAbout();
    }
}
