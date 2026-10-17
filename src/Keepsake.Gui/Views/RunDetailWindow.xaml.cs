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
            DataContext = vm;
        }

        public static void Open(RunDetailViewModel vm) => new RunDetailWindow(vm) { Owner = Ui.Owner }.Show();
    }
}
