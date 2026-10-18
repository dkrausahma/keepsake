using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Keepsake.Core.Backup;
using Keepsake.Core.Repo;
using Keepsake.Core.Reporting;
using Keepsake.Core.Restore;
using Keepsake.Core.Storage;
using Keepsake.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace Keepsake.Core.Tests
{
    /// <summary>
    /// Integration tests against a real SFTP server. They run when one is reachable, e.g.:
    /// <c>docker run -d --name keepsake-sftp -p 2222:22 atmoz/sftp:alpine kuser:Sftp-Pass-1:1001:1001:backups</c>
    /// Override with KEEPSAKE_TEST_SFTP=host:port:user:password.
    /// </summary>
    public class SftpStorageTests
    {
        private readonly ITestOutputHelper _output;

        public SftpStorageTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private SftpOptions Options(string path)
        {
            string spec = Environment.GetEnvironmentVariable("KEEPSAKE_TEST_SFTP") ?? "localhost:2222:kuser:Sftp-Pass-1";
            var parts = spec.Split(':');
            var o = new SftpOptions { Host = parts[0], Port = int.Parse(parts[1]), User = parts[2], Password = parts[3], Path = path };
            try
            {
                using (var tcp = new TcpClient())
                {
                    if (!tcp.ConnectAsync(o.Host, o.Port).Wait(1000))
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
            return o;
        }

        private SftpOptions Trusted(string path)
        {
            var o = Options(path);
            if (o == null)
                return null;
            try
            {
                new SftpStorage(o).Connect();
            }
            catch (HostKeyException ex)
            {
                o.HostKeyFingerprint = ex.Fingerprint;
            }
            return o;
        }

        [Fact]
        public void Managed_aes_ctr_matches_nist_vectors_in_any_split()
        {
            // NIST SP 800-38A, F.5.1 (CTR-AES128.Encrypt) and F.5.5 (CTR-AES256.Encrypt)
            byte[] iv = Hex.Decode("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            byte[] plain = Hex.Decode("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
            byte[] c128 = Hex.Decode("874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff5ae4df3edbd5d35e5b4f09020db03eab1e031dda2fbe03d1792170a0f3009cee");
            byte[] c256 = Hex.Decode("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c52b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6");
            var k128 = Hex.Decode("2b7e151628aed2a6abf7158809cf4f3c");
            var k256 = Hex.Decode("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");

            Assert.Equal(c128, ManagedAesCtrCipher.Create(128, k128, iv).Encrypt(plain, 0, plain.Length));
            Assert.Equal(c256, ManagedAesCtrCipher.Create(256, k256, iv).Encrypt(plain, 0, plain.Length));

            // SSH.NET decrypts the first block of a packet, then the rest: the keystream must continue across calls.
            var split = ManagedAesCtrCipher.Create(256, k256, iv);
            var parts = new System.Collections.Generic.List<byte>();
            int pos = 0;
            foreach (int n in new[] { 16, 5, 27, 0, 16 })
            {
                parts.AddRange(split.Decrypt(c256, pos, n));
                pos += n;
            }
            parts.AddRange(split.Decrypt(c256, pos, c256.Length - pos));
            Assert.Equal(plain, parts.ToArray());

            // Keys and IVs longer than needed (SSH.NET passes derived key material) are truncated.
            var longKey = new byte[64];
            Buffer.BlockCopy(k128, 0, longKey, 0, 16);
            Assert.Equal(c128, ManagedAesCtrCipher.Create(128, longKey, iv).Encrypt(plain, 0, plain.Length));
        }

        [Fact]
        public void Managed_hmac_matches_rfc4231_and_the_dotnet_classes()
        {
            // RFC 4231, test case 1: key 0x0b x 20, data "Hi There"
            var key = Enumerable.Repeat((byte)0x0b, 20).ToArray();
            var data = System.Text.Encoding.ASCII.GetBytes("Hi There");
            var h256 = new ManagedHmac(ManagedHmac.DigestFor("hmac-sha2-256"), key);
            var h512 = new ManagedHmac(ManagedHmac.DigestFor("hmac-sha2-512-etm@openssh.com"), key);
            Assert.Equal(Hex.Decode("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"), h256.ComputeHash(data));
            Assert.Equal(Hex.Decode("87aa7cdea5ef619d4ff0b4241a1d6cb02379f4e2ce4ec2787ad0b30545e17cdedaa833b7d6b8a702038b274eaea3f4e4be9d914eeb61f1702e696c203a126854"), h512.ComputeHash(data));
            Assert.Equal(256, h256.HashSize);

            // Same results as the .NET classes for SSH-sized keys, also when the instance is reused per packet and
            // fed in pieces (sequence number first, then the packet).
            var rnd = new Random(7);
            var k = new byte[32];
            rnd.NextBytes(k);
            var managed = new ManagedHmac(ManagedHmac.DigestFor("hmac-sha2-256"), k);
            using (var reference = new System.Security.Cryptography.HMACSHA256(k))
            {
                for (int i = 0; i < 20; i++)
                {
                    var packet = new byte[rnd.Next(1, 3000)];
                    rnd.NextBytes(packet);
                    var seq = BitConverter.GetBytes(i);
                    managed.TransformBlock(seq, 0, 4, null, 0);
                    managed.TransformFinalBlock(packet, 0, packet.Length);
                    var expected = reference.ComputeHash(seq.Concat(packet).ToArray());
                    Assert.Equal(expected, managed.Hash);
                    Assert.Equal(expected, managed.ComputeHash(seq.Concat(packet).ToArray()));
                }
            }
            using (var sha1 = new System.Security.Cryptography.HMACSHA1(k.Take(20).ToArray()))
                Assert.Equal(sha1.ComputeHash(data), new ManagedHmac(ManagedHmac.DigestFor("hmac-sha1"), k.Take(20).ToArray()).ComputeHash(data));
        }

        [Fact]
        public void Hmacs_use_the_managed_implementation()
        {
            var info = new Renci.SshNet.ConnectionInfo("localhost", 22, "u", new Renci.SshNet.PasswordAuthenticationMethod("u", "p"));
            SftpStorage.PreferPortableAlgorithms(info);
            foreach (var h in info.HmacAlgorithms)
                Assert.IsType<ManagedHmac>(h.Value.HashAlgorithm(new byte[h.Value.KeySize / 8]));
            Assert.True(info.HmacAlgorithms["hmac-sha2-256-etm@openssh.com"].IsEncryptThenMAC);
            Assert.False(info.HmacAlgorithms["hmac-sha2-256"].IsEncryptThenMAC);
        }

        [Fact]
        public void Aes_ctr_uses_the_managed_implementation()
        {
            var info = new Renci.SshNet.ConnectionInfo("localhost", 22, "u", new Renci.SshNet.PasswordAuthenticationMethod("u", "p"));
            SftpStorage.PreferPortableAlgorithms(info);
            foreach (string name in new[] { "aes128-ctr", "aes192-ctr", "aes256-ctr" })
            {
                var ci = info.Encryptions[name];
                Assert.IsType<ManagedAesCtrCipher>(ci.Cipher(new byte[ci.KeySize / 8], new byte[16]));
            }
        }

        [Fact]
        public void Aead_ciphers_and_etm_macs_are_preferred()
        {
            var info = new Renci.SshNet.ConnectionInfo("localhost", 22, "u", new Renci.SshNet.PasswordAuthenticationMethod("u", "p"));
            int ciphers = info.Encryptions.Count, macs = info.HmacAlgorithms.Count;
            SftpStorage.PreferPortableAlgorithms(info);
            Assert.Equal("chacha20-poly1305@openssh.com", info.Encryptions.Keys.First());
            Assert.Equal("hmac-sha2-256-etm@openssh.com", info.HmacAlgorithms.Keys.First());
            Assert.Equal(ciphers, info.Encryptions.Count); // nothing removed: old servers keep working
            Assert.Equal(macs, info.HmacAlgorithms.Count);

            var o = Trusted("/backups/negotiation");
            if (o == null)
                return; // no test server
            var live = new Renci.SshNet.ConnectionInfo(o.Host, o.Port, o.User, new Renci.SshNet.PasswordAuthenticationMethod(o.User, o.Password));
            SftpStorage.PreferPortableAlgorithms(live);
            using (var client = new Renci.SshNet.SftpClient(live))
            {
                client.Connect();
                _output.WriteLine("negotiated " + live.CurrentClientEncryption + " / " + live.CurrentClientHmacAlgorithm + " / " + live.CurrentKeyExchangeAlgorithm);
                Assert.Equal("chacha20-poly1305@openssh.com", live.CurrentClientEncryption);
            }
        }

        [Fact]
        public void Mac_error_falls_back_and_skips_combinations_the_server_does_not_offer()
        {
            var o = Trusted("/backups/fallback");
            if (o == null)
                return; // no test server
            // The test server offers no plain Diffie-Hellman groups: dh-group14, dh-gex and dh-group16 must be skipped.
            SftpStorage.SimulateMacError = p => p.Name == "preferred" || p.Name == "curve25519";
            SftpStorage.WorkingProfile.Clear();
            try
            {
                using (var s = new SftpStorage(o))
                {
                    s.Connect();
                    _output.WriteLine(s.NegotiatedAlgorithms);
                    Assert.Contains("kex ecdh-sha2-nistp256", s.NegotiatedAlgorithms);
                    Assert.Contains("after MAC errors with: preferred, curve25519", s.NegotiatedAlgorithms);
                    s.Write("data/ok", new byte[] { 1, 2, 3 });
                    Assert.Equal(new byte[] { 1, 2, 3 }, s.Read("data/ok"));
                }
                // The remembered fallback must not stick: once the strongest set works, it is used again.
                SftpStorage.SimulateMacError = null;
                using (var s = new SftpStorage(o))
                {
                    s.Connect();
                    _output.WriteLine(s.Security.Summary);
                    Assert.Equal("strong", s.Security.Level);
                    Assert.Contains("chacha20-poly1305", s.Security.Summary);
                    Assert.DoesNotContain("ecdh", s.Security.Summary);
                    Assert.Null(s.Security.Note);
                }
                // Every combination fails: a clear message instead of a hang or the first error.
                SftpStorage.SimulateMacError = p => true;
                SftpStorage.WorkingProfile.Clear();
                using (var s = new SftpStorage(o))
                {
                    var ex = Assert.Throws<StorageException>(() => s.Connect());
                    Assert.Contains("MAC error with every algorithm combination", ex.Message);
                    Assert.Contains("sftp-test", ex.Message);
                }
            }
            finally
            {
                SftpStorage.SimulateMacError = null;
                SftpStorage.WorkingProfile.Clear();
            }
        }

        [Fact]
        public void Unknown_and_changed_host_keys_are_refused()
        {
            var o = Options("/backups/hostkey");
            if (o == null)
            {
                _output.WriteLine("No SFTP server reachable – skipped.");
                return;
            }
            var unknown = Assert.Throws<HostKeyException>(() => new SftpStorage(o).Connect());
            Assert.False(unknown.Mismatch);
            Assert.StartsWith("SHA256:", unknown.Fingerprint);

            o.HostKeyFingerprint = "SHA256:somethingelse";
            var changed = Assert.Throws<HostKeyException>(() => new SftpStorage(o).Connect());
            Assert.True(changed.Mismatch);

            o.HostKeyFingerprint = unknown.Fingerprint;
            using (var s = new SftpStorage(o))
                s.Connect();
        }

        [Fact]
        public void Storage_operations_and_probe()
        {
            var o = Trusted("/backups/ops-" + Rand.HexId(4));
            if (o == null)
            {
                _output.WriteLine("No SFTP server reachable – skipped.");
                return;
            }
            using (var s = new SftpStorage(o))
            {
                byte[] data = TestRepo.RandomBytes(100_000, 1);
                s.Write("data/ab/abc", data);
                s.Write("data/ab/abc", data); // overwrite
                Assert.True(s.Exists("data/ab/abc"));
                Assert.Equal(data.Length, s.GetSize("data/ab/abc"));
                Assert.Equal(data, s.Read("data/ab/abc"));
                Assert.Equal(data.Skip(500).Take(1000).ToArray(), s.ReadRange("data/ab/abc", 500, 1000));
                Assert.Equal(new[] { "data/ab/abc" }, s.List("data").Select(e => e.Path).ToArray());
                s.Delete("data/ab/abc");
                Assert.False(s.Exists("data/ab/abc"));
                Assert.Throws<StorageException>(() => s.Read("missing"));

                var probe = StorageProbe.Test(s);
                Assert.True(probe.Reachable && probe.CanWrite && probe.CanDelete, string.Join("; ", probe.Problems));
            }
        }

        [Fact]
        public void Backup_and_restore_over_sftp()
        {
            var o = Trusted("/backups/repo-" + Rand.HexId(4));
            if (o == null)
            {
                _output.WriteLine("No SFTP server reachable – skipped.");
                return;
            }
            using (var local = new TestRepo())
            {
                local.File(@"a\one.bin", TestRepo.RandomBytes(300_000, 5));
                local.File(@"a\two.txt", "two");
                var init = Repository.Init(new SftpStorage(o), TestRepo.Password, TestRepo.SmallChunks());
                init.Repository.Dispose();

                using (var repo = Repository.Open(new SftpStorage(o), RepoCredential.FromPassword(TestRepo.Password)))
                {
                    var backup = new BackupEngine(repo).Run(new BackupOptions { Sources = { local.Source } }, null, CancellationToken.None);
                    Assert.Equal(RunStatus.Success, backup.Status);
                }
                using (var repo = Repository.Open(new SftpStorage(o), RepoCredential.FromRecoveryKeyText(init.RecoveryKey)))
                {
                    string target = Path.Combine(local.Root, "from-sftp");
                    var restore = new RestoreEngine(repo).Run(new RestoreOptions { TargetFolder = target }, null, CancellationToken.None);
                    Assert.Equal(RunStatus.Success, restore.Status);
                    Assert.Equal("two", File.ReadAllText(local.RestoredPath(target, @"a\two.txt")));
                }
            }
        }
    }
}
