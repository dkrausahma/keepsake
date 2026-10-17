using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Keepsake.Service
{
    /// <summary>Diagnostic log of the service itself (LOG-08): <c>logs\service.log</c>, rotated at 10 MB, 5 files kept.</summary>
    internal static class ServiceLog
    {
        private const long MaxSize = 10 * 1024 * 1024;
        private const int Keep = 5;
        private static readonly object Sync = new object();
        private static string _file;
        private static bool _console;

        public static void Init(string folder, bool console)
        {
            lock (Sync)
            {
                Directory.CreateDirectory(folder);
                _file = Path.Combine(folder, "service.log");
                _console = console;
            }
        }

        public static void Info(string message) => Write("INFO ", message, null);
        public static void Warn(string message, Exception ex = null) => Write("WARN ", message, ex);
        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + level + "] " + message +
                          (ex != null ? Environment.NewLine + "    " + ex : "");
            lock (Sync)
            {
                if (_console)
                    Console.WriteLine(line);
                if (_file == null)
                    return;
                try
                {
                    Rotate();
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // Logging must never take the service down.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static void Rotate()
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length < MaxSize)
                return;
            for (int i = Keep - 1; i >= 1; i--)
            {
                string from = _file + "." + i;
                string to = _file + "." + (i + 1);
                if (File.Exists(to))
                    File.Delete(to);
                if (File.Exists(from))
                    File.Move(from, to);
            }
            File.Move(_file, _file + ".1");
        }
    }
}
