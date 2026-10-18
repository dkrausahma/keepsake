using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Keepsake.Core;

namespace Keepsake.Protocol
{
    /// <summary>
    /// Checks who owns the other end of the pipe before any password is sent (SEC-005). While the service is
    /// stopped, any local account can create a pipe with the same name; clients must only talk to a server
    /// running as LocalSystem (the installed service) or as the client's own account (console mode).
    /// </summary>
    internal static class PipeServerIdentity
    {
        private const string LocalSystem = "S-1-5-18";

        public static void Verify(NamedPipeClientStream pipe)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid))
                throw new KeepsakeException("Cannot check which program answers on the Keepsake pipe (" + new Win32Exception().Message + "). Connection refused.");
            string server = ProcessUserSid(OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid), pid);
            string self = ProcessUserSid(GetCurrentProcess(), 0);
            if (server == LocalSystem || string.Equals(server, self, StringComparison.OrdinalIgnoreCase))
                return;
            throw new KeepsakeException("The program answering on the Keepsake pipe (process " + pid + ", account " + server +
                                        ") is not the Keepsake service. Connection refused – check the computer for unwanted software.");
        }

        private static string ProcessUserSid(IntPtr process, uint pid)
        {
            if (process == IntPtr.Zero)
                throw new KeepsakeException("Cannot check the program answering on the Keepsake pipe (process " + pid + ": " + new Win32Exception().Message + "). Run as administrator.");
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out token))
                    throw new KeepsakeException("Cannot check the program answering on the Keepsake pipe (" + new Win32Exception().Message + "). Run as administrator.");
                GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out int length);
                buffer = Marshal.AllocHGlobal(length);
                if (!GetTokenInformation(token, 1, buffer, length, out length))
                    throw new KeepsakeException("Cannot read the account of the Keepsake pipe server (" + new Win32Exception().Message + ").");
                IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_USER.User.Sid
                if (!ConvertSidToStringSid(sid, out IntPtr text))
                    throw new KeepsakeException("Cannot read the account of the Keepsake pipe server.");
                try
                {
                    return Marshal.PtrToStringUni(text);
                }
                finally
                {
                    LocalFree(text);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero)
                    CloseHandle(token);
                if (pid != 0)
                    CloseHandle(process);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
