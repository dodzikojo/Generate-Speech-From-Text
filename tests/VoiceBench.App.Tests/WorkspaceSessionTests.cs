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

    [Fact]
    public async Task CreateAsync_uses_the_committed_timestamp_and_allows_the_first_save()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository projects = new(new AdvancingClock());
        WorkspaceSession session = new(projects, new FileRecentProjectCatalog(catalogPath: Path.Combine(directory.Root, "recent.json")));

        await session.CreateAsync(directory.Root, "New project");
        VoiceBenchProjectDocument saved = await projects.LoadAsync(session.RequireProjectRoot());
        Assert.Equal(saved.UpdatedAt, session.RequireProject().UpdatedAt);

        await session.SaveAsync(session.RequireProject() with { Name = "Renamed" });

        Assert.Equal("Renamed", (await projects.LoadAsync(session.RequireProjectRoot())).Name);
    }

    [Fact]
    public async Task SaveAsync_rejects_a_stale_snapshot_and_preserves_the_newer_manifest()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository projects = new(new AdvancingClock());
        WorkspaceSession session = new(projects, new FileRecentProjectCatalog(catalogPath: Path.Combine(directory.Root, "recent.json")));
        await session.CreateAsync(directory.Root, "Concurrent project");
        VoiceBenchProjectDocument stale = session.RequireProject();
        await projects.UpdateAsync(session.RequireProjectRoot(), current => current with { Name = "Newer render-side update" });

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SaveAsync(stale with { Name = "Old editor overwrite" }));

        Assert.Contains("newer manifest was preserved", failure.Message, StringComparison.Ordinal);
        Assert.Equal("Newer render-side update", (await projects.LoadAsync(session.RequireProjectRoot())).Name);
    }

    [Fact]
    public async Task ApplyAutomationProject_updates_the_shared_selection_without_writing_or_reactivating_same_root()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository projects = new();
        string root = Path.Combine(directory.Root, "project");
        VoiceBenchProjectDocument document = await projects.CreateAsync(root, "Saved name");
        WorkspaceSession session = new(projects, new FileRecentProjectCatalog(catalogPath: Path.Combine(directory.Root, "recent.json")));
        int changed = 0, activated = 0;
        session.ProjectChanged += (_, _) => changed++;
        session.ProjectActivated += (_, _) => activated++;

        session.ApplyAutomationProject(root, document);
        session.ApplyAutomationProject(root, document with { Name = "Bridge snapshot" });

        Assert.Equal("Bridge snapshot", session.RequireProject().Name);
        Assert.Equal("Saved name", (await projects.LoadAsync(root)).Name);
        Assert.Equal(2, changed);
        Assert.Equal(1, activated);
        session.ApplyAutomationProject(null, null);
        Assert.False(session.HasProject);
        Assert.Equal(1, activated);
    }

    [Fact]
    public async Task BeforeProjectChange_can_refuse_switching_before_any_project_is_created()
    {
        using TemporaryDirectory directory = new();
        WorkspaceSession session = new(new FileProjectRepository(), new FileRecentProjectCatalog(catalogPath: Path.Combine(directory.Root, "recent.json")))
        {
            BeforeProjectChange = _ => throw new InvalidOperationException("Save the draft first."),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateAsync(directory.Root, "Blocked project"));

        Assert.False(Directory.Exists(Path.Combine(directory.Root, "Blocked project")));
        Assert.False(session.HasProject);
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Increment(ref _ticks));
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
