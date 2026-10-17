using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using Keepsake.Gui.Infrastructure;

namespace Keepsake.Gui.ViewModels
{
    /// <summary>Top-level state machine: connecting → setup or login → shell. Session loss returns to login.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private object _screen;

        public MainViewModel(ServiceConnection connection)
        {
            Connection = connection;
            Connection.SessionLost += message =>
            {
                if (Screen is ShellViewModel shell)
                    shell.Stop();
                Screen = new LoginViewModel(this, message);
            };
        }

        public ServiceConnection Connection { get; }

        public object Screen
        {
            get => _screen;
            private set => Set(ref _screen, value);
        }

        public void Start() => Start(null);

        public async void Start(string autoLoginPassword)
        {
            Screen = new MessageScreen("Connecting to the Keepsake service…", null);
            try
            {
                var status = await Connection.AuthStatus();
                if (status.PasswordSet && autoLoginPassword != null)
                {
                    await Connection.Login(autoLoginPassword);
                    ShowShell();
                    return;
                }
                Screen = status.PasswordSet ? (object)new LoginViewModel(this, null) : new SetupViewModel(this);
            }
            catch (Exception ex)
            {
                Screen = new MessageScreen(
                    "The Keepsake service is not reachable.",
                    ex.Message + "\n\nMake sure the 'Keepsake Backup Service' is installed and running (services.msc), then try again.",
                    new RelayCommand(Start));
            }
        }

        public void ShowShell() => Screen = new ShellViewModel(this);

        public void Logout()
        {
            if (Screen is ShellViewModel shell)
                shell.Stop();
            Connection.Logout();
            Screen = new LoginViewModel(this, null);
        }
    }

    public sealed class MessageScreen
    {
        public MessageScreen(string title, string details, ICommand retry = null)
        {
            Title = title;
            Details = details;
            Retry = retry;
        }

        public string Title { get; }
        public string Details { get; }
        public ICommand Retry { get; }
        public bool CanRetry => Retry != null;
    }

    public sealed class LoginViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _error;

        public LoginViewModel(MainViewModel main, string message)
        {
            _main = main;
            _error = message;
            LoginCommand = new AsyncCommand(Login);
        }

        public string Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        public ICommand LoginCommand { get; }

        private async Task Login(object parameter)
        {
            var box = (PasswordBox)parameter;
            Error = null;
            try
            {
                await _main.Connection.Login(box.Password);
                box.Clear();
                _main.ShowShell();
            }
            catch (Exception ex) when (ex is Core.KeepsakeException)
            {
                Error = ex.Message;
                box.SelectAll();
                box.Focus();
            }
        }
    }

    public sealed class SetupViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private string _error;

        public SetupViewModel(MainViewModel main)
        {
            _main = main;
            SaveCommand = new AsyncCommand(Save);
        }

        public string Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        public ICommand SaveCommand { get; }

        private async Task Save(object parameter)
        {
            var boxes = (object[])parameter;
            var first = (PasswordBox)boxes[0];
            var second = (PasswordBox)boxes[1];
            Error = null;
            if (first.Password.Length < 8)
            {
                Error = "The password must have at least 8 characters.";
                return;
            }
            if (first.Password != second.Password)
            {
                Error = "The two passwords do not match.";
                return;
            }
            try
            {
                await _main.Connection.Setup(first.Password);
                first.Clear();
                second.Clear();
                _main.ShowShell();
            }
            catch (Exception ex) when (ex is Core.KeepsakeException)
            {
                Error = ex.Message;
            }
        }
    }
}
