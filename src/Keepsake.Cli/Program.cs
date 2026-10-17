using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Keepsake.Core;
using Keepsake.Core.Util;

namespace Keepsake.Cli
{
    internal static class Program
    {
        internal static readonly string[] GlobalValueOptions = { "repo", "password-file", "recovery-key-file", "log" };
        internal static readonly string[] GlobalFlags = { "json", "non-interactive", "use-recovery-key", "quiet", "help" };

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // No console attached (e.g. started by a scheduler); output still works.
            }

            bool json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
            using (var cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    if (cancel.IsCancellationRequested)
                        return; // Second Ctrl+C: terminate immediately.
                    e.Cancel = true;
                    cancel.Cancel();
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Cancelling – finishing the current block. Press Ctrl+C again to abort immediately.");
                };

                try
                {
                    return Run(args, json, cancel.Token);
                }
                catch (UsageException ex)
                {
                    return Fail(json, ExitCodes.Usage, ex.Message, "Run 'keepsake help' for usage.");
                }
                catch (WrongCredentialException ex)
                {
                    return Fail(json, ExitCodes.WrongCredential, ex.Message, null);
                }
                catch (Keepsake.Protocol.ApiException ex)
                {
                    int code = ex.Code == Keepsake.Protocol.ErrorCodes.Unauthorized || ex.Code == Keepsake.Protocol.ErrorCodes.LockedOut ? ExitCodes.WrongCredential
                        : ex.Code == Keepsake.Protocol.ErrorCodes.Conflict ? ExitCodes.Locked
                        : ex.Code == Keepsake.Protocol.ErrorCodes.Invalid || ex.Code == Keepsake.Protocol.ErrorCodes.NotFound ? ExitCodes.Usage
                        : ExitCodes.Failed;
                    return Fail(json, code, ex.Message, null);
                }
                catch (RepositoryLockedException ex)
                {
                    return Fail(json, ExitCodes.Locked, ex.Message, null);
                }
                catch (OperationCanceledException)
                {
                    return Fail(json, ExitCodes.Cancelled, "Cancelled.", null);
                }
                catch (FormatException ex)
                {
                    return Fail(json, ExitCodes.Usage, ex.Message, null);
                }
                catch (KeepsakeException ex)
                {
                    return Fail(json, ExitCodes.Failed, ex.Message, null);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return Fail(json, ExitCodes.Failed, ex.Message, null);
                }
            }
        }

        private static int Run(string[] args, bool json, CancellationToken cancel)
        {
            if (args.Length == 0)
            {
                Help.PrintOverview();
                return ExitCodes.Usage;
            }

            string name = args[0].ToLowerInvariant();
            if (name == "--help" || name == "-h" || name == "/?")
                name = "help";
            if (name == "--version")
                name = "version";

            var command = Commands.All.FirstOrDefault(c => c.Name == name)
                          ?? throw new UsageException("Unknown command '" + args[0] + "'.");

            var parsed = ParsedArgs.Parse(args.Skip(1).ToList(),
                GlobalValueOptions.Concat(command.ValueOptions).ToList(),
                GlobalFlags.Concat(command.Flags).ToList());

            if (parsed.Flag("help"))
            {
                Help.PrintCommand(command);
                return ExitCodes.Success;
            }
            var context = new CommandContext(parsed, json, cancel);
            return command.Run(context);
        }

        private static int Fail(bool json, int code, string message, string hint)
        {
            ConsoleUi.ClearProgress();
            if (json)
            {
                Console.Out.WriteLine(Json.ToText(new { status = "error", exitCode = code, error = message }));
            }
            else
            {
                Console.Error.WriteLine("Error: " + message);
                if (hint != null)
                    Console.Error.WriteLine(hint);
            }
            return code;
        }
    }

    internal sealed class Command
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Usage { get; set; }
        public string Details { get; set; }
        public string[] ValueOptions { get; set; } = new string[0];
        public string[] Flags { get; set; } = new string[0];
        public Func<CommandContext, int> Run { get; set; }
    }

    internal static class Help
    {
        public static void PrintOverview()
        {
            Console.WriteLine(ProgramInfo.Name + " – encrypted, deduplicated file backup");
            Console.WriteLine();
            Console.WriteLine("Usage: keepsake <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            foreach (var c in Commands.All)
                Console.WriteLine("  " + c.Name.PadRight(11) + c.Summary);
            Console.WriteLine();
            Console.WriteLine("Common options:");
            Console.WriteLine("  --repo <path>               Repository folder (local, USB disk or \\\\server\\share). Env: KEEPSAKE_REPOSITORY");
            Console.WriteLine("  --password-file <file>      Read the password from the first line of a file. Env: KEEPSAKE_PASSWORD");
            Console.WriteLine("  --recovery-key-file <file>  Unlock with the recovery key (KS1-...) from a file. Env: KEEPSAKE_RECOVERY_KEY");
            Console.WriteLine("  --use-recovery-key          Ask for the recovery key instead of the password");
            Console.WriteLine("  --non-interactive           Never prompt; fail if a password is missing");
            Console.WriteLine("  --json                      Machine-readable output on stdout");
            Console.WriteLine("  --log <file>                Write a detailed run log (backup, restore, check)");
            Console.WriteLine("  --quiet                     No progress output");
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 success, 1 warnings, 2 failed, 3 usage error, 4 wrong password/key, 5 repository locked, 6 cancelled");
            Console.WriteLine("Run 'keepsake help <command>' for details.");
        }

        public static void PrintCommand(Command c)
        {
            Console.WriteLine("Usage: " + c.Usage);
            Console.WriteLine();
            Console.WriteLine(c.Summary);
            if (!string.IsNullOrEmpty(c.Details))
            {
                Console.WriteLine();
                Console.WriteLine(c.Details);
            }
        }
    }
}
