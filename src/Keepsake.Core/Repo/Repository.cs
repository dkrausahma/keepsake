using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Keepsake.Core.Crypto;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;

namespace Keepsake.Core.Repo
{
    public sealed class InitResult
    {
        public InitResult(Repository repository, string recoveryKey)
        {
            Repository = repository;
            RecoveryKey = recoveryKey;
        }

        public Repository Repository { get; }

        /// <summary>The recovery key in <c>KS1-...</c> text form. Shown once; not stored anywhere in plain text.</summary>
        public string RecoveryKey { get; }
    }

    /// <summary>
    /// An opened Keepsake repository: header, keys, config and (once loaded) the master index.
    /// Everything except <c>keepsake.json</c>, <c>README.txt</c> and the key slots is encrypted.
    /// </summary>
    public sealed class Repository : IDisposable
    {
        public const int FormatVersion = 1;
        private const byte FileFormat = 1;
        private const string PasswordSlot = "password";
        private const string RecoverySlot = "recovery";

        private Repository(IStorage storage, RepoHeader header, RepoKeys keys)
        {
            Storage = storage;
            Header = header;
            Keys = keys;
        }

        public IStorage Storage { get; }
        public RepoHeader Header { get; }
        public RepoKeys Keys { get; }
        public RepoConfig Config { get; private set; }
        public MasterIndex Index { get; } = new MasterIndex();

        /// <summary>Key slot that unlocked this session.</summary>
        public string KeyId { get; private set; }

        public static bool Exists(IStorage storage) => storage.Exists(Layout.Header);

        // ---- Create / open -----------------------------------------------------------------

        public static InitResult Init(IStorage storage, string password, RepoConfig config = null)
        {
            if (string.IsNullOrEmpty(password))
                throw new KeepsakeException("A password is required to create a repository.");
            if (Exists(storage))
                throw new KeepsakeException("A Keepsake repository already exists at " + storage.Location + ".");

            var header = new RepoHeader
            {
                Id = Rand.HexId(16),
                Created = DateTime.UtcNow,
                Program = ProgramInfo.Name,
            };
            byte[] master = Rand.Bytes(32);
            var repo = new Repository(storage, header, new RepoKeys(master, Hex.Decode(header.Id)))
            {
                Config = config ?? new RepoConfig(),
            };

            repo.KeyId = repo.AddPasswordKey(password, "Initial password");
            byte[] recovery = RecoveryKey.Generate();
            repo.WriteRecoverySlot(recovery, "Recovery key");
            repo.WriteFixedObject(Layout.Config, "config", repo.Config);
            storage.Write(Layout.Readme, Encoding.UTF8.GetBytes(ReadmeText(header)));
            // The header goes last: a repository without it is incomplete and is never opened.
            storage.Write(Layout.Header, Json.ToBytes(header, indented: true));
            repo.Index.Loaded = true;
            return new InitResult(repo, RecoveryKey.Format(recovery));
        }

        public static Repository Open(IStorage storage, RepoCredential credential)
        {
            if (!Exists(storage))
                throw new KeepsakeException("No Keepsake repository found at " + storage.Location + ".");
            var header = Json.FromBytes<RepoHeader>(storage.Read(Layout.Header));
            if (header.Format != "keepsake-repository")
                throw new RepositoryFormatException(storage.Location + " is not a Keepsake repository.");
            if (header.Version > FormatVersion)
                throw new RepositoryFormatException("The repository uses format version " + header.Version +
                    ", which is newer than this program supports (" + FormatVersion + "). Please update Keepsake.");

            foreach (var entry in storage.List(Layout.Keys))
            {
                KeySlot slot;
                try
                {
                    slot = Json.FromBytes<KeySlot>(storage.Read(entry.Path));
                }
                catch (RepositoryFormatException)
                {
                    continue;
                }
                byte[] master = Unlock(slot, credential, header.Id);
                if (master == null)
                    continue;

                var repo = new Repository(storage, header, new RepoKeys(master, Hex.Decode(header.Id))) { KeyId = entry.Name };
                repo.Config = repo.ReadFixedObject<RepoConfig>(Layout.Config, "config");
                return repo;
            }
            throw new WrongCredentialException(credential.Password != null
                ? "The password is not valid for this repository."
                : "The recovery key is not valid for this repository.");
        }

