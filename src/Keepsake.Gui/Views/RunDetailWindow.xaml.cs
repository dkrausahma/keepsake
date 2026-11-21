using System.Windows;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.ViewModels;

namespace Keepsake.Gui.Views
{
    public partial class RunDetailWindow : Window
    {
        private RunDetailWindow(RunDetailViewModel vm)
        {
            InitializeComponent();
            HelpFiles.Attach(this, "guide/history.html");
            DataContext = vm;
        }

        public static void Open(RunDetailViewModel vm) => new RunDetailWindow(vm) { Owner = Ui.Owner }.Show();
    }
}
