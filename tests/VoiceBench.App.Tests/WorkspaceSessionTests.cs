using IssueLab.VoiceBench.App.Services;
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