        private static byte[] Unlock(KeySlot slot, RepoCredential credential, string repoId)
        {
            byte[] kek;
            if (slot.Type == PasswordSlot && credential.Password != null && slot.Kdf != null)
                kek = PasswordKdf.Derive(credential.Password, slot.Kdf);
            else if (slot.Type == RecoverySlot && credential.RecoveryKey != null && slot.Salt != null)
                kek = Hkdf.DeriveKey(credential.RecoveryKey, Convert.FromBase64String(slot.Salt), "keepsake/v1/recovery-kek");
            else
                return null;

            try
            {
                byte[] plain = Aead.Open(kek, Convert.FromBase64String(slot.Data), Aead.Context("key/" + repoId));
                return Convert.FromBase64String(Json.FromBytes<KeyPayload>(plain).MasterKey);
            }
            catch (CryptoFailureException)
            {
                return null;
            }
        }

        // ---- Key slots ---------------------------------------------------------------------

        public List<KeySlotInfo> ListKeys()
        {
            var result = new List<KeySlotInfo>();
            foreach (var entry in Storage.List(Layout.Keys))
            {
                var slot = Json.FromBytes<KeySlot>(Storage.Read(entry.Path));
                result.Add(new KeySlotInfo
                {
                    Id = entry.Name,
                    Type = slot.Type,
                    Label = slot.Label,
                    Created = slot.Created,
                    Host = slot.Host,
                    Current = entry.Name == KeyId,
                });
            }
            return result.OrderBy(k => k.Created).ToList();
        }

        public string AddPasswordKey(string password, string label)
        {
            if (string.IsNullOrEmpty(password))
                throw new KeepsakeException("The password must not be empty.");
            var kdf = PasswordKdf.NewParams();
            return WriteSlot(new KeySlot { Type = PasswordSlot, Kdf = kdf }, PasswordKdf.Derive(password, kdf), label);
        }

        /// <summary>Adds a new recovery key slot and returns the key in text form.</summary>
        public string AddRecoveryKey(string label, out string slotId)
        {
            byte[] key = RecoveryKey.Generate();
            slotId = WriteRecoverySlot(key, label);
            return RecoveryKey.Format(key);
        }

        public void RemoveKey(string slotId)
        {
            var keys = ListKeys();
            if (!keys.Any(k => k.Id == slotId))
                throw new KeepsakeException("Key '" + slotId + "' does not exist.");
            if (keys.Count <= 1)
                throw new KeepsakeException("The last key of a repository cannot be removed.");
            Storage.Delete(Layout.KeyPath(slotId));
        }

        private string WriteRecoverySlot(byte[] recoveryKey, string label)
        {
            byte[] salt = Rand.Bytes(16);
            var slot = new KeySlot { Type = RecoverySlot, Salt = Convert.ToBase64String(salt) };
            return WriteSlot(slot, Hkdf.DeriveKey(recoveryKey, salt, "keepsake/v1/recovery-kek"), label);
        }

        private string WriteSlot(KeySlot slot, byte[] kek, string label)
        {
            slot.Label = label;
            slot.Created = DateTime.UtcNow;
            slot.Host = ProgramInfo.Host;
            byte[] payload = Json.ToBytes(new KeyPayload { MasterKey = Convert.ToBase64String(Keys.Master) });
            slot.Data = Convert.ToBase64String(Aead.Seal(kek, payload, Aead.Context("key/" + Header.Id)));
            string id = Rand.HexId(16);
            Storage.Write(Layout.KeyPath(id), Json.ToBytes(slot, indented: true));
            return id;
        }

        // ---- Encrypted objects -------------------------------------------------------------

