using System;
using System.Text;
using System.Threading;

namespace Keepsake.Cli
{
    /// <summary>Console output helpers: masked prompts and a single self-updating progress line.</summary>
    public static class ConsoleUi
    {
        private static readonly object Sync = new object();
        private static int _progressLength;

        public static bool CanPrompt => Environment.UserInteractive && !Console.IsInputRedirected;
        public static bool CanShowProgress => !Console.IsOutputRedirected;

        public static string ReadSecret(string prompt)
        {
            Console.Error.Write(prompt);
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Error.Write("\b \b");
                    }
                    continue;
                }
                if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Error.Write('*');
                }
            }
            Console.Error.WriteLine();
            return sb.ToString();
        }

        public static string ReadLine(string prompt)
        {
            Console.Error.Write(prompt);
            return Console.ReadLine() ?? "";
        }

        public static void Progress(string text)
        {
            if (!CanShowProgress)
                return;
            lock (Sync)
            {
                int width = Math.Max(20, SafeWidth() - 1);
                if (text.Length > width)
                    text = text.Substring(0, width - 3) + "...";
                string padded = text.PadRight(_progressLength);
                Console.Out.Write("\r" + padded);
                _progressLength = text.Length;
            }
        }

        public static void ClearProgress()
        {
            if (!CanShowProgress)
                return;
            lock (Sync)
            {
                if (_progressLength > 0)
                    Console.Out.Write("\r" + new string(' ', _progressLength) + "\r");
                _progressLength = 0;
            }
        }

        /// <summary>Shortens a long path for the progress line, keeping the end.</summary>
        public static string Tail(string path, int max)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max)
                return path ?? "";
            return "..." + path.Substring(path.Length - max + 3);
        }

        private static int SafeWidth()
        {
            try
            {
                return Console.WindowWidth;
            }
            catch (System.IO.IOException)
            {
                return 120;
            }
        }
    }

    /// <summary>IProgress that keeps only the latest value; a timer renders it.</summary>
    public sealed class LatestProgress<T> : IProgress<T>, IDisposable where T : class
    {
        private readonly Timer _timer;
        private T _latest;

        public LatestProgress(Action<T> render)
        {
            _timer = new Timer(_ =>
            {
                var value = Interlocked.Exchange(ref _latest, null);
                if (value != null)
                    render(value);
            }, null, 500, 500);
        }

        public void Report(T value) => Interlocked.Exchange(ref _latest, value);

        public void Dispose()
        {
            using (var done = new ManualResetEvent(false))
            {
                _timer.Dispose(done);
                done.WaitOne(2000);
            }
            ConsoleUi.ClearProgress();
        }
    }
}
