using System;
using System.Collections.Generic;
using System.Linq;

namespace Keepsake.Core.Repo
{
    public sealed class BlobLocation
    {
        public string PackId { get; set; }
        public BlobType Type { get; set; }
        public long Offset { get; set; }
        public int Length { get; set; }
        public int RawLength { get; set; }
    }

    /// <summary>In-memory union of all index files: blob ID → pack and position. Thread-safe.</summary>
    public sealed class MasterIndex
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, BlobLocation> _blobs = new Dictionary<string, BlobLocation>(StringComparer.Ordinal);
        private readonly HashSet<string> _packs = new HashSet<string>(StringComparer.Ordinal);

        public bool Loaded { get; internal set; }

        public int BlobCount
        {
            get { lock (_lock) return _blobs.Count; }
        }

        public int PackCount
        {
            get { lock (_lock) return _packs.Count; }
        }

        public void Add(IndexPack pack)
        {
            lock (_lock)
            {
                _packs.Add(pack.Id);
                foreach (var b in pack.Blobs)
                {
                    if (!_blobs.ContainsKey(b.Id))
                        _blobs[b.Id] = new BlobLocation { PackId = pack.Id, Type = b.Type, Offset = b.Offset, Length = b.Length, RawLength = b.RawLength };
                }
            }
        }

        public void Add(IndexFile file)
        {
            foreach (var p in file.Packs)
                Add(p);
        }

        public bool Contains(string blobId)
        {
            lock (_lock)
                return _blobs.ContainsKey(blobId);
        }

        public bool TryGet(string blobId, out BlobLocation location)
        {
            lock (_lock)
                return _blobs.TryGetValue(blobId, out location);
        }

        public bool ContainsPack(string packId)
        {
            lock (_lock)
                return _packs.Contains(packId);
        }

        public List<string> PackIds()
        {
            lock (_lock)
                return _packs.ToList();
        }

        /// <summary>Indexed blobs grouped by pack, each group in offset order.</summary>
        public Dictionary<string, List<KeyValuePair<string, BlobLocation>>> GroupByPack()
        {
            lock (_lock)
            {
                return _blobs
                    .GroupBy(kv => kv.Value.PackId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Value.Offset).ToList(), StringComparer.Ordinal);
            }
        }
    }
}
