using System;

namespace Keepsake.Core.Storage
{
    public static class StorageFactory
    {
        /// <summary>
        /// Opens a repository location. Today only file system paths (local, UNC, USB);
        /// <c>sftp://</c> follows in milestone 4.
        /// </summary>
        public static IStorage Open(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
                throw new KeepsakeException("No repository location given.");
            if (location.IndexOf("://", StringComparison.Ordinal) >= 0)
                throw new KeepsakeException("Unsupported repository location '" + location + "'. Only folders, network shares and USB disks are supported so far.");
            return new FileSystemStorage(location);
        }
    }
}
