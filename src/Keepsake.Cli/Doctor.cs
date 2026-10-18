using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Keepsake.Core;
using Keepsake.Core.Util;
using Keepsake.Protocol;
using Microsoft.Win32;

namespace Keepsake.Cli
{
    /// <summary>
    /// <c>keepsake doctor</c>: checks the machine for everything Keepsake depends on, with special attention to
    /// Windows Server 2008 R2 (Service Pack 1, .NET 4.8, TLS 1.2).
    /// </summary>
    internal static class Doctor
    {
        private enum Level
        {
            Ok,
            Info,
            Warn,
            Fail,
        }

        private sealed class Finding
        {
            public Level Level;
            public string Check;
            public string Result;
            public string Hint;
        }

        public static int Run(CommandContext ctx)
        {
            var findings = new List<Finding>();
            void Add(Level l, string check, string result, string hint = null) =>
                findings.Add(new Finding { Level = l, Check = check, Result = result, Hint = hint });

            // Windows
            const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string product = Registry.GetValue(nt, "ProductName", "")?.ToString() ?? "";
            string sp = Registry.GetValue(nt, "CSDVersion", "")?.ToString() ?? "";
            int.TryParse(Registry.GetValue(nt, "CurrentBuild", "0")?.ToString(), out int build);
            var os = Environment.OSVersion.Version;
            if (Registry.GetValue(nt, "CurrentMajorVersionNumber", null) is int major)
                os = new Version(major, Convert.ToInt32(Registry.GetValue(nt, "CurrentMinorVersionNumber", 0)), build);
            if (build >= 22000)
                product = product.Replace("Windows 10", "Windows 11"); // the registry still says "Windows 10" on Windows 11
            bool is2008R2 = os.Major == 6 && os.Minor == 1;
            if (os < new Version(6, 1))
                Add(Level.Fail, "Windows", product + " " + sp, "Keepsake needs Windows Server 2008 R2 SP1, Windows 7 SP1 or newer.");
            else if (is2008R2 && os.Build < 7601)
                Add(Level.Fail, "Windows", product + " (no Service Pack 1)", "Install Service Pack 1 – .NET Framework 4.8 requires it.");
            else
                Add(Level.Ok, "Windows", (product + " " + sp).Trim() + " (" + os + ")" + (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit"));

            // .NET Framework
            int release = Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0) ?? 0);
            if (release >= 528040)
                Add(Level.Ok, ".NET Framework", "4.8 or newer (release " + release + ")");
            else
                Add(Level.Fail, ".NET Framework", release == 0 ? "4.x not found" : "release " + release + " (older than 4.8)", "Install .NET Framework 4.8 (offline installer: ndp48-x86-x64-allos-enu.exe).");

            // Elevation
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Add(admin ? Level.Ok : Level.Warn, "Administrator", admin ? "running elevated" : "not elevated",
                admin ? null : "Run 'keepsake doctor' from an elevated prompt for complete results.");

            // Service
            try
            {
                using (var service = new ServiceController("KeepsakeService"))
                {
                    var status = service.Status;
                    var startType = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\KeepsakeService", "Start", 0);
                    bool auto = Convert.ToInt32(startType ?? 0) == 2;
                    Add(status == ServiceControllerStatus.Running ? (auto ? Level.Ok : Level.Warn) : Level.Fail, "Keepsake service",
                        status + (auto ? ", automatic start" : ", NOT automatic"),
                        status != ServiceControllerStatus.Running ? "Start it: keepsake-service start (or services.msc)." : auto ? null : "Set the start type to Automatic.");
                }
            }
            catch (InvalidOperationException)
            {
                Add(Level.Fail, "Keepsake service", "not installed", "Install Keepsake with the MSI, or run 'keepsake-service install' elevated.");
            }

            // API
            try
            {
                using (var client = KeepsakeClient.Connect(ctx.Args.Value("pipe"), 2000))
                {
                    var auth = client.Call<AuthStatusResult>(Methods.AuthStatus);
                    Add(auth.PasswordSet ? Level.Ok : Level.Warn, "Service interface", "reachable" + (auth.PasswordSet ? "" : ", no application password set"),
                        auth.PasswordSet ? null : "Open the Keepsake GUI or run 'keepsake setup'.");
                }
            }
            catch (KeepsakeException ex)
            {
                Add(Level.Warn, "Service interface", ex.Message);
            }

            // TLS 1.2 (SMTP needs it; Server 2008 R2 has it off by default)
            if (is2008R2)
            {
                string key = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client";
                bool enabled = Convert.ToInt32(Registry.GetValue(key, "Enabled", 0) ?? 0) == 1 && Convert.ToInt32(Registry.GetValue(key, "DisabledByDefault", 1) ?? 1) == 0;
                Add(enabled ? Level.Ok : Level.Warn, "TLS 1.2 (SChannel)", enabled ? "enabled for clients" : "not enabled",
                    enabled ? null : "Needed for e-mail over STARTTLS/SSL. Run tools\\Enable-Tls12.ps1 elevated and reboot.");
            }
            else
            {
                Add(Level.Ok, "TLS 1.2 (SChannel)", "enabled by default on this Windows version");
            }
            bool strong = Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework\v4.0.30319", "SchUseStrongCrypto", 0) ?? 0) == 1
                          || Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework\v4.0.30319", "SystemDefaultTlsVersions", 0) ?? 0) == 1;
            Add(strong || !is2008R2 ? Level.Ok : Level.Warn, ".NET strong crypto", strong ? "enabled" : "not set",
                strong || !is2008R2 ? null : "Run tools\\Enable-Tls12.ps1 so .NET uses TLS 1.2.");

            // VSS
            try
            {
                using (var vss = new ServiceController("VSS"))
                {
                    var start = Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\VSS", "Start", 3) ?? 3);
                    Add(start == 4 ? Level.Fail : Level.Ok, "Volume Shadow Copy", vss.Status + (start == 4 ? ", DISABLED" : ""),
                        start == 4 ? "Set the 'Volume Shadow Copy' service to Manual, otherwise open files cannot be backed up." : null);
                }
            }
            catch (InvalidOperationException)
            {
                Add(Level.Fail, "Volume Shadow Copy", "service not found");
            }

            // Event Log
            try
            {
                bool exists = EventLog.SourceExists("Keepsake");
                Add(exists ? Level.Ok : Level.Info, "Event Log source", exists ? "registered" : "not registered yet", exists ? null : "Created when the service starts as administrator/LocalSystem.");
            }
            catch (System.Security.SecurityException)
            {
                Add(Level.Info, "Event Log source", "cannot be checked without elevation");
            }

            // Data folder
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Keepsake");
            if (!Directory.Exists(data))
            {
                Add(Level.Info, "Data folder", data + " does not exist yet");
            }
            else
            {
                try
                {
                    var trusted = new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid }
                        .Select(t => new SecurityIdentifier(t, null)).ToList();
                    var problems = new List<string>();
                    foreach (string dir in new[] { data, Path.Combine(data, "config") }.Where(Directory.Exists))
                    {
                        var acl = Directory.GetAccessControl(dir);
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            problems.Add(dir + " is a junction or link");
                        if (!trusted.Contains((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))))
                            problems.Add(dir + " is owned by " + acl.GetOwner(typeof(NTAccount)));
                        if (acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                            .Any(r => r.AccessControlType == AccessControlType.Allow && !trusted.Contains((SecurityIdentifier)r.IdentityReference)))
                            problems.Add(dir + " grants access to other accounts");
                    }
                    bool exposed = problems.Count > 0;
                    Add(exposed ? Level.Fail : Level.Ok, "Data folder", exposed ? string.Join("; ", problems) : data + " restricted to SYSTEM and Administrators",
                        exposed ? "Restart the service; it takes ownership of the folder and resets its permissions at start-up." : null);
                }
                catch (UnauthorizedAccessException)
                {
                    Add(Level.Ok, "Data folder", data + " (not readable for this user – restricted)");
                }
            }

