using System;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Keepsake.Core;

namespace Keepsake.Service
{
    internal sealed class KeepsakeWindowsService : ServiceBase
    {
        public const string Name = "KeepsakeService";
        public const string DisplayNameText = "Keepsake Backup Service";

        private ServiceHost _host;

        public KeepsakeWindowsService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _host = new ServiceHost(Option(args, "--data"), Option(args, "--pipe"), consoleMode: false);
            _host.Start();
        }

        protected override void OnStop()
        {
            RequestAdditionalTime(70000);
            _host?.Stop();
        }

        protected override void OnShutdown() => OnStop();

        internal static string Option(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (!Environment.UserInteractive)
            {
                ServiceBase.Run(new KeepsakeWindowsService());
                return 0;
            }

            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            try
            {
                switch (command)
                {
                    case "console":
                        return RunConsole(args);
                    case "install":
                        return Install();
                    case "uninstall":
                        Sc("stop " + KeepsakeWindowsService.Name, ignoreErrors: true);
                        return Sc("delete " + KeepsakeWindowsService.Name);
                    case "start":
                        return Sc("start " + KeepsakeWindowsService.Name);
                    case "stop":
                        return Sc("stop " + KeepsakeWindowsService.Name);
                    default:
                        Console.WriteLine(ProgramInfo.Name + " – backup service");
                        Console.WriteLine();
                        Console.WriteLine("Usage: keepsake-service <command>");
                        Console.WriteLine("  install                       Register the Windows service (automatic start, LocalSystem)");
                        Console.WriteLine("  uninstall                     Stop and remove the Windows service");
                        Console.WriteLine("  start | stop                  Start or stop the installed service");
                        Console.WriteLine("  console [--data <folder>] [--pipe <name>]");
                        Console.WriteLine("                                Run in this console (for testing); Ctrl+C stops");
                        Console.WriteLine();
                        Console.WriteLine("All commands except 'console' need an elevated (administrator) prompt.");
                        return command == "help" || command == "--help" ? 0 : 3;
                }
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 2;
            }
        }

        private static int RunConsole(string[] args)
        {
            using (var stop = new ManualResetEventSlim())
            using (var host = new ServiceHost(KeepsakeWindowsService.Option(args, "--data"), KeepsakeWindowsService.Option(args, "--pipe"), consoleMode: true))
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };
                host.Start();
                Console.WriteLine("Running. Press Ctrl+C to stop.");
                stop.Wait();
                host.Stop();
            }
            return 0;
        }

        private static int Install()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int rc = Sc("create " + KeepsakeWindowsService.Name + " binPath= \"\\\"" + exe + "\\\"\" start= auto DisplayName= \"" + KeepsakeWindowsService.DisplayNameText + "\"");
            if (rc != 0)
                return rc;
            Sc("description " + KeepsakeWindowsService.Name + " \"Runs scheduled Keepsake backups.\"", ignoreErrors: true);
            // Restart after 1 minute on the first three failures within a day.
            Sc("failure " + KeepsakeWindowsService.Name + " reset= 86400 actions= restart/60000/restart/60000/restart/60000", ignoreErrors: true);
            Console.WriteLine("Service installed. Start it with: keepsake-service start");
            return 0;
        }

        private static int Sc(string arguments, bool ignoreErrors = false)
        {
            var psi = new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && !ignoreErrors)
                    Console.Error.WriteLine(output.Trim());
                else if (!ignoreErrors)
                    Console.WriteLine(output.Trim().Split('\n').FirstOrDefault()?.Trim());
                return ignoreErrors ? 0 : p.ExitCode;
            }
        }
    }
}
