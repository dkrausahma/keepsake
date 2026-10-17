using System;

namespace Keepsake.Core
{
    /// <summary>Base class for all expected Keepsake errors (shown to the user without a stack trace).</summary>
    public class KeepsakeException : Exception
    {
        public KeepsakeException(string message) : base(message) { }
        public KeepsakeException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The password or recovery key does not unlock any key slot.</summary>
    public sealed class WrongCredentialException : KeepsakeException
    {
        public WrongCredentialException(string message) : base(message) { }
    }

    /// <summary>Authenticated decryption failed: wrong key or tampered/corrupted data.</summary>
    public sealed class CryptoFailureException : KeepsakeException
    {
        public CryptoFailureException(string message) : base(message) { }
    }

    /// <summary>The repository is structurally invalid, from a newer version, or damaged.</summary>
    public class RepositoryFormatException : KeepsakeException
    {
        public RepositoryFormatException(string message) : base(message) { }
        public RepositoryFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Data referenced by the repository is missing or does not match its ID.</summary>
    public sealed class RepositoryCorruptException : RepositoryFormatException
    {
        public RepositoryCorruptException(string message) : base(message) { }
    }

    /// <summary>Another process holds a conflicting lock.</summary>
    public sealed class RepositoryLockedException : KeepsakeException
    {
        public RepositoryLockedException(string message) : base(message) { }
    }

    /// <summary>The storage backend failed (disk full, share unreachable, ...). Always fatal for a run.</summary>
    public sealed class StorageException : KeepsakeException
    {
        public StorageException(string message, Exception inner) : base(message, inner) { }
    }
}
