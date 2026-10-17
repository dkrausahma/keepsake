using System;
using System.Threading;
using ZstdSharp;

namespace Keepsake.Core.Crypto
{
    /// <summary>
    /// Plaintext envelope inside every encrypted object: first byte is the codec
    /// (0 = stored, 1 = Zstandard frame), followed by the payload.
    /// </summary>
    public static class BlobCodec
    {
        public const byte Stored = 0;
        public const byte Zstd = 1;
        private const int MinCompressSize = 128;

        private static readonly ThreadLocal<Compressor> Compressors = new ThreadLocal<Compressor>(() => new Compressor(3));
        private static readonly ThreadLocal<Decompressor> Decompressors = new ThreadLocal<Decompressor>(() => new Decompressor());

        public static byte[] Encode(byte[] data, bool compress) => Encode(data, 0, data.Length, compress);

        public static byte[] Encode(byte[] data, int offset, int count, bool compress)
        {
            if (compress && count >= MinCompressSize)
            {
                Span<byte> packed = Compressors.Value.Wrap(new ReadOnlySpan<byte>(data, offset, count));
                // Keep the compressed form only when it saves at least ~3 %.
                if (packed.Length < count - count / 32)
                {
                    var result = new byte[packed.Length + 1];
                    result[0] = Zstd;
                    packed.CopyTo(new Span<byte>(result, 1, packed.Length));
                    return result;
                }
            }
            var stored = new byte[count + 1];
            stored[0] = Stored;
            Buffer.BlockCopy(data, offset, stored, 1, count);
            return stored;
        }

        public static byte[] Decode(byte[] encoded)
        {
            if (encoded.Length == 0)
                throw new RepositoryFormatException("Empty blob payload.");
            switch (encoded[0])
            {
                case Stored:
                    var raw = new byte[encoded.Length - 1];
                    Buffer.BlockCopy(encoded, 1, raw, 0, raw.Length);
                    return raw;
                case Zstd:
                    try
                    {
                        return Decompressors.Value.Unwrap(new ReadOnlySpan<byte>(encoded, 1, encoded.Length - 1)).ToArray();
                    }
                    catch (ZstdException ex)
                    {
                        throw new RepositoryCorruptException("Compressed data is damaged: " + ex.Message);
                    }
                default:
                    throw new RepositoryFormatException("Unknown blob codec " + encoded[0] + ".");
            }
        }
    }
}
