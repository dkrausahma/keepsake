using System;
using System.Collections.Generic;
using System.Linq;
using Keepsake.Core.Util;

namespace Keepsake.Protocol
{
    /// <summary>
    /// "Clone" on the Backup jobs page (JOB-08): the settings of an existing job as the starting point of a new one.
    /// Only the job definition is copied – it holds no secrets (they live in <see cref="JobSecrets"/>, which the
    /// service never hands out). The copy gets no ID, a new name and new schedule IDs, and claims no stored passwords,
    /// so the editor asks for them and the service never resolves the source job's secrets for it.
    /// </summary>
    public static class JobClone
    {
        public const string NameSuffix = " (copy)";

        public static JobDetail ForNewJob(JobDetail source)
        {
            if (source?.Job == null)
                throw new ArgumentNullException(nameof(source));

            // Deep copy through JSON, the same shape the service stores and exports.
            var job = Json.FromText<JobDefinition>(Json.ToText(source.Job, indented: false));
            job.Id = null;
            job.Name = (source.Job.Name ?? "").Trim() + NameSuffix;
            job.Created = default(DateTime);
            job.Modified = default(DateTime);
            foreach (var s in job.Schedules ?? new List<ScheduleEntry>())
                s.Id = Guid.NewGuid().ToString("N");   // the scheduler keys its state by job and schedule ID
            job.ShareCredentials = (job.ShareCredentials ?? new List<ShareCredential>())
                .Select(c => new ShareCredential { Share = c.Share, User = c.User }).ToList();

            return new JobDetail
            {
                Job = job,
                HasRepositoryPassword = false,
                HasSftpPassword = false,
                HasSftpKey = false,
                SharesWithPassword = new List<string>(),
            };
        }
    }
}
