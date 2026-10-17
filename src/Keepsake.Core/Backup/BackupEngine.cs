using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Keepsake.Core.Chunking;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Util;

namespace Keepsake.Core.Backup
{
    public sealed class BackupOptions
    {
        public List<string> Sources { get; set; } = new List<string>();
        public List<string> Excludes { get; set; } = new List<string>();
        public bool UseDefaultExcludes { get; set; } = true;
        public BackupMode Mode { get; set; } = BackupMode.Incremental;

        /// <summary>Job name stored in the snapshot; also selects the parent snapshot.</summary>
        public string Job { get; set; }

        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>Explicit parent snapshot; default is the latest snapshot of the same job, host and sources.</summary>
        public string Parent { get; set; }

        public int Workers { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

        /// <summary>Record every excluded item in the run report (can be long).</summary>
        public bool ReportExcluded { get; set; }

        /// <summary>
        /// Maps a source path to the path that is actually read, e.g. into a VSS snapshot
        /// (<c>D:\Data</c> → <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\Data</c>). Null = read live files.
        /// Paths stored in the snapshot are always the original ones.
        /// </summary>
        public Func<string, string> ReadPath { get; set; }

        /// <summary>Issues found before the backup started (e.g. "VSS failed, live files used").</summary>
        public List<Issue> InitialIssues { get; set; } = new List<Issue>();
    }

    public sealed class BackupProgress
    {
        public long Files { get; set; }
        public long Dirs { get; set; }
        public long BytesRead { get; set; }
        public long BytesStored { get; set; }
        public string CurrentPath { get; set; }
    }

    public sealed class BackupResult : RunReport
    {
        public string SnapshotId { get; set; }
        public string ParentId { get; set; }
        public BackupMode Mode { get; set; }
        public SnapshotSummary Summary { get; set; } = new SnapshotSummary();
        public long FilesExcluded { get; set; }
    }

    /// <summary>Creates a snapshot of the configured sources. See docs/FORMAT.md for the data layout.</summary>
    public sealed class BackupEngine
    {
        private const FileAttributes StoredAttributes =
            FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

        // Formats that are already compressed; trying Zstandard on them only costs CPU.
        private static readonly HashSet<string> IncompressibleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".jar",
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp3", ".m4a", ".aac", ".ogg", ".flac",
            ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm",
            ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".vsdx", ".pst.zip",
        };

        private readonly Repository _repo;

        public BackupEngine(Repository repo)
        {
            _repo = repo;
        }

