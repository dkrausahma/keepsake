using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Keepsake.Core;
using Keepsake.Core.Backup;
using Keepsake.Core.Check;
using Keepsake.Core.Maintenance;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;

namespace Keepsake.Cli
{
    internal static class Commands
    {
        private static readonly List<Command> Offline = new List<Command>
        {
            new Command
            {
                Name = "init",
                Summary = "Create a new encrypted repository",
                Usage = "keepsake init --repo <path> [--password-file <file>] [--recovery-key-out <file>]",
                Details = "Creates the repository with a password and a recovery key. The recovery key is shown once:\n" +
                          "store it in KeePass. Without the password or the recovery key the backups cannot be restored.",
                ValueOptions = new[] { "recovery-key-out" },
                Run = Init,
            },
            new Command
            {
                Name = "backup",
                Summary = "Back up folders and files into a new snapshot",
                Usage = "keepsake backup --repo <path> <folder-or-file>... [options]",
                Details = "Options:\n" +
                          "  --exclude <pattern>       Exclude by name (*.tmp), folder (cache\\) or path (D:\\Data\\**\\obj\\). Repeatable.\n" +
                          "  --exclude-file <file>     Read exclusion patterns from a file (one per line, # for comments)\n" +
                          "  --no-default-excludes     Also back up recycle bin, System Volume Information, pagefile, ~$* files\n" +
                          "  --full                    Re-read every file instead of trusting size and modification time\n" +
                          "  --job <name>              Job name; incremental backups continue from the last snapshot of this job\n" +
                          "  --tag <tag>               Tag the snapshot. Repeatable.\n" +
                          "  --parent <snapshot>       Use a specific snapshot as the base for change detection\n" +
                          "  --workers <n>             Files processed in parallel (default: up to 4)\n" +
                          "  --report-excluded         List every excluded item in the log",
                ValueOptions = new[] { "exclude", "exclude-file", "job", "tag", "parent", "workers" },
                Flags = new[] { "no-default-excludes", "full", "report-excluded" },
                Run = Backup,
            },
            new Command
            {
                Name = "snapshots",
                Summary = "List snapshots",
                Usage = "keepsake snapshots --repo <path> [--job <name>]",
                ValueOptions = new[] { "job" },
                Run = Snapshots,
            },
            new Command
            {
                Name = "ls",
                Summary = "List the contents of a snapshot",
                Usage = "keepsake ls --repo <path> <snapshot|latest> [<path>] [--recursive]",
                Details = "Paths can be given as in the snapshot (D/Data/Finance) or as Windows paths (D:\\Data\\Finance).",
                ValueOptions = new[] { "job" },
                Flags = new[] { "recursive" },
                Run = List,
            },
            new Command
            {
                Name = "restore",
                Summary = "Restore files from a snapshot",
                Usage = "keepsake restore --repo <path> [<snapshot|latest>] (--target <folder> | --original) [options]",
                Details = "Options:\n" +
                          "  --target <folder>         Restore into this folder (default keeps the full path: <folder>\\D\\Data\\...)\n" +
                          "  --original                Restore to the original location\n" +
                          "  --include <path>          Only restore this folder or file. Repeatable. Default: everything\n" +
                          "  --flat                    With --target: put the selected folder directly into the target folder\n" +
                          "  --conflict <policy>       Existing files: skip (default), overwrite, newer, rename\n" +
                          "  --no-attributes           Do not restore read-only/hidden/system attributes\n" +
                          "  --dry-run                 Show what would be restored without writing anything\n" +
                          "  --job <name>              With 'latest': newest snapshot of this job\n\n" +
                          "NTFS permissions are not restored: files inherit the permissions of the target folder.",
                ValueOptions = new[] { "target", "include", "conflict", "job" },
                Flags = new[] { "original", "flat", "no-attributes", "dry-run" },
                Run = Restore,
            },
            new Command
            {
                Name = "check",
                Summary = "Verify repository integrity",
                Usage = "keepsake check --repo <path> [--read-data] [--read-data-percent <1-100>]",
                Details = "Without options the structure is checked: every snapshot, folder listing and data reference.\n" +
                          "--read-data also downloads and decrypts the stored data (all of it, or a random sample).",
                ValueOptions = new[] { "read-data-percent" },
                Flags = new[] { "read-data" },
                Run = Check,
            },
            new Command
            {
                Name = "prune",
                Summary = "Remove old snapshots by retention rules and free unused space",
                Usage = "keepsake prune --repo <path> [--keep-last <n>] [--keep-daily <n>] [--keep-weekly <n>] [--keep-monthly <n>] [--keep-yearly <n>] [--keep-within <days>] [--job <name>] [--dry-run]",
                Details = "Rules combine: a snapshot is kept if any rule keeps it. Protected snapshots are always kept.\n" +
                          "Without --keep-* options only unused data is removed (no snapshot is deleted).\n" +
                          "--job applies the rules only to snapshots of that job. --dry-run shows what would happen.",
                ValueOptions = new[] { "keep-last", "keep-daily", "keep-weekly", "keep-monthly", "keep-yearly", "keep-within", "job" },
                Flags = new[] { "dry-run" },
                Run = Prune,
            },
            new Command
            {
                Name = "protect",
                Summary = "Protect a snapshot from retention (or remove the protection)",
                Usage = "keepsake protect <snapshot> --repo <path> [--remove]",
                Flags = new[] { "remove" },
                Run = Protect,
            },
            new Command
            {
                Name = "repair",
                Summary = "Rebuild the repository index from the pack files",
                Usage = "keepsake repair --repo <path>",
                Details = "Use when 'check' reports missing or unreadable index files. Reads only the pack headers,\n" +
                          "writes a new index and removes the old index files. Snapshots and data are not changed.",
                Run = Repair,
            },
            new Command
            {
                Name = "doctor",
                Summary = "Check this machine for everything Keepsake needs (Server 2008 R2 aware)",
                Usage = "keepsake doctor [--json]",
                ValueOptions = new[] { "pipe" },
                Run = Doctor.Run,
            },
            new Command
            {
                Name = "sftp-test",
                Summary = "Test which SSH algorithms work with an SFTP server from this computer",
                Usage = "keepsake sftp-test --repo sftp://user@host[:port]/path [--sftp-password-file <file> | --sftp-key-file <file>] [--sftp-host-key SHA256:...]",
                Details = "Connects once per key exchange, cipher, MAC and host-key algorithm and reports which ones work.\n" +
                              "Use it when a connection fails with 'MAC error' on one computer but works on others; nothing is read or written.",
                Run = SftpTest,
            },
            new Command
            {
                Name = "key",
                Summary = "Manage passwords and recovery keys",
                Usage = "keepsake key (list | add-password | add-recovery | remove <id>) --repo <path> [--label <text>]",
                Details = "  list                      Show all keys\n" +
                          "  add-password              Add another password (--new-password-file <file>, or prompt)\n" +
                          "  add-recovery              Create an additional recovery key (--recovery-key-out <file>)\n" +
                          "  remove <id>               Remove a key (the last one cannot be removed).\n" +
                          "                            Removing does not revoke it: anyone who kept a copy of the old key file\n" +
                          "                            and knows that password can still open the repository. After a leak,\n" +
                          "                            start a new repository and let the old one expire.",
                ValueOptions = new[] { "label", "new-password-file", "recovery-key-out" },
                Run = Key,
            },
            new Command
            {
                Name = "unlock",
                Summary = "Remove stale repository locks",
                Usage = "keepsake unlock --repo <path> [--all]",
                Details = "Removes locks older than 30 minutes. --all also removes current locks: only use it when no other\n" +
                          "Keepsake process works on the repository.",
                Flags = new[] { "all" },
                Run = Unlock,
            },
            new Command
            {
                Name = "version",
                Summary = "Show the program version",
                Usage = "keepsake version",
                Run = Version,
            },
            new Command
            {
                Name = "help",
                Summary = "Show help for a command",
                Usage = "keepsake help [<command>]",
                Run = HelpCommand,
            },
        };

