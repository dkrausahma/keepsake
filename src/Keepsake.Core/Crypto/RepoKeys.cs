using System.Security.Cryptography;
using System.Threading;
using Keepsake.Core.Util;

namespace Keepsake.Core.Crypto
{
    /// <summary>
    /// Keys derived from the repository master key via HKDF (salt = repository ID):
    /// one for AES-256-GCM encryption, one for HMAC-SHA256 blob IDs, one seeding the chunker.
    /// </summary>
    public sealed class RepoKeys
    {
        private readonly ThreadLocal<HMACSHA256> _idHmac;

        public RepoKeys(byte[] masterKey, byte[] repositoryId)
        {
            Master = masterKey;
            Encryption = Hkdf.DeriveKey(masterKey, repositoryId, "keepsake/v1/encryption");
            Id = Hkdf.DeriveKey(masterKey, repositoryId, "keepsake/v1/blob-id");
            Chunker = Hkdf.DeriveKey(masterKey, repositoryId, "keepsake/v1/chunker");
            byte[] idKey = Id;
            _idHmac = new ThreadLocal<HMACSHA256>(() => new HMACSHA256(idKey));
        }

        public byte[] Master { get; }
        public byte[] Encryption { get; }
        public byte[] Id { get; }
        public byte[] Chunker { get; }

        /// <summary>
        /// Blob ID = HMAC-SHA256(id key, plaintext). Keyed so that the storage operator cannot
        /// confirm whether a known file is present by computing its hash.
        /// </summary>
        public string ComputeId(byte[] data, int offset, int count) =>
            Hex.Encode(_idHmac.Value.ComputeHash(data, offset, count));

        public string ComputeId(byte[] data) => ComputeId(data, 0, data.Length);
    }
}
