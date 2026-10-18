using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Keepsake.Core.Reporting;
using Keepsake.Core.Util;

namespace Keepsake.Service
{
    /// <summary>
    /// Shadow copies created by Keepsake that are not deleted yet (<c>state\vss.json</c>). After a crash or power
    /// loss they are removed at the next start, so they never pile up on the volume.
    /// </summary>
    internal sealed class ShadowTracker
    {
        private readonly string _file;
        private readonly object _lock = new object();

        public ShadowTracker(string file)
        {
            _file = file;
        }

        /// <summary>Tracker without persistence (tests).</summary>
        public static ShadowTracker None { get; } = new ShadowTracker(null);

        public void Add(string id) => Update(list => list.Add(id));
        public void Remove(string id) => Update(list => list.Remove(id));

        public void CleanUpLeftovers()
        {
            List<string> ids;
            lock (_lock)
                ids = Load();
            foreach (string id in ids)
            {
                if (VssSession.Delete(id))
                    ServiceLog.Info("Removed leftover shadow copy " + id + ".");
                Remove(id);
            }
        }

        private void Update(Action<List<string>> change)
        {
            if (_file == null)
                return;
            lock (_lock)
            {
                var list = Load();
                change(list);
                AtomicFile.Write(_file, Json.ToBytes(list));
            }
        }

        private List<string> Load() => _file != null && File.Exists(_file) ? Json.FromBytes<List<string>>(File.ReadAllBytes(_file)) : new List<string>();
    }

    /// <summary>
    /// VSS snapshots of the volumes a job reads from (SRC-08), via WMI <c>Win32_ShadowCopy</c>. Files are then read
    /// from the snapshot, so open and locked files are backed up consistently. Network paths are read live.
    /// </summary>
    internal sealed class VssSession : IDisposable
    {
        private readonly Dictionary<string, string> _devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _ids = new List<string>();
        private readonly ShadowTracker _tracker;

        private VssSession(ShadowTracker tracker)
        {
            _tracker = tracker;
        }

        public static VssSession Create(IEnumerable<string> sources, List<Issue> issues, ShadowTracker tracker)
        {
            var session = new VssSession(tracker);
            var volumes = sources
                .Where(s => s.Length >= 3 && s[1] == ':')
                .Select(s => Path.GetPathRoot(LongPath.Normalize(s)).ToUpperInvariant())
                .Distinct()
                .ToList();
            foreach (string volume in volumes)
            {
                try
                {
                    var drive = new DriveInfo(volume);
                    if (drive.DriveType != DriveType.Fixed)
                    {
                        Warn(issues, volume, "No VSS snapshot for this " + drive.DriveType.ToString().ToLowerInvariant() + " drive; files are read directly.");
                        continue;
                    }
                    session.Snapshot(volume);
                }
                catch (Exception ex) when (ex is ManagementException || ex is COMException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is IOException)
                {
                    Warn(issues, volume, "VSS snapshot could not be created (" + ex.Message + "). Files are read directly; open or locked files may be skipped.");
                }
            }
            return session;
        }

        /// <summary>Path inside the snapshot for a source, or the source itself when its volume has no snapshot.</summary>
        public string Map(string source)
        {
            string normal = LongPath.Normalize(source);
            if (normal.Length < 3 || normal[1] != ':')
                return source;
            string root = Path.GetPathRoot(normal);
            return _devices.TryGetValue(root, out string device) ? device + normal.Substring(root.Length) : source;
        }

        public int Count => _ids.Count;

        public void Dispose()
        {
            foreach (string id in _ids)
            {
                if (!Delete(id))
                    ServiceLog.Warn("Shadow copy " + id + " could not be deleted now; it is removed at the next service start.");
                else
                    _tracker.Remove(id);
            }
            _ids.Clear();
        }

        private void Snapshot(string volume)
        {
            using (var cls = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null))
            {
                var input = cls.GetMethodParameters("Create");
                input["Volume"] = volume;
                input["Context"] = "ClientAccessible";
                var output = cls.InvokeMethod("Create", input, null);
                uint rc = Convert.ToUInt32(output["ReturnValue"]);
                if (rc != 0)
                    throw new InvalidOperationException(Describe(rc));
                string id = (string)output["ShadowID"];
                _ids.Add(id);
                _tracker.Add(id);

                string device = null;
                using (var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='" + id + "'"))
                {
                    foreach (ManagementObject o in searcher.Get())
                        device = (string)o["DeviceObject"];
                }
                if (device == null)
                    throw new InvalidOperationException("the new shadow copy was not found");
                // \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\
                _devices[volume] = device + @"\";
                ServiceLog.Info("VSS snapshot of " + volume + " created (" + device + ").");
            }
        }

        public static bool Delete(string id)
        {
            // IDs also come from state\vss.json: only a well-formed GUID may reach the WQL query (SEC-012).
            if (!Guid.TryParse(id, out Guid parsed))
            {
                ServiceLog.Warn("Ignoring invalid shadow copy ID '" + id + "'.");
                return false; // nothing that could be deleted; the caller drops it from the list
            }
            id = parsed.ToString("B").ToUpperInvariant();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT * FROM Win32_ShadowCopy WHERE ID='" + id + "'"))
                {
                    foreach (ManagementObject o in searcher.Get())
                        o.Delete();
                }
                return true;
            }
            catch (Exception ex) when (ex is ManagementException || ex is COMException || ex is UnauthorizedAccessException)
            {
                ServiceLog.Warn("Deleting shadow copy " + id + " failed: " + ex.Message);
                return false;
            }
        }

        private static void Warn(List<Issue> issues, string volume, string message)
        {
            issues.Add(new Issue { Kind = IssueKind.Warning, Path = volume, Message = message, Time = DateTime.UtcNow });
            ServiceLog.Warn(volume + ": " + message);
        }

        private static string Describe(uint rc)
        {
            switch (rc)
            {
                case 1: return "access denied – the service must run as LocalSystem or an administrator";
                case 2: return "invalid argument";
                case 3: return "volume not found";
                case 4: return "volume not supported (shadow copies need NTFS)";
                case 5: return "unsupported shadow copy context";
                case 6: return "insufficient storage for the shadow copy";
                case 7: return "volume is in use";
                case 8: return "maximum number of shadow copies reached";
                case 9: return "another shadow copy operation is in progress";
                case 10: return "a shadow copy provider vetoed the operation";
                case 11: return "shadow copy provider not registered";
                case 12: return "shadow copy provider failure";
                default: return "unknown error " + rc;
            }
        }
    }
}
