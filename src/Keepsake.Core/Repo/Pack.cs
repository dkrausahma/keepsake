using System;
using System.Collections.Generic;
using System.IO;
using Keepsake.Core.Crypto;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;

namespace Keepsake.Core.Repo
{
    public sealed class PackBlobEntry
    {
        public BlobType Type { get; set; }
        public string Id { get; set; }
        public long Offset { get; set; }
        public int Length { get; set; }
        public int RawLength { get; set; }
    }

    public sealed class FinishedPack
    {
        public string Id { get; set; }
        public byte[] Data { get; set; }
        public List<PackBlobEntry> Blobs { get; set; }
    }

    /// <summary>
    /// Pack file layout: <c>blob_1 || ... || blob_n || sealed header || uint32 header length</c>.
    /// Each blob is individually sealed with AES-256-GCM. The header lists type, ID, sealed length
    /// and plaintext length of each blob. The pack ID is the SHA-256 of the whole file.
    /// </summary>
    internal sealed class PackBuilder
    {
        private const int EntrySize = 1 + 32 + 4 + 4;
        private readonly MemoryStream _data;

        public PackBuilder(int capacity)
        {
            _data = new MemoryStream(capacity);
        }

        public List<PackBlobEntry> Entries { get; } = new List<PackBlobEntry>();
        public long Size => _data.Length;

        public void Add(BlobType type, string id, byte[] sealedBlob, int rawLength)
        {
            Entries.Add(new PackBlobEntry { Type = type, Id = id, Offset = _data.Length, Length = sealedBlob.Length, RawLength = rawLength });
            _data.Write(sealedBlob, 0, sealedBlob.Length);
        }

        public FinishedPack Finish(byte[] encryptionKey)
        {
            var header = new MemoryStream(5 + Entries.Count * EntrySize);
            header.WriteByte(1);
            Bytes.WriteUInt32(header, (uint)Entries.Count);
            foreach (var e in Entries)
            {
                header.WriteByte((byte)e.Type);
                byte[] id = Hex.Decode(e.Id);
                header.Write(id, 0, id.Length);
                Bytes.WriteUInt32(header, (uint)e.Length);
                Bytes.WriteUInt32(header, (uint)e.RawLength);
            }
            byte[] sealedHeader = Aead.Seal(encryptionKey, header.ToArray(), Aead.Context("pack-header"));
            _data.Write(sealedHeader, 0, sealedHeader.Length);
            Bytes.WriteUInt32(_data, (uint)sealedHeader.Length);

            byte[] file = _data.ToArray();
            return new FinishedPack { Id = Sha256.HexOf(file), Data = file, Blobs = Entries };
        }
    }

    public static class PackReader
    {
        /// <summary>Reads only the header of a stored pack (two range reads).</summary>
        public static List<PackBlobEntry> ReadHeader(IStorage storage, string packId, byte[] encryptionKey)
        {
            string path = Layout.PackPath(packId);
            long size = storage.GetSize(path);
            if (size < 4)
                throw new RepositoryCorruptException("Pack " + packId + " is truncated.");
            uint headerLength = Bytes.ReadUInt32(storage.ReadRange(path, size - 4, 4), 0);
            long headerStart = size - 4 - headerLength;
            if (headerStart < 0)
                throw new RepositoryCorruptException("Pack " + packId + " has an invalid header length.");
            byte[] sealedHeader = storage.ReadRange(path, headerStart, (int)headerLength);
            return Parse(Aead.Open(encryptionKey, sealedHeader, Aead.Context("pack-header")), headerStart, packId);
        }

        /// <summary>Parses the header of a fully downloaded pack.</summary>
        public static List<PackBlobEntry> ReadHeader(byte[] pack, string packId, byte[] encryptionKey)
        {
            if (pack.Length < 4)
                throw new RepositoryCorruptException("Pack " + packId + " is truncated.");
            uint headerLength = Bytes.ReadUInt32(pack, pack.Length - 4);
            long headerStart = pack.Length - 4 - headerLength;
            if (headerStart < 0)
                throw new RepositoryCorruptException("Pack " + packId + " has an invalid header length.");
            byte[] plain = Aead.Open(encryptionKey, pack, (int)headerStart, (int)headerLength, Aead.Context("pack-header"));
            return Parse(plain, headerStart, packId);
        }

        private static List<PackBlobEntry> Parse(byte[] header, long dataEnd, string packId)
        {
            if (header.Length < 5 || header[0] != 1)
                throw new RepositoryFormatException("Pack " + packId + " has an unsupported header version.");
            int count = (int)Bytes.ReadUInt32(header, 1);
            if (header.Length != 5 + count * 41)
                throw new RepositoryCorruptException("Pack " + packId + " header has the wrong size.");
            var entries = new List<PackBlobEntry>(count);
            long offset = 0;
            int pos = 5;
            for (int i = 0; i < count; i++)
            {
                var type = (BlobType)header[pos];
                string id = Hex.Encode(header, pos + 1, 32);
                int length = (int)Bytes.ReadUInt32(header, pos + 33);
                int raw = (int)Bytes.ReadUInt32(header, pos + 37);
                entries.Add(new PackBlobEntry { Type = type, Id = id, Offset = offset, Length = length, RawLength = raw });
                offset += length;
                pos += 41;
            }
            if (offset != dataEnd)
                throw new RepositoryCorruptException("Pack " + packId + " header does not match the pack size.");
            return entries;
        }
    }
}
