using System;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;

namespace Keepsake.Cli
{
    internal sealed class CommandContext
    {
        public CommandContext(ParsedArgs args, bool json, CancellationToken cancel)
        {
            Args = args;
            Json = json;
            Cancel = cancel;
            Quiet = args.Flag("quiet") || json;
            NonInteractive = args.Flag("non-interactive") || !ConsoleUi.CanPrompt;
        }

        public ParsedArgs Args { get; }
        public bool Json { get; }
        public bool Quiet { get; }
        public bool NonInteractive { get; }
        public CancellationToken Cancel { get; }

        public string RepositoryLocation
        {
            get
            {
                string location = Args.Value("repo") ?? Environment.GetEnvironmentVariable("KEEPSAKE_REPOSITORY");
                if (string.IsNullOrWhiteSpace(location))
                    throw new UsageException("No repository given. Use --repo <path> or set KEEPSAKE_REPOSITORY.");
                return location;
            }
        }

        public Repository OpenRepository()
        {
            var storage = OpenStorage();
            try
            {
                if (!Repository.Exists(storage))
                    throw new KeepsakeException("No Keepsake repository found at " + storage.Location + ".");
                return Repository.Open(storage, Credential());
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }

        /// <summary>Folder, share, USB path or <c>sftp://user@host[:port]/path</c>.</summary>
        public IStorage OpenStorage()
        {
            string location = RepositoryLocation;
            if (!StorageFactory.IsSftp(location))
                return StorageFactory.Open(location);

            var options = SftpOptionsFromArgs(location);

            for (int attempt = 0; ; attempt++)
            {
                var storage = new SftpStorage(options);
                try
                {
                    storage.Connect();
                    return storage;
                }
                catch (HostKeyException ex) when (attempt == 0 && !ex.Mismatch && !NonInteractive)
                {
                    storage.Dispose();
                    Console.Error.WriteLine("The host key of " + options.Host + " is not known yet:");
                    Console.Error.WriteLine("    " + ex.Fingerprint);
                    Console.Error.WriteLine("Compare it with the fingerprint shown on the server (ssh-keygen -lf <host key file>).");
                    string answer = ConsoleUi.ReadLine("Trust this key and continue? [y/N] ");
                    if (!answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
                        throw new KeepsakeException("Host key not trusted.");
                    Console.Error.WriteLine("Tip: pass --sftp-host-key " + ex.Fingerprint + " to skip this question next time.");
                    options.HostKeyFingerprint = ex.Fingerprint;
                }
                catch (HostKeyException ex)
                {
                    storage.Dispose();
                    throw new KeepsakeException(ex.Message + (ex.Mismatch ? "" : " Pass --sftp-host-key " + ex.Fingerprint + " after verifying it."));
                }
                catch
                {
                    storage.Dispose();
                    throw;
                }
            }
        }

        /// <summary>SFTP location, credentials and trusted host key from the command line or environment.</summary>
        public SftpOptions SftpOptionsFromArgs(string location)
        {
            var options = StorageFactory.ParseSftp(location);
            string keyFile = Args.Value("sftp-key-file");
            if (keyFile != null)
            {
                options.PrivateKey = ReadFile(keyFile, "SFTP key");
                string passFile = Args.Value("sftp-key-passphrase-file");
                options.KeyPassphrase = passFile != null ? FirstLine(passFile, "SFTP key passphrase") : Environment.GetEnvironmentVariable("KEEPSAKE_SFTP_KEY_PASSPHRASE");
            }
            else
            {
                string pwFile = Args.Value("sftp-password-file");
                options.Password = pwFile != null ? FirstLine(pwFile, "SFTP password") : Environment.GetEnvironmentVariable("KEEPSAKE_SFTP_PASSWORD");
                if (string.IsNullOrEmpty(options.Password))
                {
                    if (NonInteractive)
                        throw new UsageException("No SFTP password given. Use --sftp-password-file, KEEPSAKE_SFTP_PASSWORD or --sftp-key-file.");
                    options.Password = ConsoleUi.ReadSecret("SFTP password for " + options.User + "@" + options.Host + ": ");
                }
            }
            options.HostKeyFingerprint = Args.Value("sftp-host-key") ?? Environment.GetEnvironmentVariable("KEEPSAKE_SFTP_HOST_KEY");

            return options;
        }

        private static string FirstLine(string path, string what) => ReadFile(path, what).Split('\n')[0].TrimEnd('\r');

        public RepoCredential Credential()
        {
            string keyFile = Args.Value("recovery-key-file");
            if (keyFile != null)
                return RepoCredential.FromRecoveryKeyText(ReadFile(keyFile, "recovery key"));

            string envKey = Environment.GetEnvironmentVariable("KEEPSAKE_RECOVERY_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                return RepoCredential.FromRecoveryKeyText(envKey);

            string password = PasswordFromFileOrEnvironment();
            if (password != null)
                return RepoCredential.FromPassword(password);

            if (NonInteractive)
                throw new UsageException("No password given. Use --password-file, KEEPSAKE_PASSWORD or --recovery-key-file.");

            if (Args.Flag("use-recovery-key"))
                return RepoCredential.FromRecoveryKeyText(ConsoleUi.ReadLine("Recovery key (KS1-...): "));
            return RepoCredential.FromPassword(ConsoleUi.ReadSecret("Repository password: "));
        }

        /// <summary>Password for a new repository or key slot (asked twice when interactive).</summary>
        public string NewPassword(string fileOption, string what)
        {
            string file = Args.Value(fileOption);
            string password = file != null ? ReadFile(file, "password").Split('\n')[0].TrimEnd('\r') : null;
            if (password == null && fileOption == "password-file")
                password = Environment.GetEnvironmentVariable("KEEPSAKE_PASSWORD");
            if (!string.IsNullOrEmpty(password))
                return password;

            if (NonInteractive)
                throw new UsageException("No " + what + " given. Use --" + fileOption + ".");
            string first = ConsoleUi.ReadSecret("New " + what + ": ");
            if (first.Length < 8)
                throw new UsageException("The " + what + " must have at least 8 characters.");
            string second = ConsoleUi.ReadSecret("Repeat " + what + ": ");
            if (first != second)
                throw new UsageException("The two entries do not match.");
            return first;
        }

        public void Info(string line = "")
        {
            if (!Json)
                Console.Out.WriteLine(line);
        }

        public void WriteJson<T>(T value)
        {
            if (Json)
                Console.Out.WriteLine(Keepsake.Core.Util.Json.ToText(value));
        }

        public void WriteLog(string title, RunReport report, System.Collections.Generic.IEnumerable<string> summary)
        {
            string path = Args.Value("log");
            if (path == null)
                return;
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(full, TextReport.Full(title, report, summary), new System.Text.UTF8Encoding(true));
        }

        public static int ExitCode(RunStatus status)
        {
            switch (status)
            {
                case RunStatus.Success: return ExitCodes.Success;
                case RunStatus.Warning: return ExitCodes.Warning;
                case RunStatus.Cancelled: return ExitCodes.Cancelled;
                default: return ExitCodes.Failed;
            }
        }

        private string PasswordFromFileOrEnvironment()
        {
            string file = Args.Value("password-file");
            if (file != null)
            {
                string line = ReadFile(file, "password").Split('\n')[0].TrimEnd('\r');
                if (line.Length == 0)
                    throw new UsageException("The password file '" + file + "' is empty.");
                return line;
            }
            string env = Environment.GetEnvironmentVariable("KEEPSAKE_PASSWORD");
            return string.IsNullOrEmpty(env) ? null : env;
        }

        private static string ReadFile(string path, string what)
        {
            try
            {
                return File.ReadAllText(path).Trim('﻿');
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new UsageException("Cannot read the " + what + " file '" + path + "': " + ex.Message);
            }
        }

        public void PrintIssues(RunReport report, int max = 20)
        {
            if (Json)
                return;
            var shown = report.Issues.Where(i => i.Kind != IssueKind.Skipped || report.Issues.Count <= max).Take(max).ToList();
            if (shown.Count == 0)
                return;
            Console.Out.WriteLine();
            Console.Out.WriteLine("Problems:");
            foreach (var i in shown)
                Console.Out.WriteLine("  " + TextReport.IssueLine(i));
            int rest = report.Issues.Count - shown.Count;
            if (rest > 0)
                Console.Out.WriteLine("  ... and " + rest + " more" + (Args.Value("log") == null ? " (use --log <file> for the full list)" : " (see the log file)"));
        }
    }
}
