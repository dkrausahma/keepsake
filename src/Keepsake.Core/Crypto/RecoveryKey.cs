using System;
using System.Text;
using Keepsake.Core.Util;

namespace Keepsake.Core.Crypto
{
    /// <summary>
    /// A 256-bit recovery key written as text for KeePass or paper:
    /// <c>KS1-XXXXX-XXXXX-...</c> (Crockford Base32 of key + 2-byte SHA-256 checksum).
    /// The checksum catches typos before an expensive and confusing "wrong key" error.
    /// </summary>
    public static class RecoveryKey
    {
        public const string Prefix = "KS1";
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int KeyLength = 32;
        private const int ChecksumLength = 2;

        public static byte[] Generate() => Rand.Bytes(KeyLength);

        public static string Format(byte[] key)
        {
            if (key.Length != KeyLength)
                throw new ArgumentException("Recovery keys are 32 bytes.", nameof(key));
            var payload = new byte[KeyLength + ChecksumLength];
            Buffer.BlockCopy(key, 0, payload, 0, KeyLength);
            Buffer.BlockCopy(Sha256.Compute(key), 0, payload, KeyLength, ChecksumLength);

            string encoded = Base32Encode(payload);
            var sb = new StringBuilder(Prefix);
            for (int i = 0; i < encoded.Length; i += 5)
                sb.Append('-').Append(encoded.Substring(i, Math.Min(5, encoded.Length - i)));
            return sb.ToString();
        }

        public static byte[] Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("The recovery key is empty.");

            // Accept whole key files (with a descriptive header): use the line holding the key.
            string candidate = text.Trim();
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = line.Trim();
                    break;
                }
            }

            var sb = new StringBuilder();
            foreach (char c in candidate.ToUpperInvariant())
            {
                if (c == '-' || char.IsWhiteSpace(c))
                    continue;
                sb.Append(c);
            }
            string s = sb.ToString();
            if (!s.StartsWith(Prefix, StringComparison.Ordinal))
                throw new FormatException("This is not a Keepsake recovery key (it must start with '" + Prefix + "-').");
            s = s.Substring(Prefix.Length);

            byte[] payload = Base32Decode(s, KeyLength + ChecksumLength);
            var key = new byte[KeyLength];
            Buffer.BlockCopy(payload, 0, key, 0, KeyLength);
            byte[] check = Sha256.Compute(key);
            if (payload[KeyLength] != check[0] || payload[KeyLength + 1] != check[1])
                throw new FormatException("The recovery key contains a typo (checksum mismatch).");
            return key;
        }

        private static string Base32Encode(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0)
                sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        private static byte[] Base32Decode(string s, int length)
        {
            var result = new byte[length];
            int buffer = 0, bits = 0, index = 0;
            foreach (char raw in s)
            {
                char c = raw == 'O' ? '0' : (raw == 'I' || raw == 'L') ? '1' : raw;
                int value = Alphabet.IndexOf(c);
                if (value < 0)
                    throw new FormatException("The recovery key contains an invalid character '" + raw + "'.");
                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    if (index >= length)
                        throw new FormatException("The recovery key is too long.");
                    result[index++] = (byte)(buffer >> (bits - 8));
                    bits -= 8;
                }
            }
            if (index != length)
                throw new FormatException("The recovery key is incomplete.");
            return result;
        }
    }
}
