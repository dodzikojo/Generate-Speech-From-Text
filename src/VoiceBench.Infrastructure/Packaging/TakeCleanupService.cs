using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Packaging;

public sealed record TakeCleanupResult(int TakesRemoved, long BytesRecycled, IReadOnlyList<string> Failures);

/// <summary>
/// Measures take storage and cleans up extra takes of approved segments. The whole clean-up runs
/// inside one project update: the plan is made from the latest project, each take's audio goes to
/// the Recycle Bin, and exactly the recycled takes are marked deleted, so no other writer (an agent
/// approving a take, a mix edit) can change the project in between.
/// </summary>
public sealed class TakeCleanupService(IProjectRepository repository, IFileRecycler recycler, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TakeStorageReport> MeasureAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => TakeCleanupRules.Measure(
            project,
            take => TakeAudioFiles.List(projectRoot, take),
            path => TakeAudioFiles.Size(projectRoot, path)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The takes clean-up would remove now, checked against the audio on disk.</summary>
    public static IReadOnlyList<TakeCleanupSegment> Plan(string projectRoot, VoiceBenchProjectDocument project) =>
        TakeCleanupRules.Plan(project, take => TakeAudioFiles.List(projectRoot, take).Count > 0);

    /// <summary>
    /// Cleans up the given takes. Each is checked again against the latest project, so a take that
    /// was approved, or placed in a mix, after the operator confirmed is kept.
    /// </summary>
    public async Task<TakeCleanupResult> CleanUpAsync(
        string projectRoot,
        IReadOnlyCollection<Guid> renderIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(renderIds);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(projectRoot);
        HashSet<Guid> requested = [.. renderIds];
        List<Guid> recycled = [];
        List<string> failures = [];
        long bytes = 0;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Recycling happens inside the update, under the project's write gate; the update always
        // returns the project with exactly the recycled takes marked, whatever else goes wrong.
        await ProjectDocumentUpdater.UpdateAsync(repository, root, latest =>
        {
            List<(SegmentDocument Segment, RenderRecord Take)> removable = Plan(root, latest)
                .SelectMany(plan => plan.Removable.Select(take => (latest.Segments.Single(item => item.SegmentId == plan.SegmentId), take)))
                .Where(item => requested.Contains(item.take.RenderId))
                .ToList();
            HashSet<Guid> removing = removable.Select(item => item.Take.RenderId).ToHashSet();

            // Never recycle a file a remaining take or audio asset still uses.
            HashSet<string> stillUsed = new(StringComparer.OrdinalIgnoreCase);
            foreach (SegmentDocument segment in latest.Segments)
            foreach (RenderRecord take in segment.RenderHistory.Where(take => !removing.Contains(take.RenderId) && take.AudioDeletedAt is null))
            foreach (string path in TakeAudioFiles.List(root, take))
                stillUsed.Add(path);
            foreach (AudioAssetRecord asset in latest.AudioAssets.Where(asset => asset.RenderId is not Guid id || !removing.Contains(id)))
                if (!string.IsNullOrWhiteSpace(asset.RelativePath)) stillUsed.Add(asset.RelativePath.Replace('\\', '/'));

            foreach ((SegmentDocument segment, RenderRecord take) in removable)
            {
                try
                {
                    List<string> files = TakeAudioFiles.List(root, take).Where(path => !stillUsed.Contains(path)).ToList();
                    long size = files.Sum(path => TakeAudioFiles.Size(root, path) ?? 0);
                    recycler.Recycle(files.Select(path => VoiceBenchProjectLayout.ResolveRelativePath(root, path)).ToList());
                    recycled.Add(take.RenderId);
                    bytes += size;
                }
                catch (Exception exception)
                {
                    failures.Add($"{segment.Title} · take {take.TakeNumber:00}: {exception.Message}");
                }
            }

            return recycled.Count == 0 ? latest : TakeCleanupRules.MarkDeleted(latest, recycled, now);
        }, CancellationToken.None).ConfigureAwait(false);

        return new TakeCleanupResult(recycled.Count, bytes, failures);
    }
}
