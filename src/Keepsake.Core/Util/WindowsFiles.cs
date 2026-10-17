using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Keepsake.Core.Util
{
    /// <summary>
    /// File access with backup semantics (SRC-09): with SeBackupPrivilege enabled, files can be read even
    /// when their ACL denies access to administrators. Without the privilege the flag changes nothing.
    /// </summary>
    public static class WindowsFiles
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint SequentialScan = 0x08000000;

        public static FileStream OpenRead(string path)
        {
            var handle = CreateFile(LongPath.ToExtended(path), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | SequentialScan, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int hr = Marshal.GetHRForLastWin32Error();
                handle.Dispose();
                Marshal.ThrowExceptionForHR(hr);
            }
            return new FileStream(handle, FileAccess.Read, 4096);
        }

        /// <summary>Enables a privilege (e.g. SeBackupPrivilege) for this process. Returns false if the account lacks it.</summary>
        public static bool EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr token)) // ADJUST_PRIVILEGES | QUERY
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, name, out long luid))
                    return false;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;
                return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED = privilege not held
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TokenPrivileges
        {
            public int Count;
            public long Luid;
            public int Attributes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);
    }
}
