using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Keepsake.Core.Storage
{
    /// <summary>
    /// TEMPORARY – investigation of "MAC error" with SFTP on Windows Server 2008 R2 (0.9.6). Writes every SFTP
    /// connection attempt (algorithm combination, result, full exception) and SSH.NET's own debug messages
    /// (algorithm negotiation) to a text file that can be sent for analysis. Never contains passwords or keys.
    /// Remove once the cause is known.
    /// </summary>
    public static class SftpDebugLog
    {
        private const long MaxBytes = 5 * 1024 * 1024;
        private static readonly object Lock = new object();
        private static string _path;

        public static string FilePath => _path;

        public static void Enable(string path)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                _path = path;
                SshNetLoggingConfiguration.InitializeLogging(new Factory());
                Write("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " log started: Keepsake " +
                      typeof(SftpDebugLog).Assembly.GetName().Version + " (AES-CTR and HMAC via BouncyCastle), " +
                      System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ", " +
                      System.Runtime.InteropServices.RuntimeInformation.OSDescription + ", " +
                      System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + ", SSH.NET " +
                      typeof(SftpClient).Assembly.GetName().Version + ", FIPS-only policy " + FipsOnly() + ", process " + System.Diagnostics.Process.GetCurrentProcess().ProcessName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _path = null; // logging must never break a backup
            }
        }

        /// <summary>Windows "use FIPS compliant algorithms" policy as .NET sees it (changes which crypto classes are used).</summary>
        public static string FipsOnly()
        {
            var p = Type.GetType("System.Security.Cryptography.CryptoConfig, mscorlib")?.GetProperty("AllowOnlyFipsAlgorithms");
            return p == null ? "unknown" : ((bool)p.GetValue(null) ? "ON" : "off");
        }

        internal static void Write(string line)
        {
            string path = _path;
            if (path == null)
                return;
            lock (Lock)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = path + ".1";
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " [" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] " + line + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Best effort.
                }
            }
        }

        private sealed class Factory : ILoggerFactory
        {
            public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
        }

        private sealed class Logger : ILogger
        {
            private readonly string _category;

            public Logger(string category)
            {
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) => Scope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                Write("ssh.net " + logLevel + " " + _category + ": " + formatter(state, exception) + (exception == null ? "" : " | " + exception));
            }
        }

        private sealed class Scope : IDisposable
        {
            public static readonly Scope Instance = new Scope();
            public void Dispose() { }
        }
    }
}
