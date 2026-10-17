using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Keepsake.Core.Reporting;
using Keepsake.Core.Util;
using Keepsake.Gui.Infrastructure;
using Keepsake.Gui.Views;
using Keepsake.Protocol;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>Destination tab of the job editor: folder/share, USB disks, SFTP; repository password and checks.</summary>
    public sealed class DestinationEditorViewModel : ObservableObject
    {
        private readonly ServiceConnection _connection;
        private readonly string _jobId;
        private readonly string _originalJson;
        private string _type = DestinationTypes.Folder;
        private string _folderPath;
        private string _shareUser;
        private string _sharePassword;
        private string _usbFolder;
        private VolumeInfo _selectedVolume;
        private string _host;
        private int _port = 22;
        private string _user;
        private bool _authIsKey;
        private string _sftpPassword;
        private string _privateKey;
        private string _privateKeySource;
        private string _keyPassphrase;
        private string _remotePath;
        private string _hostKey;
        private string _repoPassword;
        private string _repoConfirm;
        private string _status;
        private bool _ok;

        public DestinationEditorViewModel(ServiceConnection connection, JobDetail detail, Func<string> jobName)
        {
            _connection = connection;
            _jobId = detail?.Job?.Id;
            var d = detail?.Job?.Destination;
            HasStoredRepositoryPassword = detail?.HasRepositoryPassword ?? false;
            HasStoredSftpPassword = detail?.HasSftpPassword ?? false;
            HasStoredSftpKey = detail?.HasSftpKey ?? false;
            if (d != null)
            {
                _type = d.Type ?? DestinationTypes.Folder;
                switch (_type)
                {
                    case DestinationTypes.Folder:
                        _folderPath = d.Path;
                        var cred = detail.Job.ShareCredentials?.FirstOrDefault(c => IsSameShare(c.Share, d.Path));
                        _shareUser = cred?.User;
                        HasStoredSharePassword = cred != null && detail.SharesWithPassword.Any(s => IsSameShare(s, d.Path));
                        break;
                    case DestinationTypes.Usb:
                        _usbFolder = d.Path;
                        foreach (var v in d.Volumes ?? new List<UsbVolume>())
                            Disks.Add(new UsbDiskRow(v));
                        break;
                    case DestinationTypes.Sftp:
                        _host = d.Host;
                        _port = d.Port;
                        _user = d.User;
                        _authIsKey = d.Auth == "key";
                        _remotePath = d.Path;
                        _hostKey = d.HostKeyFingerprint;
                        break;
                }
                _originalJson = Json.ToText(Build(), indented: false);
                _status = HasStoredRepositoryPassword
                    ? "Passwords are stored. Leave password fields empty to keep them."
                    : "No repository password stored – enter it below.";
                _ok = HasStoredRepositoryPassword;
            }
            else
            {
                _usbFolder = "Keepsake";
                _status = "Choose where the backups go. A new encrypted repository is created there when you save.";
            }
            JobName = jobName;

            BrowseFolderCommand = new RelayCommand(() =>
            {
                string picked = Ui.PickFolder("Where should the backups be stored?", FolderPath);
                if (picked != null)
                    FolderPath = picked;
            });
            RefreshVolumesCommand = new AsyncCommand(RefreshVolumes);
            AddDiskCommand = new RelayCommand(AddDisk, () => SelectedVolume != null);
            RemoveDiskCommand = new RelayCommand(p => { Disks.Remove((UsbDiskRow)p); Changed(); });
            PrepareDiskCommand = new AsyncCommand(p => PrepareDisk((UsbDiskRow)p), p => p is UsbDiskRow row && row.Connected);
            LoadKeyCommand = new RelayCommand(LoadKey);
            ForgetHostKeyCommand = new RelayCommand(() => HostKey = null, () => !string.IsNullOrEmpty(HostKey));
            CheckCommand = new AsyncCommand(async () => await Check(null));
        }

        public Func<string> JobName { get; }

        public bool HasStoredRepositoryPassword { get; }
        public bool HasStoredSftpPassword { get; }
        public bool HasStoredSftpKey { get; }
        public bool HasStoredSharePassword { get; }

        public string Type
        {
            get => _type;
            set
            {
                if (Set(ref _type, value))
                {
                    OnPropertyChanged(nameof(IsFolder));
                    OnPropertyChanged(nameof(IsUsb));
                    OnPropertyChanged(nameof(IsSftp));
                    Changed();
                    if (IsUsb && AvailableVolumes.Count == 0)
                        RefreshVolumesCommand.Execute(null);
                }
            }
        }

        public bool IsFolder { get => Type == DestinationTypes.Folder; set { if (value) Type = DestinationTypes.Folder; } }
        public bool IsUsb { get => Type == DestinationTypes.Usb; set { if (value) Type = DestinationTypes.Usb; } }
        public bool IsSftp { get => Type == DestinationTypes.Sftp; set { if (value) Type = DestinationTypes.Sftp; } }

        // Folder / share
        public string FolderPath { get => _folderPath; set { if (Set(ref _folderPath, value)) { OnPropertyChanged(nameof(IsShare)); Changed(); } } }
        public bool IsShare => (FolderPath ?? "").StartsWith(@"\\", StringComparison.Ordinal);
        public string ShareUser { get => _shareUser; set { if (Set(ref _shareUser, value)) Changed(); } }
        public string SharePassword { get => _sharePassword; set => Set(ref _sharePassword, value); }

        // USB
        public ObservableCollection<UsbDiskRow> Disks { get; } = new ObservableCollection<UsbDiskRow>();
        public ObservableCollection<VolumeInfo> AvailableVolumes { get; } = new ObservableCollection<VolumeInfo>();
        public VolumeInfo SelectedVolume { get => _selectedVolume; set => Set(ref _selectedVolume, value); }
        public string UsbFolder { get => _usbFolder; set { if (Set(ref _usbFolder, value)) Changed(); } }

        // SFTP
        public string Host { get => _host; set { if (Set(ref _host, value)) Changed(); } }
        public int Port { get => _port; set { if (Set(ref _port, value)) Changed(); } }
        public string User { get => _user; set { if (Set(ref _user, value)) Changed(); } }
        public bool AuthIsKey { get => _authIsKey; set { if (Set(ref _authIsKey, value)) { OnPropertyChanged(nameof(AuthIsPassword)); Changed(); } } }
        public bool AuthIsPassword { get => !_authIsKey; set => AuthIsKey = !value; }
        public string SftpPassword { get => _sftpPassword; set => Set(ref _sftpPassword, value); }
        public string KeyPassphrase { get => _keyPassphrase; set => Set(ref _keyPassphrase, value); }
        public string PrivateKeySource { get => _privateKeySource; private set => Set(ref _privateKeySource, value); }
        public string RemotePath { get => _remotePath; set { if (Set(ref _remotePath, value)) Changed(); } }
        public string HostKey { get => _hostKey; set { if (Set(ref _hostKey, value)) { OnPropertyChanged(nameof(HostKeyText)); Changed(); } } }
        public string HostKeyText => string.IsNullOrEmpty(HostKey) ? "Not trusted yet – checked when you test the destination." : "Trusted host key: " + HostKey;

        public string KeyStatus => _privateKey != null ? "Key loaded from " + PrivateKeySource
            : HasStoredSftpKey ? "A private key is stored. Load a file to replace it."
            : "No key loaded.";

        // Repository password
        public string RepoPassword { get => _repoPassword; set => Set(ref _repoPassword, value); }
        public string RepoConfirm { get => _repoConfirm; set => Set(ref _repoConfirm, value); }

        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool Ok { get => _ok; private set => Set(ref _ok, value); }

        public bool IsChanged => _originalJson == null || _originalJson != Json.ToText(Build(), indented: false);

        public ICommand BrowseFolderCommand { get; }
        public ICommand RefreshVolumesCommand { get; }
        public ICommand AddDiskCommand { get; }
        public ICommand RemoveDiskCommand { get; }
        public ICommand PrepareDiskCommand { get; }
        public ICommand LoadKeyCommand { get; }
        public ICommand ForgetHostKeyCommand { get; }
        public ICommand CheckCommand { get; }

        public Destination Build()
        {
            switch (Type)
            {
                case DestinationTypes.Usb:
                    return new Destination
                    {
                        Type = Type,
                        Path = (UsbFolder ?? "").Trim().Trim('\\'),
                        Volumes = Disks.Select(d => new UsbVolume { VolumeId = d.VolumeId, Label = d.Label, TotalBytes = d.TotalBytes }).ToList(),
                    };
                case DestinationTypes.Sftp:
                    return new Destination
                    {
                        Type = Type,
                        Host = Host?.Trim(),
                        Port = Port,
                        User = User?.Trim(),
                        Auth = AuthIsKey ? "key" : "password",
                        Path = RemotePath?.Trim(),
                        HostKeyFingerprint = HostKey,
                    };
                default:
                    return new Destination { Type = DestinationTypes.Folder, Path = FolderPath?.Trim() };
            }
        }

        /// <summary>Credentials for the destination share, if one is configured.</summary>
        public ShareCredential ShareCredential()
        {
            if (!IsFolder || !IsShare || string.IsNullOrWhiteSpace(ShareUser))
                return null;
            return new ShareCredential { Share = ShareRoot(FolderPath), User = ShareUser.Trim() };
        }

        /// <summary>Secrets typed into this tab; untouched fields stay null (= keep stored value).</summary>
        public void FillSecrets(JobSecrets secrets)
        {
            if (!string.IsNullOrEmpty(RepoPassword))
                secrets.RepositoryPassword = RepoPassword;
            if (IsSftp)
            {
                if (AuthIsKey)
                {
                    if (_privateKey != null)
                        secrets.SftpPrivateKey = _privateKey;
                    if (!string.IsNullOrEmpty(KeyPassphrase))
                        secrets.SftpKeyPassphrase = KeyPassphrase;
                }
                else if (!string.IsNullOrEmpty(SftpPassword))
                {
                    secrets.SftpPassword = SftpPassword;
                }
            }
            var share = ShareCredential();
            if (share != null && !string.IsNullOrEmpty(SharePassword))
                secrets.SharePasswords[share.Share] = SharePassword;
        }

        /// <summary>Tests the destination; asks to trust an unknown SFTP host key and retries.</summary>
        public async Task<RepoTestResult> Check(string volumeId, IEnumerable<ShareCredential> extraShares = null, JobSecrets extraSecrets = null)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Status = "Checking…";
                var p = Params(volumeId, extraShares, extraSecrets);
                var result = await _connection.Call<RepoTestResult>(Methods.RepoTest, p);
                if (result.UntrustedHostKey != null && attempt == 0)
                {
                    string question = result.HostKeyChanged
                        ? "WARNING: the host key of " + Host + " has CHANGED.\n\nNew key: " + result.UntrustedHostKey + "\nTrusted: " + HostKey +
                          "\n\nThis can mean the server was reinstalled – or that the connection is intercepted. Only continue if the server administrator confirms the new key.\n\nTrust the new key?"
                        : "The SFTP server " + Host + " presents this host key:\n\n" + result.UntrustedHostKey +
                          "\n\nCompare it with the server's fingerprint (ssh-keygen -lf on the server). Trust it?";
                    if (!Ui.Confirm(question, "SFTP host key"))
                    {
                        Report(result);
                        return result;
                    }
                    HostKey = result.UntrustedHostKey;
                    continue;
                }
                Report(result);
                return result;
            }
            return null;
        }

        public RepoParams Params(string volumeId, IEnumerable<ShareCredential> extraShares, JobSecrets extraSecrets)
        {
            var secrets = extraSecrets ?? new JobSecrets();
            FillSecrets(secrets);
            var shares = (extraShares ?? Enumerable.Empty<ShareCredential>()).ToList();
            var own = ShareCredential();
            if (own != null && !shares.Any(s => string.Equals(s.Share, own.Share, StringComparison.OrdinalIgnoreCase)))
                shares.Add(own);
            return new RepoParams { JobId = _jobId, Destination = Build(), ShareCredentials = shares, Secrets = secrets, VolumeId = volumeId };
        }

        private void Report(RepoTestResult r)
        {
            var parts = new List<string>();
            if (!r.Reachable)
            {
                Ok = false;
                Status = string.Join(" ", r.Problems.DefaultIfEmpty("The destination is not reachable."));
                return;
            }
            parts.Add(r.Location ?? "");
            if (r.Exists)
                parts.Add(r.RepositoryId != null ? "Repository found, password correct – " + r.Snapshots + " snapshot(s)." : "Repository found.");
            else
                parts.Add("No repository yet – a new encrypted repository is created when you save.");
            parts.Add((r.CanWrite ? "Writable" : "NOT writable") + (r.CanWrite ? (r.CanDelete ? ", deletable" : ", but files cannot be deleted") : "") +
                      (r.FreeBytes.HasValue ? " · " + Format.Bytes(r.FreeBytes.Value) + " free" : ""));
            parts.AddRange(r.Problems);
            Ok = r.CanWrite && r.Problems.Count == 0;
            Status = string.Join(Environment.NewLine, parts.Where(s => !string.IsNullOrEmpty(s)));
        }

        private async Task RefreshVolumes()
        {
            var volumes = await _connection.Call<List<VolumeInfo>>(Methods.VolumesList);
            AvailableVolumes.Clear();
            foreach (var v in volumes.OrderBy(v => v.Drive))
                AvailableVolumes.Add(v);
            SelectedVolume = AvailableVolumes.FirstOrDefault(v => v.DriveType == "Removable") ?? AvailableVolumes.FirstOrDefault();
            foreach (var d in Disks)
                d.SetConnection(volumes.FirstOrDefault(v => string.Equals(v.VolumeId, d.VolumeId, StringComparison.OrdinalIgnoreCase)));
        }

        private void AddDisk()
        {
            var v = SelectedVolume;
            if (Disks.Any(d => string.Equals(d.VolumeId, v.VolumeId, StringComparison.OrdinalIgnoreCase)))
                return;
            var row = new UsbDiskRow(new UsbVolume { VolumeId = v.VolumeId, Label = v.Label, TotalBytes = v.TotalBytes });
            row.SetConnection(v);
            Disks.Add(row);
            Changed();
        }

        /// <summary>Creates the repository on one specific USB disk (rotation: every disk gets its own repository).</summary>
        private async Task PrepareDisk(UsbDiskRow row)
        {
            var test = await Check(row.VolumeId);
            if (test == null || !test.Reachable)
                return;
            if (test.Exists)
            {
                Ui.Info("This disk already holds a Keepsake repository for this location.");
                return;
            }
            await CreateRepository(row.VolumeId, "the USB disk " + row.Display);
        }

        /// <summary>Called by Save: makes sure a repository exists (creating it with confirmation) when the destination is new.</summary>
        public async Task<string> EnsureRepository(IEnumerable<ShareCredential> extraShares, JobSecrets extraSecrets)
        {
            bool needsCheck = IsChanged || !HasStoredRepositoryPassword || !string.IsNullOrEmpty(RepoPassword);
            if (!needsCheck)
                return null;
            if (string.IsNullOrEmpty(RepoPassword) && (!HasStoredRepositoryPassword || IsChanged))
                return "Enter the repository password (Destination tab).";

            string volume = IsUsb ? Disks.FirstOrDefault(d => d.Connected)?.VolumeId : null;
            if (IsUsb && volume == null)
            {
                // No disk connected: the job can still be saved; the disk is prepared when it is connected.
                return HasStoredRepositoryPassword || !string.IsNullOrEmpty(RepoPassword) ? null : "Enter the repository password.";
            }
            var test = await Check(volume, extraShares, extraSecrets);
            if (test == null)
                return "The destination could not be checked.";
            if (!test.Reachable || (test.UntrustedHostKey != null))
                return string.Join(" ", test.Problems.DefaultIfEmpty("The destination is not reachable."));
            if (test.Exists)
                return test.Problems.FirstOrDefault(p => p.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!await CreateRepository(volume, Build().ToString(), extraShares, extraSecrets))
                return "Saving cancelled: the repository was not created.";
            return null;
        }

        private async Task<bool> CreateRepository(string volumeId, string where, IEnumerable<ShareCredential> extraShares = null, JobSecrets extraSecrets = null)
        {
            if (string.IsNullOrEmpty(RepoPassword) || RepoPassword.Length < 8)
            {
                Ui.Info("Enter a repository password with at least 8 characters (and repeat it) to create the repository.");
                return false;
            }
            if (RepoPassword != RepoConfirm)
            {
                Ui.Info("The repository passwords do not match.");
                return false;
            }
            if (!Ui.Confirm("Create a new encrypted repository on\n" + where + " ?"))
                return false;
            var init = await _connection.Call<RepoInitResult>(Methods.RepoInit, Params(volumeId, extraShares, extraSecrets));
            RecoveryKeyWindow.ShowKey(init.RecoveryKey, where);
            Status = "Repository created on " + where + ".";
            Ok = true;
            return true;
        }

        private void LoadKey()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Private key (OpenSSH or PuTTY format)", Filter = "Key files|*.pem;*.key;*.ppk;id_*|All files|*.*" };
            if (dialog.ShowDialog(Ui.Owner) != true)
                return;
            _privateKey = File.ReadAllText(dialog.FileName);
            PrivateKeySource = Path.GetFileName(dialog.FileName);
            OnPropertyChanged(nameof(KeyStatus));
        }

        private void Changed()
        {
            Ok = false;
            OnPropertyChanged(nameof(IsChanged));
        }

        private static string ShareRoot(string uncPath)
        {
            var parts = uncPath.Trim('\\').Split('\\');
            return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : uncPath;
        }

        private static bool IsSameShare(string share, string path) =>
            share != null && path != null && path.StartsWith(@"\\", StringComparison.Ordinal) &&
            string.Equals(ShareRoot(share), ShareRoot(path), StringComparison.OrdinalIgnoreCase);
    }

    public sealed class UsbDiskRow : ObservableObject
    {
        private bool _connected;
        private string _drive;

        public UsbDiskRow(UsbVolume volume)
        {
            VolumeId = volume.VolumeId;
            Label = volume.Label;
            TotalBytes = volume.TotalBytes;
        }

        public string VolumeId { get; }
        public string Label { get; }
        public long TotalBytes { get; }
        public bool Connected { get => _connected; private set => Set(ref _connected, value); }

        public string Display => (string.IsNullOrEmpty(Label) ? "(no label)" : Label) + " · " + Format.Bytes(TotalBytes);
        public string State => Connected ? "connected as " + _drive : "not connected";

        public void SetConnection(VolumeInfo info)
        {
            _drive = info?.Drive;
            Connected = info != null;
            OnPropertyChanged(nameof(State));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public sealed class ShareCredentialRow : ObservableObject
    {
        private string _share;
        private string _user;
        private string _password;

        public ShareCredentialRow(ShareCredential c, bool hasStoredPassword)
        {
            _share = c?.Share;
            _user = c?.User;
            HasStoredPassword = hasStoredPassword;
        }

        public bool HasStoredPassword { get; }
        public string Share { get => _share; set => Set(ref _share, value); }
        public string User { get => _user; set => Set(ref _user, value); }
        public string Password { get => _password; set => Set(ref _password, value); }
        public string PasswordHint => HasStoredPassword ? "stored – leave empty to keep" : "";
    }
}
