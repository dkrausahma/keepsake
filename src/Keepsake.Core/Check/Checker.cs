using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;

namespace Keepsake.Core.Check
{
    public sealed class CheckOptions
    {
        /// <summary>Download and verify pack data, not only the structure.</summary>
        public bool ReadData { get; set; }

        /// <summary>Share of packs to verify when <see cref="ReadData"/> is set (random sample).</summary>
        public int ReadDataPercent { get; set; } = 100;
    }

    public sealed class CheckResult : RunReport
    {
        public int Snapshots { get; set; }
        public int IndexFiles { get; set; }
        public int Packs { get; set; }
        public int Trees { get; set; }
        public int PacksVerified { get; set; }
        public long BytesVerified { get; set; }
    }

    /// <summary>
    /// Quick check: index, packs and snapshot trees are consistent and all referenced data exists.
    /// With <see cref="CheckOptions.ReadData"/>: every (sampled) pack is downloaded, its hash, header and every blob verified.
    /// </summary>
    public sealed class Checker
    {
        private readonly Repository _repo;

        public Checker(Repository repo)
        {
            _repo = repo;
        }

        public CheckResult Run(CheckOptions options, IProgress<string> progress, CancellationToken cancel)
        {
            var result = new CheckResult { Start = DateTime.UtcNow };
            var phases = new PhaseRecorder(result.Phases);
            var issues = new IssueLog(result.Issues);
            try
            {
                using (RepoLock.Acquire(_repo, exclusive: false, purpose: "check"))
                {
                    var index = new MasterIndex();
                    using (phases.Begin("Index"))
                    {
                        progress?.Report("Loading index");
                        foreach (var entry in _repo.Storage.List(Layout.Index))
                        {
                            cancel.ThrowIfCancellationRequested();
                            try
                            {
                                index.Add(_repo.LoadObject<IndexFile>(entry.Path, "index"));
                                result.IndexFiles++;
                            }
                            catch (KeepsakeException ex)
                            {
                                issues.Error(entry.Path, "Index file cannot be read: " + ex.Message);
                            }
                        }
                    }

                    Dictionary<string, StorageEntry> stored;
                    using (phases.Begin("Packs"))
                    {
                        progress?.Report("Listing packs");
                        stored = _repo.Storage.List(Layout.Data).ToDictionary(e => e.Name, StringComparer.Ordinal);
                        result.Packs = stored.Count;
                        foreach (string packId in index.PackIds())
                        {
                            if (!stored.ContainsKey(packId))
                                issues.Error(Layout.PackPath(packId), "Pack is listed in the index but missing from storage.");
                        }
                        foreach (var e in stored.Values)
                        {
                            if (!index.ContainsPack(e.Name))
                                issues.Warning(e.Path, "Pack is not referenced by any index (left over from an interrupted backup).");
                        }
                    }

                    using (phases.Begin("Snapshots"))
                    {
                        var checkedTrees = new HashSet<string>(StringComparer.Ordinal);
                        var checkedFiles = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var entry in _repo.Storage.List(Layout.Snapshots))
                        {
                            cancel.ThrowIfCancellationRequested();
                            Snapshot snapshot;
                            try
                            {
                                snapshot = _repo.LoadObject<Snapshot>(entry.Path, "snapshot");
                            }
                            catch (KeepsakeException ex)
                            {
                                issues.Error(entry.Path, "Snapshot cannot be read: " + ex.Message);
                                continue;
                            }
                            result.Snapshots++;
                            progress?.Report("Checking snapshot " + entry.Name.Substring(0, 8));
                            CheckTree(index, snapshot.Tree, new List<string>(), entry.Name.Substring(0, 8), checkedTrees, checkedFiles, issues, cancel);
                        }
                        result.Trees = checkedTrees.Count;
                    }

                    if (options.ReadData)
                    {
                        using (phases.Begin("Read data"))
                            ReadData(index, stored, options.ReadDataPercent, result, issues, progress, cancel);
                    }
                }
                result.Status = issues.Count(IssueKind.Error) > 0 ? RunStatus.Failed
                    : issues.Count(IssueKind.Warning) > 0 ? RunStatus.Warning
                    : RunStatus.Success;
                if (result.Status == RunStatus.Failed)
                    result.Error = issues.Count(IssueKind.Error) + " problem(s) found.";
            }
            catch (OperationCanceledException)
            {
                result.Status = RunStatus.Cancelled;
                result.Error = "The check was cancelled.";
            }
            catch (KeepsakeException ex)
            {
                result.Status = RunStatus.Failed;
                result.Error = ex.Message;
            }
            result.End = DateTime.UtcNow;
            return result;
        }