        public BackupResult Run(BackupOptions options, IProgress<BackupProgress> progress, CancellationToken cancel)
        {
            var result = new BackupResult { Start = DateTime.UtcNow, Mode = options.Mode };
            var phases = new PhaseRecorder(result.Phases);
            var issues = new IssueLog(result.Issues);
            foreach (var i in options.InitialIssues ?? new List<Issue>())
            {
                if (i.Kind == IssueKind.Error)
                    issues.Error(i.Path, i.Message);
                else if (i.Kind == IssueKind.Warning)
                    issues.Warning(i.Path, i.Message);
                else
                    issues.Skipped(i.Path, i.Message);
            }
            Walker walker = null;
            try
            {
                if (options.Sources == null || options.Sources.Count == 0)
                    throw new KeepsakeException("No folders or files selected for backup.");

                using (RepoLock.Acquire(_repo, exclusive: false, purpose: "backup"))
                {
                    List<string> sources;
                    SnapshotRef parent;
                    Tree parentRoot = null;
                    using (phases.Begin("Prepare"))
                    {
                        sources = RepoPaths.NormalizeSources(options.Sources);
                        _repo.LoadIndex();
                        parent = FindParent(options, sources);
                        if (parent != null)
                        {
                            result.ParentId = parent.Id;
                            parentRoot = TryLoadTree(parent.Snapshot.Tree, issues, "(parent snapshot)");
                        }
                    }

                    var excludes = new List<string>(options.Excludes ?? new List<string>());
                    if (options.UseDefaultExcludes)
                        excludes.AddRange(ExcludeMatcher.Defaults);

                    walker = new Walker(_repo, options, new ExcludeMatcher(excludes), issues, progress, cancel);
                    string rootTree;
                    using (phases.Begin("Scan and upload"))
                        rootTree = walker.Run(sources, parentRoot);

                    using (phases.Begin("Finalize"))
                    {
                        walker.Writer.Flush();
                        result.End = DateTime.UtcNow;
                        walker.FillSummary(result.Summary, result.End - result.Start);
                        var snapshot = new Snapshot
                        {
                            Time = result.Start,
                            Host = ProgramInfo.Host,
                            User = ProgramInfo.User,
                            Job = options.Job,
                            Paths = sources,
                            Excludes = excludes,
                            Tags = options.Tags ?? new List<string>(),
                            Mode = options.Mode,
                            Tree = rootTree,
                            Parent = parent?.Id,
                            Program = ProgramInfo.Name,
                            Summary = result.Summary,
                        };
                        result.SnapshotId = _repo.SaveSnapshot(snapshot);
                    }
                    result.FilesExcluded = walker.Excluded;
                }
                result.Status = issues.Count(IssueKind.Error) + issues.Count(IssueKind.Warning) > 0 ? RunStatus.Warning : RunStatus.Success;
            }
            catch (OperationCanceledException)
            {
                walker?.Writer.FlushBestEffort();
                result.Status = RunStatus.Cancelled;
                result.Error = "The backup was cancelled. Data stored so far is reused by the next run.";
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is UnauthorizedAccessException)
            {
                walker?.Writer.FlushBestEffort();
                result.Status = RunStatus.Failed;
                result.Error = ex.Message;
            }
            finally
            {
                walker?.Writer.Dispose();
                if (walker != null && result.SnapshotId == null)
                    walker.FillSummary(result.Summary, DateTime.UtcNow - result.Start);
                result.End = DateTime.UtcNow;
                result.Summary.DurationSeconds = (result.End - result.Start).TotalSeconds;
            }
            return result;
        }

