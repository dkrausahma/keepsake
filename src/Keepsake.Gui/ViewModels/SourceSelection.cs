using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Keepsake.Core.Util;
using Keepsake.Gui.Infrastructure;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>
    /// What the user ticked in the folder tree, as explicit marks: <c>true</c> = include, <c>false</c> = exclude.
    /// An item is included if its nearest marked ancestor (or itself) says so. Included items become job sources,
    /// excluded items inside a source become full-path exclusion patterns.
    /// </summary>
    public sealed class SourceSelection
    {
        private readonly Dictionary<string, bool> _marks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public event Action Changed;

        public bool IsIncluded(string path)
        {
            for (string p = Key(path); p != null; p = Parent(p))
            {
                if (_marks.TryGetValue(p, out bool mark))
                    return mark;
            }
            return false;
        }

        public bool HasIncludedBelow(string path) => _marks.Any(m => m.Value && IsBelow(m.Key, Key(path)));
        public bool HasExcludedBelow(string path) => _marks.Any(m => !m.Value && IsBelow(m.Key, Key(path)));

        /// <summary>Flips an item. Returns a message if the change is not possible.</summary>
        public string Toggle(string path)
        {
            string key = Key(path);
            bool target = !IsIncluded(key);
            string parent = Parent(key);
            bool parentIncluded = parent != null && IsIncluded(parent);

            if (target && !parentIncluded && AncestorMarkedIncluded(key))
                return "This item is inside an excluded folder. Tick that folder again to include it.";

            foreach (string below in _marks.Keys.Where(k => IsBelow(k, key)).ToList())
                _marks.Remove(below);
            if (target == parentIncluded)
                _marks.Remove(key);
            else
                _marks[key] = target;
            Changed?.Invoke();
            return null;
        }

        public void Include(string path)
        {
            _marks[Key(path)] = true;
            Changed?.Invoke();
        }

        public void Remove(string path)
        {
            _marks.Remove(Key(path));
            Changed?.Invoke();
        }

        public List<string> Sources => _marks
            .Where(m => m.Value && (Parent(m.Key) == null || !IsIncluded(Parent(m.Key))))
            .Select(m => m.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public List<string> Exclusions => _marks
            .Where(m => !m.Value && Parent(m.Key) != null && IsIncluded(Parent(m.Key)))
            .Select(m => m.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>Loads a job. Returns the exclusions that are patterns (not plain paths inside a source).</summary>
        public List<string> Load(IEnumerable<string> sources, IEnumerable<string> excludes)
        {
            _marks.Clear();
            foreach (string s in sources)
                _marks[Key(s)] = true;
            var patterns = new List<string>();
            foreach (string e in excludes)
            {
                string trimmed = e.TrimEnd('\\');
                bool plainPath = trimmed.IndexOfAny(new[] { '*', '?' }) < 0 && Path.IsPathRooted(trimmed) && trimmed.Length > 3;
                if (plainPath && IsIncluded(Parent(Key(trimmed)) ?? ""))
                    _marks[Key(trimmed)] = false;
                else
                    patterns.Add(e);
            }
            Changed?.Invoke();
            return patterns;
        }

        private bool AncestorMarkedIncluded(string key)
        {
            for (string p = Parent(key); p != null; p = Parent(p))
            {
                if (_marks.TryGetValue(p, out bool mark) && mark)
                    return true;
            }
            return false;
        }

        internal static string Key(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            try
            {
                return LongPath.Normalize(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        private static string Parent(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            try
            {
                return Path.GetDirectoryName(key);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool IsBelow(string candidate, string ancestor) =>
            !string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase) && LongPath.IsSameOrBelow(candidate, ancestor);
    }

    /// <summary>A folder or file in the source tree. Children load when the node is expanded.</summary>
    public sealed class FileTreeNode : ObservableObject
    {
        private static readonly FileTreeNode Placeholder = new FileTreeNode();
        private readonly SourceSelection _selection;
        private bool _expanded;
        private bool _loaded;

        private FileTreeNode()
        {
        }

        public FileTreeNode(SourceSelection selection, string fullPath, string name, bool isFolder)
        {
            _selection = selection;
            FullPath = fullPath;
            Name = name;
            IsFolder = isFolder;
            if (isFolder)
                Children.Add(Placeholder);
        }

        public string FullPath { get; }
        public string Name { get; }
        public bool IsFolder { get; }
        public ObservableCollection<FileTreeNode> Children { get; } = new ObservableCollection<FileTreeNode>();
        public bool IsCheckable => _selection != null;

        public bool IsChecked
        {
            get => _selection != null && _selection.IsIncluded(FullPath);
            set
            {
                if (_selection == null || value == IsChecked)
                    return;
                string error = _selection.Toggle(FullPath);
                if (error != null)
                    Ui.Info(error);
                OnPropertyChanged();
            }
        }

        public string Hint
        {
            get
            {
                if (_selection == null || !IsFolder)
                    return null;
                if (IsChecked && _selection.HasExcludedBelow(FullPath))
                    return "some items excluded";
                if (!IsChecked && _selection.HasIncludedBelow(FullPath))
                    return "contains selected items";
                return null;
            }
        }

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (!Set(ref _expanded, value) || !value || _loaded)
                    return;
                _loaded = true;
                LoadChildren();
            }
        }

        /// <summary>Updates check marks and hints of this node and all loaded descendants.</summary>
        public void RefreshState()
        {
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(Hint));
            if (!_loaded)
                return;
            foreach (var c in Children)
                c.RefreshState();
        }

        private void LoadChildren()
        {
            Children.Clear();
            try
            {
                var dir = new DirectoryInfo(LongPath.ToExtended(FullPath));
                foreach (var d in dir.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    Children.Add(new FileTreeNode(_selection, Path.Combine(FullPath, d.Name), d.Name, true));
                }
                foreach (var f in dir.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    Children.Add(new FileTreeNode(_selection, Path.Combine(FullPath, f.Name), f.Name, false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Children.Add(new FileTreeNode(null, FullPath, "(cannot read: " + ex.Message + ")", false));
            }
        }

        public static IEnumerable<FileTreeNode> Drives(SourceSelection selection)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string label;
                try
                {
                    if (!drive.IsReady)
                        continue;
                    label = drive.Name.TrimEnd('\\') + (string.IsNullOrEmpty(drive.VolumeLabel) ? "" : "  " + drive.VolumeLabel) +
                            "  (" + drive.DriveType.ToString().ToLowerInvariant() + ")";
                }
                catch (IOException)
                {
                    continue;
                }
                yield return new FileTreeNode(selection, drive.Name, label, true);
            }
        }
    }
}
