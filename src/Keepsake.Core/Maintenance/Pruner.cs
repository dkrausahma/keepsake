using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;

namespace Keepsake.Core.Maintenance
{
    public sealed class PruneOptions
    {
        /// <summary>Retention to apply first ("forget"). Null or empty = only remove unused data.</summary>
        public RetentionPolicy Policy { get; set; }

        /// <summary>Apply the policy only to snapshots of this job (null = all snapshots).</summary>
        public string Job { get; set; }

        /// <summary>Repack packs whose share of unused data is at least this large.</summary>
        public double RepackThreshold { get; set; } = 0.25;

        public bool DryRun { get; set; }
    }

    public sealed class PruneResult : RunReport
    {
        public bool DryRun { get; set; }
        public int SnapshotsKept { get; set; }
        public List<string> SnapshotsRemoved { get; set; } = new List<string>();
        public int PacksDeleted { get; set; }
        public int PacksRepacked { get; set; }
        public int PacksWritten { get; set; }
        public long BytesFreed { get; set; }
        public long BytesRepacked { get; set; }
    }

    /// <summary>
    /// Forget + prune (RET-03). Runs under an exclusive lock. Order is crash-safe: new packs, then a complete
    /// new index, then removal of old index files, then removal of packs nobody references any more.
    /// </summary>
    public sealed class Pruner
    {
        private readonly Repository _repo;

        public Pruner(Repository repo)
        {
            _repo = repo;
        }

