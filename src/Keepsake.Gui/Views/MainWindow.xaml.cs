using System.Windows;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.ViewModels;

namespace Keepsake.Gui.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // F1: the page of the current screen; the sidebar's Help button passes the start page.
            HelpFiles.Attach(this, () => (DataContext as MainViewModel)?.HelpPage);
        }
    }
}
