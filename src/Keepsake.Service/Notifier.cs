using System;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Keepsake.Core;
using Keepsake.Core.Reporting;
using Keepsake.Protocol;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Keepsake.Service
{
    /// <summary>E-mail notifications (NOT-01..03) via SMTP with STARTTLS, implicit SSL or plain.</summary>
    internal sealed class Notifier
    {
        private readonly SettingsStore _settings;
        private readonly JobStore _jobs;
        private readonly RunHistory _history;

        public Notifier(SettingsStore settings, JobStore jobs, RunHistory history)
        {
            _settings = settings;
            _jobs = jobs;
            _history = history;
        }

        public bool Configured => _settings.Get().Smtp?.Enabled == true;

        /// <summary>Sends a mail with the stored settings. Throws <see cref="KeepsakeException"/> with a readable reason.</summary>
        public void Send(string subject, string body)
        {
            var s = _settings.Get();
            var smtp = s.Smtp;
            if (smtp == null || !smtp.Enabled)
                throw new KeepsakeException("No SMTP server or recipient configured.");
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(smtp.From) ? "keepsake@" + ProgramInfo.Host.ToLowerInvariant() : smtp.From));
            foreach (string to in smtp.To)
                message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            try
            {
                using (var client = new SmtpClient())
                {
                    client.Timeout = 30000;
                    // Server 2008 R2 needs TLS 1.2 enabled in SChannel; TLS 1.3 only exists on newer Windows.
                    client.SslProtocols = Environment.OSVersion.Version >= new Version(10, 0, 20348) ? SslProtocols.Tls12 | SslProtocols.Tls13 : SslProtocols.Tls12;
                    var security = smtp.Security == "ssl" ? SecureSocketOptions.SslOnConnect
                        : smtp.Security == "none" ? SecureSocketOptions.None
                        : SecureSocketOptions.StartTls;
                    client.Connect(smtp.Host, smtp.Port, security);
                    if (!string.IsNullOrEmpty(smtp.User))
                    {
                        // Kept for internal relays without TLS, but never silently (SEC-020).
                        if (security == SecureSocketOptions.None)
                            ServiceLog.Warn("SMTP login to " + smtp.Host + " without encryption: the password and the report travel in clear text. Use STARTTLS or SSL.");
                        client.Authenticate(smtp.User, Secrets.Unprotect(s.SmtpPassword) ?? "");
                    }
                    client.Send(message);
                    client.Disconnect(true);
                }
            }
            catch (Exception ex) when (ex is SocketException || ex is System.Security.Authentication.AuthenticationException || ex is MailKit.Security.AuthenticationException ||
                                       ex is SmtpCommandException || ex is SmtpProtocolException || ex is SslHandshakeException ||
                                       ex is ServiceNotConnectedException || ex is TimeoutException || ex is System.IO.IOException || ex is ParseException ||
                                       ex is NotSupportedException)
            {
                throw new KeepsakeException("E-mail could not be sent via " + smtp.Host + ":" + smtp.Port + ": " + ex.Message);
            }
        }

        /// <summary>Called after every run; sends according to the job's notification settings.</summary>
        public void OnRunFinished(RunRecord r)
        {
            if (!Configured)
                return;
            var job = _jobs.Get(r.JobId);
            if (job == null)
                return;
            bool send = r.State == RunState.Failed ? job.NotifyOnFailure
                : r.State == RunState.Warning ? job.NotifyOnWarning
                : r.State == RunState.Success ? job.NotifyOnSuccess && r.Kind == RunKind.Backup
                : false;
            if (!send)
                return;

            string state = r.State == RunState.Failed ? "FAILED" : r.State == RunState.Warning ? "WARNING" : "OK";
            string subject = "[Keepsake] " + state + ": " + job.Name + " – " + r.Kind.ToString().ToLowerInvariant() + " on " + ProgramInfo.Host;
            var body = new StringBuilder();
            body.AppendLine("Job:      " + job.Name);
            body.AppendLine("Server:   " + ProgramInfo.Host);
            body.AppendLine("Run:      " + r.Kind + " (" + r.Trigger.ToString().ToLowerInvariant() + "), " + r.Id);
            body.AppendLine("Started:  " + Format.LocalTime(r.Start) + ", duration " + Format.Duration(r.DurationSeconds));
            body.AppendLine("Result:   " + r.State + (string.IsNullOrEmpty(r.Message) ? "" : " – " + r.Message));
            if (r.Summary != null)
                body.AppendLine("Files:    " + Format.Count(r.Summary.Files) + " (" + Format.Count(r.Summary.FilesSkipped) + " not backed up), " + Format.Bytes(r.Summary.BytesAdded) + " new");
            var detail = _history.Get(r.Id);
            var problems = detail?.Issues.Where(i => i.Kind != IssueKind.Skipped).Take(30).ToList();
            if (problems != null && problems.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("Problems (first " + problems.Count + "):");
                foreach (var i in problems)
                    body.AppendLine("  " + TextReport.IssueLine(i));
            }
            body.AppendLine();
            body.AppendLine("Full log: Keepsake → History & logs, or 'keepsake log " + r.Id + "'.");
            TrySend(subject, body.ToString());
        }

        public void NoSuccessAlert(JobDefinition job, DateTime? lastSuccessUtc)
        {
            string since = lastSuccessUtc.HasValue ? "since " + Format.LocalTime(lastSuccessUtc.Value) : "since the job was created";
            string message = "Keepsake: no successful backup of '" + job.Name + "' on " + ProgramInfo.Host + " " + since +
                             " (alert after " + job.AlertNoSuccessHours + " h). Check that the service runs, the schedule is enabled and the destination is reachable.";
            EventLogWriter.Error(1004, message);
            ServiceLog.Warn(message);
            if (Configured)
                TrySend("[Keepsake] NO BACKUP: " + job.Name + " on " + ProgramInfo.Host + " – " + since, message);
        }

        private void TrySend(string subject, string body)
        {
            try
            {
                Send(subject, body);
            }
            catch (KeepsakeException ex)
            {
                ServiceLog.Error(ex.Message);
                EventLogWriter.Warning(1005, ex.Message);
            }
        }
    }
}
