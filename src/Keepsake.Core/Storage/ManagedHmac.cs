using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace Keepsake.Core.Storage
{
    /// <summary>
    /// HMAC for SSH packets, computed with BouncyCastle instead of the .NET/Windows HMACSHA* classes. On Windows
    /// Server 2008 R2 with .NET Framework 4.8 every connection that needs an HMAC (AES-CTR + hmac-sha2-*, e.g. ProFTPD
    /// mod_sftp on a Hetzner Storage Box, port 22) failed with "MAC error" – even with AES-CTR computed by BouncyCastle –
    /// while AES-GCM (no HMAC) worked. Derives from <see cref="HMAC"/> so SSH.NET can treat it like the built-in ones.
    /// </summary>
    internal sealed class ManagedHmac : HMAC
    {
        private readonly HMac _mac;

        public ManagedHmac(Func<IDigest> digest, byte[] key)
        {
            _mac = new HMac(digest());
            KeyValue = (byte[])key.Clone();
            HashSizeValue = _mac.GetMacSize() * 8;
            _mac.Init(new KeyParameter(KeyValue));
        }

        public override byte[] Key
        {
            get => (byte[])KeyValue.Clone();
            set => throw new NotSupportedException("The key is fixed for an SSH session.");
        }

        public override void Initialize() => _mac.Reset();

        protected override void HashCore(byte[] array, int ibStart, int cbSize) => _mac.BlockUpdate(array, ibStart, cbSize);

        protected override byte[] HashFinal()
        {
            var result = new byte[_mac.GetMacSize()];
            _mac.DoFinal(result, 0); // also resets for the next packet
            return result;
        }

        internal static Func<IDigest> DigestFor(string sshName)
        {
            if (sshName.StartsWith("hmac-sha2-256", StringComparison.Ordinal))
                return () => new Sha256Digest();
            if (sshName.StartsWith("hmac-sha2-512", StringComparison.Ordinal))
                return () => new Sha512Digest();
            if (sshName.StartsWith("hmac-sha1", StringComparison.Ordinal))
                return () => new Sha1Digest();
            return null;
        }
    }
}
