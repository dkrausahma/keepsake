using System;
using System.Security.Cryptography;
using System.Text;

namespace Keepsake.Service
{
    /// <summary>
    /// Secrets at rest (SEC-05, KEY-04): DPAPI with machine scope, so the service account (LocalSystem) can
    /// read them without a logged-on user. The copy is a convenience only – recovery never depends on it.
    /// </summary>
    internal static class Secrets
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Keepsake/secret/v1");

        public static string Protect(string plain)
        {
            if (plain == null)
                return null;
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return null;
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Stored secret has an unknown format.");
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), Entropy, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(data);
            }
            catch (CryptographicException ex)
            {
                throw new Core.KeepsakeException("A stored password cannot be decrypted on this machine (" + ex.Message +
                                                 "). Enter the repository password again in the job settings.");
            }
        }
    }
}
