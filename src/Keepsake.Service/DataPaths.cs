using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Keepsake.Core.Util;

namespace Keepsake.Service
{
    /// <summary>
    /// Service data folder, by default <c>%ProgramData%\Keepsake</c>:
    /// <c>config\</c> (jobs, settings), <c>state\</c> (scheduler), <c>runs\</c> (history and logs), <c>logs\</c> (service log).
    /// </summary>
    internal sealed class DataPaths
    {
        public DataPaths(string root)
        {
            Root = Path.GetFullPath(root);
        }

        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Keepsake");

        public string Root { get; }
        public string Config => Path.Combine(Root, "config");
        public string State => Path.Combine(Root, "state");
        public string Runs => Path.Combine(Root, "runs");
        public string Logs => Path.Combine(Root, "logs");

        public string JobsFile => Path.Combine(Config, "jobs.json");
        public string DebugLogFile => Path.Combine(Logs, "debug.log");
        public string SettingsFile => Path.Combine(Config, "settings.json");
        public string SchedulerFile => Path.Combine(State, "scheduler.json");

        /// <summary>
        /// Creates the folders. Junctions and symbolic links anywhere in the data folder are refused, because they
        /// would redirect what the service reads and writes as SYSTEM.
        /// With <paramref name="restrict"/>, only SYSTEM and Administrators can access the folder (SEC-001): the owner
        /// of the folder and of everything below it becomes Administrators, the root gets a protected ACL, and every
        /// child loses its explicit entries and only inherits. This also takes the folder back from a user who
        /// created it before Keepsake was installed.
        /// </summary>
        public void Ensure(bool restrict)
        {
            if (IsLink(Root))
                throw new InvalidOperationException("The data folder " + Root + " is a junction or symbolic link. Replace it with a normal folder.");
            CheckNoLinks(Root);

            if (!restrict)
            {
                foreach (string dir in new[] { Root, Config, State, Runs, Logs })
                    Directory.CreateDirectory(dir);
                return;
            }

            // Ownership can only be taken back with these privileges (LocalSystem and elevated admins hold them).
            WindowsFiles.EnablePrivilege("SeTakeOwnershipPrivilege");
            WindowsFiles.EnablePrivilege("SeRestorePrivilege");
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            if (!Directory.Exists(Root))
                Directory.CreateDirectory(Root, RootSecurity());
            // Owner first: a pre-created folder may deny SYSTEM the right to change its ACL until it owns it.
            SetOwner(new DirectoryInfo(Root), admins);
            Directory.SetAccessControl(Root, RootSecurity());

            foreach (string dir in new[] { Config, State, Runs, Logs })
                Directory.CreateDirectory(dir);
            ResetChildren(new DirectoryInfo(Root), admins);
        }

        private static DirectorySecurity RootSecurity()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            return security;
        }

        /// <summary>Owner Administrators, no explicit entries, permissions inherited from the root – for every child.</summary>
        private static void ResetChildren(DirectoryInfo dir, SecurityIdentifier owner)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The data folder contains a junction or symbolic link: " + entry.FullName + ". Remove it and start the service again.");
                SetOwner(entry, owner);
                if (entry is DirectoryInfo sub)
                {
                    var inherit = new DirectorySecurity();
                    inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    sub.SetAccessControl(inherit);
                    ResetChildren(sub, owner);
                }
                else
                {
                    var inherit = new FileSecurity();
                    inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    ((FileInfo)entry).SetAccessControl(inherit);
                }
            }
        }

        private static void SetOwner(FileSystemInfo entry, SecurityIdentifier owner)
        {
            if (entry is DirectoryInfo dir)
            {
                var s = new DirectorySecurity();
                s.SetOwner(owner);
                dir.SetAccessControl(s);
            }
            else
            {
                var s = new FileSecurity();
                s.SetOwner(owner);
                ((FileInfo)entry).SetAccessControl(s);
            }
        }

        private static bool IsLink(string path) =>
            (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static void CheckNoLinks(string dir)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The data folder contains a junction or symbolic link: " + entry.FullName + ". Remove it and start the service again.");
                if (entry is DirectoryInfo)
                    CheckNoLinks(entry.FullName);
            }
        }
    }

    /// <summary>Writes JSON files atomically (temp file + replace).</summary>
    internal static class AtomicFile
    {
        public static void Write(string path, byte[] data)
        {
            string temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
    }
}