        public PruneResult Run(PruneOptions options, IProgress<string> progress, CancellationToken cancel)
        {
            var result = new PruneResult { Start = DateTime.UtcNow, DryRun = options.DryRun };
            var phases = new PhaseRecorder(result.Phases);
            var issues = new IssueLog(result.Issues);
            try
            {
                using (RepoLock.Acquire(_repo, exclusive: true, purpose: "prune"))
                {
                    // 1. Forget snapshots according to the retention policy.
                    List<SnapshotRef> remaining;
                    using (phases.Begin("Apply retention"))
                    {
                        progress?.Report("Applying retention rules");
                        var all = _repo.ListSnapshots();
                        var scoped = options.Job == null ? all : all.Where(s => string.Equals(s.Snapshot.Job, options.Job, StringComparison.OrdinalIgnoreCase)).ToList();
                        var decisions = Retention.Apply(scoped, options.Policy, DateTime.UtcNow);
                        var forget = decisions.Where(d => !d.Keep).Select(d => d.Snapshot).ToList();
                        foreach (var s in forget)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (!options.DryRun)
                                _repo.Storage.Delete(Layout.SnapshotPath(s.Id));
                            result.SnapshotsRemoved.Add(s.Id);
                        }
                        var forgotten = new HashSet<string>(forget.Select(s => s.Id));
                        remaining = all.Where(s => !forgotten.Contains(s.Id)).ToList();
                        result.SnapshotsKept = remaining.Count;
                    }

                    // 2. Which blobs are still referenced?
                    var index = new MasterIndex();
                    var indexFiles = new List<string>();
                    var used = new HashSet<string>(StringComparer.Ordinal);
                    using (phases.Begin("Find used data"))
                    {
                        progress?.Report("Reading index");
                        foreach (var entry in Layout.ListObjects(_repo.Storage, Layout.Index))
                        {
                            index.Add(_repo.LoadObject<IndexFile>(entry.Path, "index"));
                            indexFiles.Add(entry.Path);
                        }
                        // The repository object's index serves tree loading below.
                        _repo.LoadIndex();
                        foreach (var s in remaining)
                        {
                            cancel.ThrowIfCancellationRequested();
                            progress?.Report("Scanning snapshot " + s.ShortId);
                            MarkTree(s.Snapshot.Tree, used, cancel);
                        }
                    }

                    // 3. Classify packs.
                    var stored = Layout.ListObjects(_repo.Storage, Layout.Data).ToDictionary(e => e.Name, e => e.Size, StringComparer.Ordinal);
                    var byPack = index.GroupByPack();
                    var keep = new Dictionary<string, List<KeyValuePair<string, BlobLocation>>>(StringComparer.Ordinal);
                    var repack = new List<string>();
                    var delete = new List<string>();
                    foreach (var pack in stored)
                    {
                        if (!byPack.TryGetValue(pack.Key, out var blobs))
                        {
                            delete.Add(pack.Key); // not in any index: left over from an interrupted backup
                            continue;
                        }
                        var needed = blobs.Where(b => used.Contains(b.Key)).ToList();
                        if (needed.Count == 0)
                        {
                            delete.Add(pack.Key);
                            continue;
                        }
                        long neededBytes = needed.Sum(b => (long)b.Value.Length);
                        double unused = pack.Value <= 0 ? 0 : 1.0 - neededBytes / (double)pack.Value;
                        if (unused >= options.RepackThreshold)
                            repack.Add(pack.Key);
                        else
                            keep[pack.Key] = needed;
                    }
                    foreach (var missing in used.Where(id => !index.TryGet(id, out var loc) || !stored.ContainsKey(loc.PackId)).Take(20))
                        issues.Error(missing, "Referenced data is missing from the repository – run 'check'.");
                    if (issues.Count(IssueKind.Error) > 0)
                        throw new KeepsakeException("The repository has missing data; prune was stopped to avoid making it worse. Run a check first.");

                    result.PacksDeleted = delete.Count;
                    result.PacksRepacked = repack.Count;
                    result.BytesFreed = delete.Sum(p => stored[p]);

                    if (options.DryRun)
                    {
                        result.BytesFreed += repack.Sum(p => stored[p] - byPack[p].Where(b => used.Contains(b.Key)).Sum(b => (long)b.Value.Length));
                    }
                    else
                    {
                        // 4. Repack the still-needed blobs of mostly-empty packs.
                        var newPacks = new List<IndexPack>();
                        using (phases.Begin("Repack"))
                        {
                            PackBuilder builder = null;
                            var copied = new HashSet<string>(StringComparer.Ordinal);
                            int done = 0;
                            foreach (string packId in repack)
                            {
                                cancel.ThrowIfCancellationRequested();
                                progress?.Report("Repacking " + (++done) + " of " + repack.Count);
                                byte[] data = _repo.Storage.Read(Layout.PackPath(packId));
                                if (!string.Equals(Sha256.HexOf(data), packId, StringComparison.Ordinal))
                                    throw new RepositoryCorruptException("Pack " + packId + " is damaged; prune stopped. Run a check.");
                                foreach (var entry in PackReader.ReadHeader(data, packId, _repo.Keys.Encryption))
                                {
                                    if (!used.Contains(entry.Id) || copied.Contains(entry.Id) || IsKeptElsewhere(entry.Id, keep, index))
                                        continue;
                                    // Verify before copying: never spread damaged data into new packs.
                                    _repo.DecodeBlob(entry.Id, data, (int)entry.Offset, entry.Length);
                                    var sealedBlob = new byte[entry.Length];
                                    Buffer.BlockCopy(data, (int)entry.Offset, sealedBlob, 0, entry.Length);
                                    builder = builder ?? new PackBuilder(_repo.Config.PackSize + 1024 * 1024);
                                    builder.Add(entry.Type, entry.Id, sealedBlob, entry.RawLength);
                                    copied.Add(entry.Id);
                                    result.BytesRepacked += entry.Length;
                                    if (builder.Size >= _repo.Config.PackSize)
                                    {
                                        newPacks.Add(WritePack(builder));
                                        builder = null;
                                    }
                                }
                            }
                            if (builder != null && builder.Entries.Count > 0)
                                newPacks.Add(WritePack(builder));
                            result.PacksWritten = newPacks.Count;
                            result.BytesFreed += repack.Sum(p => stored[p]) - newPacks.Sum(p => p.Blobs.Sum(b => (long)b.Length));
                        }

                        // 5. One complete new index, then remove the old index files.
                        using (phases.Begin("Rewrite index"))
                        {
                            progress?.Report("Writing new index");
                            var packs = keep.Select(k => new IndexPack
                            {
                                Id = k.Key,
                                Blobs = k.Value.Select(b => new IndexBlob { Id = b.Key, Type = b.Value.Type, Offset = b.Value.Offset, Length = b.Value.Length, RawLength = b.Value.RawLength }).ToList(),
                            }).Concat(newPacks).ToList();
                            var written = new HashSet<string>(StringComparer.Ordinal);
                            for (int i = 0; i < packs.Count; i += 2000)
                                written.Add(Layout.IndexPath(_repo.SaveIndex(new IndexFile { Packs = packs.Skip(i).Take(2000).ToList() })));
                            foreach (string old in indexFiles.Where(f => !written.Contains(f)))
                                _repo.Storage.Delete(old);
                        }

                        // 6. Remove packs that are no longer referenced.
                        using (phases.Begin("Delete unused packs"))
                        {
                            int done = 0;
                            foreach (string packId in delete.Concat(repack))
                            {
                                cancel.ThrowIfCancellationRequested();
                                if (++done % 50 == 0)
                                    progress?.Report("Deleting packs " + done + " of " + (delete.Count + repack.Count));
                                _repo.Storage.Delete(Layout.PackPath(packId));
                            }
                        }
                    }
                }
                result.Status = issues.Count(IssueKind.Warning) > 0 ? RunStatus.Warning : RunStatus.Success;
            }
            catch (OperationCanceledException)
            {
                result.Status = RunStatus.Cancelled;
                result.Error = "Prune was cancelled. The repository stays consistent; unused data is removed by the next prune.";
            }
            catch (KeepsakeException ex)
            {
                result.Status = RunStatus.Failed;
                result.Error = ex.Message;
            }
            result.End = DateTime.UtcNow;
            return result;
        }

        private IndexPack WritePack(PackBuilder builder)
        {
            var pack = builder.Finish(_repo.Keys.Encryption);
            _repo.Storage.Write(Layout.PackPath(pack.Id), pack.Data);
            return new IndexPack
            {
                Id = pack.Id,
                Blobs = pack.Blobs.Select(b => new IndexBlob { Id = b.Id, Type = b.Type, Offset = b.Offset, Length = b.Length, RawLength = b.RawLength }).ToList(),
            };
        }

        /// <summary>A blob that also lives in a pack we keep does not need to be copied.</summary>
        private static bool IsKeptElsewhere(string blobId, Dictionary<string, List<KeyValuePair<string, BlobLocation>>> keep, MasterIndex index) =>
            index.TryGet(blobId, out var loc) && keep.ContainsKey(loc.PackId);

        private void MarkTree(string treeId, HashSet<string> used, CancellationToken cancel)
        {
            if (!used.Add(treeId))
                return;
            cancel.ThrowIfCancellationRequested();
            var tree = _repo.LoadTree(treeId);
            foreach (var node in tree.Nodes)
            {
                if (node.Type == NodeType.Dir && node.Subtree != null)
                    MarkTree(node.Subtree, used, cancel);
                else if (node.Content != null)
                {
                    foreach (string id in node.Content)
                        used.Add(id);
                }
            }
        }
    }
}
