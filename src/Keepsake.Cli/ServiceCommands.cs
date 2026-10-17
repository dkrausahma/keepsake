using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Util;
using Keepsake.Protocol;

namespace Keepsake.Cli
{
    /// <summary>Commands that talk to the Keepsake service (jobs, schedules, run history).</summary>
    internal static class ServiceCommands
    {
        public static IEnumerable<Command> All()
        {
            yield return new Command
            {
                Name = "setup",
                Summary = "Set the application password of the service (first start only)",
                Usage = "keepsake setup [--new-password-file <file>]",
                Details = "The application password protects the service (GUI and CLI). It is not the repository password.",
                ValueOptions = new[] { "new-password-file", "pipe" },
                Run = Setup,
            };
            yield return new Command
            {
                Name = "status",
                Summary = "Show the service status and all jobs",
                Usage = "keepsake status [--app-password-file <file>]",
                ValueOptions = new[] { "app-password-file", "pipe" },
                Run = Status,
            };
            yield return new Command
            {
                Name = "jobs",
                Summary = "List, show, import, export or remove backup jobs",
                Usage = "keepsake jobs [list | show <job> | export <job> [--out <file>] | import <file> [--repository-password-file <file>] | remove <job>]",
                Details = "<job> is the job name or ID.\n" +
                          "import creates a job from a JSON file (see 'jobs export'), or updates the job with the same name.\n" +
                          "A new job needs --repository-password-file; the repository must exist (keepsake init).",
                ValueOptions = new[] { "app-password-file", "pipe", "out", "repository-password-file" },
                Run = Jobs,
            };
            yield return new Command
            {
                Name = "run",
                Summary = "Start a job now",
                Usage = "keepsake run <job> [--full] [--wait]",
                Details = "--wait shows progress until the run ends and returns its exit code. Ctrl+C then cancels the run.",
                ValueOptions = new[] { "app-password-file", "pipe" },
                Flags = new[] { "full", "wait" },
                Run = RunJob,
            };
            yield return new Command
            {
                Name = "cancel",
                Summary = "Cancel a running or waiting job",
                Usage = "keepsake cancel <job>",
                ValueOptions = new[] { "app-password-file", "pipe" },
                Run = Cancel,
            };
            yield return new Command
            {
                Name = "runs",
                Summary = "Show the run history",
                Usage = "keepsake runs [<job>] [--limit <n>]",
                ValueOptions = new[] { "app-password-file", "pipe", "limit" },
                Run = Runs,
            };
            yield return new Command
            {
                Name = "log",
                Summary = "Show the full log of a run",
                Usage = "keepsake log <run-id>",
                ValueOptions = new[] { "app-password-file", "pipe" },
                Run = Log,
            };
        }

        // ---- Connection ------------------------------------------------------------------------

