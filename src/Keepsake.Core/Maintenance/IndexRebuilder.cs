using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Storage;

namespace Keepsake.Core.Maintenance
{
    public sealed class RepairResult : RunReport
    {
        public int PacksRead { get; set; }
        public int PacksDamaged { get; set; }
        public int BlobsIndexed { get; set; }
        public int OldIndexFiles { get; set; }
    }

    /// <summary>
    /// Rebuilds the index from the pack headers (VER-05) – for lost or damaged index files. Only headers
    /// are read (two small reads per pack). Damaged packs are reported and left untouched.
    /// </summary>
    public sealed class IndexRebuilder
    {
        private readonly Repository _repo;

        public IndexRebuilder(Repository repo)
        {
            _repo = repo;
        }

        public RepairResult Run(IProgress<string> progress, CancellationToken cancel)
        {
            var result = new RepairResult { Start = DateTime.UtcNow };
            var phases = new PhaseRecorder(result.Phases);
            var issues = new IssueLog(result.Issues);
            try
            {
                using (RepoLock.Acquire(_repo, exclusive: true, purpose: "repair"))
                {
                    var packs = new List<IndexPack>();
                    using (phases.Begin("Read pack headers"))
                    {
                        var stored = _repo.Storage.List(Layout.Data).ToList();
                        foreach (var entry in stored)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (++result.PacksRead % 25 == 0)
                                progress?.Report("Reading pack headers " + result.PacksRead + " of " + stored.Count);
                            try
                            {
                                var blobs = PackReader.ReadHeader(_repo.Storage, entry.Name, _repo.Keys.Encryption);
                                packs.Add(new IndexPack
                                {
                                    Id = entry.Name,
                                    Blobs = blobs.Select(b => new IndexBlob { Id = b.Id, Type = b.Type, Offset = b.Offset, Length = b.Length, RawLength = b.RawLength }).ToList(),
                                });
                                result.BlobsIndexed += blobs.Count;
                            }
                            catch (KeepsakeException ex)
                            {
                                result.PacksDamaged++;
                                issues.Error(entry.Path, "Pack header cannot be read, the pack is left out of the index: " + ex.Message);
                            }
                        }
                    }

                    using (phases.Begin("Write index"))
                    {
                        var old = _repo.Storage.List(Layout.Index).Select(e => e.Path).ToList();
                        result.OldIndexFiles = old.Count;
                        var written = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < packs.Count; i += 2000)
                            written.Add(Layout.IndexPath(_repo.SaveIndex(new IndexFile { Packs = packs.Skip(i).Take(2000).ToList() })));
                        foreach (string path in old.Where(p => !written.Contains(p)))
                            _repo.Storage.Delete(path);
                    }
                }
                result.Status = result.PacksDamaged > 0 ? RunStatus.Warning : RunStatus.Success;
            }
            catch (OperationCanceledException)
            {
                result.Status = RunStatus.Cancelled;
                result.Error = "Repair was cancelled; the old index files are still in place.";
            }
            catch (KeepsakeException ex)
            {
                result.Status = RunStatus.Failed;
                result.Error = ex.Message;
            }
            result.End = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>
    /// Keeps a portable copy of the command-line tool in the repository (<c>tools/keepsake-&lt;version&gt;/</c>,
    /// unencrypted, REP-11), so a restore is possible even when nothing but the backup is left.
    /// </summary>
    public static class ToolCopy
    {
        public const string Folder = "tools";

        /// <summary>Uploads keepsake.exe and its libraries once per version. Returns true when something was copied.</summary>
        public static bool Ensure(IStorage storage, string installFolder, string version)
        {
            string exe = Path.Combine(installFolder, "keepsake.exe");
            if (!File.Exists(exe))
                return false; // development build without the CLI next to the service
            string target = Folder + "/keepsake-" + version;
            if (storage.Exists(target + "/keepsake.exe"))
                return false;
            var files = Directory.GetFiles(installFolder, "*.dll")
                .Concat(new[] { exe + ".config", exe })
                .Where(File.Exists)
                .ToList();
            foreach (string f in files)
                storage.Write(target + "/" + Path.GetFileName(f), File.ReadAllBytes(f));
            // The executable is written last: its presence marks a complete copy.
            return true;
        }
    }
}
