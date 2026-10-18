using System;
using System.Text;
using Keepsake.Core.Util;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Keepsake.Core.Crypto
{
    /// <summary>Argon2id parameters, stored in plain text in each password key slot.</summary>
    public sealed class KdfParams
    {
        public string Algorithm { get; set; } = "argon2id";
        public int Version { get; set; } = 0x13;
        public int MemoryKiB { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
        public string Salt { get; set; }
    }

    public static class PasswordKdf
    {
        // 64 MiB / 3 passes / 2 lanes: roughly one second on older server hardware.
        internal static int DefaultMemoryKiB = 64 * 1024;
        internal static int DefaultIterations = 3;
        internal static int DefaultParallelism = 2;

        public const int MaxMemoryKiB = 1024 * 1024;
        public const int MaxIterations = 20;
        public const int MaxParallelism = 16;

        public static KdfParams NewParams() => new KdfParams
        {
            MemoryKiB = DefaultMemoryKiB,
            Iterations = DefaultIterations,
            Parallelism = DefaultParallelism,
            Salt = Convert.ToBase64String(Rand.Bytes(16)),
        };

        public static byte[] Derive(string password, KdfParams p)
        {
            if (!string.Equals(p.Algorithm, "argon2id", StringComparison.Ordinal) || p.Version != 0x13)
                throw new RepositoryFormatException("Unsupported key derivation '" + p.Algorithm + "' (version " + p.Version + ").");
            // The parameters are read from the repository, which may be tampered with: refuse values that would
            // exhaust memory or run for hours (SEC-007). Keepsake itself writes 64 MiB / 3 / 2.
            if (p.Parallelism < 1 || p.Parallelism > MaxParallelism || p.Iterations < 1 || p.Iterations > MaxIterations ||
                p.MemoryKiB < 8 * p.Parallelism || p.MemoryKiB > MaxMemoryKiB || string.IsNullOrEmpty(p.Salt) || p.Salt.Length > 128)
                throw new RepositoryFormatException("Key derivation parameters out of range (memory " + p.MemoryKiB + " KiB, " +
                                                    p.Iterations + " iterations, parallelism " + p.Parallelism + ").");
            byte[] salt;
            try
            {
                salt = Convert.FromBase64String(p.Salt);
            }
            catch (FormatException)
            {
                throw new RepositoryFormatException("Invalid key derivation salt.");
            }

            var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
                .WithVersion(Argon2Parameters.Version13)
                .WithMemoryAsKB(p.MemoryKiB)
                .WithIterations(p.Iterations)
                .WithParallelism(p.Parallelism)
                .WithSalt(salt)
                .Build();

            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);
            var key = new byte[32];
            generator.GenerateBytes(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), key);
            return key;
        }
    }
}