        private static KeepsakeClient Connect(CommandContext ctx)
        {
            var client = KeepsakeClient.Connect(ctx.Args.Value("pipe"));
            try
            {
                var auth = client.Call<AuthStatusResult>(Methods.AuthStatus);
                if (!auth.PasswordSet)
                    throw new KeepsakeException("The service has no application password yet. Run 'keepsake setup' first.");
                client.Login(AppPassword(ctx));
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static string AppPassword(CommandContext ctx)
        {
            string file = ctx.Args.Value("app-password-file");
            if (file != null)
                return File.ReadAllText(file).Trim('﻿').Split('\n')[0].TrimEnd('\r');
            string env = Environment.GetEnvironmentVariable("KEEPSAKE_APP_PASSWORD");
            if (!string.IsNullOrEmpty(env))
                return env;
            if (ctx.NonInteractive)
                throw new UsageException("No application password given. Use --app-password-file or KEEPSAKE_APP_PASSWORD.");
            return ConsoleUi.ReadSecret("Keepsake application password: ");
        }

        private static JobSummary FindJob(KeepsakeClient client, string reference)
        {
            var jobs = client.Call<List<JobSummary>>(Methods.JobsList);
            return jobs.FirstOrDefault(j => j.Id == reference)
                   ?? jobs.FirstOrDefault(j => string.Equals(j.Name, reference, StringComparison.OrdinalIgnoreCase))
                   ?? throw new KeepsakeException("Job '" + reference + "' not found. 'keepsake jobs' lists all jobs.");
        }

        private static string Arg(CommandContext ctx, int index, string usage)
        {
            if (ctx.Args.Positionals.Count <= index)
                throw new UsageException("Usage: " + usage);
            return ctx.Args.Positionals[index];
        }

        // ---- Commands --------------------------------------------------------------------------

        private static int Setup(CommandContext ctx)
        {
            using (var client = KeepsakeClient.Connect(ctx.Args.Value("pipe")))
            {
                if (client.Call<AuthStatusResult>(Methods.AuthStatus).PasswordSet)
                    throw new KeepsakeException("The application password is already set.");
                string password = ctx.NewPassword("new-password-file", "application password");
                client.Call<LoginResult>(Methods.AuthSetup, new PasswordParams { Password = password });
            }
            ctx.WriteJson(new { status = "success" });
            ctx.Info("Application password set. Keep it in KeePass – it protects the Keepsake service, not the backups.");
            return ExitCodes.Success;
        }

        private static int Status(CommandContext ctx)
        {
            using (var client = Connect(ctx))
            {
                var status = client.Call<ServiceStatus>(Methods.Status);
                var jobs = client.Call<List<JobSummary>>(Methods.JobsList);
                ctx.WriteJson(new { service = status, jobs });
                if (ctx.Json)
                    return ExitCodes.Success;

                ctx.Info("Keepsake service " + status.Version + " on " + status.Host + ", running since " + Format.LocalTime(status.StartedAt));
                ctx.Info("Data folder: " + status.DataFolder + "   Parallel jobs: " + status.MaxConcurrentJobs);
                ctx.Info();
                if (jobs.Count == 0)
                {
                    ctx.Info("No jobs configured. Create one with the GUI or 'keepsake jobs import <file>'.");
                    return ExitCodes.Success;
                }
                ctx.Info("Job                   State         Last run               Result     Next run");
                ctx.Info(new string('-', 96));
                foreach (var j in jobs)
                {
                    string state = !j.Enabled ? "disabled" : j.Progress != null ? "running" : j.Queued ? "waiting" : "idle";
                    string last = j.LastRun == null ? "never" : Format.LocalTime(j.LastRun.Start);
                    string result = j.LastRun == null ? "" : j.LastRun.State.ToString().ToLowerInvariant();
                    string next = j.NextRun.HasValue ? j.NextRun.Value.ToString("yyyy-MM-dd HH:mm") : "-";
                    ctx.Info(Pad(j.Name, 20) + "  " + state.PadRight(12) + "  " + last.PadRight(21) + "  " + result.PadRight(9) + "  " + next);
                    if (j.Progress != null)
                        ctx.Info("    " + j.Progress.Phase + ": " + Format.Count(j.Progress.Files) + " files, " + Format.Bytes(j.Progress.BytesRead) + " read · " + ConsoleUi.Tail(j.Progress.CurrentPath, 60));
                }
            }
            return ExitCodes.Success;
        }

        private static int Jobs(CommandContext ctx)
        {
            string sub = ctx.Args.Positionals.Count == 0 ? "list" : ctx.Args.Positionals[0].ToLowerInvariant();
            using (var client = Connect(ctx))
            {
                switch (sub)
                {
                    case "list":
                    {
                        var jobs = client.Call<List<JobSummary>>(Methods.JobsList);
                        ctx.WriteJson(jobs);
                        foreach (var j in jobs)
                            ctx.Info(j.Id.Substring(0, 8) + "  " + Pad(j.Name, 20) + "  " + (j.Enabled ? "enabled " : "disabled") + "  " + j.Destination +
                                     "\n          " + (j.ScheduleText.Count == 0 ? "manual only" : string.Join("; ", j.ScheduleText)));
                        if (jobs.Count == 0)
                            ctx.Info("No jobs configured.");
                        return ExitCodes.Success;
                    }
                    case "show":
                    {
                        var job = FindJob(client, Arg(ctx, 1, "keepsake jobs show <job>"));
                        var detail = client.Call<JobDetail>(Methods.JobsGet, new IdParams { Id = job.Id });
                        ctx.WriteJson(detail);
                        var d = detail.Job;
                        ctx.Info("Name:         " + d.Name + (d.Enabled ? "" : " (disabled)"));
                        ctx.Info("ID:           " + d.Id);
                        ctx.Info("Destination:  " + d.Destination.Path + (detail.HasRepositoryPassword ? "" : "   (NO PASSWORD STORED)"));
                        ctx.Info("Sources:      " + string.Join("\n              ", d.Sources));
                        ctx.Info("Excludes:     " + (d.Excludes.Count == 0 ? "-" : string.Join(", ", d.Excludes)) + (d.UseDefaultExcludes ? " (+ defaults)" : ""));
                        ctx.Info("Schedules:    " + (detail.ScheduleText.Count == 0 ? "manual only" : string.Join("\n              ", detail.ScheduleText)));
                        ctx.Info("Next run:     " + (detail.NextRun.HasValue ? detail.NextRun.Value.ToString("yyyy-MM-dd HH:mm") : "-"));
                        ctx.Info("Missed runs:  " + (d.RunMissed ? "run once after start-up" : "skipped"));
                        ctx.Info("Retries:      " + d.RetryCount + " × after " + d.RetryDelayMinutes + " min");
                        if (!string.IsNullOrEmpty(d.PreCommand))
                            ctx.Info("Pre-command:  " + d.PreCommand);
                        if (!string.IsNullOrEmpty(d.PostCommand))
                            ctx.Info("Post-command: " + d.PostCommand);
                        return ExitCodes.Success;
                    }
                    case "export":
                    {
                        var job = FindJob(client, Arg(ctx, 1, "keepsake jobs export <job> [--out <file>]"));
                        var detail = client.Call<JobDetail>(Methods.JobsGet, new IdParams { Id = job.Id });
                        string json = Json.ToText(detail.Job);
                        string outFile = ctx.Args.Value("out");
                        if (outFile != null)
                        {
                            File.WriteAllText(outFile, json);
                            ctx.Info("Exported '" + job.Name + "' to " + Path.GetFullPath(outFile) + " (without passwords).");
                        }
                        else
                        {
                            Console.Out.WriteLine(json);
                        }
                        return ExitCodes.Success;
                    }
                    case "import":
                    {
                        string file = Arg(ctx, 1, "keepsake jobs import <file> [--repository-password-file <file>]");
                        var job = Json.FromText<JobDefinition>(File.ReadAllText(file));
                        var existing = client.Call<List<JobSummary>>(Methods.JobsList)
                            .FirstOrDefault(j => j.Id == job.Id || string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));
                        job.Id = existing?.Id;
                        string pwFile = ctx.Args.Value("repository-password-file");
                        string password = pwFile != null ? File.ReadAllText(pwFile).Trim('﻿').Split('\n')[0].TrimEnd('\r') : null;
                        if (existing == null && password == null)
                        {
                            if (ctx.NonInteractive)
                                throw new UsageException("A new job needs --repository-password-file.");
                            password = ConsoleUi.ReadSecret("Repository password for '" + job.Destination?.Path + "': ");
                        }
                        var saved = client.Call<JobDetail>(Methods.JobsSave, new JobSaveParams { Job = job, RepositoryPassword = password });
                        ctx.WriteJson(saved);
                        ctx.Info((existing == null ? "Created" : "Updated") + " job '" + saved.Job.Name + "' (" + saved.Job.Id.Substring(0, 8) + ").");
                        if (saved.NextRun.HasValue)
                            ctx.Info("Next run: " + saved.NextRun.Value.ToString("yyyy-MM-dd HH:mm"));
                        return ExitCodes.Success;
                    }
                    case "remove":
                    {
                        var job = FindJob(client, Arg(ctx, 1, "keepsake jobs remove <job>"));
                        client.Call(Methods.JobsDelete, new IdParams { Id = job.Id });
                        ctx.WriteJson(new { status = "success", id = job.Id });
                        ctx.Info("Job '" + job.Name + "' removed. Its repository and backups were not touched.");
                        return ExitCodes.Success;
                    }
                    default:
                        throw new UsageException("Unknown jobs command '" + sub + "'.");
                }
            }
        }

        private static int RunJob(CommandContext ctx)
        {
            using (var client = Connect(ctx))
            {
                var job = FindJob(client, Arg(ctx, 0, "keepsake run <job> [--full] [--wait]"));
                var started = client.Call<RunStarted>(Methods.JobsRun, new RunJobParams { Id = job.Id, Mode = ctx.Args.Flag("full") ? BackupMode.Full : BackupMode.Incremental });
                if (!ctx.Args.Flag("wait"))
                {
                    ctx.WriteJson(new { status = "started", runId = started.RunId });
                    ctx.Info("Started '" + job.Name + "' (run " + started.RunId + "). Follow it with 'keepsake status'.");
                    return ExitCodes.Success;
                }

                ctx.Info("Running '" + job.Name + "' (run " + started.RunId + ")...");
                bool cancelSent = false;
                RunRecord record;
                while (true)
                {
                    if (ctx.Cancel.IsCancellationRequested && !cancelSent)
                    {
                        client.Call(Methods.JobsCancel, new IdParams { Id = job.Id });
                        cancelSent = true;
                    }
                    record = client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = job.Id, Limit = 5 }).FirstOrDefault(r => r.Id == started.RunId);
                    if (record != null && record.State != RunState.Queued && record.State != RunState.Running)
                        break;
                    if (!ctx.Quiet && ConsoleUi.CanShowProgress)
                    {
                        var p = client.Call<ServiceStatus>(Methods.Status).Running.FirstOrDefault(r => r.RunId == started.RunId);
                        ConsoleUi.Progress(p == null
                            ? "Waiting for a free slot..."
                            : p.Phase + ": " + Format.Count(p.Files) + " files · " + Format.Bytes(p.BytesRead) + " read · " + Format.Bytes(p.BytesStored) + " stored · " + ConsoleUi.Tail(p.CurrentPath, 50));
                    }
                    Thread.Sleep(1000);
                }
                ConsoleUi.ClearProgress();
                ctx.WriteJson(record);
                PrintRecord(ctx, record);
                return ExitCode(record.State);
            }
        }

