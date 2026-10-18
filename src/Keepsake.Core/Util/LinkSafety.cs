using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Keepsake.Core.Util
{
    /// <summary>
    /// Guards against junctions and symbolic links that would make the service (running as SYSTEM) read, write or
    /// delete outside the folder it means to work in (SEC-003, SEC-004). A link at or above the chosen folder is
    /// the admin's own setup and is allowed; only links <i>below</i> that anchor are refused.
    /// </summary>
    public static class LinkSafety
    {
        public static bool IsLink(string path)
        {
            try
            {
                string extended = LongPath.ToExtended(path);
                return (File.Exists(extended) || Directory.Exists(extended)) &&
                       (File.GetAttributes(extended) & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Throws when an existing folder between <paramref name="anchor"/> (exclusive) and <paramref name="dir"/>
        /// (inclusive) is a junction or symbolic link. Folders that do not exist yet are fine – they are created
        /// as normal folders. Does nothing when <paramref name="dir"/> is not below the anchor.
        /// </summary>
        public static void EnsureNoLinksBelow(string anchor, string dir)
        {
            string a = LongPath.Normalize(anchor);
            string d = LongPath.Normalize(dir);
            if (!LongPath.IsSameOrBelow(d, a) || string.Equals(a, d, StringComparison.OrdinalIgnoreCase))
                return;
            string rest = d.Substring(a.TrimEnd('\\').Length).TrimStart('\\');
            string current = a;
            foreach (string part in rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                string extended = LongPath.ToExtended(current);
                if (!Directory.Exists(extended) && !File.Exists(extended))
                    return; // the rest does not exist yet
                if ((File.GetAttributes(extended) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(current + " is a junction or symbolic link. It is not followed, for safety; remove it or restore to a different folder.");
            }
        }

        /// <summary>The path the handle really refers to (links resolved), in normal form; null when Windows cannot tell.</summary>
        public static string FinalPath(SafeFileHandle handle)
        {
            var sb = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
            if (n == 0)
                return null;
            if (n >= sb.Capacity)
            {
                sb = new StringBuilder((int)n + 1);
                n = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                if (n == 0 || n >= sb.Capacity)
                    return null;
            }
            return LongPath.ToNormal(sb.ToString());
        }

        /// <summary>The real location of an existing folder (links resolved); null when it cannot be determined.</summary>
        public static string FinalDirectoryPath(string dir)
        {
            using (var h = CreateFile(LongPath.ToExtended(dir), 0, 0x7, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02000000 /* BACKUP_SEMANTICS */, IntPtr.Zero))
            {
                return h.IsInvalid ? null : FinalPath(h);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