        /// <summary>Repository commands, then service commands, then version/help.</summary>
        public static readonly List<Command> All = Offline.Take(Offline.Count - 2)
            .Concat(ServiceCommands.All())
            .Concat(Offline.Skip(Offline.Count - 2))
            .ToList();

        // ---- init ------------------------------------------------------------------------------

        private static int Init(CommandContext ctx)
        {
            var storage = ctx.OpenStorage();
            string password = ctx.NewPassword("password-file", "repository password");
            var result = Repository.Init(storage, password);
            string keyOut = ctx.Args.Value("recovery-key-out");
            if (keyOut != null)
                WriteRecoveryKeyFile(keyOut, result.RecoveryKey, result.Repository);

            ctx.WriteJson(new { status = "success", repository = storage.Location, id = result.Repository.Header.Id, recoveryKey = result.RecoveryKey });
            ctx.Info("Repository created at " + storage.Location);
            ctx.Info("  ID: " + result.Repository.Header.Id);
            ctx.Info();
            ctx.Info("RECOVERY KEY – store it in KeePass now. It is shown only once:");
            ctx.Info();
            ctx.Info("    " + result.RecoveryKey);
            ctx.Info();
            ctx.Info("Without the password or this recovery key the backups cannot be restored.");
            if (keyOut != null)
                ctx.Info("The recovery key was also written to " + Path.GetFullPath(keyOut) + " – move it off this server.");
            return ExitCodes.Success;
        }

