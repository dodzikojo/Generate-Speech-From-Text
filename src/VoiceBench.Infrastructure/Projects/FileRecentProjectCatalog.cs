using System.Text.Json;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed record RecentProjectEntry(
    string ProjectRoot,
    string Name,
    DateTimeOffset LastOpenedAt);

public sealed class FileRecentProjectCatalog
{
    private const int MaximumEntries = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicFileCommitter _committer;

    public FileRecentProjectCatalog(
        TimeProvider? timeProvider = null,
        string? catalogPath = null,
        IAtomicFileCommitter? committer = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _committer = committer ?? new AtomicFileCommitter();
        CatalogPath = Path.GetFullPath(catalogPath ?? GetDefaultCatalogPath());
    }

    public string CatalogPath { get; }

    public async Task<IReadOnlyList<RecentProjectEntry>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecentProjectCatalogDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return document.Projects
                .OrderByDescending(project => project.LastOpenedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(
        string projectRoot,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecentProjectCatalogDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            List<RecentProjectEntry> projects = document.Projects
                .Where(project => !string.Equals(project.ProjectRoot, root, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(project => project.LastOpenedAt)
                .ToList();
            projects.Insert(0, new RecentProjectEntry(root, name.Trim(), _timeProvider.GetUtcNow()));
            await SaveAsync(
                document with { Projects = projects.Take(MaximumEntries).ToList() },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(document with
            {
                Projects = document.Projects.Where(item => !string.Equals(
                    Path.TrimEndingDirectorySeparator(item.ProjectRoot), root, StringComparison.OrdinalIgnoreCase)).ToList(),
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string GetDefaultCatalogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IssueLab",
        "VoiceBench",
        "recent-projects.json");

    private async Task<RecentProjectCatalogDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CatalogPath))
        {
            return new RecentProjectCatalogDocument();
        }

        try
        {
            await using FileStream stream = new(
                CatalogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<RecentProjectCatalogDocument>(
                       stream,
                       ProjectJson.SerializerOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? new RecentProjectCatalogDocument();
        }
        catch (JsonException)
        {
            return new RecentProjectCatalogDocument();
        }
    }

    private async Task SaveAsync(
        RecentProjectCatalogDocument document,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(CatalogPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(CatalogPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    ProjectJson.SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _committer.Commit(temporaryPath, CatalogPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record RecentProjectCatalogDocument
    {
        public int SchemaVersion { get; init; } = 1;

        public List<RecentProjectEntry> Projects { get; init; } = [];
    }
}
