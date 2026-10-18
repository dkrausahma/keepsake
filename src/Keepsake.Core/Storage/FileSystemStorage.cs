using System;
using System.Collections.Generic;
using System.IO;
using Keepsake.Core.Util;

namespace Keepsake.Core.Storage
{
    /// <summary>Repository on a local disk, a USB disk or a network share (UNC path).</summary>
    public sealed class FileSystemStorage : IStorage, IFreeSpace
    {
        private const string TempMarker = ".tmp-";
        private readonly string _root;

        public FileSystemStorage(string root)
        {
            Location = LongPath.Normalize(root);
            _root = LongPath.ToExtended(Location).TrimEnd('\\');
        }

        public string Location { get; }

        public bool Exists(string path) => File.Exists(Local(path));

        public long GetSize(string path) => Guard(path, () => new FileInfo(Local(path)).Length);

        public byte[] Read(string path) => Guard(path, () => File.ReadAllBytes(Local(path)));

        public byte[] ReadRange(string path, long offset, int length) => Guard(path, () =>
        {
            using (var fs = new FileStream(Local(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = fs.Read(buffer, read, length - read);
                    if (n == 0)
                        throw new IOException("Unexpected end of file (wanted " + length + " bytes at offset " + offset + ").");
                    read += n;
                }
                return buffer;
            }
        });

        public void Write(string path, byte[] data)
        {
            string target = Local(path);
            string temp = target + TempMarker + Rand.HexId(6);
            try
            {
                CheckNoLinks(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(temp, target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(temp);
                throw new StorageException("Cannot write '" + path + "' to " + Location + ": " + ex.Message, ex);
            }
        }

        public void Delete(string path) => Guard(path, () =>
        {
            string local = Local(path);
            CheckNoLinks(local);
            if (File.Exists(local))
                File.Delete(local);
            return true;
        });

        public IEnumerable<StorageEntry> List(string directory)
        {
            string local = Local(directory);
            if (!Directory.Exists(local))
                yield break;
            if (LinkSafety.IsLink(local))
                throw new StorageException("'" + directory + "' in " + Location + " is a junction or symbolic link; it is not followed.", null);
            IEnumerable<FileInfo> files;
            try
            {
                files = Walk(new DirectoryInfo(local), 0);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new StorageException("Cannot list '" + directory + "' in " + Location + ": " + ex.Message, ex);
            }
            foreach (var file in files)
            {
                if (file.Name.IndexOf(TempMarker, StringComparison.Ordinal) >= 0)
                    continue;
                string relative = file.FullName.Substring(_root.Length + 1).Replace('\\', '/');
                yield return new StorageEntry(relative, file.Length);
            }
        }

        public long? FreeBytes()
        {
            string dir = Location.EndsWith("\\", StringComparison.Ordinal) ? Location : Location + "\\";
            return GetDiskFreeSpaceEx(dir, out ulong free, out _, out _) ? (long?)(long)free : null;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

        public void Dispose()
        {
        }

        private string Local(string path)
        {
            foreach (string part in path.Split('/', '\\'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0)
                    throw new StorageException("Invalid repository path '" + path + "'.", null);
            }
            return _root + "\\" + path.Replace('/', '\\');
        }

        /// <summary>
        /// The repository layout is at most two folders deep (data/xx/id). Junctions and symbolic links are never
        /// followed: they could make prune, repair or unlock delete files outside the repository (SEC-004).
        /// </summary>
        private const int MaxDepth = 2;

        private static IEnumerable<FileInfo> Walk(DirectoryInfo dir, int depth)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (entry is DirectoryInfo sub)
                {
                    if (depth < MaxDepth)
                    {
                        foreach (var f in Walk(sub, depth + 1))
                            yield return f;
                    }
                }
                else
                {
                    yield return (FileInfo)entry;
                }
            }
        }

        /// <summary>Refuses a write or delete whose folder inside the repository is a junction or link (SEC-004).</summary>
        private void CheckNoLinks(string localFile)
        {
            try
            {
                LinkSafety.EnsureNoLinksBelow(Location, LongPath.ToNormal(Path.GetDirectoryName(localFile)));
            }
            catch (IOException ex)
            {
                throw new StorageException(ex.Message, ex);
            }
        }

        private T Guard<T>(string path, Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new StorageException("Cannot access '" + path + "' in " + Location + ": " + ex.Message, ex);
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception)
            {
                // Best effort; leftover temp files are ignored by List().
            }
        }
    }
}
