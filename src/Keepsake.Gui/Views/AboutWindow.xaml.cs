using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace Keepsake.Gui.Views
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            var asm = typeof(AboutWindow).Assembly;
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version.ToString(3);
            // Strip the "+commit" build metadata the SDK appends.
            var plus = version.IndexOf('+');
            VersionText.Text = "Version " + (plus > 0 ? version.Substring(0, plus) : version);
            CopyrightText.Text = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        }

        public static void ShowAbout()
        {
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
            new AboutWindow { Owner = owner }.ShowDialog();
        }

        private void OnNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No handler for the link (e.g. tel: on a server) – nothing useful to do.
            }
            e.Handled = true;
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
