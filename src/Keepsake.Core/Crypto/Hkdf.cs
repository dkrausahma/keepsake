using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Keepsake.Core.Crypto
{
    /// <summary>HKDF-SHA256 (RFC 5869).</summary>
    public static class Hkdf
    {
        public static byte[] DeriveKey(byte[] inputKey, byte[] salt, string info, int length = 32) =>
            DeriveKey(inputKey, salt, Encoding.UTF8.GetBytes(info), length);

        internal static byte[] DeriveKey(byte[] inputKey, byte[] salt, byte[] infoBytes, int length)
        {
            byte[] prk;
            using (var extract = new HMACSHA256(salt ?? new byte[32]))
                prk = extract.ComputeHash(inputKey);

            var okm = new byte[length];
            var previous = new byte[0];
            using (var expand = new HMACSHA256(prk))
            {
                int written = 0;
                for (byte counter = 1; written < length; counter++)
                {
                    var input = new MemoryStream();
                    input.Write(previous, 0, previous.Length);
                    input.Write(infoBytes, 0, infoBytes.Length);
                    input.WriteByte(counter);
                    previous = expand.ComputeHash(input.ToArray());
                    int take = Math.Min(previous.Length, length - written);
                    Buffer.BlockCopy(previous, 0, okm, written, take);
                    written += take;
                }
            }
            return okm;
        }
    }
}
