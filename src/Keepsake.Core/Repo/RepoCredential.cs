using Keepsake.Core.Crypto;

namespace Keepsake.Core.Repo
{
    /// <summary>What unlocks a repository: a password or a recovery key.</summary>
    public sealed class RepoCredential
    {
        private RepoCredential(string password, byte[] recoveryKey)
        {
            Password = password;
            RecoveryKey = recoveryKey;
        }

        public string Password { get; }
        public byte[] RecoveryKey { get; }

        public static RepoCredential FromPassword(string password) => new RepoCredential(password, null);

        public static RepoCredential FromRecoveryKey(byte[] key) => new RepoCredential(null, key);

        /// <summary>Parses the <c>KS1-...</c> text form.</summary>
        public static RepoCredential FromRecoveryKeyText(string text) => new RepoCredential(null, Crypto.RecoveryKey.Parse(text));
    }
}
