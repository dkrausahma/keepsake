using System;
using System.IO;
using System.Linq;
using System.Threading;
using Keepsake.Core.Backup;
using Keepsake.Core.Crypto;
using Keepsake.Core.Repo;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;

namespace Keepsake.Core.Tests
{
    /// <summary>Temporary source folder + repository, removed on dispose.</summary>
    public sealed class TestRepo : IDisposable
    {
        public const string Password = "correct horse battery staple";

        static TestRepo()
        {
            // Fast Argon2 for tests; production defaults are much higher.
            PasswordKdf.DefaultMemoryKiB = 1024;
            PasswordKdf.DefaultIterations = 1;
            PasswordKdf.DefaultParallelism = 1;
        }

        public TestRepo(RepoConfig config = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "keepsake-test-" + Rand.HexId(6));
            Source = Path.Combine(Root, "source");
            RepoPath = Path.Combine(Root, "repo");
            Directory.CreateDirectory(Source);
            var init = Repository.Init(new FileSystemStorage(RepoPath), Password, config ?? SmallChunks());
            RecoveryKey = init.RecoveryKey;
            Repo = init.Repository;
        }

        public string Root { get; }
        public string Source { get; }
        public string RepoPath { get; }
        public string RecoveryKey { get; }
        public Repository Repo { get; private set; }

        /// <summary>Small chunk and pack sizes so tests exercise multi-chunk files and multiple packs.</summary>
        public static RepoConfig SmallChunks() => new RepoConfig
        {
            ChunkerMin = 4 * 1024,
            ChunkerAvg = 16 * 1024,
            ChunkerMax = 64 * 1024,
            PackSize = 256 * 1024,
        };

        public string File(string relative, byte[] content)
        {
            string path = Path.Combine(Source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(LongPath.ToExtended(path)));
            System.IO.File.WriteAllBytes(LongPath.ToExtended(path), content);
            return path;
        }

        public string File(string relative, string content) => File(relative, System.Text.Encoding.UTF8.GetBytes(content));

        public static byte[] RandomBytes(int length, int seed)
        {
            var b = new byte[length];
            new Random(seed).NextBytes(b);
            return b;
        }

        /// <summary>Re-opens the repository from storage, like a new process would.</summary>
        public Repository Reopen(RepoCredential credential = null)
        {
            Repo = Repository.Open(new FileSystemStorage(RepoPath), credential ?? RepoCredential.FromPassword(Password));
            return Repo;
        }

        public BackupResult Backup(BackupMode mode = BackupMode.Incremental, params string[] excludes)
        {
            Reopen();
            return new BackupEngine(Repo).Run(new BackupOptions
            {
                Sources = { Source },
                Excludes = excludes.ToList(),
                Mode = mode,
                Job = "test",
            }, null, CancellationToken.None);
        }

        public RestoreResult RestoreTo(string target, RestoreOptions options = null)
        {
            Reopen();
            options = options ?? new RestoreOptions();
            options.Target = RestoreTargetMode.Folder;
            options.TargetFolder = target;
            return new RestoreEngine(Repo).Run(options, null, CancellationToken.None);
        }

        /// <summary>Where a source-relative path lands in a full-path restore into <paramref name="target"/>.</summary>
        public string RestoredPath(string target, string relative)
        {
            var parts = RepoPaths.Split(Path.Combine(Source, relative));
            return RepoPaths.Combine(target, parts);
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(LongPath.ToExtended(Root), "*", SearchOption.AllDirectories))
                    System.IO.File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(LongPath.ToExtended(Root), true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
