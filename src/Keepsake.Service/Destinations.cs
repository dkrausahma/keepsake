using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Keepsake.Core;
using Keepsake.Core.Storage;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>
    /// Connection to a network share with explicit credentials (DST-02, SRC-02). Lives only for the run:
    /// the service account itself never needs access to the share.
    /// </summary>
    internal sealed class ShareConnection : IDisposable
    {
        private const int ErrorSessionCredentialConflict = 1219;
        private const int ErrorAlreadyAssigned = 85;
        private readonly string _share;

        private ShareConnection(string share)
        {
            _share = share;
        }

        public static ShareConnection Connect(string share, string user, string password)
        {
            share = share.TrimEnd('\\');
            var resource = new NetResource { Type = 1, RemoteName = share };
            int rc = WNetAddConnection2(resource, password, user, 0);
            if (rc == ErrorSessionCredentialConflict)
            {
                // Another connection to this server exists with different credentials: replace it.
                WNetCancelConnection2(share, 0, true);
                rc = WNetAddConnection2(resource, password, user, 0);
            }
            if (rc != 0 && rc != ErrorAlreadyAssigned)
                throw new KeepsakeException("Cannot connect to " + share + " as '" + user + "': " + new Win32Exception(rc).Message);
            return new ShareConnection(share);
        }

        public void Dispose() => WNetCancelConnection2(_share, 0, true);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class NetResource
        {
            public int Scope;
            public int Type;
            public int DisplayType;
            public int Usage;
            public string LocalName;
            public string RemoteName;
            public string Comment;
            public string Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(NetResource resource, string password, string user, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);
    }

    /// <summary>An opened destination: the storage plus the share connections it needs.</summary>
    internal sealed class OpenedDestination : IDisposable
    {
        private readonly List<ShareConnection> _connections;

        public OpenedDestination(IStorage storage, List<ShareConnection> connections)
        {
            Storage = storage;
            _connections = connections;
        }

        public IStorage Storage { get; }

        public void Dispose()
        {
            Storage?.Dispose();
            foreach (var c in _connections)
            {
                try
                {
                    c.Dispose();
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn("Could not disconnect a network share: " + ex.Message);
                }
            }
        }
    }

    /// <summary>A repository together with the destination connections it depends on.</summary>
    internal sealed class OpenedRepository : IDisposable
    {
        private readonly OpenedDestination _destination;

        public OpenedRepository(Core.Repo.Repository repository, OpenedDestination destination)
        {
            Repository = repository;
            _destination = destination;
        }

        public Core.Repo.Repository Repository { get; }

        public void Dispose()
        {
            Repository.Dispose();
            _destination.Dispose();
        }
    }

    /// <summary>Turns a job destination into a storage: folder / UNC (with credentials), USB disk by volume ID, SFTP.</summary>
    internal static class DestinationResolver
    {
        /// <param name="volumeId">USB only: use this disk instead of the first connected one.</param>
        public static OpenedDestination Open(Destination destination, IEnumerable<ShareCredential> shares, ResolvedSecrets secrets, string volumeId = null)
        {
            var connections = new List<ShareConnection>();
            try
            {
                foreach (var c in shares ?? Enumerable.Empty<ShareCredential>())
                {
                    if (secrets.SharePasswords.TryGetValue(c.Share.TrimEnd('\\'), out string pw))
                        connections.Add(ShareConnection.Connect(c.Share, c.User, pw));
                }
                return new OpenedDestination(OpenStorage(destination, secrets, volumeId), connections);
            }
            catch
            {
                foreach (var c in connections)
                    c.Dispose();
                throw;
            }
        }

        private static IStorage OpenStorage(Destination d, ResolvedSecrets secrets, string volumeId)
        {
            switch (d.Type)
            {
                case DestinationTypes.Folder:
                    return new FileSystemStorage(d.Path);

                case DestinationTypes.Usb:
                {
                    var candidates = volumeId != null
                        ? d.Volumes.Where(v => string.Equals(v.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase)).ToList()
                        : d.Volumes;
                    var mounted = LocalVolumes.List();
                    foreach (var v in candidates)
                    {
                        var hit = mounted.FirstOrDefault(m => string.Equals(m.VolumeId, v.VolumeId, StringComparison.OrdinalIgnoreCase));
                        if (hit != null)
                            return new FileSystemStorage(System.IO.Path.Combine(hit.Drive, d.Path.TrimStart('\\')));
                    }
                    throw new KeepsakeException("None of the USB disks of this job is connected (" +
                                                string.Join(", ", candidates.Select(v => string.IsNullOrEmpty(v.Label) ? v.VolumeId : v.Label)) + ").");
                }

                case DestinationTypes.Sftp:
                {
                    var options = new SftpOptions
                    {
                        Host = d.Host,
                        Port = d.Port,
                        User = d.User,
                        Path = d.Path,
                        HostKeyFingerprint = d.HostKeyFingerprint,
                    };
                    if (d.Auth == "key")
                    {
                        options.PrivateKey = secrets.SftpKey ?? throw new KeepsakeException("No SFTP private key is stored for this job.");
                        options.KeyPassphrase = secrets.SftpPassphrase;
                    }
                    else
                    {
                        options.Password = secrets.SftpPassword ?? throw new KeepsakeException("No SFTP password is stored for this job.");
                    }
                    var storage = new SftpStorage(options);
                    try
                    {
                        storage.Connect();
                    }
                    catch
                    {
                        storage.Dispose();
                        throw;
                    }
                    return storage;
                }

                default:
                    throw new KeepsakeException("Unknown destination type '" + d.Type + "'.");
            }
        }
    }
}
