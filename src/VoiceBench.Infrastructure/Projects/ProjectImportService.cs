using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class ProjectImportService(
    IProjectRepository repository,
    ScriptImportService? splitter = null,
    Func<string>? stableIdFactory = null)
{
    private readonly ScriptImportService _splitter = splitter ?? new ScriptImportService();
    private readonly Func<string> _stableIdFactory = stableIdFactory ?? (() => Guid.NewGuid().ToString("N"));

    public async Task<VoiceBenchProjectDocument> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(sourcePath);
        if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("VoiceBench imports only .txt and .md script files.");
        }

        string content = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ImportedScriptSegment> imported = _splitter.Split(Path.GetFileName(sourcePath), content);
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int startingOrdinal = project.SegmentOrder.Count;
        List<SegmentDocument> segments = [.. project.Segments];
        List<string> order = [.. project.SegmentOrder];

        foreach (ImportedScriptSegment item in imported)
        {
            string segmentId = _stableIdFactory();
            int ordinal = startingOrdinal + item.Ordinal;
            string fileName = ScriptSegmentNaming.CreateFileName(ordinal, item.Title, segmentId);
            string relativePath = VoiceBenchProjectLayout.ToManifestPath(
                Path.Combine(VoiceBenchProjectLayout.ScriptsDirectory, fileName));
            string fullPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativePath);
            await File.WriteAllTextAsync(fullPath, item.Content, cancellationToken).ConfigureAwait(false);

            segments.Add(new SegmentDocument
            {
                SegmentId = segmentId,
                Title = item.Title,
                ScriptRelativePath = relativePath,
                Status = SegmentStatus.Draft,
                SourceSha256 = SourceTextHasher.Compute(item.Content),
            });
            order.Add(segmentId);
        }

        VoiceBenchProjectDocument updated = project with { Segments = segments, SegmentOrder = order };
        await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
