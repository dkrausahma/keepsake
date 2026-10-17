using System.Windows;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.ViewModels;
using Keepsake.Protocol;

namespace Keepsake.Gui.Views
{
    public partial class JobEditorWindow : Window
    {
        private readonly JobEditorViewModel _vm;

        private JobEditorWindow(JobEditorViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Loaded += (s, e) => NameBox.Focus();
        }

        /// <summary>Shows the editor; returns true when the job was saved.</summary>
        public static bool Open(ServiceConnection connection, JobDetail detail)
        {
            var window = new JobEditorWindow(new JobEditorViewModel(connection, detail)) { Owner = Ui.Owner };
            return window.ShowDialog() == true;
        }

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            SaveButton.IsEnabled = false;
            try
            {
                if (await _vm.Save())
                    DialogResult = true;
            }
            catch (System.Exception ex)
            {
                Ui.ShowError(ex);
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }
    }
}
