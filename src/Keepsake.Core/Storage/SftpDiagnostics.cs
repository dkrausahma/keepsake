using System;
using System.Collections.Generic;
using System.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Keepsake.Core.Storage
{
    /// <summary>
    /// Tries every key exchange, cipher, MAC and host-key algorithm on its own against an SFTP server
    /// (<c>keepsake sftp-test</c>). Shows which combination fails on a particular computer, e.g. "MAC error" on
    /// Windows Server 2008 R2 against a server that works from Windows 10/11. Each attempt logs in and out again;
    /// nothing is read or written. The host key must be trusted (fingerprint) before any password is sent.
    /// </summary>
    public static class SftpDiagnostics
    {
        /// <summary>Returns the server's host-key fingerprint without logging in (the key exchange stops before authentication).</summary>
        public static string ProbeHostKey(SftpOptions options)
        {
            string seen = null;
            var info = new ConnectionInfo(options.Host, options.Port, options.User, new PasswordAuthenticationMethod(options.User, "")) { Timeout = TimeSpan.FromSeconds(30) };
            SftpStorage.PreferPortableAlgorithms(info);
            using (var client = new SftpClient(info))
            {
                client.HostKeyReceived += (s, e) =>
                {
                    seen = "SHA256:" + e.FingerPrintSHA256;
                    e.CanTrust = false;
                };
                try
                {
                    client.Connect();
                }
                catch (Exception ex) when (ex is SshException || ex is System.Net.Sockets.SocketException || ex is TimeoutException)
                {
                    if (seen == null)
                        throw new KeepsakeException("Cannot reach " + options.Host + ":" + options.Port + ": " + ex.Message);
                }
            }
            return seen;
        }

        public static IEnumerable<string> Run(SftpOptions options)
        {
            if (string.IsNullOrEmpty(options.HostKeyFingerprint))
                throw new KeepsakeException("The host key must be trusted first.");
            var methods = SftpStorage.AuthMethods(options);
            var all = new ConnectionInfo(options.Host, options.Port, options.User, methods.ToArray());

            yield return "Client:  " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ", " +
                         System.Runtime.InteropServices.RuntimeInformation.OSDescription + ", " +
                         System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + ", SSH.NET " +
                         typeof(SftpClient).Assembly.GetName().Version;
            yield return "Server:  " + options.Host + ":" + options.Port;
            yield return "";

            var preferred = Try(options, methods, null, out string described, out SftpSecurity rating);
            yield return Line("Default (preferred order)", preferred, described);
            if (rating != null)
            {
                yield return "Encryption used by Keepsake: " + rating.Summary;
                if (rating.Note != null)
                    yield return "  " + rating.Note;
            }
            yield return "";

            yield return "Key exchange (each on its own):";
            string goodKex = null;
            foreach (string kex in all.KeyExchangeAlgorithms.Keys.ToList())
            {
                string error = Try(options, methods, new AlgorithmProfile(kex, new[] { kex }, null, null), out described);
                if (error == null && goodKex == null)
                    goodKex = kex;
                yield return Line("  " + kex, error, described);
            }
            string[] kexForRest = goodKex == null ? null : new[] { goodKex };
            yield return "";

            yield return "Ciphers (each on its own" + (goodKex == null ? "" : ", key exchange " + goodKex) + "):";
            string goodCtr = null;
            foreach (string cipher in all.Encryptions.Keys.ToList())
            {
                string error = Try(options, methods, new AlgorithmProfile(cipher, kexForRest, new[] { cipher }, null), out described);
                if (error == null && goodCtr == null && cipher.Contains("-ctr"))
                    goodCtr = cipher;
                yield return Line("  " + cipher, error, described);
            }
            yield return "";

            yield return "MACs (each on its own, cipher " + (goodCtr ?? "aes256-ctr") + "):";
            foreach (string mac in all.HmacAlgorithms.Keys.ToList())
            {
                string error = Try(options, methods, new AlgorithmProfile(mac, kexForRest, new[] { goodCtr ?? "aes256-ctr" }, new[] { mac }), out described);
                yield return Line("  " + mac, error, described);
            }
            yield return "";
            yield return "Host key: " + options.HostKeyFingerprint + " (trusted; other host-key types would have a different fingerprint).";
        }

        private static string Line(string label, string error, string described) =>
            label.PadRight(44) + (error == null ? "OK    " + described : "FAIL  " + error);

        private static string Try(SftpOptions options, List<AuthenticationMethod> methods, AlgorithmProfile profile, out string described) =>
            Try(options, methods, profile, out described, out _);

        private static string Try(SftpOptions options, List<AuthenticationMethod> methods, AlgorithmProfile profile, out string described, out SftpSecurity rating)
        {
            var info = SftpStorage.NewConnectionInfo(options, methods, profile);
            string error = Connect(options, info, out described);
            rating = error == null ? SftpStorage.Rate(info, options.Host) : null;
            return error;
        }

        private static string Connect(SftpOptions options, ConnectionInfo info, out string described)
        {
            // Servers limit how many new connections arrive in a short time (OpenSSH MaxStartups, hosting providers).
            System.Threading.Thread.Sleep(700);
            described = null;
            string seen = null;
            using (var client = new SftpClient(info))
            {
                client.HostKeyReceived += (s, e) =>
                {
                    seen = "SHA256:" + e.FingerPrintSHA256;
                    e.CanTrust = string.Equals(seen, options.HostKeyFingerprint, StringComparison.Ordinal);
                };
                try
                {
                    client.Connect();
                    described = "cipher " + info.CurrentClientEncryption +
                                (string.IsNullOrEmpty(info.CurrentClientHmacAlgorithm) ? "" : ", mac " + info.CurrentClientHmacAlgorithm) +
                                ", kex " + info.CurrentKeyExchangeAlgorithm + ", host key " + info.CurrentHostKeyAlgorithm;
                    client.Disconnect();
                    SftpDebugLog.Write("sftp-test OK: " + described);
                    return null;
                }
                catch (Exception ex) when (ex is SshException || ex is System.Net.Sockets.SocketException || ex is TimeoutException || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    SftpDebugLog.Write("sftp-test FAILED kex [" + string.Join(",", info.KeyExchangeAlgorithms.Keys) + "] ciphers [" + string.Join(",", info.Encryptions.Keys) +
                                       "] macs [" + string.Join(",", info.HmacAlgorithms.Keys) + "] (negotiated: kex " + info.CurrentKeyExchangeAlgorithm + ", cipher " +
                                       info.CurrentClientEncryption + ", mac " + info.CurrentClientHmacAlgorithm + ", server " + info.ServerVersion + "): " + ex);
                    if (seen != null && !string.Equals(seen, options.HostKeyFingerprint, StringComparison.Ordinal))
                        return "host key " + seen + " is not the trusted one";
                    return ex.GetType().Name + ": " + ex.Message;
                }
            }
        }
    }
}
