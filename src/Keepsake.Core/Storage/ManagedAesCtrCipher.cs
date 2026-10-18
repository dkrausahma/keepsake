using System;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Renci.SshNet.Security.Cryptography;

namespace Keepsake.Core.Storage
{
    /// <summary>
    /// AES in CTR mode for SSH (RFC 4344), computed with BouncyCastle instead of the Windows crypto provider.
    /// SSH.NET's own aes*-ctr uses the Windows AES implementation; on Windows Server 2008 R2 with .NET Framework 4.8
    /// that produced "MAC error" against servers offering only AES-CTR (e.g. ProFTPD mod_sftp on a Hetzner Storage
    /// Box, port 22), while BouncyCastle's AES-GCM worked. The counter is the IV, incremented big-endian per block;
    /// the keystream continues across calls, so packets may be processed in any split.
    /// </summary>
    internal sealed class ManagedAesCtrCipher : Cipher
    {
        private const int BlockSize = 16;
        private readonly AesEngine _aes = new AesEngine();
        private readonly byte[] _counter = new byte[BlockSize];
        private readonly byte[] _keystream = new byte[BlockSize];
        private int _used = BlockSize;

        public ManagedAesCtrCipher(byte[] key, byte[] iv)
        {
            if (iv == null || iv.Length < BlockSize)
                throw new ArgumentException("AES-CTR needs a 16-byte IV.", nameof(iv));
            _aes.Init(true, new KeyParameter(key));
            Buffer.BlockCopy(iv, 0, _counter, 0, BlockSize);
        }

        public override byte MinimumSize => BlockSize;

        public override byte[] Encrypt(byte[] input, int offset, int length) => Transform(input, offset, length);

        public override byte[] Decrypt(byte[] input, int offset, int length) => Transform(input, offset, length);

        private byte[] Transform(byte[] input, int offset, int length)
        {
            var output = new byte[length];
            for (int i = 0; i < length; i++)
            {
                if (_used == BlockSize)
                {
                    _aes.ProcessBlock(_counter, 0, _keystream, 0);
                    for (int j = BlockSize - 1; j >= 0 && ++_counter[j] == 0; j--)
                    {
                        // carry
                    }
                    _used = 0;
                }
                output[i] = (byte)(input[offset + i] ^ _keystream[_used++]);
            }
            return output;
        }

        /// <summary>Factory for <see cref="Renci.SshNet.CipherInfo"/>: SSH.NET may pass longer key/IV buffers.</summary>
        internal static Cipher Create(int keyBits, byte[] key, byte[] iv)
        {
            var k = new byte[keyBits / 8];
            Buffer.BlockCopy(key, 0, k, 0, k.Length);
            var v = new byte[BlockSize];
            Buffer.BlockCopy(iv, 0, v, 0, BlockSize);
            return new ManagedAesCtrCipher(k, v);
        }
    }
}