            // Backup privilege
            if (admin)
            {
                bool priv = WindowsFiles.EnablePrivilege("SeBackupPrivilege");
                Add(priv ? Level.Ok : Level.Warn, "Backup privilege", priv ? "available" : "not available",
                    priv ? null : "Files that deny administrators access will be skipped.");
            }

            // Free space for logs
            var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            Add(system.AvailableFreeSpace > 1L << 30 ? Level.Ok : Level.Warn, "System drive", Core.Reporting.Format.Bytes(system.AvailableFreeSpace) + " free",
                system.AvailableFreeSpace > 1L << 30 ? null : "Run logs and VSS need some free space on the system drive.");

            ctx.WriteJson(findings.Select(f => new { level = f.Level.ToString().ToLowerInvariant(), check = f.Check, result = f.Result, hint = f.Hint }));
            if (!ctx.Json)
            {
                ctx.Info("Keepsake doctor – " + ProgramInfo.Name + " on " + ProgramInfo.Host);
                ctx.Info();
                foreach (var f in findings)
                {
                    string tag = f.Level == Level.Ok ? "  OK  " : f.Level == Level.Info ? " INFO " : f.Level == Level.Warn ? " WARN " : " FAIL ";
                    ctx.Info("[" + tag + "] " + f.Check.PadRight(22) + f.Result);
                    if (f.Hint != null)
                        ctx.Info("         " + new string(' ', 22) + "→ " + f.Hint);
                }
            }
            return findings.Any(f => f.Level == Level.Fail) ? ExitCodes.Failed
                : findings.Any(f => f.Level == Level.Warn) ? ExitCodes.Warning
                : ExitCodes.Success;
        }
    }
}
