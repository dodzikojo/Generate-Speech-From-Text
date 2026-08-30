using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class FileRecentProjectCatalogTests
{
    [Fact]
    public async Task RecordAsync_persists_distinct_projects_in_most_recent_order()
    {
        using TemporaryDirectory directory = new();
        string catalogPath = Path.Combine(directory.Root, "recent-projects.json");
        DateTimeOffset now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
        MutableTimeProvider time = new(now);
        FileRecentProjectCatalog catalog = new(time, catalogPath);
        string firstRoot = Path.Combine(directory.Root, "first");
        string secondRoot = Path.Combine(directory.Root, "second");

        await catalog.RecordAsync(firstRoot, "First");
        time.Advance(TimeSpan.FromMinutes(1));
        await catalog.RecordAsync(secondRoot, "Second");
        time.Advance(TimeSpan.FromMinutes(1));
        await catalog.RecordAsync(firstRoot, "First renamed");

        FileRecentProjectCatalog restarted = new(catalogPath: catalogPath);
        IReadOnlyList<RecentProjectEntry> entries = await restarted.GetAsync();

        Assert.Collection(
            entries,
            first =>
            {
                Assert.Equal(Path.GetFullPath(firstRoot), first.ProjectRoot);
                Assert.Equal("First renamed", first.Name);
                Assert.Equal(now.AddMinutes(2), first.LastOpenedAt);
            },
            second => Assert.Equal(Path.GetFullPath(secondRoot), second.ProjectRoot));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task GetAsync_keeps_missing_projects_for_an_actionable_unavailable_state()
    {
        using TemporaryDirectory directory = new();
        string catalogPath = Path.Combine(directory.Root, "recent-projects.json");
        FileRecentProjectCatalog catalog = new(catalogPath: catalogPath);
        string missingRoot = Path.Combine(directory.Root, "moved-project");

        await catalog.RecordAsync(missingRoot, "Moved project");

        RecentProjectEntry entry = Assert.Single(await catalog.GetAsync());
        Assert.Equal(Path.GetFullPath(missingRoot), entry.ProjectRoot);
        Assert.False(Directory.Exists(entry.ProjectRoot));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }
}
