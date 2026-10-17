using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Keepsake.Gui.Infrastructure
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Refreshes every binding of this object.</summary>
        protected void OnAllChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => _execute(parameter);
    }

    /// <summary>Command running an async action; disabled while it runs. Errors are shown to the user.</summary>
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Func<object, bool> _canExecute;
        private bool _running;

        public AsyncCommand(Func<Task> execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public AsyncCommand(Func<object, Task> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object parameter)
        {
            _running = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                Ui.ShowError(ex);
            }
            finally
            {
                _running = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
