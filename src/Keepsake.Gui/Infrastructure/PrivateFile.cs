using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>
    /// Writes a file that only the current user (and SYSTEM) can read – for recovery keys, which decrypt every
    /// backup (SEC-021). Inherited permissions of the chosen folder are not applied.
    /// </summary>
    internal static class PrivateFile
    {
        public static void WriteAllText(string path, string text)
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = WindowsIdentity.GetCurrent().User;
            if (user != null)
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            byte[] data = new UTF8Encoding(false).GetBytes(text);
            if (File.Exists(path))
                File.Delete(path); // an existing file would keep its old permissions
            using (var fs = File.Create(path, 4096, FileOptions.None, security))
                fs.Write(data, 0, data.Length);
        }
    }
}
