using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Util;

namespace Keepsake.Core.Restore
{
    public enum RestoreTargetMode
    {
        /// <summary>Back to where the files came from (<c>D/Data/x</c> → <c>D:\Data\x</c>).</summary>
        Original,

        /// <summary>Into a different folder.</summary>
        Folder,
    }

    public enum ConflictPolicy
    {
        /// <summary>Keep the existing file.</summary>
        Skip,

        /// <summary>Replace the existing file.</summary>
        Overwrite,

        /// <summary>Replace only if the backed-up version is newer.</summary>
        OverwriteIfNewer,

        /// <summary>Restore next to it as <c>name (restored 1).ext</c>.</summary>
        Rename,
    }

    public sealed class RestoreOptions
    {
        public string Snapshot { get; set; } = "latest";
        public string Job { get; set; }

        /// <summary>Folders or files to restore (snapshot or Windows paths). Empty = everything.</summary>
        public List<string> Includes { get; set; } = new List<string>();

        public RestoreTargetMode Target { get; set; } = RestoreTargetMode.Folder;
        public string TargetFolder { get; set; }

        /// <summary>
        /// Folder target only. True: <c>target\D\Data\Finance\...</c>. False: the selected folder is placed directly
        /// in the target (<c>target\Finance\...</c>).
        /// </summary>
        public bool KeepFullPath { get; set; } = true;

        public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Skip;
        public bool RestoreAttributes { get; set; } = true;
        public bool DryRun { get; set; }
        public int Workers { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
    }

    public sealed class RestoreProgress
    {
        public long Files { get; set; }
        public long BytesWritten { get; set; }
        public string CurrentPath { get; set; }
    }

    public sealed class RestoreStats
    {
        public long FilesRestored { get; set; }
        public long FilesUnchanged { get; set; }
        public long FilesSkipped { get; set; }
        public long FilesFailed { get; set; }
        public long BytesWritten { get; set; }
    }

    public sealed class RestoreResult : RunReport
    {
        public string SnapshotId { get; set; }
        public DateTime SnapshotTime { get; set; }
        public bool DryRun { get; set; }
        public RestoreStats Stats { get; set; } = new RestoreStats();
    }

    /// <summary>
    /// Restores files from a snapshot. NTFS permissions are never applied: restored items inherit the
    /// permissions of the target folder, so they are accessible on any machine and in any domain.
    /// </summary>
    public sealed class RestoreEngine
    {
        private const string TempSuffix = ".keepsake-restore";
        private readonly Repository _repo;

        public RestoreEngine(Repository repo)
        {
            _repo = repo;
        }

        public RestoreResult Run(RestoreOptions options, IProgress<RestoreProgress> progress, CancellationToken cancel)
        {
            var result = new RestoreResult { Start = DateTime.UtcNow, DryRun = options.DryRun };
            var phases = new PhaseRecorder(result.Phases);
            var issues = new IssueLog(result.Issues);
            try
            {
                if (options.Target == RestoreTargetMode.Folder && string.IsNullOrWhiteSpace(options.TargetFolder))
                    throw new KeepsakeException("No target folder given.");

                using (RepoLock.Acquire(_repo, exclusive: false, purpose: "restore"))
                {
                    SnapshotRef snapshot;
                    Tree root;
                    List<List<string>> includes;
                    using (phases.Begin("Prepare"))
                    {
                        _repo.LoadIndex();
                        snapshot = _repo.FindSnapshot(options.Snapshot, options.Job);
                        result.SnapshotId = snapshot.Id;
                        result.SnapshotTime = snapshot.Snapshot.Time;
                        root = _repo.LoadTree(snapshot.Snapshot.Tree);
                        includes = (options.Includes ?? new List<string>()).Select(RepoPaths.Parse).Where(p => p.Count > 0).ToList();
                        if (includes.Count > 0 && !includes.All(i => Exists(root, i)))
                        {
                            var missing = includes.First(i => !Exists(root, i));
                            throw new KeepsakeException("'" + RepoPaths.Join(missing) + "' is not part of snapshot " + snapshot.ShortId + ".");
                        }
                    }

                    var run = new RestoreRun(_repo, options, includes, snapshot.Snapshot.Paths, result.Stats, issues, progress, cancel);
                    try
                    {
                        using (phases.Begin("Restore files"))
                            run.Walk(root, new List<string>(), includes.Count == 0 ? 0 : -1);
                    }
                    finally
                    {
                        run.Publish();
                    }
                }
                result.Status = result.Stats.FilesFailed > 0 || issues.Count(IssueKind.Error) + issues.Count(IssueKind.Warning) > 0
                    ? RunStatus.Warning
                    : RunStatus.Success;
            }
            catch (OperationCanceledException)
            {
                result.Status = RunStatus.Cancelled;
                result.Error = "The restore was cancelled.";
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Status = RunStatus.Failed;
                result.Error = ex.Message;
            }
            result.End = DateTime.UtcNow;
            return result;
        }

