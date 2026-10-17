using System;
using System.Windows;
using System.Windows.Threading;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.ViewModels;
using Keepsake.Gui.Views;

namespace Keepsake.Gui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandled;

            // --pipe <name> connects to a service started with 'keepsake-service console --pipe <name>' (testing).
            string pipe = null;
            string autoLoginFile = null;
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (string.Equals(e.Args[i], "--pipe", StringComparison.OrdinalIgnoreCase))
                    pipe = e.Args[i + 1];
#if DEBUG
                // Automated UI tests only; not available in release builds.
                if (string.Equals(e.Args[i], "--app-password-file", StringComparison.OrdinalIgnoreCase))
                    autoLoginFile = e.Args[i + 1];
#endif
            }

            var main = new MainViewModel(new ServiceConnection(pipe));
            var window = new MainWindow { DataContext = main };
            MainWindow = window;
            window.Show();
            main.Start(autoLoginFile == null ? null : System.IO.File.ReadAllText(autoLoginFile).Trim());
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Ui.ShowError(e.Exception);
            e.Handled = true;
        }
    }
}
