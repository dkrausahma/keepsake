using System;
using System.Diagnostics;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>
    /// Important events in the Windows Application log, source "Keepsake" (LOG-07), so existing monitoring
    /// (PRTG, Zabbix, Nagios, SCOM) can alert on them. Event IDs:
    /// 1000 run succeeded · 1001 run failed · 1002 run with warnings · 1003 scheduled run skipped/missed ·
    /// 1004 no successful backup for too long · 1005 notification could not be sent.
    /// </summary>
    internal static class EventLogWriter
    {
        public const string Source = "Keepsake";
        private static bool _available;

        public static void Init()
        {
            try
            {
                if (!EventLog.SourceExists(Source))
                    EventLog.CreateEventSource(Source, "Application");
                _available = true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is InvalidOperationException || ex is ArgumentException)
            {
                ServiceLog.Warn("Windows Event Log not available (needs administrator rights once): " + ex.Message);
            }
        }

        public static void RunFinished(RunRecord r)
        {
            string what = r.Kind.ToString().ToLowerInvariant() + " of '" + r.JobName + "'";
            switch (r.State)
            {
                case RunState.Success:
                    Write(EventLogEntryType.Information, 1000, "Keepsake " + what + " succeeded." + Detail(r));
                    break;
                case RunState.Warning:
                    Write(EventLogEntryType.Warning, 1002, "Keepsake " + what + " finished with warnings: " + r.Message + Detail(r));
                    break;
                case RunState.Failed:
                    Write(EventLogEntryType.Error, 1001, "Keepsake " + what + " FAILED: " + r.Message + Detail(r));
                    break;
            }
        }

        public static void Warning(int id, string message) => Write(EventLogEntryType.Warning, id, message);
        public static void Error(int id, string message) => Write(EventLogEntryType.Error, id, message);

        private static string Detail(RunRecord r) => "\r\n\r\nRun: " + r.Id + "\r\nJob ID: " + r.JobId + "\r\nStarted: " + r.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        private static void Write(EventLogEntryType type, int id, string message)
        {
            if (!_available)
                return;
            try
            {
                EventLog.WriteEntry(Source, message.Length > 30000 ? message.Substring(0, 30000) : message, type, id);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                ServiceLog.Warn("Could not write to the Event Log: " + ex.Message);
            }
        }
    }
}