        private SnapshotRef FindParent(BackupOptions options, List<string> sources)
        {
            if (!string.IsNullOrEmpty(options.Parent))
                return _repo.FindSnapshot(options.Parent);

            var key = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
            return _repo.ListSnapshots()
                .Where(s => string.Equals(s.Snapshot.Host, ProgramInfo.Host, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(s.Snapshot.Job ?? "", options.Job ?? "", StringComparison.OrdinalIgnoreCase)
                            && key.SetEquals(s.Snapshot.Paths))
                .OrderBy(s => s.Snapshot.Time)
                .LastOrDefault();
        }

        private Tree TryLoadTree(string id, IssueLog issues, string path)
        {
            try
            {
                return _repo.LoadTree(id);
            }
            catch (RepositoryFormatException ex)
            {
                issues.Warning(path, "Previous backup data could not be read, files are read again: " + ex.Message);
                return null;
            }
        }

        /// <summary>Walks the sources once and builds the tree blobs bottom-up.</summary>
        private sealed class Walker
        {
            private readonly Repository _repo;
            private readonly BackupOptions _options;
            private readonly ExcludeMatcher _excludes;
            private readonly IssueLog _issues;
            private readonly IProgress<BackupProgress> _progress;
            private readonly CancellationToken _cancel;
            private readonly SemaphoreSlim _workers;
            private readonly ThreadLocal<Chunker> _chunkers;
            private readonly bool _full;

            private long _filesNew, _filesChanged, _filesUnchanged, _filesSkipped, _dirs, _excluded;
            private long _totalBytes, _bytesRead;
            private int _sourcesFound;
            private string _current;
            private DateTime _lastReport = DateTime.MinValue;

            public Walker(Repository repo, BackupOptions options, ExcludeMatcher excludes, IssueLog issues,
                IProgress<BackupProgress> progress, CancellationToken cancel)
            {
                _repo = repo;
                _options = options;
                _excludes = excludes;
                _issues = issues;
                _progress = progress;
                _cancel = cancel;
                _full = options.Mode == BackupMode.Full;
                _workers = new SemaphoreSlim(Math.Max(1, options.Workers));
                var cfg = repo.Config;
                _chunkers = new ThreadLocal<Chunker>(() => new Chunker(repo.Keys.Chunker, cfg.ChunkerMin, cfg.ChunkerAvg, cfg.ChunkerMax));
                Writer = new BlobWriter(repo);
            }

            public BlobWriter Writer { get; }
            public long Excluded => Interlocked.Read(ref _excluded);

            public string Run(List<string> sources, Tree parentRoot)
            {
                var root = new VirtualDir();
                foreach (string source in sources)
                {
                    var node = root;
                    foreach (string component in RepoPaths.Split(source))
                        node = node.Child(component);
                    node.Source = source;
                }
                string id = BuildVirtual(root, new List<string>(), parentRoot);
                Report(force: true);
                if (_sourcesFound == 0)
                    throw new KeepsakeException("None of the selected folders or files could be read. No snapshot was created.");
                return id;
            }

            public void FillSummary(SnapshotSummary s, TimeSpan duration)
            {
                s.FilesNew = Interlocked.Read(ref _filesNew);
                s.FilesChanged = Interlocked.Read(ref _filesChanged);
                s.FilesUnchanged = Interlocked.Read(ref _filesUnchanged);
                s.FilesSkipped = Interlocked.Read(ref _filesSkipped);
                s.Dirs = Interlocked.Read(ref _dirs);
                s.TotalBytes = Interlocked.Read(ref _totalBytes);
                s.BytesRead = Interlocked.Read(ref _bytesRead);
                s.BytesAdded = Writer.BytesAdded;
                s.BytesStored = Writer.BytesStored;
                s.DurationSeconds = duration.TotalSeconds;
            }

            /// <summary>Folders above the selected sources (<c>D</c>, <c>D/Data</c>, ...).</summary>
            private string BuildVirtual(VirtualDir dir, List<string> components, Tree parentTree)
            {
                var tree = new Tree();
                foreach (var child in dir.Children.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    var path = new List<string>(components) { child.Key };
                    Node previous = parentTree?.Find(child.Key);
                    Node node;
                    if (child.Value.Source != null)
                    {
                        node = BackupSource(child.Value.Source, child.Key, previous);
                    }
                    else
                    {
                        Tree previousSub = LoadPreviousTree(previous);
                        string sub = BuildVirtual(child.Value, path, previousSub);
                        node = VirtualDirNode(child.Key, path, sub);
                    }
                    if (node != null)
                        tree.Nodes.Add(node);
                }
                return StoreTree(tree);
            }

            private Node VirtualDirNode(string name, List<string> path, string subtree)
            {
                var node = new Node { Name = name, Type = NodeType.Dir, Subtree = subtree };
                try
                {
                    // "UNC" and "UNC/server" have no folder behind them.
                    if (path[0] != RepoPaths.UncRoot || path.Count >= 3)
                    {
                        var info = new DirectoryInfo(LongPath.ToExtended(RepoPaths.ToWindows(path)));
                        if (info.Exists)
                            ApplyMetadata(node, info);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is KeepsakeException)
                {
                    // Metadata of parent folders is cosmetic.
                }
                return node;
            }

            private Node BackupSource(string source, string name, Node previous)
            {
                string extended = LongPath.ToExtended(_options.ReadPath?.Invoke(source) ?? source);
                try
                {
                    if (File.Exists(extended))
                    {
                        _sourcesFound++;
                        var node = BackupFile(new FileInfo(extended), source, previous);
                        if (node != null)
                            node.Name = name;
                        return node;
                    }
                    if (Directory.Exists(extended))
                    {
                        _sourcesFound++;
                        var info = new DirectoryInfo(extended);
                        string sub = BackupDir(info, source, LoadPreviousTree(previous));
                        var node = new Node { Name = name, Type = NodeType.Dir, Subtree = sub };
                        ApplyMetadata(node, info);
                        Interlocked.Increment(ref _dirs);
                        return node;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _issues.Error(source, "Source cannot be read: " + Describe(ex));
                    return null;
                }
                _issues.Error(source, "Source folder or file not found – it was not backed up.");
                return null;
            }

            private string BackupDir(DirectoryInfo dir, string displayPath, Tree previous)
            {
                _cancel.ThrowIfCancellationRequested();
                var tree = new Tree();
                FileSystemInfo[] entries;
                try
                {
                    entries = dir.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _issues.Error(displayPath, "Folder cannot be read: " + Describe(ex));
                    return StoreTree(tree);
                }
                Array.Sort(entries, (a, b) => string.CompareOrdinal(a.Name, b.Name));

                var nodes = new Node[entries.Length];
                var tasks = new List<Task>();
                try
                {
                    for (int i = 0; i < entries.Length; i++)
                    {
                        _cancel.ThrowIfCancellationRequested();
                        var entry = entries[i];
                        string childPath = displayPath.TrimEnd('\\') + "\\" + entry.Name;
                        bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;

                        string pattern = _excludes.Match(childPath, entry.Name, isDir);
                        if (pattern != null)
                        {
                            Interlocked.Increment(ref _excluded);
                            if (_options.ReportExcluded)
                                _issues.Skipped(childPath, "Excluded by pattern '" + pattern + "'.");
                            continue;
                        }

                        Node previousNode = previous?.Find(entry.Name);
                        if (isDir)
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                _issues.Warning(childPath, "Junction or symbolic link – the linked folder is not followed.");
                                continue;
                            }
                            var subDir = (DirectoryInfo)entry;
                            string sub = BackupDir(subDir, childPath, LoadPreviousTree(previousNode));
                            var node = new Node { Name = entry.Name, Type = NodeType.Dir, Subtree = sub };
                            ApplyMetadata(node, subDir);
                            nodes[i] = node;
                            Interlocked.Increment(ref _dirs);
                        }
                        else
                        {
                            int slot = i;
                            var file = (FileInfo)entry;
                            _workers.Wait(_cancel);
                            tasks.Add(Task.Run(() =>
                            {
                                try
                                {
                                    nodes[slot] = BackupFile(file, childPath, previousNode);
                                }
                                finally
                                {
                                    _workers.Release();
                                }
                            }));
                        }
                    }
                }
                catch
                {
                    WaitQuietly(tasks);
                    throw;
                }
                WaitAndRethrow(tasks);

                foreach (var n in nodes)
                {
                    if (n != null)
                        tree.Nodes.Add(n);
                }
                return StoreTree(tree);
            }

            private Node BackupFile(FileInfo file, string displayPath, Node previous)
            {
                var node = new Node
                {
                    Name = file.Name,
                    Type = NodeType.File,
                    Size = file.Length,
                    MTime = file.LastWriteTimeUtc.Ticks,
                    CTime = file.CreationTimeUtc.Ticks,
                    Attributes = (int)(file.Attributes & StoredAttributes),
                };
                _current = displayPath;

                if (!_full && previous != null && previous.Type == NodeType.File && previous.Size == node.Size
                    && previous.MTime == node.MTime && previous.CTime == node.CTime
                    && previous.Content != null && previous.Content.All(Writer.IsKnown))
                {
                    node.Content = previous.Content;
                    Interlocked.Increment(ref _filesUnchanged);
                    Interlocked.Add(ref _totalBytes, node.Size);
                    Report(false);
                    return node;
                }

                node.Content = new List<string>();
                long read = 0;
                try
                {
                    using (var stream = WindowsFiles.OpenRead(file.FullName))
                    {
                        var chunker = _chunkers.Value;
                        chunker.Reset(stream);
                        bool compress = !IncompressibleExtensions.Contains(file.Extension);
                        while (chunker.Next(out var chunk))
                        {
                            _cancel.ThrowIfCancellationRequested();
                            string id = _repo.Keys.ComputeId(chunk.Array, chunk.Offset, chunk.Count);
                            Writer.Add(BlobType.Data, id, chunk.Array, chunk.Offset, chunk.Count, compress);
                            node.Content.Add(id);
                            read += chunk.Count;
                            Interlocked.Add(ref _bytesRead, chunk.Count);
                            Report(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    bool vanished = ex is FileNotFoundException || ex is DirectoryNotFoundException;
                    if (vanished)
                        _issues.Warning(displayPath, "File was deleted during the backup.");
                    else
                        _issues.Error(displayPath, Describe(ex));
                    Interlocked.Increment(ref _filesSkipped);
                    return null;
                }

                node.Size = read;
                file.Refresh();
                if (file.Exists && (file.LastWriteTimeUtc.Ticks != node.MTime || file.Length != read))
                    _issues.Warning(displayPath, "File changed while it was being backed up; the stored copy may be inconsistent.");

                if (previous == null || previous.Type != NodeType.File)
                    Interlocked.Increment(ref _filesNew);
                else if (previous.Content != null && previous.Content.SequenceEqual(node.Content))
                    Interlocked.Increment(ref _filesUnchanged); // re-read (full mode or touched), same content
                else
                    Interlocked.Increment(ref _filesChanged);
                Interlocked.Add(ref _totalBytes, read);
                return node;
            }

            private string StoreTree(Tree tree)
            {
                byte[] json = Json.ToBytes(tree);
                string id = _repo.Keys.ComputeId(json);
                Writer.Add(BlobType.Tree, id, json, 0, json.Length, compress: true);
                return id;
            }

            private Tree LoadPreviousTree(Node previous)
            {
                if (previous == null || previous.Type != NodeType.Dir || previous.Subtree == null)
                    return null;
                try
                {
                    return _repo.LoadTree(previous.Subtree);
                }
                catch (KeepsakeException)
                {
                    return null;
                }
            }

            private static void ApplyMetadata(Node node, FileSystemInfo info)
            {
                node.MTime = info.LastWriteTimeUtc.Ticks;
                node.CTime = info.CreationTimeUtc.Ticks;
                node.Attributes = (int)(info.Attributes & StoredAttributes);
            }

            private void Report(bool force)
            {
                if (_progress == null)
                    return;
                var now = DateTime.UtcNow;
                if (!force && (now - _lastReport).TotalMilliseconds < 250)
                    return;
                _lastReport = now;
                _progress.Report(new BackupProgress
                {
                    Files = Interlocked.Read(ref _filesNew) + Interlocked.Read(ref _filesChanged) + Interlocked.Read(ref _filesUnchanged),
                    Dirs = Interlocked.Read(ref _dirs),
                    BytesRead = Interlocked.Read(ref _bytesRead),
                    BytesStored = Writer.BytesStored,
                    CurrentPath = _current,
                });
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
                    ExceptionDispatchInfo.Capture(first).Throw();
                }
            }
        }

        private sealed class VirtualDir
        {
            public Dictionary<string, VirtualDir> Children { get; } = new Dictionary<string, VirtualDir>(StringComparer.OrdinalIgnoreCase);
            public string Source { get; set; }

            public VirtualDir Child(string name)
            {
                if (!Children.TryGetValue(name, out var child))
                {
                    child = new VirtualDir();
                    Children[name] = child;
                }
                return child;
            }
        }

        internal static string Describe(Exception ex)
        {
            const int SharingViolation = unchecked((int)0x80070020);
            const int LockViolation = unchecked((int)0x80070021);
            if (ex is UnauthorizedAccessException)
                return "Access denied.";
            if (ex.HResult == SharingViolation || ex.HResult == LockViolation)
                return "File is locked by another program. Enable VSS for the job to back up open files.";
            if (ex is PathTooLongException)
                return "Path is too long.";
            return ex.Message;
        }
    }
}
