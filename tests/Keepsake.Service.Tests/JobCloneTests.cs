using System;
using System.Collections.Generic;
using Keepsake.Core.Maintenance;
using Keepsake.Core.Repo;
using Keepsake.Protocol;
using Xunit;

namespace Keepsake.Service.Tests
{
    public class JobCloneTests
    {
        private static JobDetail Source() => new JobDetail
        {
            Job = new JobDefinition
            {
                Id = "0123456789abcdef0123456789abcdef",
                Name = "Accounting",
                Enabled = false,
                Sources = new List<string> { @"S:\Shares\Accounting" },
                Excludes = new List<string> { "*.bak", @"S:\Shares\Accounting\Temp\" },
                UseDefaultExcludes = false,
                Destination = new Destination { Type = DestinationTypes.Sftp, Host = "backup.example.org", Port = 23, User = "u1", Auth = "key", Path = "/backups/acc", HostKeyFingerprint = "SHA256:abc" },
                ShareCredentials = new List<ShareCredential> { new ShareCredential { Share = @"\\nas\data", User = @"DOMAIN\backup" } },
                Schedules = new List<ScheduleEntry> { new ScheduleEntry { Id = "s1", Kind = ScheduleKind.Daily, Times = { "23:30" }, Mode = BackupMode.Full } },
                RetryCount = 5,
                PreCommand = "pre.cmd",
                UseVss = false,
                Workers = 3,
                Retention = new RetentionPolicy { KeepDaily = 7, KeepMonthly = 24 },
                CheckReadDataPercent = 25,
                AlertNoSuccessHours = 30,
                Created = new DateTime(2026, 1, 1),
                Modified = new DateTime(2026, 2, 1),
            },
            HasRepositoryPassword = true,
            HasSftpPassword = true,
            HasSftpKey = true,
            SharesWithPassword = new List<string> { @"\\nas\data" },
            NextRun = new DateTime(2026, 11, 22),
        };

        [Fact]
        public void Copies_all_settings()
        {
            var source = Source();
            var copy = JobClone.ForNewJob(source).Job;

            Assert.Equal(source.Job.Sources, copy.Sources);
            Assert.Equal(source.Job.Excludes, copy.Excludes);
            Assert.False(copy.UseDefaultExcludes);
            Assert.False(copy.Enabled);
            Assert.Equal("backup.example.org", copy.Destination.Host);
            Assert.Equal(23, copy.Destination.Port);
            Assert.Equal("u1", copy.Destination.User);
            Assert.Equal("key", copy.Destination.Auth);
            Assert.Equal("/backups/acc", copy.Destination.Path);
            Assert.Equal("SHA256:abc", copy.Destination.HostKeyFingerprint);
            Assert.Equal(@"DOMAIN\backup", Assert.Single(copy.ShareCredentials).User);
            Assert.Equal("23:30", Assert.Single(Assert.Single(copy.Schedules).Times));
            Assert.Equal(BackupMode.Full, copy.Schedules[0].Mode);
            Assert.Equal(5, copy.RetryCount);
            Assert.Equal("pre.cmd", copy.PreCommand);
            Assert.False(copy.UseVss);
            Assert.Equal(3, copy.Workers);
            Assert.Equal(7, copy.Retention.KeepDaily);
            Assert.Equal(24, copy.Retention.KeepMonthly);
            Assert.Equal(25, copy.CheckReadDataPercent);
            Assert.Equal(30, copy.AlertNoSuccessHours);
        }

        [Fact]
        public void Copy_is_a_new_job_without_secrets()
        {
            var source = Source();
            var clone = JobClone.ForNewJob(source);

            Assert.Null(clone.Job.Id);
            Assert.Equal("Accounting (copy)", clone.Job.Name);
            Assert.NotEqual("s1", clone.Job.Schedules[0].Id);
            Assert.Equal(default(DateTime), clone.Job.Created);
            Assert.False(clone.HasRepositoryPassword);
            Assert.False(clone.HasSftpPassword);
            Assert.False(clone.HasSftpKey);
            Assert.Empty(clone.SharesWithPassword);
            Assert.Null(clone.NextRun);
        }

        [Fact]
        public void Source_is_not_changed()
        {
            var source = Source();
            var clone = JobClone.ForNewJob(source);
            clone.Job.Sources.Add(@"S:\Other");
            clone.Job.Destination.Path = "/elsewhere";
            clone.Job.ShareCredentials[0].User = "other";

            Assert.Equal("0123456789abcdef0123456789abcdef", source.Job.Id);
            Assert.Equal("s1", source.Job.Schedules[0].Id);
            Assert.Single(source.Job.Sources);
            Assert.Equal("/backups/acc", source.Job.Destination.Path);
            Assert.Equal(@"DOMAIN\backup", source.Job.ShareCredentials[0].User);
            Assert.True(source.HasRepositoryPassword);
        }
    }
}