        private void CheckTree(MasterIndex index, string treeId, List<string> path, string snapshot, HashSet<string> checkedTrees,
            HashSet<string> checkedFiles, IssueLog issues, CancellationToken cancel)
        {
            if (!checkedTrees.Add(treeId))
                return;
            cancel.ThrowIfCancellationRequested();
            string where = snapshot + ":" + (path.Count == 0 ? "/" : RepoPaths.Join(path));

            if (!index.TryGet(treeId, out var location) || location.Type != BlobType.Tree)
            {
                issues.Error(where, "Folder listing " + treeId + " is missing.");
                return;
            }
            Tree tree;
            try
            {
                byte[] sealedBlob = _repo.Storage.ReadRange(Layout.PackPath(location.PackId), location.Offset, location.Length);
                tree = Json.FromBytes<Tree>(_repo.DecodeBlob(treeId, sealedBlob, 0, sealedBlob.Length));
            }
            catch (KeepsakeException ex)
            {
                issues.Error(where, "Folder listing cannot be read: " + ex.Message);
                return;
            }

            foreach (var node in tree.Nodes)
            {
                var childPath = new List<string>(path) { node.Name };
                if (node.Type == NodeType.Dir)
                {
                    if (string.IsNullOrEmpty(node.Subtree))
                        issues.Error(snapshot + ":" + RepoPaths.Join(childPath), "Folder entry has no contents reference.");
                    else
                        CheckTree(index, node.Subtree, childPath, snapshot, checkedTrees, checkedFiles, issues, cancel);
                    continue;
                }
                long size = 0;
                bool ok = true;
                foreach (string id in node.Content ?? new List<string>())
                {
                    if (checkedFiles.Contains(id))
                    {
                        size += index.TryGet(id, out var known) ? known.RawLength : 0;
                        continue;
                    }
                    if (!index.TryGet(id, out var blob) || blob.Type != BlobType.Data)
                    {
                        issues.Error(snapshot + ":" + RepoPaths.Join(childPath), "Data block " + id + " is missing – the file cannot be restored.");
                        ok = false;
                        break;
                    }
                    checkedFiles.Add(id);
                    size += blob.RawLength;
                }
                if (ok && size != node.Size)
                    issues.Error(snapshot + ":" + RepoPaths.Join(childPath), "Stored size " + size + " does not match the file size " + node.Size + ".");
            }
        }

        private void ReadData(MasterIndex index, Dictionary<string, StorageEntry> stored, int percent, CheckResult result, IssueLog issues,
            IProgress<string> progress, CancellationToken cancel)
        {
            var byPack = index.GroupByPack();
            var packs = stored.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (percent < 100)
            {
                var rng = new Random();
                int take = Math.Max(1, (int)Math.Ceiling(packs.Count * Math.Max(1, percent) / 100.0));
                packs = packs.OrderBy(_ => rng.Next()).Take(take).ToList();
            }

            int done = 0;
            foreach (string packId in packs)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report("Verifying pack " + (++done) + " of " + packs.Count);
                string path = Layout.PackPath(packId);
                try
                {
                    byte[] data = _repo.Storage.Read(path);
                    result.BytesVerified += data.Length;
                    if (!string.Equals(Sha256.HexOf(data), packId, StringComparison.Ordinal))
                    {
                        issues.Error(path, "Pack content does not match its name (damaged on disk).");
                        continue;
                    }
                    var entries = PackReader.ReadHeader(data, packId, _repo.Keys.Encryption);
                    foreach (var e in entries)
                        _repo.DecodeBlob(e.Id, data, (int)e.Offset, e.Length);

                    if (byPack.TryGetValue(packId, out var indexed))
                    {
                        var headerById = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
                        foreach (var kv in indexed)
                        {
                            if (!headerById.TryGetValue(kv.Key, out var h) || h.Offset != kv.Value.Offset || h.Length != kv.Value.Length)
                                issues.Error(path, "Index entry for block " + kv.Key + " does not match the pack contents.");
                        }
                    }
                    result.PacksVerified++;
                }
                catch (KeepsakeException ex)
                {
                    issues.Error(path, ex.Message);
                }
            }
        }
    }
}
