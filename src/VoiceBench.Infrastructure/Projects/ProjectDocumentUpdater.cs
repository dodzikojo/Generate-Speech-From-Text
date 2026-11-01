using System.Collections.Concurrent;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public static class ProjectDocumentUpdater
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static SemaphoreSlim Gate(string root) => Gates.GetOrAdd(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), _ => new SemaphoreSlim(1, 1));

    public static async Task<VoiceBenchProjectDocument> UpdateAsync(
        IProjectRepository repository,
        string root,
        Func<VoiceBenchProjectDocument, VoiceBenchProjectDocument> update,
        CancellationToken cancellationToken = default)
    {
        if (repository is FileProjectRepository files)
            return await files.UpdateAsync(root, update, cancellationToken).ConfigureAwait(false);
        var current = await repository.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        var updated = update(current);
        await repository.SaveAsync(root, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
