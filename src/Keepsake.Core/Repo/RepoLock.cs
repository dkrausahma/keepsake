using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Keepsake.Core.Util;

namespace Keepsake.Core.Repo
{
    /// <summary>
    /// Repository lock file (<c>locks/&lt;random id&gt;</c>, encrypted). Backups, restores and checks take shared
    /// locks; maintenance that deletes data (prune) takes an exclusive one. Locks are refreshed every
    /// 5 minutes and count as stale after 30 minutes without refresh.
    /// </summary>
    public sealed class RepoLock : IDisposable
    {
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(5);

        private readonly Repository _repo;
        private readonly LockInfo _info;
        private readonly Timer _timer;
        private readonly object _sync = new object();
        private string _path;
        private bool _disposed;

        private RepoLock(Repository repo, LockInfo info, string path)
        {
            _repo = repo;
            _info = info;
            _path = path;
            _timer = new Timer(_ => Refresh(), null, RefreshEvery, RefreshEvery);
        }

        public static RepoLock Acquire(Repository repo, bool exclusive, string purpose)
        {
            var info = new LockInfo
            {
                Host = ProgramInfo.Host,
                User = ProgramInfo.User,
                Pid = Process.GetCurrentProcess().Id,
                Time = DateTime.UtcNow,
                Exclusive = exclusive,
                Purpose = purpose,
            };
            ThrowIfConflicting(repo, exclusive, null);
            string path = Layout.LockPath(Rand.HexId(16));
            repo.Storage.Write(path, repo.EncryptObject(info, "lock"));
            try
            {
                // Re-check after writing: closes the race between two processes starting at once.
                ThrowIfConflicting(repo, exclusive, path);
            }
            catch
            {
                repo.Storage.Delete(path);
                throw;
            }
            return new RepoLock(repo, info, path);
        }

        /// <summary>All lock files with their content; unreadable ones have a null value.</summary>
        public static List<KeyValuePair<string, LockInfo>> List(Repository repo)
        {
            var result = new List<KeyValuePair<string, LockInfo>>();
            foreach (var entry in repo.Storage.List(Layout.Locks))
            {
                LockInfo info = null;
                try
                {
                    info = repo.DecryptObject<LockInfo>(repo.Storage.Read(entry.Path), "lock");
                }
                catch (KeepsakeException)
                {
                    // Unreadable lock: treated like a stale one.
                }
                result.Add(new KeyValuePair<string, LockInfo>(entry.Path, info));
            }
            return result;
        }

        public static bool IsStale(LockInfo info) => info == null || DateTime.UtcNow - info.Time > StaleAfter;

        /// <summary>Removes stale locks (or all locks with <paramref name="all"/>). Returns the number removed.</summary>
        public static int RemoveLocks(Repository repo, bool all)
        {
            int removed = 0;
            foreach (var l in List(repo))
            {
                if (all || IsStale(l.Value))
                {
                    repo.Storage.Delete(l.Key);
                    removed++;
                }
            }
            return removed;
        }

        private static void ThrowIfConflicting(Repository repo, bool exclusive, string ownPath)
        {
            foreach (var l in List(repo))
            {
                if (l.Key == ownPath || IsStale(l.Value))
                    continue;
                if (exclusive || l.Value.Exclusive)
                {
                    var o = l.Value;
                    throw new RepositoryLockedException(
                        "The repository is in use by '" + o.Purpose + "' on " + o.Host + " (process " + o.Pid + ", user " + o.User +
                        ", since " + o.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "). If that process no longer runs, " +
                        "wait 30 minutes or run 'keepsake unlock'.");
                }
            }
        }

        private void Refresh()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                try
                {
                    _info.Time = DateTime.UtcNow;
                    string newPath = Layout.LockPath(Rand.HexId(16));
                    _repo.Storage.Write(newPath, _repo.EncryptObject(_info, "lock"));
                    _repo.Storage.Delete(_path);
                    _path = newPath;
                }
                catch (Exception)
                {
                    // A failed refresh is retried on the next tick; the run itself reports storage errors.
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _timer.Dispose();
                try
                {
                    _repo.Storage.Delete(_path);
                }
                catch (Exception)
                {
                    // Leaves a lock that becomes stale after 30 minutes.
                }
            }
        }
    }
}
