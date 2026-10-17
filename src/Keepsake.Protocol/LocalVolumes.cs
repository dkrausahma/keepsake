using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Keepsake.Protocol
{
    public sealed class VolumeInfo
    {
        /// <summary><c>Volume{GUID}</c>.</summary>
        public string VolumeId { get; set; }

        /// <summary>Current mount point, e.g. <c>E:\</c>.</summary>
        public string Drive { get; set; }

        public string Label { get; set; }
        public string DriveType { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        public string Display => Drive.TrimEnd('\\') + "  " + (string.IsNullOrEmpty(Label) ? "(no label)" : Label) +
                                 "  · " + (TotalBytes / (1024 * 1024 * 1024)) + " GB · " + DriveType.ToLowerInvariant();
    }

    /// <summary>
    /// Mounted volumes and their stable volume GUIDs (DST-03): a USB disk is found again under whatever
    /// drive letter Windows gives it next time.
    /// </summary>
    public static class LocalVolumes
    {
        public static List<VolumeInfo> List()
        {
            var result = new List<VolumeInfo>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                    continue;
                try
                {
                    if (!drive.IsReady)
                        continue;
                    string id = VolumeId(drive.Name);
                    if (id == null)
                        continue;
                    result.Add(new VolumeInfo
                    {
                        VolumeId = id,
                        Drive = drive.Name,
                        Label = drive.VolumeLabel,
                        DriveType = drive.DriveType.ToString(),
                        TotalBytes = drive.TotalSize,
                        FreeBytes = drive.AvailableFreeSpace,
                    });
                }
                catch (IOException)
                {
                    // Drive vanished while enumerating.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return result;
        }

        /// <summary>Current drive (e.g. <c>E:\</c>) of a volume, or null when it is not connected.</summary>
        public static string FindDrive(string volumeId) =>
            List().FirstOrDefault(v => string.Equals(v.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))?.Drive;

        private static string VolumeId(string mountPoint)
        {
            var sb = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(mountPoint, sb, sb.Capacity))
                return null;
            // \\?\Volume{GUID}\  →  Volume{GUID}
            return sb.ToString().Replace(@"\\?\", "").TrimEnd('\\');
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
    }
}