        /// <summary>File format: <c>0x01 || AES-GCM(codec(JSON))</c>, associated data = object kind.</summary>
        public byte[] EncryptObject<T>(T value, string kind)
        {
            byte[] plain = BlobCodec.Encode(Json.ToBytes(value), compress: true);
            byte[] sealedData = Aead.Seal(Keys.Encryption, plain, Aead.Context(kind));
            var file = new byte[sealedData.Length + 1];
            file[0] = FileFormat;
            Buffer.BlockCopy(sealedData, 0, file, 1, sealedData.Length);
            return file;
        }

        public T DecryptObject<T>(byte[] file, string kind)
        {
            if (file.Length < 1 || file[0] != FileFormat)
                throw new RepositoryFormatException("Unsupported " + kind + " file format.");
            byte[] plain = Aead.Open(Keys.Encryption, file, 1, file.Length - 1, Aead.Context(kind));
            return Json.FromBytes<T>(BlobCodec.Decode(plain));
        }

        /// <summary>Stores an object under the SHA-256 of its encrypted bytes; returns the ID.</summary>
        public string SaveObject<T>(string directory, string kind, T value)
        {
            byte[] file = EncryptObject(value, kind);
            string id = Sha256.HexOf(file);
            Storage.Write(directory + "/" + id, file);
            return id;
        }

        /// <summary>Loads a content-addressed object and verifies that its name matches its hash.</summary>
        public T LoadObject<T>(string path, string kind)
        {
            byte[] file = Storage.Read(path);
            string name = path.Substring(path.LastIndexOf('/') + 1);
            if (!string.Equals(Sha256.HexOf(file), name, StringComparison.Ordinal))
                throw new RepositoryCorruptException("File '" + path + "' is damaged (hash mismatch).");
            return DecryptObject<T>(file, kind);
        }

        private void WriteFixedObject<T>(string path, string kind, T value) => Storage.Write(path, EncryptObject(value, kind));

        private T ReadFixedObject<T>(string path, string kind) => DecryptObject<T>(Storage.Read(path), kind);

        // ---- Index -------------------------------------------------------------------------

        public void LoadIndex()
        {
            if (Index.Loaded)
                return;
            foreach (var entry in Storage.List(Layout.Index))
                Index.Add(LoadObject<IndexFile>(entry.Path, "index"));
            Index.Loaded = true;
        }

        public string SaveIndex(IndexFile file) => SaveObject(Layout.Index, "index", file);

        // ---- Blobs -------------------------------------------------------------------------

        public byte[] LoadBlob(string id)
        {
            if (!Index.TryGet(id, out var location))
                throw new RepositoryCorruptException("Data block " + id + " is missing from the repository index.");
            byte[] sealedBlob = Storage.ReadRange(Layout.PackPath(location.PackId), location.Offset, location.Length);
            return DecodeBlob(id, sealedBlob, 0, sealedBlob.Length);
        }

        /// <summary>Decrypts and decompresses a blob and verifies that its content matches its ID.</summary>
        public byte[] DecodeBlob(string id, byte[] data, int offset, int count)
        {
            byte[] raw;
            try
            {
                raw = BlobCodec.Decode(Aead.Open(Keys.Encryption, data, offset, count, null));
            }
            catch (CryptoFailureException)
            {
                throw new RepositoryCorruptException("Data block " + id + " is damaged (authentication failed).");
            }
            if (!string.Equals(Keys.ComputeId(raw), id, StringComparison.Ordinal))
                throw new RepositoryCorruptException("Data block " + id + " is damaged (content does not match its ID).");
            return raw;
        }

        public Tree LoadTree(string id) => Json.FromBytes<Tree>(LoadBlob(id));

        // ---- Snapshots ---------------------------------------------------------------------

        public string SaveSnapshot(Snapshot snapshot) => SaveObject(Layout.Snapshots, "snapshot", snapshot);

        public const string ProtectedTag = "protected";

