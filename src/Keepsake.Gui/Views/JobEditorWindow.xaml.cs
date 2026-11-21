using System;
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
            HelpFiles.Attach(this, () => TabPages[Math.Max(0, Math.Min(Tabs.SelectedIndex, TabPages.Length - 1))]);
        }

        /// <summary>F1: the help page of the selected tab, in tab order.</summary>
        private static readonly string[] TabPages =
        {
            "guide/job-sources.html",      // What to back up
            "guide/job-sources.html",      // Exclusions
            "guide/job-destination.html",
            "guide/job-schedule.html",
            "guide/job-retention.html",
            "guide/job-options.html",
        };

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
