using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Keepsake.Core;
using Keepsake.Core.Util;
using Keepsake.Protocol;
using Microsoft.Win32.SafeHandles;

namespace Keepsake.Service
{
    /// <summary>
    /// Local named-pipe server (SEC-04). Only SYSTEM and Administrators may connect, network access is
    /// denied explicitly, and every request except login needs a session token.
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        private readonly string _name;
        private readonly ApiHandler _handler;
        private readonly PipeSecurity _security;
        private readonly object _lock = new object();
        private readonly List<NamedPipeServerStream> _connections = new List<NamedPipeServerStream>();
        private Thread _acceptThread;
        private NamedPipeServerStream _listening;
        private volatile bool _stopping;

        /// <param name="extraUser">Additional account allowed to connect (console/development mode).</param>
        public PipeServer(string name, ApiHandler handler, SecurityIdentifier extraUser)
        {
            _name = name;
            _handler = handler;
            _security = CreateSecurity(extraUser);
        }

        public void Start()
        {
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Keepsake pipe accept" };
            _acceptThread.Start();
            ServiceLog.Info("Listening on pipe '" + _name + "'.");
        }

        public void Dispose()
        {
            _stopping = true;
            // A blocking WaitForConnection does not return on Dispose in .NET Framework: wake it with a dummy client.
            try
            {
                using (var wake = new NamedPipeClientStream(".", _name, PipeDirection.InOut))
                    wake.Connect(500);
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
            {
                // Nothing listening any more.
            }
            lock (_lock)
            {
                _listening?.Dispose();
                foreach (var c in _connections)
                    c.Dispose();
                _connections.Clear();
            }
            _acceptThread?.Join(TimeSpan.FromSeconds(5));
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server;
                int count = ConnectionCount();
                if (count >= MaxConnections)
                {
                    Thread.Sleep(200); // bounded number of clients (SEC-015): wait for a free slot
                    continue;
                }
                bool first = count == 0; // no instance of ours exists: claim the name exclusively
                try
                {
                    server = CreateInstance(first);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (_stopping)
                        return;
                    ServiceLog.Error("Cannot create pipe '" + _name + "' (is another instance running?)", ex);
                    Thread.Sleep(5000);
                    continue;
                }

                lock (_lock)
                    _listening = server;
                try
                {
                    server.WaitForConnection();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    server.Dispose();
                    if (_stopping)
                        return;
                    continue;
                }

                lock (_lock)
                {
                    _listening = null;
                    _connections.Add(server);
                }
                var thread = new Thread(() => Serve(server)) { IsBackground = true, Name = "Keepsake pipe client" };
                thread.Start();
            }
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            try
            {
                while (!_stopping && pipe.IsConnected)
                {
                    byte[] raw = Wire.Read(pipe, Wire.MaxRequest);
                    if (raw == null)
                        break;
                    Response response;
                    try
                    {
                        var request = Json.FromBytes<Request>(raw);
                        response = _handler.Handle(request);
                    }
                    catch (KeepsakeException ex)
                    {
                        response = new Response { Ok = false, Error = new ApiError { Code = ErrorCodes.Invalid, Message = "Malformed request: " + ex.Message } };
                    }
                    Wire.Write(pipe, Json.ToBytes(response));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // Client went away.
            }
            finally
            {
                lock (_lock)
                    _connections.Remove(pipe);
                pipe.Dispose();
            }
        }

        private const int MaxConnections = 16;

        private int ConnectionCount()
        {
            lock (_lock)
                return _connections.Count;
        }

        /// <summary>
        /// Creates one server instance. When no instance of ours exists, the name is claimed with
        /// FILE_FLAG_FIRST_PIPE_INSTANCE: if another process already owns a pipe with this name, creation fails
        /// instead of silently joining it, so a squatter cannot share the name with the service (SEC-005).
        /// Remote clients are rejected by the pipe itself, in addition to the NETWORK deny entry.
        /// </summary>
        private NamedPipeServerStream CreateInstance(bool first)
        {
            byte[] sd = _security.GetSecurityDescriptorBinaryForm();
            var pinned = GCHandle.Alloc(sd, GCHandleType.Pinned);
            try
            {
                var sa = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = pinned.AddrOfPinnedObject() };
                uint openMode = PipeAccessDuplex | (first ? FileFlagFirstPipeInstance : 0);
                var handle = CreateNamedPipe(@"\\.\pipe\" + _name, openMode, PipeRejectRemoteClients, 255, 65536, 65536, 0, ref sa);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (first && (error == 5 || error == 231)) // access denied / pipe busy: the name belongs to someone else
                        throw new IOException("The pipe name is already in use by another process – possibly a program impersonating the Keepsake service.");
                    throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                }
                return new NamedPipeServerStream(PipeDirection.InOut, false, false, handle);
            }
            finally
            {
                pinned.Free();
            }
        }

        private const uint PipeAccessDuplex = 0x00000003;
        private const uint FileFlagFirstPipeInstance = 0x00080000;
        private const uint PipeRejectRemoteClients = 0x00000008; // byte mode, blocking

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
            uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes security);

        private static PipeSecurity CreateSecurity(SecurityIdentifier extraUser)
        {
            var security = new PipeSecurity();
            // Network logons must never reach the API, whatever else is allowed.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            // The process owner must be able to create further instances of the pipe.
            var self = WindowsIdentity.GetCurrent().User;
            if (self != null)
                security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
            if (extraUser != null)
                security.AddAccessRule(new PipeAccessRule(extraUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }
    }
}
