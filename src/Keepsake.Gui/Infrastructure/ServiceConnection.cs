using System;
using System.Threading.Tasks;
using System.Windows;
using Keepsake.Core;
using Keepsake.Protocol;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>
    /// The GUI's link to the service. Calls run on a worker thread (Argon2 logins, repository access) so the
    /// window never freezes. Raises <see cref="SessionLost"/> when the session expired or the service restarted.
    /// </summary>
    public sealed class ServiceConnection
    {
        private readonly string _pipeName;
        private readonly object _lock = new object();
        private KeepsakeClient _client;
        private string _token;

        public ServiceConnection(string pipeName)
        {
            _pipeName = pipeName;
        }

        public event Action<string> SessionLost;

        public bool LoggedIn => _token != null;

        public Task<T> Call<T>(string method, object parameters = null) => Task.Run(() => Invoke(c => c.Call<T>(method, parameters)));

        public Task Call(string method, object parameters = null) => Task.Run(() => Invoke(c =>
        {
            c.Call(method, parameters);
            return true;
        }));

        public Task<AuthStatusResult> AuthStatus() => Task.Run(() => Invoke(c => c.Call<AuthStatusResult>(Methods.AuthStatus), auth: false));

        public Task Setup(string password) => Task.Run(() =>
        {
            var result = Invoke(c => c.Call<LoginResult>(Methods.AuthSetup, new PasswordParams { Password = password }), auth: false);
            UseToken(result.Token);
        });

        public Task Login(string password) => Task.Run(() =>
        {
            var result = Invoke(c => c.Call<LoginResult>(Methods.AuthLogin, new PasswordParams { Password = password }), auth: false);
            UseToken(result.Token);
        });

        public void Logout()
        {
            string token;
            lock (_lock)
            {
                token = _token;
                _token = null;
            }
            if (token != null)
                Task.Run(() =>
                {
                    try
                    {
                        Invoke(c =>
                        {
                            c.Token = token;
                            c.Call(Methods.AuthLogout);
                            c.Token = null;
                            return true;
                        }, auth: false);
                    }
                    catch (KeepsakeException)
                    {
                        // Already gone.
                    }
                });
        }

        private void UseToken(string token)
        {
            lock (_lock)
            {
                _token = token;
                if (_client != null)
                    _client.Token = token;
            }
        }

        private T Invoke<T>(Func<KeepsakeClient, T> call, bool auth = true)
        {
            KeepsakeClient client;
            lock (_lock)
            {
                if (_client == null)
                {
                    _client = KeepsakeClient.Connect(_pipeName, 3000);
                    _client.Token = _token;
                }
                client = _client;
            }
            try
            {
                return call(client);
            }
            catch (ApiException ex) when (auth && ex.Code == ErrorCodes.Unauthorized)
            {
                lock (_lock)
                    _token = null;
                RaiseSessionLost(ex.Message);
                throw;
            }
            catch (ApiException)
            {
                throw;
            }
            catch (KeepsakeException ex)
            {
                // Pipe broken (service restarted): the next call reconnects; the old session is gone.
                lock (_lock)
                {
                    _client?.Dispose();
                    _client = null;
                    bool hadSession = _token != null;
                    _token = null;
                    if (hadSession && auth)
                        RaiseSessionLost("The connection to the Keepsake service was lost. Please log in again.");
                }
                throw new KeepsakeException(ex.Message);
            }
        }

        private void RaiseSessionLost(string message)
        {
            var handler = SessionLost;
            if (handler != null)
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => handler(message)));
        }
    }
}