        private static int Cancel(CommandContext ctx)
        {
            using (var client = Connect(ctx))
            {
                var job = FindJob(client, Arg(ctx, 0, "keepsake cancel <job>"));
                client.Call(Methods.JobsCancel, new IdParams { Id = job.Id });
                ctx.WriteJson(new { status = "success" });
                ctx.Info("Cancel requested for '" + job.Name + "'.");
            }
            return ExitCodes.Success;
        }

        private static int Runs(CommandContext ctx)
        {
            using (var client = Connect(ctx))
            {
                string jobId = ctx.Args.Positionals.Count > 0 ? FindJob(client, ctx.Args.Positionals[0]).Id : null;
                var runs = client.Call<List<RunRecord>>(Methods.RunsList, new RunsListParams { JobId = jobId, Limit = ctx.Args.IntValue("limit", 20, 1, 1000) });
                ctx.WriteJson(runs);
                if (ctx.Json)
                    return ExitCodes.Success;
                if (runs.Count == 0)
                {
                    ctx.Info("No runs yet.");
                    return ExitCodes.Success;
                }
                ctx.Info("Run ID                    Job                   Started              Duration  Result     Trigger   Files / data");
                ctx.Info(new string('-', 118));
                foreach (var r in runs)
                {
                    string data = r.Summary != null
                        ? Format.Count(r.Summary.Files) + " files, " + Format.Bytes(r.Summary.BytesAdded) + " new"
                        : r.RestoreStats != null ? Format.Count(r.RestoreStats.FilesRestored) + " restored" : "";
                    ctx.Info(r.Id.PadRight(24) + "  " + Pad(r.JobName, 20) + "  " + Format.LocalTime(r.Start) + "  " +
                             Format.Duration(r.DurationSeconds).PadLeft(8) + "  " + r.State.ToString().ToLowerInvariant().PadRight(9) + "  " +
                             r.Trigger.ToString().ToLowerInvariant().PadRight(8) + "  " + data);
                    if (!string.IsNullOrEmpty(r.Message) && r.State != RunState.Success)
                        ctx.Info("    " + r.Message);
                }
            }
            return ExitCodes.Success;
        }

