using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>
    /// The help: static HTML pages in help\ next to the program (generated from docs\ by tools\help-docs\build.py),
    /// opened in the default browser. Works offline and always matches the installed version.
    /// </summary>
    public static class HelpFiles
    {
        public const string Index = "index.html";

        public static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "help");

        /// <summary>The local file of a page ("guide/restore.html"), or null if it is not installed.</summary>
        public static string PathOf(string page)
        {
            string path = Path.Combine(Folder, (string.IsNullOrEmpty(page) ? Index : page).Replace('/', '\\'));
            return File.Exists(path) ? path : null;
        }

        /// <summary>Opens a help page; an unknown page opens the start page.</summary>
        public static void Open(string page)
        {
            string path = PathOf(page) ?? PathOf(Index);
            if (path == null)
            {
                Ui.Info("The help files were not found in\n" + Folder + "\n\nReinstall Keepsake to restore them.");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                Ui.Info("Could not open the help:\n" + ex.Message);
            }
        }

        /// <summary>F1 (and any Help command button) in the window opens <paramref name="page"/>.</summary>
        public static void Attach(Window window, string page) => Attach(window, () => page);

        /// <summary>
        /// F1 in the window opens the page <paramref name="page"/> returns at that moment. A Help command with a page as
        /// its parameter (a Help button) opens that page instead.
        /// </summary>
        public static void Attach(Window window, Func<string> page)
        {
            window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Help, (s, e) =>
            {
                Open(e.Parameter as string ?? page());
                e.Handled = true;
            }));
        }
    }
}
