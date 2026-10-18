using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Keepsake.Core.Util;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Keepsake.Core.Storage
{
    public sealed class SftpOptions
    {
        public string Host { get; set; }
        public int Port { get; set; } = 22;
        public string User { get; set; }

        /// <summary>Remote repository folder, e.g. <c>/backups/fileserver</c>.</summary>
        public string Path { get; set; }

        public string Password { get; set; }

        /// <summary>Private key file content (OpenSSH or PuTTY format).</summary>
        public string PrivateKey { get; set; }

        public string KeyPassphrase { get; set; }

        /// <summary>Trusted host key as <c>SHA256:base64</c> (OpenSSH style). Null = unknown, connection is refused.</summary>
        public string HostKeyFingerprint { get; set; }

        public string Display => (User ?? "") + "@" + Host + (Port == 22 ? "" : ":" + Port) + "/" + (Path ?? "").TrimStart('/');
    }

    /// <summary>The server's host key is not (yet) trusted. Carries the fingerprint to show the user.</summary>
    public sealed class HostKeyException : KeepsakeException
    {
        public HostKeyException(string message, string fingerprint, bool mismatch) : base(message)
        {
            Fingerprint = fingerprint;
            Mismatch = mismatch;
        }

        public string Fingerprint { get; }

        /// <summary>True when a different key was pinned before (possible attack or reinstalled server).</summary>
        public bool Mismatch { get; }
    }

    /// <summary>
    /// Repository on an SFTP server (DST-05). The host key must be pinned; a changed key is refused.
    /// Writes go to a temporary name and are renamed into place.
    /// </summary>
    public sealed class SftpStorage : IStorage, IFreeSpace
    {
        private const string TempMarker = ".tmp-";
        private readonly SftpOptions _options;
        private readonly string _root;
        private readonly object _connectLock = new object();
        private SftpClient _client;
        private string _seenFingerprint;

        public SftpStorage(SftpOptions options)
        {
            _options = options;
            _root = "/" + (options.Path ?? "").Replace('\\', '/').Trim('/');
            Location = "sftp://" + options.Display;
        }

        public string Location { get; }

        /// <summary>Connects (also used to test credentials and the host key).</summary>
        public void Connect() => Client();

        public bool Exists(string path) => Run(c => c.Exists(Remote(path)), path);

        public long GetSize(string path) => Run(c => c.GetAttributes(Remote(path)).Size, path);

        public byte[] Read(string path) => Run(c =>
        {
            using (var ms = new MemoryStream())
            {
                c.DownloadFile(Remote(path), ms);
                return ms.ToArray();
            }
        }, path);

        public byte[] ReadRange(string path, long offset, int length) => Run(c =>
        {
            using (var stream = c.Open(Remote(path), FileMode.Open, FileAccess.Read))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(buffer, read, length - read);
                    if (n == 0)
                        throw new IOException("Unexpected end of file (wanted " + length + " bytes at offset " + offset + ").");
                    read += n;
                }
                return buffer;
            }
        }, path);

        public void Write(string path, byte[] data) => Run(c =>
        {
            string target = Remote(path);
            string dir = target.Substring(0, target.LastIndexOf('/'));
            EnsureDirectory(c, dir);
            string temp = target + TempMarker + Rand.HexId(6);
            using (var ms = new MemoryStream(data, false))
                c.UploadFile(ms, temp, canOverride: true);
            try
            {
                // POSIX rename replaces atomically on OpenSSH; plain SFTP rename fails if the target exists.
                c.RenameFile(temp, target, isPosix: true);
            }
            catch (SshException)
            {
                if (c.Exists(target))
                    c.DeleteFile(target);
                c.RenameFile(temp, target);
            }
            return true;
        }, path);

        public void Delete(string path) => Run(c =>
        {
            string remote = Remote(path);
            if (c.Exists(remote))
                c.DeleteFile(remote);
            return true;
        }, path);

        public IEnumerable<StorageEntry> List(string directory) => Run(c =>
        {
            var result = new List<StorageEntry>();
            string start = Remote(directory);
            if (!c.Exists(start))
                return result;
            // A malicious server could report endless or looping folders: the layout is at most two levels deep,
            // and names with path separators are ignored (SEC-009).
            var pending = new Stack<KeyValuePair<string, int>>();
            pending.Push(new KeyValuePair<string, int>(start, 0));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string dir = current.Key;
                foreach (ISftpFile f in c.ListDirectory(dir))
                {
                    if (f.Name == "." || f.Name == ".." || f.Name.IndexOf('/') >= 0 || f.Name.IndexOf('\\') >= 0)
                        continue;
                    if (result.Count >= MaxListEntries)
                        throw new StorageException("Listing " + directory + " on " + Location + " returned more than " + MaxListEntries + " entries.", null);
                    if (f.IsDirectory)
                    {
                        if (current.Value < 2)
                            pending.Push(new KeyValuePair<string, int>(f.FullName, current.Value + 1));
                    }
                    else if (f.IsRegularFile && f.Name.IndexOf(TempMarker, StringComparison.Ordinal) < 0)
                        result.Add(new StorageEntry(f.FullName.Substring(_root.Length + 1), f.Length));
                }
            }
            return result;
        }, directory);

        private const int MaxListEntries = 10000000;

        public long? FreeBytes()
        {
            try
            {
                return Run(c =>
                {
                    var stat = c.GetStatus(_root);
                    return (long?)(long)(stat.FreeBlocks * stat.BlockSize);
                }, "");
            }
            catch (KeepsakeException)
            {
                return null; // server without the statvfs extension
            }
        }

        public void Dispose()
        {
            lock (_connectLock)
            {
                if (_client != null)
                {
                    try
                    {
                        if (_client.IsConnected)
                            _client.Disconnect();
                    }
                    catch (SshException)
                    {
                    }
                    _client.Dispose();
                    _client = null;
                }
            }
        }

        private string Remote(string path) => string.IsNullOrEmpty(path) ? _root : _root + "/" + path.Replace('\\', '/').TrimStart('/');

        private static void EnsureDirectory(SftpClient c, string dir)
        {
            if (string.IsNullOrEmpty(dir) || dir == "/" || c.Exists(dir))
                return;
            EnsureDirectory(c, dir.Substring(0, Math.Max(0, dir.LastIndexOf('/'))));
            try
            {
                c.CreateDirectory(dir);
            }
            catch (SshException) when (c.Exists(dir))
            {
                // Created concurrently.
            }
        }

        /// <summary>Runs an operation; reconnects once if the session dropped.</summary>
        private T Run<T>(Func<SftpClient, T> operation, string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                var client = Client();
                try
                {
                    return operation(client);
                }
                catch (SshConnectionException) when (attempt == 0)
                {
                    Reset(client);
                }
                catch (SocketException) when (attempt == 0)
                {
                    Reset(client);
                }
                catch (SftpPathNotFoundException ex)
                {
                    throw new StorageException("'" + path + "' not found on " + Location + ".", ex);
                }
                catch (SftpPermissionDeniedException ex)
                {
                    throw new StorageException("Permission denied for '" + path + "' on " + Location + ".", ex);
                }
                catch (Exception ex) when (ex is SshException || ex is SocketException || ex is IOException)
                {
                    throw new StorageException("SFTP error for '" + path + "' on " + Location + ": " + ex.Message, ex);
                }
            }
        }

        private void Reset(SftpClient broken)
        {
            lock (_connectLock)
            {
                if (_client == broken)
                {
                    try
                    {
                        _client.Dispose();
                    }
                    catch (Exception)
                    {
                        // Already broken.
                    }
                    _client = null;
                }
            }
        }

        private SftpClient Client()
        {
            lock (_connectLock)
            {
                if (_client != null && _client.IsConnected)
                    return _client;
                _client?.Dispose();
                _client = null;

                var methods = new List<AuthenticationMethod>();
                if (!string.IsNullOrEmpty(_options.PrivateKey))
                {
                    try
                    {
                        var key = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_options.PrivateKey)), _options.KeyPassphrase);
                        methods.Add(new PrivateKeyAuthenticationMethod(_options.User, key));
                    }
                    catch (SshException ex)
                    {
                        throw new KeepsakeException("The SFTP private key cannot be read: " + ex.Message +
                                                    (string.IsNullOrEmpty(_options.KeyPassphrase) ? " (does it need a passphrase?)" : ""));
                    }
                }
                if (!string.IsNullOrEmpty(_options.Password))
                    methods.Add(new PasswordAuthenticationMethod(_options.User, _options.Password));
                if (methods.Count == 0)
                    throw new KeepsakeException("No SFTP password or private key configured.");

                var info = new ConnectionInfo(_options.Host, _options.Port, _options.User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(30) };
                var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(5) };
                _seenFingerprint = null;
                client.HostKeyReceived += (s, e) =>
                {
                    _seenFingerprint = "SHA256:" + e.FingerPrintSHA256;
                    e.CanTrust = string.Equals(_seenFingerprint, _options.HostKeyFingerprint, StringComparison.Ordinal);
                };
                try
                {
                    client.Connect();
                    client.KeepAliveInterval = TimeSpan.FromSeconds(30);
                }
                catch (SshConnectionException) when (_seenFingerprint != null && !string.Equals(_seenFingerprint, _options.HostKeyFingerprint, StringComparison.Ordinal))
                {
                    client.Dispose();
                    bool mismatch = !string.IsNullOrEmpty(_options.HostKeyFingerprint);
                    throw new HostKeyException(mismatch
                            ? "WARNING: the host key of " + _options.Host + " has CHANGED (now " + _seenFingerprint + ", trusted " + _options.HostKeyFingerprint +
                              "). This can mean the server was reinstalled – or that someone intercepts the connection. Verify with the server administrator."
                            : "The host key of " + _options.Host + " is not trusted yet: " + _seenFingerprint + ". Verify it and confirm to continue.",
                        _seenFingerprint, mismatch);
                }
                catch (SshAuthenticationException ex)
                {
                    client.Dispose();
                    throw new KeepsakeException("SFTP login to " + _options.Host + " as '" + _options.User + "' failed: " + ex.Message);
                }
                catch (Exception ex) when (ex is SshException || ex is SocketException || ex is TimeoutException)
                {
                    client.Dispose();
                    throw new StorageException("Cannot connect to SFTP server " + _options.Host + ":" + _options.Port + ": " + ex.Message, ex);
                }
                _client = client;
                return client;
            }
        }
    }
}
