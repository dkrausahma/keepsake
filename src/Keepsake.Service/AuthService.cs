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

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (now < _lockedUntil)
                    throw new ApiException(ErrorCodes.LockedOut, "Too many wrong passwords. Try again in " + Math.Ceiling((_lockedUntil - now).TotalSeconds) + " seconds.");
            }

            // Argon2 runs outside the lock; concurrent attempts are still counted below.
            bool ok = Verify(password ?? "", stored);
            lock (_lock)
            {
                if (!ok)
                {
                    _failures++;
                    if (_failures >= FreeAttempts)
                    {
                        double seconds = Math.Min(MaxLockout.TotalSeconds, Math.Pow(2, _failures - FreeAttempts + 1));
                        _lockedUntil = DateTime.UtcNow.AddSeconds(seconds);
                    }
                    ServiceLog.Warn("Failed login attempt (" + _failures + " in a row).");
                    throw new ApiException(ErrorCodes.Unauthorized, "Wrong application password.");
                }
                _failures = 0;
                _lockedUntil = DateTime.MinValue;
                return NewSession();
            }
        }

        public void ChangePassword(string oldPassword, string newPassword)
        {
            var stored = _settings.Get().AppPassword;
            if (stored == null || !Verify(oldPassword ?? "", stored))
                throw new ApiException(ErrorCodes.Unauthorized, "The current application password is wrong.");
            CheckStrength(newPassword);
            _settings.Update(s => s.AppPassword = Hash(newPassword));
            ServiceLog.Info("Application password changed.");
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