        private bool Exists(Tree root, List<string> path)
        {
            Tree tree = root;
            for (int i = 0; i < path.Count; i++)
            {
                var node = tree.Find(path[i]);
                if (node == null)
                    return false;
                if (i == path.Count - 1)
                    return true;
                if (node.Type != NodeType.Dir)
                    return false;
                tree = _repo.LoadTree(node.Subtree);
            }
            return true;
        }

        private sealed class RestoreRun
        {
            private readonly Repository _repo;
            private readonly RestoreOptions _options;
            private readonly List<List<string>> _includes;
            private readonly RestoreStats _stats;
            private readonly IssueLog _issues;
            private readonly IProgress<RestoreProgress> _progress;
            private readonly CancellationToken _cancel;
            private readonly List<string> _sources;
            private readonly SemaphoreSlim _workers;
            private DateTime _lastReport = DateTime.MinValue;
            private long _restored, _unchanged, _skipped, _failed, _bytes;
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _finalAnchors =
                new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public RestoreRun(Repository repo, RestoreOptions options, List<List<string>> includes, List<string> sources, RestoreStats stats,
                IssueLog issues, IProgress<RestoreProgress> progress, CancellationToken cancel)
            {
                _repo = repo;
                _options = options;
                _includes = includes;
                _sources = sources ?? new List<string>();
                _stats = stats;
                _issues = issues;
                _progress = progress;
                _cancel = cancel;
                _workers = new SemaphoreSlim(Math.Max(1, options.Workers));
            }

            /// <summary>Copies the counters into the result (also after a cancellation).</summary>
            public void Publish()
            {
                _stats.FilesRestored = Interlocked.Read(ref _restored);
                _stats.FilesUnchanged = Interlocked.Read(ref _unchanged);
                _stats.FilesSkipped = Interlocked.Read(ref _skipped);
                _stats.FilesFailed = Interlocked.Read(ref _failed);
                _stats.BytesWritten = Interlocked.Read(ref _bytes);
            }

            /// <summary>
            /// Files of a folder are restored in parallel; the folder's own times are set once all of
            /// its files and subfolders are done.
            /// </summary>
            /// <param name="baseLength">
            /// -1 while above every selected path; otherwise the number of leading components to drop for
            /// "keep full path = false" targets.
            /// </param>
            public void Walk(Tree tree, List<string> path, int baseLength)
            {
                var tasks = new List<Task>();
                try
                {
                    WalkEntries(tree, path, baseLength, tasks);
                }
                catch
                {
                    WaitQuietly(tasks);
                    throw;
                }
                WaitAndRethrow(tasks);
            }

