using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Keepsake.Core.Crypto;
using Keepsake.Core.Repo;

namespace Keepsake.Core.Backup
{
    /// <summary>
    /// Deduplicates, compresses, encrypts and packs blobs, and uploads finished packs on a background
    /// thread. Packs reach the index only after they are stored, and index files are written every
    /// 20 packs or 5 minutes, so an interrupted backup leaves reusable data behind (resume).
    /// </summary>
    internal sealed class BlobWriter : IDisposable
    {
        private const int IndexFlushPacks = 20;
        private static readonly TimeSpan IndexFlushInterval = TimeSpan.FromMinutes(5);

        private readonly Repository _repo;
        private readonly object _lock = new object();
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);
        private readonly BlockingCollection<PackBuilder> _queue = new BlockingCollection<PackBuilder>(2);
        private readonly List<IndexPack> _unindexed = new List<IndexPack>();
        private readonly Thread _uploader;
        private PackBuilder _dataPack;
        private PackBuilder _treePack;
        private DateTime _lastIndexWrite = DateTime.UtcNow;
        private volatile ExceptionDispatchInfo _failure;
        private bool _finished;

        private long _blobsAdded;
        private long _bytesAdded;
        private long _bytesStored;
        private long _packsWritten;

        public BlobWriter(Repository repo)
        {
            _repo = repo;
            _uploader = new Thread(UploadLoop) { IsBackground = true, Name = "Keepsake pack upload" };
            _uploader.Start();
        }

        public long BlobsAdded => Interlocked.Read(ref _blobsAdded);

        /// <summary>Plaintext bytes of new (not deduplicated) blobs.</summary>
        public long BytesAdded => Interlocked.Read(ref _bytesAdded);

        /// <summary>Bytes written to storage (compressed and encrypted packs).</summary>
        public long BytesStored => Interlocked.Read(ref _bytesStored);

        public long PacksWritten => Interlocked.Read(ref _packsWritten);

        /// <summary>True when the blob is already stored or on its way.</summary>
        public bool IsKnown(string id)
        {
            lock (_lock)
            {
                if (_pending.Contains(id))
                    return true;
            }
            return _repo.Index.Contains(id);
        }

        /// <summary>Adds a blob unless it already exists. Returns true when it was new.</summary>
        public bool Add(BlobType type, string id, byte[] buffer, int offset, int count, bool compress)
        {
            ThrowIfFailed();
            lock (_lock)
            {
                if (_pending.Contains(id) || _repo.Index.Contains(id))
                    return false;
                _pending.Add(id);
            }

            // CPU-heavy work runs outside the lock, in parallel across workers.
            byte[] plain = BlobCodec.Encode(buffer, offset, count, compress);
            byte[] sealedBlob = Aead.Seal(_repo.Keys.Encryption, plain, null);

            PackBuilder full = null;
            lock (_lock)
            {
                PackBuilder pack;
                if (type == BlobType.Data)
                    pack = _dataPack ?? (_dataPack = NewPack());
                else
                    pack = _treePack ?? (_treePack = NewPack());

                pack.Add(type, id, sealedBlob, count);
                if (pack.Size >= _repo.Config.PackSize)
                {
                    full = pack;
                    if (type == BlobType.Data)
                        _dataPack = null;
                    else
                        _treePack = null;
                }
            }
            Interlocked.Increment(ref _blobsAdded);
            Interlocked.Add(ref _bytesAdded, count);
            if (full != null)
                Enqueue(full);
            return true;
        }

        /// <summary>Uploads all remaining packs and writes the final index file.</summary>
        public void Flush()
        {
            ThrowIfFailed();
            CompleteUploads();
            ThrowIfFailed();
            WriteIndex(force: true);
        }

        /// <summary>
        /// After a cancellation or error: store whatever is complete so the next run can reuse it.
        /// Never throws.
        /// </summary>
        public void FlushBestEffort()
        {
            try
            {
                CompleteUploads();
            }
            catch (Exception)
            {
                // Upload already failed; the remaining index is still written below.
            }
            try
            {
                WriteIndex(force: true);
            }
            catch (Exception)
            {
                // Storage unavailable: those packs are re-uploaded by the next run.
            }
        }

        public void Dispose()
        {
            if (!_finished)
            {
                _queue.CompleteAdding();
                _uploader.Join();
                _finished = true;
            }
            _queue.Dispose();
        }

        private PackBuilder NewPack() => new PackBuilder(_repo.Config.PackSize + 1024 * 1024);

        private void CompleteUploads()
        {
            if (_finished)
                return;
            PackBuilder data, tree;
            lock (_lock)
            {
                data = _dataPack;
                tree = _treePack;
                _dataPack = null;
                _treePack = null;
            }
            if (data != null && _failure == null)
                Enqueue(data);
            if (tree != null && _failure == null)
                Enqueue(tree);
            _queue.CompleteAdding();
            _uploader.Join();
            _finished = true;
        }

        private void Enqueue(PackBuilder pack)
        {
            while (!_queue.TryAdd(pack, 500))
                ThrowIfFailed();
        }

        private void UploadLoop()
        {
            try
            {
                foreach (var builder in _queue.GetConsumingEnumerable())
                {
                    if (_failure != null)
                        continue;
                    var pack = builder.Finish(_repo.Keys.Encryption);
                    _repo.Storage.Write(Layout.PackPath(pack.Id), pack.Data);

                    var indexPack = new IndexPack
                    {
                        Id = pack.Id,
                        Blobs = pack.Blobs.Select(b => new IndexBlob { Id = b.Id, Type = b.Type, Offset = b.Offset, Length = b.Length, RawLength = b.RawLength }).ToList(),
                    };
                    // Index first, then clear pending: IsKnown() stays true throughout.
                    _repo.Index.Add(indexPack);
                    lock (_lock)
                    {
                        foreach (var b in pack.Blobs)
                            _pending.Remove(b.Id);
                        _unindexed.Add(indexPack);
                    }
                    Interlocked.Add(ref _bytesStored, pack.Data.Length);
                    Interlocked.Increment(ref _packsWritten);
                    WriteIndex(force: false);
                }
            }
            catch (Exception ex)
            {
                _failure = ExceptionDispatchInfo.Capture(ex);
                // Keep draining so producers blocked in Enqueue() wake up and see the failure.
                foreach (var _ in _queue.GetConsumingEnumerable())
                {
                }
            }
        }

        private void WriteIndex(bool force)
        {
            List<IndexPack> batch;
            lock (_lock)
            {
                if (_unindexed.Count == 0)
                    return;
                if (!force && _unindexed.Count < IndexFlushPacks && DateTime.UtcNow - _lastIndexWrite < IndexFlushInterval)
                    return;
                batch = _unindexed.ToList();
                _unindexed.Clear();
                _lastIndexWrite = DateTime.UtcNow;
            }
            try
            {
                _repo.SaveIndex(new IndexFile { Packs = batch });
            }
            catch
            {
                lock (_lock)
                    _unindexed.InsertRange(0, batch);
                throw;
            }
        }

        private void ThrowIfFailed() => _failure?.Throw();
    }
}
