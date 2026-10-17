using System;
using System.IO;
using System.Security.Cryptography;

namespace Keepsake.Core.Util
{
    internal static class Bytes
    {
        public static void WriteUInt32(Stream s, uint value)
        {
            s.WriteByte((byte)value);
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)(value >> 16));
            s.WriteByte((byte)(value >> 24));
        }

        public static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        public static ulong ReadUInt64(byte[] data, int offset) =>
            ReadUInt32(data, offset) | ((ulong)ReadUInt32(data, offset + 4) << 32);

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>Cryptographically secure random bytes.</summary>
    public static class Rand
    {
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] Bytes(int count)
        {
            var buffer = new byte[count];
            lock (Rng)
                Rng.GetBytes(buffer);
            return buffer;
        }

        public static string HexId(int byteCount = 16) => Hex.Encode(Bytes(byteCount));
    }

    /// <summary>SHA-256, using the faster CNG/CAPI implementation on .NET Framework when available.</summary>
    public static class Sha256
    {
        [ThreadStatic] private static HashAlgorithm _hash;

        public static byte[] Compute(byte[] data) => Compute(data, 0, data.Length);

        public static byte[] Compute(byte[] data, int offset, int count)
        {
            var h = _hash ?? (_hash = Create());
            return h.ComputeHash(data, offset, count);
        }

        public static string HexOf(byte[] data) => Hex.Encode(Compute(data));

        private static HashAlgorithm Create()
        {
            foreach (var name in new[]
            {
                "System.Security.Cryptography.SHA256Cng, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
                "System.Security.Cryptography.SHA256CryptoServiceProvider, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            })
            {
                try
                {
                    var type = Type.GetType(name, false);
                    if (type != null && Activator.CreateInstance(type) is HashAlgorithm alg)
                        return alg;
                }
                catch (Exception)
                {
                    // Fall through to the managed implementation (e.g. FIPS policy or non-Framework runtime).
                }
            }
            return SHA256.Create();
        }
    }
}
