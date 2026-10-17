using System;
using System.Linq;
using Keepsake.Core.Chunking;
using Keepsake.Core.Crypto;
using Keepsake.Core.Util;
using Xunit;

namespace Keepsake.Core.Tests
{
    public class CryptoTests
    {
        [Fact]
        public void Aead_round_trips_and_detects_tampering()
        {
            byte[] key = Rand.Bytes(32);
            byte[] plain = TestRepo.RandomBytes(1000, 1);
            byte[] sealedData = Aead.Seal(key, plain, Aead.Context("test"));

            Assert.Equal(plain.Length + Aead.Overhead, sealedData.Length);
            Assert.Equal(plain, Aead.Open(key, sealedData, Aead.Context("test")));

            sealedData[40] ^= 1;
            Assert.Throws<CryptoFailureException>(() => Aead.Open(key, sealedData, Aead.Context("test")));
        }

        [Fact]
        public void Aead_binds_associated_data_and_key()
        {
            byte[] key = Rand.Bytes(32);
            byte[] sealedData = Aead.Seal(key, new byte[] { 1, 2, 3 }, Aead.Context("snapshot"));
            Assert.Throws<CryptoFailureException>(() => Aead.Open(key, sealedData, Aead.Context("index")));
            Assert.Throws<CryptoFailureException>(() => Aead.Open(Rand.Bytes(32), sealedData, Aead.Context("snapshot")));
        }

        [Fact]
        public void Hkdf_matches_rfc5869_test_case_1()
        {
            byte[] ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
            byte[] salt = Hex.Decode("000102030405060708090a0b0c");
            byte[] info = Hex.Decode("f0f1f2f3f4f5f6f7f8f9");
            byte[] okm = Hkdf.DeriveKey(ikm, salt, info, 42);
            Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", Hex.Encode(okm));
        }

        [Fact]
        public void Recovery_key_round_trips_and_tolerates_formatting()
        {
            byte[] key = RecoveryKey.Generate();
            string text = RecoveryKey.Format(key);

            Assert.StartsWith("KS1-", text);
            Assert.Equal(key, RecoveryKey.Parse(text));
            Assert.Equal(key, RecoveryKey.Parse(text.ToLowerInvariant().Replace("-", " ")));
            Assert.Equal(key, RecoveryKey.Parse("Keepsake recovery key\r\nRepository: X\r\n\r\n" + text + "\r\n"));
        }

        [Fact]
        public void Recovery_key_typo_is_detected()
        {
            string text = RecoveryKey.Format(RecoveryKey.Generate());
            char[] chars = text.ToCharArray();
            int pos = 6;
            chars[pos] = chars[pos] == 'A' ? 'B' : 'A';
            var ex = Assert.Throws<FormatException>(() => RecoveryKey.Parse(new string(chars)));
            Assert.Contains("typo", ex.Message);
        }

        [Fact]
        public void Codec_compresses_text_and_stores_random_data()
        {
            byte[] text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("keepsake ", 1000)));
            byte[] packed = BlobCodec.Encode(text, compress: true);
            Assert.Equal(BlobCodec.Zstd, packed[0]);
            Assert.True(packed.Length < text.Length / 10);
            Assert.Equal(text, BlobCodec.Decode(packed));

            byte[] random = TestRepo.RandomBytes(10000, 2);
            byte[] stored = BlobCodec.Encode(random, compress: true);
            Assert.Equal(BlobCodec.Stored, stored[0]);
            Assert.Equal(random, BlobCodec.Decode(stored));
        }
    }

    public class ChunkerTests
    {
        private static readonly byte[] Key = Rand.Bytes(32);

        private static System.Collections.Generic.List<byte[]> Chunks(byte[] data, byte[] key = null)
        {
            var chunker = new Chunker(key ?? Key, 4 * 1024, 16 * 1024, 64 * 1024);
            chunker.Reset(new System.IO.MemoryStream(data));
            var result = new System.Collections.Generic.List<byte[]>();
            while (chunker.Next(out var seg))
                result.Add(seg.ToArray());
            return result;
        }

        [Fact]
        public void Chunks_cover_input_and_respect_bounds()
        {
            byte[] data = TestRepo.RandomBytes(1_000_000, 3);
            var chunks = Chunks(data);
            Assert.Equal(data, chunks.SelectMany(c => c).ToArray());
            Assert.All(chunks.Take(chunks.Count - 1), c => Assert.InRange(c.Length, 4 * 1024, 64 * 1024));
            double avg = data.Length / (double)chunks.Count;
            Assert.InRange(avg, 8 * 1024, 40 * 1024);
        }

        [Fact]
        public void Insertion_only_changes_nearby_chunks()
        {
            byte[] data = TestRepo.RandomBytes(1_000_000, 4);
            byte[] modified = data.Take(500_000).Concat(new byte[] { 1, 2, 3, 4, 5 }).Concat(data.Skip(500_000)).ToArray();

            var before = Chunks(data).Select(c => Hex.Encode(Sha256.Compute(c))).ToList();
            var after = Chunks(modified).Select(c => Hex.Encode(Sha256.Compute(c))).ToList();
            int shared = after.Count(before.Contains);
            Assert.True(shared >= after.Count - 3, "Only chunks around the insertion should change (" + shared + " of " + after.Count + " shared).");
        }

        [Fact]
        public void Different_keys_give_different_boundaries()
        {
            byte[] data = TestRepo.RandomBytes(500_000, 5);
            var a = Chunks(data).Select(c => c.Length).ToList();
            var b = Chunks(data, Rand.Bytes(32)).Select(c => c.Length).ToList();
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Small_and_empty_input()
        {
            Assert.Empty(Chunks(new byte[0]));
            var one = Chunks(new byte[] { 42 });
            Assert.Single(one);
        }
    }
}
