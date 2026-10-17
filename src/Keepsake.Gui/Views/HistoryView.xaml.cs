using System.Windows.Controls;
using System.Windows.Input;
using Keepsake.Gui.ViewModels;

namespace Keepsake.Gui.Views
{
    public partial class HistoryView : UserControl
    {
        public HistoryView()
        {
            InitializeComponent();
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is HistoryViewModel vm && Grid.SelectedItem is RunRowViewModel row && vm.OpenCommand.CanExecute(row))
                vm.OpenCommand.Execute(row);
        }
    }
}
