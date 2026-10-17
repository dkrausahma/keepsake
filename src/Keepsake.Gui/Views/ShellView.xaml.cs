using System.Windows.Controls;

namespace Keepsake.Gui.Views
{
    public partial class ShellView : UserControl
    {
        public ShellView()
        {
            InitializeComponent();
        }

        private void OnAbout(object sender, System.Windows.RoutedEventArgs e) => AboutWindow.ShowAbout();
    }
}
