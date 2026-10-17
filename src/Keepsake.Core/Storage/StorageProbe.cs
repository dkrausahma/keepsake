using System;
using System.Collections.Generic;
using System.IO;
using Keepsake.Core.Repo;
using Keepsake.Core.Util;

namespace Keepsake.Core.Storage
{
    /// <summary>Optional: storages that can report free space.</summary>
    public interface IFreeSpace
    {
        long? FreeBytes();
    }

    public sealed class ProbeResult
    {
        public bool Reachable { get; set; }
        public bool CanWrite { get; set; }
        public bool CanDelete { get; set; }
        public bool RepositoryExists { get; set; }
        public long? FreeBytes { get; set; }
        public List<string> Problems { get; set; } = new List<string>();
    }

    /// <summary>"Test connection" (DST-06): reachability, write/read/delete permission, free space.</summary>
    public static class StorageProbe
    {
        public static ProbeResult Test(IStorage storage)
        {
            var result = new ProbeResult();
            try
            {
                result.RepositoryExists = Repository.Exists(storage);
                result.Reachable = true;
            }
            catch (KeepsakeException ex)
            {
                result.Problems.Add(ex.Message);
                return result;
            }

            string probe = "keepsake-probe-" + Rand.HexId(6);
            byte[] data = Rand.Bytes(64);
            try
            {
                storage.Write(probe, data);
                result.CanWrite = Bytes.ConstantTimeEquals(storage.Read(probe), data);
                if (!result.CanWrite)
                    result.Problems.Add("Data read back from the destination differs from what was written.");
            }
            catch (KeepsakeException ex)
            {
                result.Problems.Add("Cannot write: " + ex.Message);
            }
            if (result.CanWrite)
            {
                try
                {
                    storage.Delete(probe);
                    result.CanDelete = !storage.Exists(probe);
                    if (!result.CanDelete)
                        result.Problems.Add("Files cannot be deleted (cleanup of old backups will not work).");
                }
                catch (KeepsakeException ex)
                {
                    result.Problems.Add("Cannot delete: " + ex.Message + " (cleanup of old backups will not work).");
                }
            }
            result.FreeBytes = (storage as IFreeSpace)?.FreeBytes();
            return result;
        }
    }
}
