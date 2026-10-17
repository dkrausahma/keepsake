using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

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
        public string SettingsFile => Path.Combine(Config, "settings.json");
        public string SchedulerFile => Path.Combine(State, "scheduler.json");

        /// <summary>
        /// Creates the folders. With <paramref name="restrict"/>, only SYSTEM and Administrators can access them:
        /// stored secrets are DPAPI-protected for the machine, so the file permissions are what keeps other
        /// local users out.
        /// </summary>
        public void Ensure(bool restrict)
        {
            foreach (string dir in new[] { Root, Config, State, Runs, Logs })
                Directory.CreateDirectory(dir);
            if (!restrict)
                return;

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
            Directory.SetAccessControl(Root, security);
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
