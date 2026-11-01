using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class WorkspaceSessionTests
{
    [Fact]
    public async Task RestoreMostRecentAsync_reopens_the_latest_project_without_activating_workbench()
    {
        using TemporaryDirectory directory = new();
        string catalogPath = Path.Combine(directory.Root, "recent-projects.json");
        FileProjectRepository projects = new();
        FileRecentProjectCatalog catalog = new(catalogPath: catalogPath);
        WorkspaceSession firstSession = new(projects, catalog);
        await firstSession.CreateAsync(directory.Root, "Persisted narration");
        string expectedRoot = firstSession.RequireProjectRoot();

        WorkspaceSession restartedSession = new(projects, new FileRecentProjectCatalog(catalogPath: catalogPath));
        bool activatedWorkbench = false;
        restartedSession.ProjectActivated += (_, _) => activatedWorkbench = true;

        bool restored = await restartedSession.RestoreMostRecentAsync();

        Assert.True(restored);
        Assert.False(activatedWorkbench);
        Assert.Equal(expectedRoot, restartedSession.RequireProjectRoot());
        Assert.Equal("Persisted narration", restartedSession.RequireProject().Name);
    }

    [Fact]
    public async Task OpenAndRefreshAsync_recover_interrupted_authoring_before_loading()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository projects = new();
        FileRecentProjectCatalog catalog = new(catalogPath: Path.Combine(directory.Root, "recent.json"));
        VoiceBenchProjectDocument project = await projects.CreateAsync(Path.Combine(directory.Root, "project"), "Recovery");
        RecordingTransactions transactions = new();
        WorkspaceSession session = new(projects, catalog, transactions);

        await session.OpenAsync(Path.Combine(directory.Root, "project"));
        await session.RefreshAsync();

        Assert.Equal(2, transactions.RecoveryCalls);
        Assert.Equal(project.ProjectId, session.RequireProject().ProjectId);
    }

    private sealed class RecordingTransactions : IAuthoringTransactionManager
    {
        public int RecoveryCalls { get; private set; }

        public Task<AuthoringTransactionState> GetStateAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthoringTransactionState(1, [], 0, false));

        public Task ExecuteAsync(string projectRoot, string label, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            action(cancellationToken);

        public Task<AuthoringTransactionRecord> UndoAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuthoringTransactionRecord> RedoAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RecoverInterruptedAsync(string projectRoot, CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            return Task.FromResult(false);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "IssueLab.VoiceBench.App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
