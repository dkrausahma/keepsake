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

        /// <summary>
        /// Cipher preference: the AEAD ciphers first – ChaCha20-Poly1305 and AES-GCM are computed by BouncyCastle
        /// inside the process, independent of the Windows crypto providers, which differ on Server 2008 R2 (where the
        /// default AES-CTR + HMAC path failed with "MAC error"). Then AES-CTR with encrypt-then-MAC. Everything SSH.NET
        /// supports stays available as a fallback, so servers with only older algorithms still work.
        /// </summary>
        internal static readonly string[] CipherPreference =
        {
            "chacha20-poly1305@openssh.com", "aes256-gcm@openssh.com", "aes128-gcm@openssh.com",
            "aes256-ctr", "aes192-ctr", "aes128-ctr",
        };

        internal static readonly string[] MacPreference =
        {
            "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com",
            "hmac-sha2-256", "hmac-sha2-512", "hmac-sha1-etm@openssh.com", "hmac-sha1",
        };

        internal static void PreferPortableAlgorithms(ConnectionInfo info)
        {
            // AES-CTR through BouncyCastle instead of the Windows provider (fails on Server 2008 R2, see ManagedAesCtrCipher).
            foreach (var ctr in new[] { new { Name = "aes128-ctr", Bits = 128 }, new { Name = "aes192-ctr", Bits = 192 }, new { Name = "aes256-ctr", Bits = 256 } })
            {
                if (!info.Encryptions.ContainsKey(ctr.Name))
                    continue;
                int bits = ctr.Bits;
                info.Encryptions[ctr.Name] = new CipherInfo(bits, (key, iv) => ManagedAesCtrCipher.Create(bits, key, iv), false);
            }
            // HMACs through BouncyCastle as well: the remaining Windows-dependent part on Server 2008 R2 (see ManagedHmac).
            foreach (string name in info.HmacAlgorithms.Keys.ToList())
            {
                var digest = ManagedHmac.DigestFor(name);
                if (digest == null)
                    continue;
                var old = info.HmacAlgorithms[name];
                info.HmacAlgorithms[name] = new HashInfo(old.KeySize, key => new ManagedHmac(digest, key), old.IsEncryptThenMAC);
            }
            Reorder(info.Encryptions, CipherPreference);
            Reorder(info.HmacAlgorithms, MacPreference);
        }

        private static void Reorder<T>(IDictionary<string, T> algorithms, string[] preferred)
        {
            var all = algorithms.ToList();
            var ordered = preferred.Select(name => all.FirstOrDefault(a => a.Key == name)).Where(a => a.Key != null)
                .Concat(all.Where(a => !preferred.Contains(a.Key)))
                .ToList();
            algorithms.Clear();
            foreach (var a in ordered)
                algorithms.Add(a.Key, a.Value);
        }

        /// <summary>
        /// Algorithm combinations tried in turn when a connection fails with "MAC error" – the two sides derived
        /// different keys or MACs. Seen with .NET Framework 4.8 on Windows Server 2008 R2 against servers that work
        /// from Windows 10/11; which algorithm is affected depends on the server. The first combination that works
        /// is remembered per server for the lifetime of the process. Null = keep the full preference list.
        /// </summary>
        internal static readonly AlgorithmProfile[] FallbackProfiles =
        {
            new AlgorithmProfile("preferred", null, null, null),
            new AlgorithmProfile("curve25519", new[] { "curve25519-sha256", "curve25519-sha256@libssh.org" }, null, null),
            new AlgorithmProfile("dh-group14", new[] { "diffie-hellman-group14-sha256" }, null, null),
            new AlgorithmProfile("dh-gex", new[] { "diffie-hellman-group-exchange-sha256" }, null, null),
            new AlgorithmProfile("dh-group16", new[] { "diffie-hellman-group16-sha512" }, null, null),
            new AlgorithmProfile("ecdh-p256", new[] { "ecdh-sha2-nistp256" }, null, null),
            new AlgorithmProfile("aes-ctr + hmac", null, new[] { "aes256-ctr", "aes128-ctr" }, new[] { "hmac-sha2-256", "hmac-sha2-512" }),
            new AlgorithmProfile("curve25519 + aes-ctr + hmac", new[] { "curve25519-sha256", "curve25519-sha256@libssh.org" }, new[] { "aes256-ctr", "aes128-ctr" }, new[] { "hmac-sha2-256", "hmac-sha2-512" }),
            new AlgorithmProfile("dh-group14 + aes-ctr + hmac", new[] { "diffie-hellman-group14-sha256" }, new[] { "aes256-ctr", "aes128-ctr" }, new[] { "hmac-sha2-256", "hmac-sha2-512" }),
            new AlgorithmProfile("aes-gcm", null, new[] { "aes256-gcm@openssh.com", "aes128-gcm@openssh.com" }, null),
            new AlgorithmProfile("aes-ctr + hmac-etm", null, new[] { "aes256-ctr", "aes128-ctr" }, new[] { "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com" }),
        };

        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AlgorithmProfile> WorkingProfile =
            new System.Collections.Concurrent.ConcurrentDictionary<string, AlgorithmProfile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The algorithms of the current connection (for logs and diagnostics).</summary>
        public string NegotiatedAlgorithms { get; private set; }

        /// <summary>How strong the encryption of the current connection is, in words (for the destination check and run logs).</summary>
        public SftpSecurity Security { get; private set; }

        /// <summary>
        /// Rates what was negotiated. Keepsake offers the same preference list on every Windows version, so the result
        /// depends on what the server supports: "strong" = authenticated encryption (AES-GCM, ChaCha20-Poly1305) with a
        /// modern key exchange; "good" = AES-CTR with HMAC-SHA2; "weak" = CBC, 3DES or SHA-1.
        /// </summary>
        internal static SftpSecurity Rate(ConnectionInfo info, string host)
        {
            string cipher = info.CurrentClientEncryption ?? "";
            string mac = info.CurrentClientHmacAlgorithm ?? "";
            string kex = info.CurrentKeyExchangeAlgorithm ?? "";
            bool aead = cipher.Contains("gcm") || cipher.Contains("chacha20");
            bool weak = cipher.Contains("cbc") || cipher.Contains("3des") || (!aead && mac.StartsWith("hmac-sha1", StringComparison.Ordinal)) ||
                        kex.EndsWith("sha1", StringComparison.Ordinal);
            string level = weak ? "weak" : aead ? "strong" : "good";
            string text = (aead ? cipher + " (authenticated encryption)" : cipher + " + " + mac) + ", key exchange " + kex + " – " + level;
            string note = null;
            if (!aead)
            {
                note = "The server offers no authenticated encryption (AES-GCM or ChaCha20-Poly1305); Keepsake uses the strongest it offers.";
                if ((info.ServerVersion ?? "").IndexOf("mod_sftp", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (host ?? "").EndsWith(".your-storagebox.de", StringComparison.OrdinalIgnoreCase))
                    note += " Hetzner Storage Box: use port 23 (OpenSSH), which offers AES-GCM; port 22 is ProFTPD mod_sftp.";
            }
            return new SftpSecurity { Level = level, Summary = text, Note = note, Details = Describe(info) };
        }

        /// <summary>The server supports none of the algorithms a fallback combination allows.</summary>
        internal static bool IsNoMatch(Exception ex) =>
            ex is SshConnectionException && (ex.Message ?? "").StartsWith("No matching", StringComparison.OrdinalIgnoreCase);

        /// <summary>Tests only: makes a connection with the given combination behave as if it failed with "MAC error".</summary>
        internal static Func<AlgorithmProfile, bool> SimulateMacError;

        internal static bool IsMacError(Exception ex) =>
            ex is SshConnectionException sce && (sce.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.MacError ||
                                                 (sce.Message ?? "").IndexOf("MAC", StringComparison.Ordinal) >= 0);

        internal static List<AuthenticationMethod> AuthMethods(SftpOptions options)
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(options.PrivateKey))
            {
                try
                {
                    var key = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(options.PrivateKey)), options.KeyPassphrase);
                    methods.Add(new PrivateKeyAuthenticationMethod(options.User, key));
                }
                catch (SshException ex)
                {
                    throw new KeepsakeException("The SFTP private key cannot be read: " + ex.Message +
                                                (string.IsNullOrEmpty(options.KeyPassphrase) ? " (does it need a passphrase?)" : ""));
                }
            }
            if (!string.IsNullOrEmpty(options.Password))
                methods.Add(new PasswordAuthenticationMethod(options.User, options.Password));
            if (methods.Count == 0)
                throw new KeepsakeException("No SFTP password or private key configured.");
            return methods;
        }

        /// <summary>Exception filter helper: writes the failure to the debug log, never catches.</summary>
        private bool Log(AlgorithmProfile profile, ConnectionInfo info, Exception ex)
        {
            SftpDebugLog.Write("FAILED " + _options.Host + ":" + _options.Port + " profile '" + profile.Name + "' (negotiated so far: kex " + info.CurrentKeyExchangeAlgorithm +
                               ", cipher " + info.CurrentClientEncryption + "/" + info.CurrentServerEncryption + ", mac " + info.CurrentClientHmacAlgorithm + "/" +
                               info.CurrentServerHmacAlgorithm + ", host key " + info.CurrentHostKeyAlgorithm + ", server " + info.ServerVersion + "): " +
                               ex.GetType().FullName + (ex is SshConnectionException sce ? " [" + sce.DisconnectReason + "]" : "") + ": " + ex.Message +
                               Environment.NewLine + ex);
            return false;
        }

        internal static ConnectionInfo NewConnectionInfo(SftpOptions options, List<AuthenticationMethod> methods, AlgorithmProfile profile)
        {
            var info = new ConnectionInfo(options.Host, options.Port, options.User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(30) };
            PreferPortableAlgorithms(info);
            profile?.Apply(info);
            return info;
        }

        internal static string Describe(ConnectionInfo info) =>
            "kex " + info.CurrentKeyExchangeAlgorithm + ", cipher " + info.CurrentClientEncryption +
            (string.IsNullOrEmpty(info.CurrentClientHmacAlgorithm) ? "" : ", mac " + info.CurrentClientHmacAlgorithm) +
            ", host key " + info.CurrentHostKeyAlgorithm + ", server " + info.ServerVersion;

        private SftpClient Client()
        {
            lock (_connectLock)
            {
                if (_client != null && _client.IsConnected)
                    return _client;
                _client?.Dispose();
                _client = null;

                var methods = AuthMethods(_options);
                string server = _options.Host + ":" + _options.Port;
                // The full preference list (strongest first) is always tried first, so a server that gained stronger
                // algorithms – or a computer where they work – gets them. A combination that worked after a MAC error
                // is only moved up among the fallbacks.
                var profiles = new List<AlgorithmProfile> { FallbackProfiles[0] };
                if (WorkingProfile.TryGetValue(server, out var known) && known != FallbackProfiles[0])
                    profiles.Add(known);
                profiles.AddRange(FallbackProfiles.Skip(1).Where(f => !profiles.Contains(f)));
                var macErrors = new List<string>();
                var notOffered = new List<string>();
                foreach (var profile in profiles)
                {
                    var info = NewConnectionInfo(_options, methods, profile);
                    if (!profile.IsPossible(info))
                        continue;
                    SftpDebugLog.Write("connect " + server + " profile '" + profile.Name + "': kex [" + string.Join(",", info.KeyExchangeAlgorithms.Keys) +
                                       "] ciphers [" + string.Join(",", info.Encryptions.Keys) + "] macs [" + string.Join(",", info.HmacAlgorithms.Keys) + "]");
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
                        if (SimulateMacError != null && SimulateMacError(profile))
                            throw new SshConnectionException("MAC error", Renci.SshNet.Messages.Transport.DisconnectReason.MacError);
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
                    catch (Exception ex) when (Log(profile, info, ex))
                    {
                        throw; // never reached: Log() records every failure and returns false, so the handlers below run
                    }
                    catch (Exception ex) when (IsMacError(ex))
                    {
                        // Keys or MACs computed differently on both sides: try the next algorithm combination.
                        client.Dispose();
                        macErrors.Add(profile.Name);
                        continue;
                    }
                    catch (Exception ex) when (macErrors.Count > 0 && IsNoMatch(ex))
                    {
                        // A fallback combination the server does not offer: try the next one.
                        client.Dispose();
                        notOffered.Add(profile.Name);
                        continue;
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
                    WorkingProfile[server] = profile;
                    SftpDebugLog.Write("connected " + server + " profile '" + profile.Name + "': " + Describe(info));
                    NegotiatedAlgorithms = Describe(info) + (macErrors.Count == 0 ? "" : " (after MAC errors with: " + string.Join(", ", macErrors) + ")");
                    Security = Rate(info, _options.Host);
                    _client = client;
                    return client;
                }
                throw new StorageException("Cannot connect to SFTP server " + _options.Host + ":" + _options.Port + ": MAC error with every algorithm combination the server supports (" +
                                           string.Join(", ", macErrors) + (notOffered.Count == 0 ? "" : "; not offered by the server: " + string.Join(", ", notOffered)) +
                                           "). Run 'keepsake sftp-test --repo sftp://...' on this computer and send the output.", null);
            }
        }
    }

    /// <summary>A restriction of the key exchange, cipher and MAC lists; null keeps the full list.</summary>
    internal sealed class AlgorithmProfile
    {
        public AlgorithmProfile(string name, string[] kex, string[] ciphers, string[] macs)
        {
            Name = name;
            Kex = kex;
            Ciphers = ciphers;
            Macs = macs;
        }

        public string Name { get; }
        public string[] Kex { get; }
        public string[] Ciphers { get; }
        public string[] Macs { get; }

        public void Apply(ConnectionInfo info)
        {
            Keep(info.KeyExchangeAlgorithms, Kex);
            Keep(info.Encryptions, Ciphers);
            Keep(info.HmacAlgorithms, Macs);
        }

        /// <summary>False when the client does not support any algorithm of a restricted list.</summary>
        public bool IsPossible(ConnectionInfo info) =>
            info.KeyExchangeAlgorithms.Count > 0 && info.Encryptions.Count > 0 && info.HmacAlgorithms.Count > 0;

        private static void Keep<T>(IDictionary<string, T> algorithms, string[] names)
        {
            if (names == null)
                return;
            var keep = names.Where(algorithms.ContainsKey).Select(n => new KeyValuePair<string, T>(n, algorithms[n])).ToList();
            algorithms.Clear();
            foreach (var a in keep)
                algorithms.Add(a.Key, a.Value);
        }
    }

    public sealed class SftpSecurity
    {
        /// <summary>"strong", "good" or "weak".</summary>
        public string Level { get; set; }
        public string Summary { get; set; }
        /// <summary>Why it is not "strong", and what would help; null when strong.</summary>
        public string Note { get; set; }
        public string Details { get; set; }
    }
}
