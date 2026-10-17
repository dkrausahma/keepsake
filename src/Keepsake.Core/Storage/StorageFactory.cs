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
            if (IsSftp(location))
                throw new KeepsakeException("SFTP locations need credentials; open them with an SftpStorage.");
            if (location.IndexOf("://", StringComparison.Ordinal) >= 0)
                throw new KeepsakeException("Unsupported repository location '" + location + "'. Use a folder, a network share or sftp://user@host/path.");
            return new FileSystemStorage(location);
        }

        public static bool IsSftp(string location) => location != null && location.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);

        /// <summary>Parses <c>sftp://user@host[:port]/path</c> (credentials are supplied separately).</summary>
        public static SftpOptions ParseSftp(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
                throw new KeepsakeException("'" + url + "' is not a valid SFTP location (sftp://user@host[:port]/path).");
            if (string.IsNullOrEmpty(uri.UserInfo))
                throw new KeepsakeException("The SFTP location needs a user name: sftp://user@" + uri.Host + uri.AbsolutePath);
            if (uri.UserInfo.Contains(":"))
                throw new KeepsakeException("Do not put the password into the SFTP location. Use --sftp-password-file instead.");
            return new SftpOptions
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort || uri.Port <= 0 ? 22 : uri.Port,
                User = Uri.UnescapeDataString(uri.UserInfo),
                Path = Uri.UnescapeDataString(uri.AbsolutePath),
            };
        }
    }
}
