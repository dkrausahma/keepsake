using System;
using System.Linq;
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
            // TEMPORARY: SFTP connection details (recovery mode connects itself) for the 2008 R2 "MAC error" investigation.
            Core.Storage.SftpDebugLog.Enable(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keepsake-sftp-debug.log"));

            string pipe = null;
            string autoLoginFile = null;
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
#if DEBUG
                // Development and automated UI tests only; not available in release builds, where the GUI always
                // talks to the installed service (SEC-016).
                // --pipe <name> connects to a service started with 'keepsake-service console --pipe <name>'.
                if (string.Equals(e.Args[i], "--pipe", StringComparison.OrdinalIgnoreCase))
                    pipe = e.Args[i + 1];
                if (string.Equals(e.Args[i], "--app-password-file", StringComparison.OrdinalIgnoreCase))
                    autoLoginFile = e.Args[i + 1];
#endif
            }

            // --recovery: restore without the service (portable version on a replacement computer, RST-07).
            bool recovery = e.Args.Any(a => string.Equals(a, "--recovery", StringComparison.OrdinalIgnoreCase));

            var main = new MainViewModel(new ServiceConnection(pipe));
            var window = new MainWindow { DataContext = main };
            MainWindow = window;
            window.Show();
            if (recovery)
                main.StartRecovery();
            else
                main.Start(autoLoginFile == null ? null : System.IO.File.ReadAllText(autoLoginFile).Trim());
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Ui.ShowError(e.Exception);
            e.Handled = true;
        }
    }
}