            private void WalkEntries(Tree tree, List<string> path, int baseLength, List<Task> tasks)
            {
                foreach (var node in tree.Nodes)
                {
                    _cancel.ThrowIfCancellationRequested();
                    var childPath = new List<string>(path) { node.Name };
                    int childBase = baseLength;
                    if (childBase < 0)
                    {
                        if (_includes.Any(i => i.Count == childPath.Count && RepoPaths.IsPrefix(i, childPath)))
                            childBase = childPath.Count - 1;
                        else if (!_includes.Any(i => RepoPaths.IsPrefix(childPath, i)))
                            continue;
                    }

                    if (node.Type == NodeType.Dir)
                    {
                        Tree sub;
                        try
                        {
                            sub = _repo.LoadTree(node.Subtree);
                        }
                        catch (RepositoryFormatException ex)
                        {
                            _issues.Error(RepoPaths.Join(childPath), "Folder contents cannot be read from the backup: " + ex.Message);
                            continue;
                        }
                        Walk(sub, childPath, childBase);
                        if (childBase >= 0)
                            FinishDirectory(node, childPath, childBase);
                    }
                    else if (childBase >= 0)
                    {
                        var file = node;
                        int fileBase = childBase;
                        _workers.Wait(_cancel);
                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                RestoreFile(file, childPath, fileBase);
                            }
                            finally
                            {
                                _workers.Release();
                            }
                        }));
                    }
                }
            }

            private static void WaitQuietly(List<Task> tasks)
            {
                try
                {
                    Task.WaitAll(tasks.ToArray());
                }
                catch (AggregateException)
                {
                    // The original exception is rethrown by the caller.
                }
            }

            private static void WaitAndRethrow(List<Task> tasks)
            {
                try
                {
                    Task.WaitAll(tasks.ToArray());
                }
                catch (AggregateException ex)
                {
                    var inner = ex.Flatten().InnerExceptions;
                    var first = inner.FirstOrDefault(e => !(e is OperationCanceledException)) ?? inner[0];
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
                }
            }

            private string TargetPath(List<string> path, int baseLength)
            {
                // Names come from authenticated trees, but a repository written by another holder of the key must
                // still not be able to point outside the target (SEC-017).
                foreach (string name in path)
                    CheckName(name);
                if (_options.Target == RestoreTargetMode.Original)
                    return RepoPaths.ToWindows(path);
                var parts = _options.KeepFullPath ? path : path.Skip(baseLength).ToList();
                string folder = LongPath.Normalize(_options.TargetFolder);
                string target = RepoPaths.Combine(folder, parts);
                if (!LongPath.IsSameOrBelow(LongPath.Normalize(target), folder))
                    throw new KeepsakeException("'" + RepoPaths.Join(path) + "' would be restored outside the target folder.");
                return target;
            }

            private static readonly char[] BadNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

            private static void CheckName(string name)
            {
                if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(BadNameChars) >= 0 || name.Any(char.IsControl))
                    throw new KeepsakeException("The backup contains an invalid file name ('" + name + "'); it is skipped.");
            }

            /// <summary>
            /// The folder below which no junction or link is followed: the chosen target folder, or for a restore to
            /// the original location the backed-up source folder the file belongs to (SEC-003).
            /// </summary>
            private string AnchorFor(string target)
            {
                if (_options.Target != RestoreTargetMode.Original)
                    return LongPath.Normalize(_options.TargetFolder);
                string best = null;
                foreach (string s in _sources)
                {
                    if (LongPath.IsSameOrBelow(target, s) && (best == null || s.Length > best.Length))
                        best = s;
                }
                return best ?? Path.GetPathRoot(target);
            }

            private string FinalAnchor(string anchor) =>
                _finalAnchors.GetOrAdd(anchor, a => Directory.Exists(LongPath.ToExtended(a)) ? LinkSafety.FinalDirectoryPath(a) : null);

            private void RestoreFile(Node node, List<string> path, int baseLength)
            {
                string target;
                try
                {
                    target = TargetPath(path, baseLength);
                }
                catch (KeepsakeException ex)
                {
                    _issues.Error(RepoPaths.Join(path), ex.Message);
                    Interlocked.Increment(ref _failed);
                    return;
                }
                string extended = LongPath.ToExtended(target);
                string temp = null;
                try
                {
                    string anchor = AnchorFor(target);
                    LinkSafety.EnsureNoLinksBelow(anchor, Path.GetDirectoryName(target));
                    var existing = new FileInfo(extended);
                    if (existing.Exists)
                    {
                        if (existing.Length == node.Size && existing.LastWriteTimeUtc.Ticks == node.MTime)
                        {
                            Interlocked.Increment(ref _unchanged);
                            Report(target);
                            return;
                        }
                        switch (_options.Conflict)
                        {
                            case ConflictPolicy.Skip:
                                _issues.Skipped(target, "A different file already exists – kept (conflict policy: skip).");
                                Interlocked.Increment(ref _skipped);
                                return;
                            case ConflictPolicy.OverwriteIfNewer:
                                if (existing.LastWriteTimeUtc.Ticks >= node.MTime)
                                {
                                    _issues.Skipped(target, "The existing file is newer or equal – kept (conflict policy: newer).");
                                    Interlocked.Increment(ref _skipped);
                                    return;
                                }
                                break;
                            case ConflictPolicy.Rename:
                                extended = UniqueName(extended);
                                target = LongPath.ToNormal(extended);
                                break;
                        }
                    }

                    if (_options.DryRun)
                    {
                        Interlocked.Increment(ref _restored);
                        Interlocked.Add(ref _bytes, node.Size);
                        Report(target);
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(extended));
                    temp = extended + TempSuffix;
                    long written = 0;
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        // A folder swapped for a junction after the check above would redirect the write: compare
                        // where the new file really is with where the anchor really is.
                        string finalAnchor = FinalAnchor(anchor);
                        string finalTemp = finalAnchor == null ? null : LinkSafety.FinalPath(output.SafeFileHandle);
                        if (finalTemp != null && !LongPath.IsSameOrBelow(finalTemp, finalAnchor))
                            throw new IOException(target + " resolves to a location outside " + anchor + " (junction or link); not restored.");
                        foreach (string id in node.Content ?? new List<string>())
                        {
                            _cancel.ThrowIfCancellationRequested();
                            byte[] data = _repo.LoadBlob(id);
                            output.Write(data, 0, data.Length);
                            written += data.Length;
                            Interlocked.Add(ref _bytes, data.Length);
                            Report(target);
                        }
                    }
                    if (written != node.Size)
                        throw new RepositoryCorruptException("Restored size " + written + " does not match the backed-up size " + node.Size + ".");

                    if (File.Exists(extended))
                    {
                        File.SetAttributes(extended, FileAttributes.Normal);
                        File.Delete(extended);
                    }
                    File.Move(temp, extended);
                    temp = null;
                    File.SetCreationTimeUtc(extended, node.CreatedUtc);
                    File.SetLastWriteTimeUtc(extended, node.ModifiedUtc);
                    if (_options.RestoreAttributes && node.Attributes != 0)
                        File.SetAttributes(extended, (FileAttributes)node.Attributes);
                    Interlocked.Increment(ref _restored);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is RepositoryFormatException || ex is ArgumentException)
                {
                    _issues.Error(target, ex is UnauthorizedAccessException ? "Access denied." : ex.Message);
                    Interlocked.Increment(ref _failed);
                }
                finally
                {
                    if (temp != null)
                        TryDelete(temp);
                }
            }

            private void FinishDirectory(Node node, List<string> path, int baseLength)
            {
                if (_options.DryRun)
                    return;
                if (_options.Target == RestoreTargetMode.Original)
                {
                    // Folders above the backed-up sources (e.g. C:\Users) belong to the system: never touch them.
                    if (RepoPaths.IsRootLevel(path))
                        return;
                    string original = RepoPaths.ToWindows(path);
                    if (!_sources.Any(s => LongPath.IsSameOrBelow(original, s)))
                        return;
                }
                try
                {
                    string targetDir = TargetPath(path, baseLength);
                    LinkSafety.EnsureNoLinksBelow(AnchorFor(targetDir), targetDir);
                    string extended = LongPath.ToExtended(targetDir);
                    if (!Directory.Exists(extended))
                    {
                        // Restore empty folders too.
                        Directory.CreateDirectory(extended);
                    }
                    Directory.SetCreationTimeUtc(extended, node.CreatedUtc);
                    Directory.SetLastWriteTimeUtc(extended, node.ModifiedUtc);
                    if (_options.RestoreAttributes)
                    {
                        var wanted = (FileAttributes)node.Attributes & (FileAttributes.Hidden | FileAttributes.System);
                        if (wanted != 0)
                            File.SetAttributes(extended, File.GetAttributes(extended) | wanted);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is KeepsakeException || ex is ArgumentException)
                {
                    _issues.Warning(RepoPaths.Join(path), "Folder times/attributes could not be set: " + ex.Message);
                }
            }

            private static string UniqueName(string path)
            {
                string dir = Path.GetDirectoryName(path);
                string name = Path.GetFileNameWithoutExtension(path);
                string ext = Path.GetExtension(path);
                for (int i = 1; ; i++)
                {
                    string candidate = Path.Combine(dir, name + " (restored " + i + ")" + ext);
                    if (!File.Exists(candidate))
                        return candidate;
                }
            }

            private void Report(string path)
            {
                if (_progress == null)
                    return;
                var now = DateTime.UtcNow;
                if ((now - _lastReport).TotalMilliseconds < 250)
                    return;
                _lastReport = now;
                _progress.Report(new RestoreProgress
                {
                    Files = Interlocked.Read(ref _restored) + Interlocked.Read(ref _unchanged) + Interlocked.Read(ref _skipped) + Interlocked.Read(ref _failed),
                    BytesWritten = Interlocked.Read(ref _bytes),
                    CurrentPath = path,
                });
            }

            private static void TryDelete(string file)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Leftover partial file; reported via the file error already.
                }
            }
        }
    }
}
