using System;
using System.Collections.Generic;

namespace Keepsake.Core.Storage
{
    public sealed class StorageEntry
    {
        public StorageEntry(string path, long size)
        {
            Path = path;
            Size = size;
        }

        /// <summary>Path relative to the repository root, using '/' separators.</summary>
        public string Path { get; }
        public long Size { get; }

        public string Name
        {
            get
            {
                int i = Path.LastIndexOf('/');
                return i < 0 ? Path : Path.Substring(i + 1);
            }
        }
    }

    /// <summary>
    /// Minimal object store a repository lives on. Implementations: local/UNC/USB file system;
    /// SFTP and S3 later. All paths are relative and use '/' separators.
    /// </summary>
    public interface IStorage : IDisposable
    {
        /// <summary>Human-readable location, e.g. <c>E:\Backups\fileserver</c>.</summary>
        string Location { get; }

        bool Exists(string path);
        long GetSize(string path);
        byte[] Read(string path);
        byte[] ReadRange(string path, long offset, int length);

        /// <summary>Writes atomically: readers see either the old file, the complete new file, or nothing.</summary>
        void Write(string path, byte[] data);

        void Delete(string path);

        /// <summary>All files below <paramref name="directory"/>, recursively.</summary>
        IEnumerable<StorageEntry> List(string directory);
    }
}
