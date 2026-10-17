using System;
using System.Text;
using Keepsake.Core.Util;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Keepsake.Core.Crypto
{
    /// <summary>
    /// AES-256-GCM with a random 96-bit nonce. Output layout: <c>nonce (12) || ciphertext || tag (16)</c>.
    /// .NET Framework 4.8 has no AES-GCM, so BouncyCastle provides the implementation.
    /// </summary>
    public static class Aead
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;
        public const int Overhead = NonceSize + TagSize;

        /// <summary>Associated data for a repository object kind, binding ciphertext to its purpose.</summary>
        public static byte[] Context(string kind) => Encoding.UTF8.GetBytes("keepsake/v1/" + kind);

        public static byte[] Seal(byte[] key, byte[] plaintext, byte[] associatedData) =>
            Seal(key, plaintext, 0, plaintext.Length, associatedData);

        public static byte[] Seal(byte[] key, byte[] plaintext, int offset, int count, byte[] associatedData)
        {
            byte[] nonce = Rand.Bytes(NonceSize);
            var gcm = new GcmBlockCipher(new AesEngine());
            gcm.Init(true, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, associatedData));
            var output = new byte[NonceSize + gcm.GetOutputSize(count)];
            Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
            int written = gcm.ProcessBytes(plaintext, offset, count, output, NonceSize);
            gcm.DoFinal(output, NonceSize + written);
            return output;
        }

        public static byte[] Open(byte[] key, byte[] data, byte[] associatedData) =>
            Open(key, data, 0, data.Length, associatedData);

        public static byte[] Open(byte[] key, byte[] data, int offset, int count, byte[] associatedData)
        {
            if (count < Overhead)
                throw new CryptoFailureException("Encrypted data is too short.");
            var nonce = new byte[NonceSize];
            Buffer.BlockCopy(data, offset, nonce, 0, NonceSize);
            var gcm = new GcmBlockCipher(new AesEngine());
            gcm.Init(false, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, associatedData));
            var output = new byte[gcm.GetOutputSize(count - NonceSize)];
            try
            {
                int written = gcm.ProcessBytes(data, offset + NonceSize, count - NonceSize, output, 0);
                written += gcm.DoFinal(output, written);
                if (written != output.Length)
                    Array.Resize(ref output, written);
                return output;
            }
            catch (InvalidCipherTextException)
            {
                throw new CryptoFailureException("Decryption failed: wrong key, or the data is corrupted.");
            }
        }
    }
}
