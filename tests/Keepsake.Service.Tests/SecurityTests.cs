using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Collections.Generic;
using Keepsake.Core.Util;
using Keepsake.Protocol;
using Xunit;

namespace Keepsake.Service.Tests
{
    /// <summary>Regression tests for the security audit findings (audits/SECURITY_AUDIT_REPORT.md).</summary>
    public class SecurityTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "keepsake-sec-" + Rand.HexId(6));

        public SecurityTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                        Directory.Delete(d); // removes the junction, not its target
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void Junction(string link, string target)
        {
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
        }

        private static string LegacyProtect(string plain) =>
            "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain),
                Encoding.UTF8.GetBytes("Keepsake/secret/v1"), DataProtectionScope.LocalMachine));

        // ── SEC-001 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Secrets_use_account_scope_and_still_read_the_old_machine_scope_format()
        {
            string current = Secrets.Protect("s3cret value");
            Assert.StartsWith("dpapi-u:", current);
            Assert.Equal("s3cret value", Secrets.Unprotect(current));

            string legacy = LegacyProtect("old secret");
            Assert.True(Secrets.IsLegacy(legacy));
            Assert.Equal("old secret", Secrets.Unprotect(legacy));
            string upgraded = Secrets.Upgrade(legacy);
            Assert.StartsWith("dpapi-u:", upgraded);
            Assert.Equal("old secret", Secrets.Unprotect(upgraded));
        }

        [Fact]
        public void Job_store_converts_old_secrets_when_it_loads()
        {
            string file = Path.Combine(_root, "jobs.json");
            var store = new JobStore(file);
            var job = store.Save(new JobDefinition { Name = "Docs", Sources = { @"C:\Data" }, Destination = new Destination { Path = @"D:\Backup" } }, "repo password 1");

            string text = File.ReadAllText(file);
            string current = System.Text.RegularExpressions.Regex.Match(text, "dpapi-u:[^\"]+").Value;
            File.WriteAllText(file, text.Replace(current, LegacyProtect("repo password 1")));
            Assert.Contains("\"dpapi:", File.ReadAllText(file));

            var reloaded = new JobStore(file);
            Assert.DoesNotContain("\"dpapi:", File.ReadAllText(file));
            Assert.Contains("dpapi-u:", File.ReadAllText(file));
            Assert.Equal("repo password 1", reloaded.GetPassword(job.Id));
        }

        [Fact]
        public void Data_folder_with_a_junction_is_refused()
        {
            string data = Path.Combine(_root, "data");
            string elsewhere = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(Path.Combine(data, "config"));
            Directory.CreateDirectory(elsewhere);
            Junction(Path.Combine(data, "runs"), elsewhere);

            var ex = Assert.Throws<InvalidOperationException>(() => new DataPaths(data).Ensure(restrict: false));
            Assert.Contains("junction", ex.Message);
        }

        [Fact]
        public void Restricted_data_folder_takes_ownership_and_removes_extra_permissions()
        {
            // Changing the owner needs SeRestorePrivilege: only meaningful in an elevated test run.
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                return;

            string data = Path.Combine(_root, "data-restricted");
            string config = Path.Combine(data, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "jobs.json"), "{}");
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var open = Directory.GetAccessControl(config);
            open.SetAccessRuleProtection(true, true);
            open.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(config, open);

            new DataPaths(data).Ensure(restrict: true);

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (string path in new[] { data, config })
            {
                var acl = Directory.GetAccessControl(path);
                Assert.Equal(admins, acl.GetOwner(typeof(SecurityIdentifier)));
                foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    Assert.NotEqual(users, r.IdentityReference);
            }
            var file = File.GetAccessControl(Path.Combine(config, "jobs.json"));
            Assert.Equal(admins, file.GetOwner(typeof(SecurityIdentifier)));
            foreach (FileSystemAccessRule r in file.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Assert.NotEqual(users, r.IdentityReference);
        }

        // ── SEC-007 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Key_derivation_parameters_from_the_repository_are_bounded()
        {
            var huge = new Keepsake.Core.Crypto.KdfParams { MemoryKiB = 64 * 1024 * 1024, Iterations = 3, Parallelism = 2, Salt = Convert.ToBase64String(new byte[16]) };
            Assert.Throws<Keepsake.Core.RepositoryFormatException>(() => Keepsake.Core.Crypto.PasswordKdf.Derive("x", huge));
            var slow = new Keepsake.Core.Crypto.KdfParams { MemoryKiB = 1024, Iterations = 1000000, Parallelism = 1, Salt = Convert.ToBase64String(new byte[16]) };
            Assert.Throws<Keepsake.Core.RepositoryFormatException>(() => Keepsake.Core.Crypto.PasswordKdf.Derive("x", slow));
            var badSalt = new Keepsake.Core.Crypto.KdfParams { MemoryKiB = 1024, Iterations = 1, Parallelism = 1, Salt = "not base64!" };
            Assert.Throws<Keepsake.Core.RepositoryFormatException>(() => Keepsake.Core.Crypto.PasswordKdf.Derive("x", badSalt));
        }

        // ── SEC-011 ──────────────────────────────────────────────────────────────

        [Fact]
        public void A_client_supplied_job_id_that_is_not_a_plain_id_is_replaced()
        {
            var store = new JobStore(Path.Combine(_root, "jobs.json"));
            var job = store.Save(new JobDefinition { Id = @"..\..\Windows", Name = "Docs", Sources = { @"C:\Data" }, Destination = new Destination { Path = @"D:\Backup" } }, "repo password 1");
            Assert.True(JobStore.IsJobId(job.Id));
            var kept = store.Save(new JobDefinition { Id = "0123456789abcdef0123456789abcdef", Name = "Other", Sources = { @"C:\Data" }, Destination = new Destination { Path = @"D:\Backup2" } }, "repo password 1");
            Assert.Equal("0123456789abcdef0123456789abcdef", kept.Id);
        }

        // ── SEC-014 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Changing_the_app_password_ends_other_sessions_but_keeps_the_current_one()
        {
            using (var t = new TestService())
            using (var other = t.Client())
            using (var current = t.Client())
            {
                current.Call(Methods.AuthChangePassword, new ChangePasswordParams { OldPassword = TestService.AppPassword, NewPassword = "new app password 2" });
                current.Call<List<JobSummary>>(Methods.JobsList);
                var ex = Assert.Throws<ApiException>(() => other.Call<List<JobSummary>>(Methods.JobsList));
                Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
            }
        }
    }
}
