using System.Collections.Generic;
using System.Linq;
using Keepsake.Core.Storage;

namespace Keepsake.Core.Repo
{
    /// <summary>File layout of a repository. See docs/FORMAT.md.</summary>
    public static class Layout
    {
        public const string Header = "keepsake.json";
        public const string Readme = "README.txt";
        public const string Config = "config";
        public const string Keys = "keys";
        public const string Data = "data";
        public const string Index = "index";
        public const string Snapshots = "snapshots";
        public const string Locks = "locks";

        public static string PackPath(string id)
        {
            if (id == null || id.Length < 2)
                throw new RepositoryFormatException("Invalid pack ID '" + id + "'.");
            return Data + "/" + id.Substring(0, 2) + "/" + id;
        }
        public static string KeyPath(string id) => Keys + "/" + id;
        public static string IndexPath(string id) => Index + "/" + id;
        public static string SnapshotPath(string id) => Snapshots + "/" + id;
        public static string LockPath(string id) => Locks + "/" + id;

        /// <summary>Largest index or snapshot file read into memory; larger files are ignored (SEC-009).</summary>
        public const long MaxObjectBytes = 1L << 30;

        /// <summary>Largest lock file read.</summary>
        public const long MaxLockBytes = 1 << 20;

        /// <summary>64 lowercase hex characters: the name of every content-addressed object.</summary>
        public static bool IsObjectId(string name) => name != null && name.Length == 64 && IsHex(name);

        /// <summary>Random IDs of locks and key slots (32 hex characters; 16–64 accepted).</summary>
        public static bool IsRandomId(string name) => name != null && name.Length >= 16 && name.Length <= 64 && IsHex(name);

        private static bool IsHex(string s) => s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        /// <summary>
        /// Lists a repository folder from untrusted storage, keeping only entries that can be repository objects:
        /// valid names, in the right place (packs in their two-character subfolder, so a name occurs only once)
        /// and of a plausible size. Anything else is left alone – never parsed, never deleted (SEC-009).
        /// </summary>
        public static IEnumerable<StorageEntry> ListObjects(IStorage storage, string folder)
        {
            foreach (var e in storage.List(folder))
            {
                switch (folder)
                {
                    case Data:
                        if (IsObjectId(e.Name) && e.Path == PackPath(e.Name))
                            yield return e;
                        break;
                    case Locks:
                        if (IsRandomId(e.Name) && e.Path == LockPath(e.Name) && e.Size <= MaxLockBytes)
                            yield return e;
                        break;
                    default:
                        if (IsObjectId(e.Name) && e.Path == folder + "/" + e.Name && e.Size <= MaxObjectBytes)
                            yield return e;
                        break;
                }
            }
        }
    }
}
