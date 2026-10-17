using System;
using System.Security.Principal;
using Keepsake.Core;
using Keepsake.Protocol;

namespace Keepsake.Service
{
    /// <summary>Shared service components, handed to the API.</summary>
    internal sealed class ServiceContext
    {
        public DataPaths Paths;
        public SettingsStore Settings;
        public JobStore Jobs;
        public RunHistory History;
        public AuthService Auth;
        public JobRunner Runner;
        public Scheduler Scheduler;
        public DateTime StartedAt;
    }

    /// <summary>Starts and stops all service components, both as Windows service and in console mode.</summary>
    internal sealed class ServiceHost : IDisposable
    {
        private readonly DataPaths _paths;
        private readonly string _pipeName;
        private readonly bool _consoleMode;
        private PipeServer _pipe;
        private bool _started;

        public ServiceHost(string dataFolder, string pipeName, bool consoleMode)
        {
            _paths = new DataPaths(dataFolder ?? DataPaths.DefaultRoot);
            _pipeName = pipeName ?? Wire.DefaultPipeName;
            _consoleMode = consoleMode;
        }

        public ServiceContext Context { get; private set; }

        public void Start()
        {
            // Restricting the data folder needs elevation; a console run with a custom folder is for development.
            _paths.Ensure(restrict: !_consoleMode);
            ServiceLog.Init(_paths.Logs, _consoleMode);
            ServiceLog.Info(ProgramInfo.Name + " service starting on " + ProgramInfo.Host + " (data: " + _paths.Root + ", account: " + ProgramInfo.User + ").");

            var settings = new SettingsStore(_paths.SettingsFile);
            var jobs = new JobStore(_paths.JobsFile);
            var history = new RunHistory(_paths.Runs);
            history.MarkInterrupted();
            var runner = new JobRunner(jobs, settings, history);
            var scheduler = new Scheduler(jobs, runner, history, settings, _paths.SchedulerFile);

            Context = new ServiceContext
            {
                Paths = _paths,
                Settings = settings,
                Jobs = jobs,
                History = history,
                Auth = new AuthService(settings),
                Runner = runner,
                Scheduler = scheduler,
                StartedAt = DateTime.UtcNow,
            };

            var handler = new ApiHandler(Context);
            _pipe = new PipeServer(_pipeName, handler, _consoleMode ? WindowsIdentity.GetCurrent().User : null);
            _pipe.Start();
            scheduler.Start();
            _started = true;
            if (!Context.Auth.PasswordSet)
                ServiceLog.Warn("No application password is set. Run 'keepsake setup' or open the Keepsake GUI.");
            ServiceLog.Info("Service started. " + jobs.List().Count + " job(s) configured.");
        }

        public void Stop()
        {
            if (!_started)
                return;
            _started = false;
            ServiceLog.Info("Service stopping...");
            Context.Scheduler.Dispose();
            _pipe.Dispose();
            Context.Runner.StopAll(TimeSpan.FromSeconds(60));
            ServiceLog.Info("Service stopped.");
        }

        public void Dispose() => Stop();
    }
}
