using System;
using System.Security.Cryptography;
using System.Text;

namespace Keepsake.Service
{
    /// <summary>
    /// Secrets at rest (SEC-05, KEY-04): DPAPI for the account the service runs as (LocalSystem), so other local
    /// users cannot decrypt a stored value even if they get hold of the file (SEC-001). Values written by
    /// Keepsake 0.9.1 and earlier used machine scope (<c>dpapi:</c>); they are still read and are converted at
    /// service start. The copy is a convenience only – recovery never depends on it.
    /// </summary>
    internal static class Secrets
    {
        private const string Prefix = "dpapi-u:";
        private const string LegacyPrefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Keepsake/secret/v1");

        public static string Protect(string plain)
        {
            if (plain == null)
                return null;
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return null;
            string payload;
            DataProtectionScope scope;
            if (stored.StartsWith(Prefix, StringComparison.Ordinal))
            {
                payload = stored.Substring(Prefix.Length);
                scope = DataProtectionScope.CurrentUser;
            }
            else if (stored.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                payload = stored.Substring(LegacyPrefix.Length);
                scope = DataProtectionScope.LocalMachine;
            }
            else
            {
                throw new InvalidOperationException("Stored secret has an unknown format.");
            }
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(payload), Entropy, scope);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                throw new Core.KeepsakeException("A stored password cannot be decrypted on this machine (" + ex.Message +
                                                 "). Enter the repository password again in the job settings.");
            }
        }

        /// <summary>True for a value in the old machine-scope format.</summary>
        public static bool IsLegacy(string stored) =>
            !string.IsNullOrEmpty(stored) && stored.StartsWith(LegacyPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Re-protects a legacy value for the service account. Returns the value unchanged when it is already
        /// current or cannot be decrypted (the user then re-enters it, as before).
        /// </summary>
        public static string Upgrade(string stored)
        {
            if (!IsLegacy(stored))
                return stored;
            try
            {
                return Protect(Unprotect(stored));
            }
            catch (Core.KeepsakeException)
            {
                return stored;
            }
        }
    }
}