        private static int Log(CommandContext ctx)
        {
            using (var client = Connect(ctx))
            {
                var detail = client.Call<RunDetail>(Methods.RunsGet, new IdParams { Id = Arg(ctx, 0, "keepsake log <run-id>") });
                ctx.WriteJson(detail);
                if (!ctx.Json)
                    Console.Out.WriteLine(string.IsNullOrEmpty(detail.Log) ? "(no log for this run)" : detail.Log);
            }
            return ExitCodes.Success;
        }

        private static void PrintRecord(CommandContext ctx, RunRecord r)
        {
            if (r.Summary != null)
            {
                var s = r.Summary;
                ctx.Info("Files:     " + Format.Count(s.FilesNew) + " new, " + Format.Count(s.FilesChanged) + " changed, " + Format.Count(s.FilesUnchanged) + " unchanged, " + Format.Count(s.FilesSkipped) + " not backed up");
                ctx.Info("Data:      " + Format.Bytes(s.TotalBytes) + " total, " + Format.Bytes(s.BytesAdded) + " new, " + Format.Bytes(s.BytesStored) + " stored");
            }
            ctx.Info("Duration:  " + Format.Duration(r.DurationSeconds));
            ctx.Info();
            ctx.Info("Result: " + r.State.ToString().ToUpperInvariant() + (string.IsNullOrEmpty(r.Message) ? "" : " – " + r.Message));
            ctx.Info("Full log: keepsake log " + r.Id);
        }

        private static int ExitCode(RunState state)
        {
            switch (state)
            {
                case RunState.Success: return ExitCodes.Success;
                case RunState.Warning: return ExitCodes.Warning;
                case RunState.Cancelled: return ExitCodes.Cancelled;
                default: return ExitCodes.Failed;
            }
        }

        private static string Pad(string s, int width)
        {
            s = s ?? "";
            return (s.Length > width ? s.Substring(0, width - 1) + "…" : s).PadRight(width);
        }
    }
}
