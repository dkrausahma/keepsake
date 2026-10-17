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

        public static string PackPath(string id) => Data + "/" + id.Substring(0, 2) + "/" + id;
        public static string KeyPath(string id) => Keys + "/" + id;
        public static string IndexPath(string id) => Index + "/" + id;
        public static string SnapshotPath(string id) => Snapshots + "/" + id;
        public static string LockPath(string id) => Locks + "/" + id;
    }
}
