using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Keepsake.Core.Crypto;

namespace Keepsake.Core.Repo
{
    /// <summary>Plain-text <c>keepsake.json</c> at the repository root. Its presence marks a complete repository.</summary>
    public sealed class RepoHeader
    {
        public string Format { get; set; } = "keepsake-repository";
        public int Version { get; set; } = Repository.FormatVersion;
        public string Id { get; set; }
        public DateTime Created { get; set; }
        public string Program { get; set; }
    }

    /// <summary>Encrypted repository settings (<c>config</c>). Fixed at creation time.</summary>
    public sealed class RepoConfig
    {
        public int ChunkerMin { get; set; } = 512 * 1024;
        public int ChunkerAvg { get; set; } = 1024 * 1024;
        public int ChunkerMax { get; set; } = 8 * 1024 * 1024;
        public int PackSize { get; set; } = 16 * 1024 * 1024;
        public string Compression { get; set; } = "zstd";
    }

    /// <summary>Plain-text key slot (<c>keys/&lt;id&gt;</c>) wrapping the master key.</summary>
    public sealed class KeySlot
    {
        public int Version { get; set; } = 1;

        /// <summary><c>password</c> or <c>recovery</c>.</summary>
        public string Type { get; set; }

        public string Label { get; set; }
        public DateTime Created { get; set; }
        public string Host { get; set; }

        /// <summary>Argon2id parameters (password slots).</summary>
        public KdfParams Kdf { get; set; }

        /// <summary>HKDF salt, base64 (recovery slots).</summary>
        public string Salt { get; set; }

        /// <summary>AES-256-GCM sealed <see cref="KeyPayload"/>, base64.</summary>
        public string Data { get; set; }
    }

    public sealed class KeyPayload
    {
        public string MasterKey { get; set; }
    }

    public sealed class KeySlotInfo
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Label { get; set; }
        public DateTime Created { get; set; }
        public string Host { get; set; }
        public bool Current { get; set; }
    }

    public enum BlobType : byte
    {
        Data = 0,
        Tree = 1,
    }

    // ---- Index -------------------------------------------------------------------------------

    public sealed class IndexFile
    {
        public List<IndexPack> Packs { get; set; } = new List<IndexPack>();
    }

    public sealed class IndexPack
    {
        public string Id { get; set; }
        public List<IndexBlob> Blobs { get; set; } = new List<IndexBlob>();
    }

    public sealed class IndexBlob
    {
        public string Id { get; set; }
        public BlobType Type { get; set; }
        public long Offset { get; set; }
        public int Length { get; set; }
        public int RawLength { get; set; }
    }

    // ---- Trees -------------------------------------------------------------------------------

    public enum NodeType
    {
        File,
        Dir,
    }

    /// <summary>A file or folder inside a <see cref="Tree"/>. Times are UTC ticks (100 ns since 0001-01-01).</summary>
    public sealed class Node
    {
        public string Name { get; set; }
        public NodeType Type { get; set; }
        public long Size { get; set; }
        public long MTime { get; set; }
        public long CTime { get; set; }
        public int Attributes { get; set; }

        /// <summary>Data blob IDs in file order (files only).</summary>
        public List<string> Content { get; set; }

        /// <summary>Tree blob ID of the folder contents (folders only).</summary>
        public string Subtree { get; set; }

        [JsonIgnore] public DateTime ModifiedUtc => new DateTime(MTime, DateTimeKind.Utc);
        [JsonIgnore] public DateTime CreatedUtc => new DateTime(CTime, DateTimeKind.Utc);
    }

    /// <summary>Folder listing, sorted by name (ordinal). Stored as a tree blob; its ID is the HMAC of the JSON.</summary>
    public sealed class Tree
    {
        private Dictionary<string, Node> _byName;

        public List<Node> Nodes { get; set; } = new List<Node>();

        public Node Find(string name)
        {
            if (_byName == null)
            {
                var map = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in Nodes)
                    map[n.Name] = n;
                _byName = map;
            }
            return _byName.TryGetValue(name, out var node) ? node : null;
        }
    }

    // ---- Snapshots ---------------------------------------------------------------------------

    public enum BackupMode
    {
        Incremental,
        Full,
    }

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public DateTime Time { get; set; }
        public string Host { get; set; }
        public string User { get; set; }
        public string Job { get; set; }
        public List<string> Paths { get; set; } = new List<string>();
        public List<string> Excludes { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public BackupMode Mode { get; set; }
        public string Tree { get; set; }
        public string Parent { get; set; }
        public string Program { get; set; }
        public SnapshotSummary Summary { get; set; }
    }

    public sealed class SnapshotSummary
    {
        public long FilesNew { get; set; }
        public long FilesChanged { get; set; }
        public long FilesUnchanged { get; set; }
        public long FilesSkipped { get; set; }
        public long Dirs { get; set; }
        public long TotalBytes { get; set; }
        public long BytesRead { get; set; }
        public long BytesAdded { get; set; }
        public long BytesStored { get; set; }
        public double DurationSeconds { get; set; }

        [JsonIgnore] public long Files => FilesNew + FilesChanged + FilesUnchanged;
    }

    /// <summary>A snapshot together with its storage ID.</summary>
    public sealed class SnapshotRef
    {
        public SnapshotRef(string id, Snapshot snapshot)
        {
            Id = id;
            Snapshot = snapshot;
        }

        public string Id { get; }
        public string ShortId => Id.Substring(0, 8);
        public Snapshot Snapshot { get; }
    }

    // ---- Locks -------------------------------------------------------------------------------

    public sealed class LockInfo
    {
        public string Host { get; set; }
        public string User { get; set; }
        public int Pid { get; set; }
        public DateTime Time { get; set; }
        public bool Exclusive { get; set; }
        public string Purpose { get; set; }
    }
}
