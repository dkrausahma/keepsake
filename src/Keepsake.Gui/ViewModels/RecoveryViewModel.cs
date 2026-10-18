using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Keepsake.Core;
using Keepsake.Core.Repo;
using Keepsake.Core.Storage;
using Keepsake.Gui.Infrastructure;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>
    /// Recovery mode (RST-07): open a repository by its location and the password or recovery key, then browse and
    /// restore – without the Keepsake service and without any configuration. Meant for the portable version on a
    /// replacement computer.
    /// </summary>
    public sealed class RecoveryViewModel : ObservableObject
    {
        private readonly Action _backToService;
        private readonly DispatcherTimer _timer;
        private string _location;
        private bool _useRecoveryKey;
        private string _recoveryKey;
        private string _sftpKeyFile;
        private string _error;
        private bool _busy;
        private LocalRestoreBackend _backend;
        private RestoreViewModel _restore;

        public RecoveryViewModel(Action backToService)
        {
            _backToService = backToService;
            BrowseFolderCommand = new RelayCommand(() =>
            {
                string picked = Ui.PickFolder("Folder of the backup (the folder that contains keepsake.json)", Location);
                if (picked != null)
                    Location = picked;
            });
            LoadKeyFileCommand = new RelayCommand(() =>
            {
                string file = Ui.OpenFile("Recovery key file", "Text files (*.txt)|*.txt|All files (*.*)|*.*");
                if (file != null)
                    RecoveryKey = File.ReadAllText(file);
            });
            BrowseSftpKeyCommand = new RelayCommand(() =>
            {
                string file = Ui.OpenFile("SFTP private key", "Key files (*.ppk;*.pem;*.key;id_*)|*.ppk;*.pem;*.key;id_*|All files (*.*)|*.*");
                if (file != null)
                    SftpKeyFile = file;
            });
            CloseCommand = new RelayCommand(Close, () => IsOpen && !(_backend?.IsRunning ?? false));
            BackToServiceCommand = new RelayCommand(() => { Close(); _backToService?.Invoke(); }, () => _backToService != null);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (s, e) =>
            {
                if (_restore != null && _restore.AutoRefresh)
                {
                    try
                    {
                        await _restore.Refresh();
                    }
                    catch (KeepsakeException ex)
                    {
                        Ui.ShowError(ex);
                    }
                }
            };
        }

        public string Location
        {
            get => _location;
            set
            {
                if (Set(ref _location, value))
                    OnPropertyChanged(nameof(IsSftp));
            }
        }

        public bool IsSftp => StorageFactory.IsSftp(Location?.Trim());
        public bool UseRecoveryKey { get => _useRecoveryKey; set { if (Set(ref _useRecoveryKey, value)) OnPropertyChanged(nameof(UsePassword)); } }
        public bool UsePassword { get => !_useRecoveryKey; set => UseRecoveryKey = !value; }
        public string RecoveryKey { get => _recoveryKey; set => Set(ref _recoveryKey, value); }
        public string SftpKeyFile { get => _sftpKeyFile; set => Set(ref _sftpKeyFile, value); }
        public string Error { get => _error; private set => Set(ref _error, value); }
        public bool Busy { get => _busy; private set => Set(ref _busy, value); }
        public bool CanGoBackToService => _backToService != null;

        public RestoreViewModel Restore
        {
            get => _restore;
            private set
            {
                if (Set(ref _restore, value))
                {
                    OnPropertyChanged(nameof(IsOpen));
                    OnPropertyChanged(nameof(OpenLocation));
                }
            }
        }

        public bool IsOpen => _restore != null;
        public string OpenLocation => _backend?.Location;

        public ICommand BrowseFolderCommand { get; }
        public ICommand LoadKeyFileCommand { get; }
        public ICommand BrowseSftpKeyCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand BackToServiceCommand { get; }

        /// <summary>Opens the repository. Passwords come from the view's password boxes and are not kept.</summary>
        public async Task Open(string password, string sftpPassword, string sftpKeyPassphrase)
        {
            Error = null;
            string location = (Location ?? "").Trim();
            if (location.Length == 0)
            {
                Error = "Enter the folder, network share, USB disk or sftp://user@host/path of the backup.";
                return;
            }
            Busy = true;
            try
            {
                RepoCredential credential = UseRecoveryKey
                    ? RepoCredential.FromRecoveryKeyText(RecoveryKey ?? "")
                    : RepoCredential.FromPassword(password ?? "");
                if (!UseRecoveryKey && string.IsNullOrEmpty(password))
                    throw new KeepsakeException("Enter the repository password, or choose the recovery key.");

                IStorage storage = StorageFactory.IsSftp(location)
                    ? await OpenSftp(location, sftpPassword, sftpKeyPassphrase)
                    : await Task.Run(() => StorageFactory.Open(location));
                try
                {
                    var repo = await Task.Run(() => Repository.Open(storage, credential));
                    _backend = new LocalRestoreBackend(repo);
                }
                catch
                {
                    storage.Dispose();
                    throw;
                }
                var restore = new RestoreViewModel(_backend, _backend.Location);
                Restore = restore;
                await restore.Refresh();
                _timer.Start();
            }
            catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Error = ex.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        private async Task<IStorage> OpenSftp(string location, string password, string keyPassphrase)
        {
            var options = StorageFactory.ParseSftp(location);
            if (!string.IsNullOrWhiteSpace(SftpKeyFile))
            {
                options.PrivateKey = File.ReadAllText(SftpKeyFile);
                options.KeyPassphrase = string.IsNullOrEmpty(keyPassphrase) ? null : keyPassphrase;
            }
            else
            {
                if (string.IsNullOrEmpty(password))
                    throw new KeepsakeException("Enter the SFTP password, or choose a private key file.");
                options.Password = password;
            }
            for (int attempt = 0; ; attempt++)
            {
                var storage = new SftpStorage(options);
                try
                {
                    await Task.Run(() => storage.Connect());
                    return storage;
                }
                catch (HostKeyException ex) when (attempt == 0 && !ex.Mismatch)
                {
                    storage.Dispose();
                    // First connection from this computer: the admin compares the fingerprint (e.g. with KeePass).
                    if (!Ui.Confirm("The SFTP server " + options.Host + " identifies itself with this host key:\n\n    " + ex.Fingerprint +
                                    "\n\nCompare it with the fingerprint you recorded for this server. Trust it and connect?", "Unknown host key"))
                        throw new KeepsakeException("Host key not trusted – not connected.");
                    options.HostKeyFingerprint = ex.Fingerprint;
                }
                catch
                {
                    storage.Dispose();
                    throw;
                }
            }
        }

        private void Close()
        {
            _timer.Stop();
            Restore = null;
            _backend?.Dispose();
            _backend = null;
        }
    }
}
