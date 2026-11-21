using System;
using System.Collections.Generic;
using System.Text;
using Keepsake.Protocol;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>
    /// The text block for a job's KeePass entry: everything a restore on another computer needs besides the secrets
    /// themselves - the exact location, host key, kind of login and how to restore. Passwords are never in it (the
    /// service does not hand stored secrets back); the recovery key only when it was just created.
    /// </summary>
    public static class KeePassEntry
    {
        public static string Build(string jobName, Destination d, string shareUser, string repositoryId, string recoveryKey)
        {
            var lines = new List<string> { "Keepsake backup:  " + (string.IsNullOrWhiteSpace(jobName) ? "(unnamed job)" : jobName.Trim()) };
            switch (d.Type)
            {
                case DestinationTypes.Sftp:
                    lines.Add("Location:         " + d);
                    lines.Add("Host key:         " + (string.IsNullOrEmpty(d.HostKeyFingerprint) ? "not confirmed yet - run 'Check destination' first" : d.HostKeyFingerprint));
                    lines.Add(d.Auth == "key"
                        ? "SFTP login:       private key - attach the key file to this entry, with its passphrase"
                        : "SFTP login:       password - store it in this entry");
                    break;
                case DestinationTypes.Usb:
                    lines.Add("Location:         USB disk, folder \\" + (d.Path ?? "").TrimStart('\\'));
                    foreach (var v in d.Volumes ?? new List<UsbVolume>())
                        lines.Add("  Disk:           " + (string.IsNullOrEmpty(v.Label) ? "(no label)" : v.Label) + "  (volume " + v.VolumeId + ")");
                    break;
                default:
                    lines.Add("Location:         " + d.Path);
                    if (!string.IsNullOrWhiteSpace(shareUser))
                        lines.Add("Share login:      " + shareUser.Trim() + " - store the password in this entry");
                    break;
            }
            if (!string.IsNullOrEmpty(repositoryId))
                lines.Add("Repository ID:    " + repositoryId);
            lines.Add("Repository password: store it in this entry");
            if (string.IsNullOrEmpty(recoveryKey))
            {
                lines.Add("Recovery key:     store the KS1-... key from when the repository was created in this entry");
            }
            else
            {
                // On a line of its own, so this text also works as a recovery key file (--recovery-key-file, "Load from file…").
                lines.Add("Recovery key:");
                lines.Add("    " + recoveryKey);
            }
            lines.Add("Backed up from:   " + Environment.MachineName + ", recorded " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            lines.Add("Restore with:     Start menu > Keepsake Recovery (portable ZIP: Keepsake Recovery.cmd),");
            lines.Add("                  the location above + the repository password or the recovery key");

            var sb = new StringBuilder();
            foreach (string line in lines)
                sb.Append(line).Append("\r\n");
            return sb.ToString();
        }
    }
}
