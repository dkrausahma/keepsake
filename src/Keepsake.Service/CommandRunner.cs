using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Keepsake.Service
{
    /// <summary>Runs a job's pre/post command through <c>cmd.exe /c</c> with a timeout and captures its output.</summary>
    internal static class CommandRunner
    {
        public static int Run(string command, TimeSpan timeout, IDictionary<string, string> environment, StringBuilder log, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            };
            foreach (var kv in environment)
                psi.EnvironmentVariables[kv.Key] = kv.Value;

            var output = new StringBuilder();
            log.AppendLine("> " + command);
            using (var process = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler capture = (s, e) =>
                {
                    if (e.Data == null)
                        return;
                    lock (output)
                        output.AppendLine("  " + e.Data);
                };
                process.OutputDataReceived += capture;
                process.ErrorDataReceived += capture;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var watch = Stopwatch.StartNew();
                while (!process.WaitForExit(500))
                {
                    if (cancel.IsCancellationRequested || watch.Elapsed > timeout)
                    {
                        KillTree(process.Id);
                        process.WaitForExit(10000);
                        lock (output)
                            log.Append(output);
                        string reason = cancel.IsCancellationRequested ? "cancelled" : "timed out after " + timeout.TotalMinutes + " minutes";
                        log.AppendLine("  Command " + reason + ".");
                        throw new Core.KeepsakeException("The command '" + command + "' " + reason + ".");
                    }
                }
                process.WaitForExit(); // flushes the async output readers
                lock (output)
                    log.Append(output);
                log.AppendLine("  Exit code: " + process.ExitCode);
                return process.ExitCode;
            }
        }

        private static void KillTree(int pid)
        {
            try
            {
                using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + pid) { CreateNoWindow = true, UseShellExecute = false }))
                    kill?.WaitForExit(10000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                ServiceLog.Warn("Could not stop command process " + pid + ": " + ex.Message);
            }
        }
    }
}
