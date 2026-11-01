using System.Text.Json;
using IssueLab.VoiceBench.Application;
using IssueLab.VoiceBench.Application.Bridge;
using IssueLab.VoiceBench.App.Services;
using Windows.Storage;

namespace IssueLab.VoiceBench.App.Automation;

public sealed class DesktopAutomationHost : IAsyncDisposable
{
    private const string EnabledSetting = "Automation.LocalMcpEnabled";
    private readonly MainWindow _window;
    private readonly WorkspaceSession _session;
    private readonly SemaphoreSlim _serverGate = new(1, 1);
    private LocalControlServer? _server;
    private bool _syncing;
    private bool _disposed;
    public AutomationDispatcher Dispatcher { get; }
    public bool Enabled { get; private set; }
    public string? StartupError { get; private set; }

    public DesktopAutomationHost(MainWindow window)
    {
        _window = window;
        _session = App.Services.Session;
        Enabled = ApplicationData.Current.LocalSettings.Values[EnabledSetting] is not bool enabled || enabled;
        Dispatcher = new AutomationDispatcher(new DesktopAutomationAdapter(window), App.Services.Projects,
            App.Services.RecentProjects, App.Services.AuthoringTransactions, App.Services.WritingAssistant);
        App.Services.Automation = Dispatcher;
        _session.BeforeActivate = BeforeActivate;
        _session.BeforeProjectChange = BeforeProjectChange;
        _session.ProjectChanged += Session_ProjectChanged;
        Dispatcher.ProjectChanged += Dispatcher_ProjectChanged;
    }

    public async Task StartAsync()
    {
        try
        {
            if (_session.HasProject) Dispatcher.BindProject(_session.ProjectRoot, _session.Project);
            if (_session.HasProject) await Dispatcher.RecoverJobsAsync(_session.RequireProjectRoot(), CancellationToken.None);
            await SetEnabledAsync(Enabled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AutomationException)
        {
            StartupError = exception is AutomationException automation ? automation.Message : "The local control pipe could not be started. Close another VoiceBench instance and retry.";
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        await _serverGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Enabled = enabled;
            ApplicationData.Current.LocalSettings.Values[EnabledSetting] = enabled;
            if (_server is not null)
            {
                _server.Enabled = enabled;
                return;
            }
            LocalControlServer server = new(Dispatcher, new LocalControlOptions { Enabled = enabled });
            await server.StartAsync();
            _server = server;
            StartupError = null;
        }
        finally { _serverGate.Release(); }
    }

    private void BeforeActivate(string root, Core.VoiceBenchProjectDocument project)
    {
        if (_syncing) return;
        _syncing = true;
        try { Dispatcher.BindProject(root, project); }
        finally { _syncing = false; }
    }

    private void BeforeProjectChange(string root)
    {
        if (_syncing || string.Equals(root, _session.ProjectRoot, StringComparison.OrdinalIgnoreCase)) return;
        JsonElement workspace = AutomationJson.Element(_window.ReadAutomationWorkspace());
        if (workspace.GetProperty("editor").GetProperty("isDirty").GetBoolean())
            throw new AutomationException("unsaved_draft", "Save or reload the Workbench draft before opening another project.");
        if (Dispatcher.HasActiveJobs) throw new AutomationException("project.jobs-active", "Finish or cancel active jobs before opening another project.");
    }

    private void Session_ProjectChanged(object? sender, EventArgs args)
    {
        if (_syncing || _disposed) return;
        if (!_window.DispatcherQueue.HasThreadAccess)
        {
            _window.DispatcherQueue.TryEnqueue(() => Session_ProjectChanged(sender, args));
            return;
        }
        _syncing = true;
        try { Dispatcher.BindProject(_session.ProjectRoot, _session.Project); }
        finally { _syncing = false; }
    }

    private void Dispatcher_ProjectChanged(object? sender, AutomationProjectChangedEventArgs args)
    {
        if (_syncing || _disposed) return;
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _syncing = true;
            try { _session.ApplyAutomationProject(args.ProjectRoot, args.Project); }
            finally { _syncing = false; }
        });
    }

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken = default) => new
    {
        enabled = Enabled, transport = "stdio / same-user named pipe", processId = Environment.ProcessId,
        connectedClients = _server?.ConnectedClientCount ?? 0,
        clients = _server?.Connections,
        activeRequests = _server?.ActiveRequestCount ?? 0,
        hasActiveJobs = Dispatcher.HasActiveJobs,
        jobs = _session.HasProject ? AutomationJson.Element((await Dispatcher.GetJobsAsync(cancellationToken)).Select(job =>
            new { job.JobId, job.Command, job.Status, job.Progress, job.StartedAt, job.FinishedAt })) : AutomationJson.Element(Array.Empty<object>()),
        startupError = StartupError,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _session.BeforeActivate = null;
        _session.BeforeProjectChange = null;
        _session.ProjectChanged -= Session_ProjectChanged;
        Dispatcher.ProjectChanged -= Dispatcher_ProjectChanged;
        await Dispatcher.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        App.Services.Automation = null;
    }
}
