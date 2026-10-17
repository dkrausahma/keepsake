using System;
using System.IO;
using System.Security.Cryptography;

namespace Keepsake.Core.Chunking
{
    /// <summary>
    /// FastCDC content-defined chunking (Xia et al., 2016) with normalized chunking.
    /// Boundaries depend only on content, so inserting bytes into a file shifts at most a few chunks.
    /// The gear table is derived from the repository's chunker key, so chunk sizes do not reveal
    /// which known files a repository contains.
    /// </summary>
    public sealed class Chunker
    {
        private readonly ulong[] _gear;
        private readonly int _min;
        private readonly int _avg;
        private readonly int _max;
        private readonly ulong _maskSmall;
        private readonly ulong _maskLarge;
        private readonly byte[] _buffer;
        private Stream _stream;
        private int _start;
        private int _end;
        private bool _eof;

        public Chunker(byte[] chunkerKey, int min, int avg, int max)
        {
            if (!(min > 64 && min < avg && avg < max))
                throw new ArgumentException("Invalid chunk sizes.");
            _gear = GearTable(chunkerKey);
            _min = min;
            _avg = avg;
            _max = max;
            int bits = (int)Math.Floor(Math.Log(avg, 2));
            _maskSmall = Mask(bits + 2);
            _maskLarge = Mask(bits - 2);
            _buffer = new byte[2 * max];
        }

        public void Reset(Stream stream)
        {
            _stream = stream;
            _start = 0;
            _end = 0;
            _eof = false;
        }

        /// <summary>
        /// Returns the next chunk. The segment points into an internal buffer and is only valid until the next call.
        /// </summary>
        public bool Next(out ArraySegment<byte> chunk)
        {
            int available = _end - _start;
            if (available < _max && !_eof)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, available);
                    _start = 0;
                    _end = available;
                }
                while (!_eof && _end < _buffer.Length)
                {
                    int n = _stream.Read(_buffer, _end, _buffer.Length - _end);
                    if (n == 0)
                        _eof = true;
                    else
                        _end += n;
                }
                available = _end - _start;
            }

            if (available == 0)
            {
                chunk = default(ArraySegment<byte>);
                return false;
            }

            int cut = FindCut(_buffer, _start, available);
            chunk = new ArraySegment<byte>(_buffer, _start, cut);
            _start += cut;
            return true;
        }

        private int FindCut(byte[] b, int offset, int length)
        {
            if (length <= _min)
                return length;
            int n = length < _max ? length : _max;
            int normal = n < _avg ? n : _avg;
            ulong fp = 0;
            int i = _min;
            for (; i < normal; i++)
            {
                fp = (fp << 1) + _gear[b[offset + i]];
                if ((fp & _maskSmall) == 0)
                    return i + 1;
            }
            for (; i < n; i++)
            {
                fp = (fp << 1) + _gear[b[offset + i]];
                if ((fp & _maskLarge) == 0)
                    return i + 1;
            }
            return n;
        }

        /// <summary>Mask over the most significant bits, which carry the most history in a gear hash.</summary>
        private static ulong Mask(int bits) => ((1UL << bits) - 1) << (64 - bits);

        /// <summary>gear[i] = first 8 bytes (little-endian) of HMAC-SHA256(chunker key, uint32 LE i).</summary>
        internal static ulong[] GearTable(byte[] key)
        {
            var table = new ulong[256];
            using (var hmac = new HMACSHA256(key))
            {
                for (int i = 0; i < 256; i++)
                {
                    byte[] h = hmac.ComputeHash(new[] { (byte)i, (byte)0, (byte)0, (byte)0 });
                    table[i] = Util.Bytes.ReadUInt64(h, 0);
                }
            }
            return table;
        }
    }
}
