using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Keepsake.Core;
using Keepsake.Protocol;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>Message boxes and file dialogs, always owned by the active window.</summary>
    public static class Ui
    {
        public static Window Owner =>
            Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

        public static void ShowError(Exception ex)
        {
            // Session problems are handled by the login screen; no extra dialog.
            if (ex is ApiException api && api.Code == ErrorCodes.Unauthorized)
                return;
            string message = ex is KeepsakeException || ex is System.IO.IOException || ex is UnauthorizedAccessException
                ? ex.Message
                : "Unexpected error: " + ex.Message;
            MessageBox.Show(Owner, message, "Keepsake Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public static void Info(string message) =>
            MessageBox.Show(Owner, message, "Keepsake Backup", MessageBoxButton.OK, MessageBoxImage.Information);

        public static bool Confirm(string message, string title = "Keepsake Backup") =>
            MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        public static string PickFolder(string description, string initial = null)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
            {
                if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
                    dialog.SelectedPath = initial;
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        public static string SaveFile(string title, string fileName, string filter)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
            return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
        }

        public static string OpenFile(string title, string filter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
        }

        /// <summary>
        /// Copies text to the clipboard. With <paramref name="secret"/> it is kept out of Windows clipboard history and the
        /// cloud clipboard, and cleared after two minutes if it is still there (SEC-021).
        /// </summary>
        public static void CopyToClipboard(string text, bool secret)
        {
            var data = new DataObject();
            data.SetText(text);
            if (secret)
            {
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[4]));
                data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(new byte[4]));
            }
            Clipboard.SetDataObject(data, copy: true);
            if (!secret)
                return;
            var clear = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            clear.Tick += (s, e) =>
            {
                clear.Stop();
                try
                {
                    if (Clipboard.ContainsText() && Clipboard.GetText() == text)
                        Clipboard.Clear();
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // Clipboard busy: nothing to do.
                }
            };
            clear.Start();
        }
    }

    // ---- Converters --------------------------------------------------------------------------

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool visible = value is bool b ? b
                : value is int n ? n != 0
                : value != null && !(value is string s && s.Length == 0);
            if (Invert)
                visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Run state → colour of status dots and labels.</summary>
    public sealed class StateBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key;
            switch (value as string ?? value?.ToString())
            {
                case "Success": key = "SuccessBrush"; break;
                case "Warning": key = "WarningBrush"; break;
                case "Failed": key = "DangerBrush"; break;
                case "Running":
                case "Queued": key = "InfoBrush"; break;
                default: key = "MutedBrush"; break;
            }
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
