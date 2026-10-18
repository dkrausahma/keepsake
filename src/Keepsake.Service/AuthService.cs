using System;
using System.Collections.Generic;
using System.Linq;
using Keepsake.Core.Crypto;
using Keepsake.Core.Util;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>
    /// Application password and sessions (SEC-01/02/04). The password is stored as an Argon2id hash and is
    /// unrelated to any repository key. After 5 failed attempts, logins are blocked for an increasing time
    /// (2 s, 4 s, 8 s ... up to 5 minutes). Sessions expire after 30 minutes without use.
    /// </summary>
    internal sealed class AuthService
    {
        public const int MinPasswordLength = 8;
        private const int FreeAttempts = 5;
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(5);

        private readonly SettingsStore _settings;
        private readonly object _lock = new object();
        // One password check at a time (SEC-013, SEC-015): parallel requests cannot get extra guesses past the
        // lockout, and at most one Argon2 computation (64 MiB) runs for logins.
        private readonly System.Threading.SemaphoreSlim _verifyGate = new System.Threading.SemaphoreSlim(1, 1);
        private const int MaxSessions = 100;
        private readonly Dictionary<string, DateTime> _sessions = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private int _failures;
        private DateTime _lockedUntil = DateTime.MinValue;

        public AuthService(SettingsStore settings)
        {
            _settings = settings;
        }

        public bool PasswordSet => _settings.Get().AppPassword != null;

        /// <summary>Sets the first application password. Only possible while none is set.</summary>
        public LoginResult Setup(string password)
        {
            CheckStrength(password);
            lock (_lock)
            {
                if (PasswordSet)
                    throw new ApiException(ErrorCodes.Conflict, "The application password is already set. Log in instead.");
                _settings.Update(s => s.AppPassword = Hash(password));
                ServiceLog.Info("Application password set.");
                return NewSession();
            }
        }

        public LoginResult Login(string password)
        {
            var stored = _settings.Get().AppPassword;
            if (stored == null)
                throw new ApiException(ErrorCodes.SetupRequired, "No application password is set yet. Run the first-time setup.");

            if (!CheckPassword(password, stored, "login"))
                throw new ApiException(ErrorCodes.Unauthorized, "Wrong application password.");
            lock (_lock)
                return NewSession();
        }

        /// <summary>
        /// Changes the password. All other sessions end (someone who knew the old password is logged out);
        /// the session that made the change stays valid (SEC-014).
        /// </summary>
        public void ChangePassword(string oldPassword, string newPassword, string currentToken)
        {
            var stored = _settings.Get().AppPassword;
            if (stored == null || !CheckPassword(oldPassword, stored, "password change"))
                throw new ApiException(ErrorCodes.Unauthorized, "The current application password is wrong.");
            CheckStrength(newPassword);
            _settings.Update(s => s.AppPassword = Hash(newPassword));
            lock (_lock)
            {
                foreach (var other in _sessions.Keys.Where(k => k != currentToken).ToList())
                    _sessions.Remove(other);
            }
            ServiceLog.Info("Application password changed; other sessions were ended.");
        }

        /// <summary>Checks a password under the lockout rules; every attempt counts, whatever the API call.</summary>
        private bool CheckPassword(string password, PasswordHash stored, string purpose)
        {
            _verifyGate.Wait();
            try
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    if (now < _lockedUntil)
                        throw new ApiException(ErrorCodes.LockedOut, "Too many wrong passwords. Try again in " + Math.Ceiling((_lockedUntil - now).TotalSeconds) + " seconds.");
                }
                bool ok = Verify(password ?? "", stored);
                lock (_lock)
                {
                    if (ok)
                    {
                        _failures = 0;
                        _lockedUntil = DateTime.MinValue;
                        return true;
                    }
                    _failures++;
                    if (_failures >= FreeAttempts)
                    {
                        double seconds = Math.Min(MaxLockout.TotalSeconds, Math.Pow(2, _failures - FreeAttempts + 1));
                        _lockedUntil = DateTime.UtcNow.AddSeconds(seconds);
                    }
                    ServiceLog.Warn("Failed " + purpose + " attempt (" + _failures + " in a row).");
                    return false;
                }
            }
            finally
            {
                _verifyGate.Release();
            }
        }

        /// <summary>Throws unless the token belongs to a live session; refreshes its idle timer.</summary>
        public void Validate(string token)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var lastUse))
                    throw new ApiException(ErrorCodes.Unauthorized, PasswordSet ? "Please log in." : "No application password is set yet. Run the first-time setup.");
                if (DateTime.UtcNow - lastUse > IdleTimeout)
                {
                    _sessions.Remove(token);
                    throw new ApiException(ErrorCodes.Unauthorized, "The session expired. Please log in again.");
                }
                _sessions[token] = DateTime.UtcNow;
            }
        }

        public void Logout(string token)
        {
            lock (_lock)
            {
                if (token != null)
                    _sessions.Remove(token);
            }
        }

        private LoginResult NewSession()
        {
            var now = DateTime.UtcNow;
            foreach (var expired in _sessions.Where(s => now - s.Value > IdleTimeout).Select(s => s.Key).ToList())
                _sessions.Remove(expired);
            // Bounded number of sessions: the least recently used one goes first.
            while (_sessions.Count >= MaxSessions)
                _sessions.Remove(_sessions.OrderBy(s => s.Value).First().Key);
            string token = Rand.HexId(32);
            _sessions[token] = now;
            return new LoginResult { Token = token, ExpiresIdle = now + IdleTimeout };
        }

        private static void CheckStrength(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
                throw new ApiException(ErrorCodes.Invalid, "The application password must have at least " + MinPasswordLength + " characters.");
        }

        private static PasswordHash Hash(string password)
        {
            var kdf = PasswordKdf.NewParams();
            return new PasswordHash { Kdf = kdf, Hash = Convert.ToBase64String(PasswordKdf.Derive(password, kdf)) };
        }

        private static bool Verify(string password, PasswordHash stored) =>
            Bytes.ConstantTimeEquals(PasswordKdf.Derive(password, stored.Kdf), Convert.FromBase64String(stored.Hash));
    }
}
