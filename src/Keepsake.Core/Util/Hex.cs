using System;

namespace Keepsake.Core.Util
{
    /// <summary>Lowercase hexadecimal encoding, used for all object IDs in a repository.</summary>
    public static class Hex
    {
        private const string Digits = "0123456789abcdef";

        public static string Encode(byte[] data) => Encode(data, 0, data.Length);

        public static string Encode(byte[] data, int offset, int count)
        {
            var chars = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                chars[2 * i] = Digits[b >> 4];
                chars[2 * i + 1] = Digits[b & 0xF];
            }
            return new string(chars);
        }

        public static byte[] Decode(string hex)
        {
            if (hex == null || hex.Length % 2 != 0)
                throw new FormatException("Invalid hex string.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((Nibble(hex[2 * i]) << 4) | Nibble(hex[2 * i + 1]));
            return bytes;
        }

        public static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                    return false;
            }
            return true;
        }

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new FormatException("Invalid hex character '" + c + "'.");
        }
    }
}