        private static void WriteRecoveryKeyFile(string path, string key, Repository repo)
        {
            string text = "Keepsake recovery key\r\n" +
                          "Repository: " + repo.Storage.Location + "\r\n" +
                          "Repository ID: " + repo.Header.Id + "\r\n" +
                          "Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n\r\n" +
                          key + "\r\n";
            PrivateFile.WriteAllText(path, text);
        }

        // ---- backup ----------------------------------------------------------------------------

        private static int Backup(CommandContext ctx)
        {
            if (ctx.Args.Positionals.Count == 0)
                throw new UsageException("Give at least one folder or file to back up.");

            var excludes = new List<string>(ctx.Args.Values("exclude"));
            foreach (string file in ctx.Args.Values("exclude-file"))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                        excludes.Add(t);
                }
            }

            var options = new BackupOptions
            {
                Sources = ctx.Args.Positionals.ToList(),
                Excludes = excludes,
                UseDefaultExcludes = !ctx.Args.Flag("no-default-excludes"),
                Mode = ctx.Args.Flag("full") ? BackupMode.Full : BackupMode.Incremental,
                Job = ctx.Args.Value("job"),
                Tags = ctx.Args.Values("tag"),
                Parent = ctx.Args.Value("parent"),
                Workers = ctx.Args.IntValue("workers", Math.Max(1, Math.Min(4, Environment.ProcessorCount)), 1, 32),
                ReportExcluded = ctx.Args.Flag("report-excluded"),
            };

            BackupResult result;
            using (var repo = ctx.OpenRepository())
            {
                ctx.Info("Backing up to " + repo.Storage.Location + (options.Mode == BackupMode.Full ? " (full)" : ""));
                LatestProgress<BackupProgress> progress = null;
                if (!ctx.Quiet && ConsoleUi.CanShowProgress)
                {
                    progress = new LatestProgress<BackupProgress>(p => ConsoleUi.Progress(
                        Format.Count(p.Files) + " files · " + Format.Bytes(p.BytesRead) + " read · " + Format.Bytes(p.BytesStored) +
                        " stored · " + ConsoleUi.Tail(p.CurrentPath, 60)));
                }
                using (progress)
                    result = new BackupEngine(repo).Run(options, progress, ctx.Cancel);
            }