        /// <summary>
        /// Snapshots are content-addressed, so changing one (e.g. its tags) writes a new file and removes the old one.
        /// Returns the new snapshot.
        /// </summary>
        public SnapshotRef UpdateSnapshot(SnapshotRef snapshot, Action<Snapshot> change)
        {
            var copy = Json.FromBytes<Snapshot>(Json.ToBytes(snapshot.Snapshot));
            change(copy);
            string id = SaveSnapshot(copy);
            if (id != snapshot.Id)
                Storage.Delete(Layout.SnapshotPath(snapshot.Id));
            return new SnapshotRef(id, copy);
        }

        public SnapshotRef SetProtected(SnapshotRef snapshot, bool isProtected) => UpdateSnapshot(snapshot, s =>
        {
            s.Tags = (s.Tags ?? new List<string>()).Where(t => !string.Equals(t, ProtectedTag, StringComparison.OrdinalIgnoreCase)).ToList();
            if (isProtected)
                s.Tags.Add(ProtectedTag);
        });

        public static bool IsProtected(Snapshot s) => s.Tags != null && s.Tags.Any(t => string.Equals(t, ProtectedTag, StringComparison.OrdinalIgnoreCase));

        /// <summary>All snapshots, oldest first.</summary>
        public List<SnapshotRef> ListSnapshots()
        {
            var result = new List<SnapshotRef>();
            foreach (var entry in Storage.List(Layout.Snapshots))
                result.Add(new SnapshotRef(entry.Name, LoadObject<Snapshot>(entry.Path, "snapshot")));
            return result.OrderBy(s => s.Snapshot.Time).ToList();
        }

        /// <summary>Resolves <c>latest</c>, a full ID or a unique ID prefix (at least 4 characters).</summary>
        public SnapshotRef FindSnapshot(string reference, string job = null)
        {
            var all = ListSnapshots();
            if (job != null)
                all = all.Where(s => string.Equals(s.Snapshot.Job, job, StringComparison.OrdinalIgnoreCase)).ToList();

            if (string.IsNullOrEmpty(reference) || string.Equals(reference, "latest", StringComparison.OrdinalIgnoreCase))
            {
                if (all.Count == 0)
                    throw new KeepsakeException(job == null ? "The repository contains no snapshots." : "There are no snapshots for job '" + job + "'.");
                return all[all.Count - 1];
            }

            if (reference.Length < 4 || !Hex.IsHex(reference))
                throw new KeepsakeException("'" + reference + "' is not a snapshot ID. Use 'latest' or at least 4 characters of an ID.");
            var matches = all.Where(s => s.Id.StartsWith(reference.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
                throw new KeepsakeException("No snapshot matches '" + reference + "'.");
            if (matches.Count > 1)
                throw new KeepsakeException("'" + reference + "' matches " + matches.Count + " snapshots. Use more characters.");
            return matches[0];
        }

        public void Dispose() => Storage.Dispose();

        private static string ReadmeText(RepoHeader header) =>
            "KEEPSAKE BACKUP REPOSITORY\r\n" +
            "==========================\r\n\r\n" +
            "This folder contains encrypted backups made with Keepsake.\r\n" +
            "Repository ID: " + header.Id + "\r\n" +
            "Created:       " + header.Created.ToString("yyyy-MM-dd HH:mm") + " UTC\r\n\r\n" +
            "Do not change, move or delete files inside this folder by hand.\r\n\r\n" +
            "HOW TO RESTORE ON ANOTHER COMPUTER\r\n" +
            "1. Copy keepsake.exe (with its DLL files) to the computer, or install Keepsake.\r\n" +
            "2. Make this folder reachable from that computer (USB disk, network share, ...).\r\n" +
            "3. Have the repository password or the recovery key (KS1-...) ready.\r\n" +
            "4. List the backups:\r\n" +
            "     keepsake snapshots --repo \"<this folder>\"\r\n" +
            "5. Restore everything from the newest backup into a new folder:\r\n" +
            "     keepsake restore latest --repo \"<this folder>\" --target \"D:\\Restore\"\r\n" +
            "   Use --recovery-key-file <file> instead of the password if needed.\r\n\r\n" +
            "The file format is documented in docs/FORMAT.md of the Keepsake source code.\r\n";
    }
}