            var summary = TextReport.SummaryLines(result);
            ctx.WriteLog("Keepsake backup", result, summary);
            ctx.WriteJson(new
            {
                status = result.Status,
                snapshotId = result.SnapshotId,
                parentId = result.ParentId,
                mode = result.Mode,
                summary = result.Summary,
                filesExcluded = result.FilesExcluded,
                start = result.Start,
                end = result.End,
                durationSeconds = result.DurationSeconds,
                error = result.Error,
                phases = result.Phases,
                issues = result.Issues,
            });
            if (!ctx.Json)
            {
                foreach (string line in summary)
                    ctx.Info(line);
                ctx.PrintIssues(result);
                PrintStatus(ctx, result);
            }
            return CommandContext.ExitCode(result.Status);
        }

        // ---- snapshots / ls --------------------------------------------------------------------

        private static int SftpTest(CommandContext ctx)
        {
            string location = ctx.RepositoryLocation;
            if (!StorageFactory.IsSftp(location))
                throw new UsageException("sftp-test needs an SFTP location: --repo sftp://user@host[:port]/path");
            var options = ctx.SftpOptionsFromArgs(location);
            if (string.IsNullOrEmpty(options.HostKeyFingerprint))
            {
                string fingerprint = SftpDiagnostics.ProbeHostKey(options);
                Console.Error.WriteLine("Host key of " + options.Host + ": " + fingerprint);
                Console.Error.WriteLine("Compare it with the fingerprint shown by your provider (e.g. Hetzner Robot) or ssh-keygen -lf.");
                if (ctx.NonInteractive || !ConsoleUi.ReadLine("Trust this key for the test? [y/N] ").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
                    throw new KeepsakeException("Host key not trusted. Pass --sftp-host-key " + fingerprint + " after verifying it.");
                options.HostKeyFingerprint = fingerprint;
            }
            if (!SftpDebugLog.IsEnabled)
                SftpDebugLog.Enable(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keepsake-debug.log"));
            Console.WriteLine(".NET Framework release: " + NetFrameworkRelease() + ", Windows: " + WindowsName() + ", FIPS-only policy: " + SftpDebugLog.FipsOnly());
            foreach (string line in SftpDiagnostics.Run(options))
                Console.WriteLine(line);
            Console.WriteLine();
            Console.WriteLine("Detailed log (send it together with this output): " + SftpDebugLog.FilePath);
            return ExitCodes.Success;
        }

        private static string NetFrameworkRelease()
        {
            using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                       .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                return key?.GetValue("Release")?.ToString() ?? "unknown";
        }

        private static string WindowsName()
        {
            using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                       .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                return (key?.GetValue("ProductName") + " build " + key?.GetValue("CurrentBuild")).Trim();
        }

        private static int Snapshots(CommandContext ctx)
        {
            using (var repo = ctx.OpenRepository())
            {
                var list = repo.ListSnapshots();
                string job = ctx.Args.Value("job");
                if (job != null)
                    list = list.Where(s => string.Equals(s.Snapshot.Job, job, StringComparison.OrdinalIgnoreCase)).ToList();

                ctx.WriteJson(list.Select(s => new { id = s.Id, s.Snapshot.Time, s.Snapshot.Host, s.Snapshot.Job, s.Snapshot.Mode, s.Snapshot.Paths, s.Snapshot.Tags, s.Snapshot.Summary }));
                if (ctx.Json)
                    return ExitCodes.Success;
                if (list.Count == 0)
                {
                    ctx.Info("No snapshots.");
                    return ExitCodes.Success;
                }
                ctx.Info("ID        Time                 Job              Mode         Files        Size  Paths");
                ctx.Info(new string('-', 100));
                foreach (var s in list)
                {
                    var snap = s.Snapshot;
                    string paths = snap.Paths.Count == 0 ? "" : snap.Paths[0] + (snap.Paths.Count > 1 ? " (+" + (snap.Paths.Count - 1) + ")" : "");
                    ctx.Info(s.ShortId + (Repository.IsProtected(snap) ? "* " : "  ") + Format.LocalTime(snap.Time) + "  " + Trim(snap.Job ?? "-", 15).PadRight(15) + "  " +
                             snap.Mode.ToString().ToLowerInvariant().PadRight(11) + "  " +
                             Format.Count(snap.Summary?.Files ?? 0).PadLeft(9) + "  " + Format.Bytes(snap.Summary?.TotalBytes ?? 0).PadLeft(9) + "  " + paths);
                }
                ctx.Info();
                ctx.Info(list.Count + " snapshot(s)" + (list.Any(s => Repository.IsProtected(s.Snapshot)) ? ", * = protected" : ""));
            }
            return ExitCodes.Success;
        }

        private static int List(CommandContext ctx)
        {
            if (ctx.Args.Positionals.Count < 1 || ctx.Args.Positionals.Count > 2)
                throw new UsageException("Usage: keepsake ls --repo <path> <snapshot|latest> [<path>]");
            using (var repo = ctx.OpenRepository())
            {
                repo.LoadIndex();
                var snapshot = repo.FindSnapshot(ctx.Args.Positionals[0], ctx.Args.Value("job"));
                var tree = repo.LoadTree(snapshot.Snapshot.Tree);
                var path = ctx.Args.Positionals.Count == 2 ? RepoPaths.Parse(ctx.Args.Positionals[1]) : new List<string>();
                Node fileNode = null;
                for (int i = 0; i < path.Count; i++)
                {
                    var node = tree.Find(path[i]) ?? throw new KeepsakeException("'" + RepoPaths.Join(path.Take(i + 1)) + "' is not in snapshot " + snapshot.ShortId + ".");
                    if (node.Type == NodeType.Dir)
                        tree = repo.LoadTree(node.Subtree);
                    else if (i == path.Count - 1)
                        fileNode = node;
                    else
                        throw new KeepsakeException("'" + node.Name + "' is a file, not a folder.");
                }

                var rows = new List<object>();
                if (fileNode != null)
                    Print(ctx, rows, fileNode, RepoPaths.Join(path));
                else
                    ListTree(ctx, repo, tree, path, ctx.Args.Flag("recursive"), rows);
                ctx.WriteJson(rows);
            }
            return ExitCodes.Success;
        }

        private static void ListTree(CommandContext ctx, Repository repo, Tree tree, List<string> path, bool recursive, List<object> rows)
        {
            foreach (var node in tree.Nodes)
            {
                var child = new List<string>(path) { node.Name };
                Print(ctx, rows, node, RepoPaths.Join(child));
                if (recursive && node.Type == NodeType.Dir)
                    ListTree(ctx, repo, repo.LoadTree(node.Subtree), child, true, rows);
            }
        }

        private static void Print(CommandContext ctx, List<object> rows, Node node, string path)
        {
            bool dir = node.Type == NodeType.Dir;
            rows.Add(new { path, type = node.Type, size = dir ? (long?)null : node.Size, modified = node.ModifiedUtc });
            ctx.Info((dir ? "d " : "f ") + Format.LocalTime(node.ModifiedUtc) + "  " + (dir ? "" : Format.Bytes(node.Size)).PadLeft(9) + "  " + path + (dir ? "/" : ""));
        }

        // ---- restore ---------------------------------------------------------------------------

        private static int Restore(CommandContext ctx)
        {
            if (ctx.Args.Positionals.Count > 1)
                throw new UsageException("Only one snapshot can be restored at a time.");
            string target = ctx.Args.Value("target");
            bool original = ctx.Args.Flag("original");
            if ((target == null) == !original)
                throw new UsageException("Give either --target <folder> or --original.");

            var options = new RestoreOptions
            {
                Snapshot = ctx.Args.Positionals.Count == 1 ? ctx.Args.Positionals[0] : "latest",
                Job = ctx.Args.Value("job"),
                Includes = ctx.Args.Values("include"),
                Target = original ? RestoreTargetMode.Original : RestoreTargetMode.Folder,
                TargetFolder = target,
                KeepFullPath = !ctx.Args.Flag("flat"),
                Conflict = ParseConflict(ctx.Args.Value("conflict")),
                RestoreAttributes = !ctx.Args.Flag("no-attributes"),
                DryRun = ctx.Args.Flag("dry-run"),
            };
            if (original && options.Conflict == ConflictPolicy.Skip && ctx.Args.Value("conflict") == null)
                ctx.Info("Note: existing files that differ are kept. Use --conflict overwrite|newer|rename to change this.");

            RestoreResult result;
            using (var repo = ctx.OpenRepository())
            {
                LatestProgress<RestoreProgress> progress = null;
                if (!ctx.Quiet && ConsoleUi.CanShowProgress)
                {
                    progress = new LatestProgress<RestoreProgress>(p => ConsoleUi.Progress(
                        Format.Count(p.Files) + " files · " + Format.Bytes(p.BytesWritten) + " · " + ConsoleUi.Tail(p.CurrentPath, 60)));
                }
                using (progress)
                    result = new RestoreEngine(repo).Run(options, progress, ctx.Cancel);
            }

            var summary = TextReport.SummaryLines(result);
            ctx.WriteLog("Keepsake restore", result, summary);
            ctx.WriteJson(new
            {
                status = result.Status,
                snapshotId = result.SnapshotId,
                dryRun = result.DryRun,
                stats = result.Stats,
                start = result.Start,
                end = result.End,
                durationSeconds = result.DurationSeconds,
                error = result.Error,
                phases = result.Phases,
                issues = result.Issues,
            });
            if (!ctx.Json)
            {
                foreach (string line in summary)
                    ctx.Info(line);
                ctx.PrintIssues(result);
                PrintStatus(ctx, result);
            }
            return CommandContext.ExitCode(result.Status);
        }

        private static ConflictPolicy ParseConflict(string value)
        {
            switch ((value ?? "skip").ToLowerInvariant())
            {
                case "skip": return ConflictPolicy.Skip;
                case "overwrite": return ConflictPolicy.Overwrite;
                case "newer": return ConflictPolicy.OverwriteIfNewer;
                case "rename": return ConflictPolicy.Rename;
                default: throw new UsageException("--conflict must be skip, overwrite, newer or rename.");
            }
        }

        // ---- check -----------------------------------------------------------------------------

        private static int Check(CommandContext ctx)
        {
            var options = new CheckOptions
            {
                ReadData = ctx.Args.Flag("read-data") || ctx.Args.Value("read-data-percent") != null,
                ReadDataPercent = ctx.Args.IntValue("read-data-percent", 100, 1, 100),
            };
            CheckResult result;
            using (var repo = ctx.OpenRepository())
            {
                LatestProgress<string> progress = null;
                if (!ctx.Quiet && ConsoleUi.CanShowProgress)
                    progress = new LatestProgress<string>(ConsoleUi.Progress);
                using (progress)
                    result = new Checker(repo).Run(options, progress, ctx.Cancel);
            }
            var summary = TextReport.SummaryLines(result);
            ctx.WriteLog("Keepsake check", result, summary);
            ctx.WriteJson(new
            {
                status = result.Status,
                result.Snapshots,
                result.IndexFiles,
                result.Packs,
                result.Trees,
                result.PacksVerified,
                result.BytesVerified,
                durationSeconds = result.DurationSeconds,
                error = result.Error,
                phases = result.Phases,
                issues = result.Issues,
            });
            if (!ctx.Json)
            {
                foreach (string line in summary)
                    ctx.Info(line);
                ctx.PrintIssues(result, 50);
                PrintStatus(ctx, result);
            }
            return CommandContext.ExitCode(result.Status);
        }

        // ---- prune / protect --------------------------------------------------------------------

        private static int Prune(CommandContext ctx)
        {
            var policy = new RetentionPolicy
            {
                KeepLast = ctx.Args.IntValue("keep-last", 0, 0, 100000),
                KeepDaily = ctx.Args.IntValue("keep-daily", 0, 0, 100000),
                KeepWeekly = ctx.Args.IntValue("keep-weekly", 0, 0, 100000),
                KeepMonthly = ctx.Args.IntValue("keep-monthly", 0, 0, 100000),
                KeepYearly = ctx.Args.IntValue("keep-yearly", 0, 0, 100000),
                KeepWithinDays = ctx.Args.IntValue("keep-within", 0, 0, 100000),
            };
            var options = new PruneOptions { Policy = policy, Job = ctx.Args.Value("job"), DryRun = ctx.Args.Flag("dry-run") };
            PruneResult result;
            using (var repo = ctx.OpenRepository())
            {
                ctx.Info("Retention: " + policy.Describe() + (options.Job != null ? " (job '" + options.Job + "')" : ""));
                LatestProgress<string> progress = null;
                if (!ctx.Quiet && ConsoleUi.CanShowProgress)
                    progress = new LatestProgress<string>(ConsoleUi.Progress);
                using (progress)
                    result = new Pruner(repo).Run(options, progress, ctx.Cancel);
            }
            var summary = TextReport.SummaryLines(result);
            ctx.WriteLog("Keepsake prune", result, summary);
            ctx.WriteJson(new
            {
                status = result.Status,
                dryRun = result.DryRun,
                snapshotsRemoved = result.SnapshotsRemoved,
                snapshotsKept = result.SnapshotsKept,
                packsDeleted = result.PacksDeleted,
                packsRepacked = result.PacksRepacked,
                bytesFreed = result.BytesFreed,
                error = result.Error,
                phases = result.Phases,
            });
            if (!ctx.Json)
            {
                foreach (string line in summary)
                    ctx.Info(line);
                foreach (string id in result.SnapshotsRemoved.Take(50))
                    ctx.Info("  " + (result.DryRun ? "would remove " : "removed ") + id.Substring(0, 8));
                PrintStatus(ctx, result);
            }
            return CommandContext.ExitCode(result.Status);
        }

        private static int Repair(CommandContext ctx)
        {
            RepairResult result;
            using (var repo = ctx.OpenRepository())
            {
                LatestProgress<string> progress = null;
                if (!ctx.Quiet && ConsoleUi.CanShowProgress)
                    progress = new LatestProgress<string>(ConsoleUi.Progress);
                using (progress)
                    result = new IndexRebuilder(repo).Run(progress, ctx.Cancel);
            }
            var summary = new List<string>
            {
                "Packs:     " + Format.Count(result.PacksRead) + " read, " + result.PacksDamaged + " damaged",
                "Index:     " + Format.Count(result.BlobsIndexed) + " blocks indexed, " + result.OldIndexFiles + " old index file(s) replaced",
                TextReport.DurationLine(result),
            };
            ctx.WriteLog("Keepsake repair", result, summary);
            ctx.WriteJson(new { status = result.Status, result.PacksRead, result.PacksDamaged, result.BlobsIndexed, error = result.Error, issues = result.Issues });
            if (!ctx.Json)
            {
                foreach (string line in summary)
                    ctx.Info(line);
                ctx.PrintIssues(result);
                PrintStatus(ctx, result);
                if (result.Status != RunStatus.Failed)
                    ctx.Info("Run 'keepsake check' to see whether any snapshot still misses data.");
            }
            return CommandContext.ExitCode(result.Status);
        }

        private static int Protect(CommandContext ctx)
        {
            if (ctx.Args.Positionals.Count != 1)
                throw new UsageException("Usage: keepsake protect <snapshot> --repo <path> [--remove]");
            using (var repo = ctx.OpenRepository())
            {
                var snapshot = repo.FindSnapshot(ctx.Args.Positionals[0]);
                bool protect = !ctx.Args.Flag("remove");
                using (RepoLock.Acquire(repo, exclusive: false, purpose: "protect"))
                {
                    var updated = repo.SetProtected(snapshot, protect);
                    ctx.WriteJson(new { status = "success", oldId = snapshot.Id, id = updated.Id, @protected = protect });
                    ctx.Info("Snapshot " + snapshot.ShortId + (protect ? " is now protected" : " is no longer protected") + " (new ID " + updated.ShortId + ").");
                }
            }
            return ExitCodes.Success;
        }

        // ---- key / unlock / version / help -----------------------------------------------------

        private static int Key(CommandContext ctx)
        {
            if (ctx.Args.Positionals.Count == 0)
                throw new UsageException("Usage: keepsake key (list | add-password | add-recovery | remove <id>) --repo <path>");
            string sub = ctx.Args.Positionals[0].ToLowerInvariant();
            using (var repo = ctx.OpenRepository())
            {
                switch (sub)
                {
                    case "list":
                    {
                        var keys = repo.ListKeys();
                        ctx.WriteJson(keys);
                        foreach (var k in keys)
                            ctx.Info((k.Current ? "* " : "  ") + k.Id.Substring(0, 8) + "  " + k.Type.PadRight(9) + "  " + Format.LocalTime(k.Created) + "  " + (k.Host ?? "").PadRight(15) + "  " + k.Label);
                        ctx.Info();
                        ctx.Info("* = key used for this session");
                        return ExitCodes.Success;
                    }
                    case "add-password":
                    {
                        string password = ctx.NewPassword("new-password-file", "password");
                        string id = repo.AddPasswordKey(password, ctx.Args.Value("label") ?? "Password");
                        ctx.WriteJson(new { status = "success", id });
                        ctx.Info("Password added (key " + id.Substring(0, 8) + ").");
                        return ExitCodes.Success;
                    }
                    case "add-recovery":
                    {
                        string key = repo.AddRecoveryKey(ctx.Args.Value("label") ?? "Recovery key", out string id);
                        string keyOut = ctx.Args.Value("recovery-key-out");
                        if (keyOut != null)
                            WriteRecoveryKeyFile(keyOut, key, repo);
                        ctx.WriteJson(new { status = "success", id, recoveryKey = key });
                        ctx.Info("New recovery key (key " + id.Substring(0, 8) + ") – store it in KeePass now:");
                        ctx.Info();
                        ctx.Info("    " + key);
                        return ExitCodes.Success;
                    }
                    case "remove":
                    {
                        if (ctx.Args.Positionals.Count != 2)
                            throw new UsageException("Usage: keepsake key remove <id> --repo <path>");
                        string prefix = ctx.Args.Positionals[1].ToLowerInvariant();
                        var matches = repo.ListKeys().Where(k => k.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                        if (matches.Count != 1)
                            throw new KeepsakeException(matches.Count == 0 ? "No key matches '" + prefix + "'." : "'" + prefix + "' matches several keys.");
                        repo.RemoveKey(matches[0].Id);
                        ctx.WriteJson(new { status = "success", id = matches[0].Id });
                        ctx.Info("Key " + matches[0].Id.Substring(0, 8) + " removed.");
                        return ExitCodes.Success;
                    }
                    default:
                        throw new UsageException("Unknown key command '" + sub + "'.");
                }
            }
        }

        private static int Unlock(CommandContext ctx)
        {
            using (var repo = ctx.OpenRepository())
            {
                int removed = RepoLock.RemoveLocks(repo, ctx.Args.Flag("all"));
                ctx.WriteJson(new { status = "success", removed });
                ctx.Info(removed + " lock(s) removed.");
            }
            return ExitCodes.Success;
        }

        private static int Version(CommandContext ctx)
        {
            ctx.WriteJson(new { version = ProgramInfo.Version, repositoryFormat = Repository.FormatVersion });
            ctx.Info(ProgramInfo.Name + " (repository format " + Repository.FormatVersion + ", " + Environment.Version + ")");
            return ExitCodes.Success;
        }

        private static int HelpCommand(CommandContext ctx)
        {
            if (ctx.Args.Positionals.Count == 0)
            {
                Help.PrintOverview();
                return ExitCodes.Success;
            }
            var command = All.FirstOrDefault(c => c.Name == ctx.Args.Positionals[0].ToLowerInvariant())
                          ?? throw new UsageException("Unknown command '" + ctx.Args.Positionals[0] + "'.");
            Help.PrintCommand(command);
            return ExitCodes.Success;
        }

        private static void PrintStatus(CommandContext ctx, RunReport result)
        {
            ctx.Info();
            switch (result.Status)
            {
                case RunStatus.Success:
                    ctx.Info("Finished successfully.");
                    break;
                case RunStatus.Warning:
                    ctx.Info("Finished with warnings.");
                    break;
                case RunStatus.Cancelled:
                    ctx.Info("Cancelled. " + result.Error);
                    break;
                default:
                    ctx.Info("FAILED: " + result.Error);
                    break;
            }
        }

        private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
