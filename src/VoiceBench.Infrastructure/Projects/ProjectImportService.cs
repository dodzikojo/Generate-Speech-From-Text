using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class ProjectImportService(
    IProjectRepository repository,
    ScriptImportService? splitter = null,
    Func<string>? stableIdFactory = null,
    Func<string>? chapterIdFactory = null)
{
    private readonly ScriptImportService _splitter = splitter ?? new ScriptImportService();
    private readonly Func<string> _stableIdFactory = stableIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Func<string> _chapterIdFactory = chapterIdFactory ?? (() => Guid.NewGuid().ToString("N"));

    public async Task<VoiceBenchProjectDocument> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        await ImportFilesAsync(projectRoot, [sourcePath], cancellationToken).ConfigureAwait(false);

    public async Task<VoiceBenchProjectDocument> ImportFilesAsync(
        string projectRoot,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("Choose at least one script to import.", nameof(sourcePaths));
        }

        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        List<SegmentDocument> segments = [.. project.Segments];
        List<ChapterDocument> chapters = [.. project.Chapters];
        List<string> chapterOrder = [.. project.ChapterOrder];
        List<string> createdDirectories = [];

        try
        {
            foreach (string sourcePath in sourcePaths)
            {
                string extension = Path.GetExtension(sourcePath);
                if (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("VoiceBench imports only .txt and .md script files.");
                }

                string content = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ImportedScriptSegment> imported = _splitter.Split(Path.GetFileName(sourcePath), content);
                string chapterId = _chapterIdFactory();
                string chapterTitle = Path.GetFileNameWithoutExtension(sourcePath).Trim();
                int chapterOrdinal = chapterOrder.Count + 1;
                string chapterDirectoryName = ScriptSegmentNaming.CreateDirectoryName(
                    chapterOrdinal,
                    chapterTitle,
                    chapterId);
                string chapterRelativeDirectory = VoiceBenchProjectLayout.ToManifestPath(
                    Path.Combine(VoiceBenchProjectLayout.ScriptsDirectory, chapterDirectoryName));
                string chapterDirectory = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, chapterRelativeDirectory);
                Directory.CreateDirectory(chapterDirectory);
                createdDirectories.Add(chapterDirectory);
                List<string> segmentOrder = [];

                foreach (ImportedScriptSegment item in imported)
                {
                    string segmentId = _stableIdFactory();
                    string fileName = ScriptSegmentNaming.CreateFileName(item.Ordinal, item.Title, segmentId);
                    string relativePath = VoiceBenchProjectLayout.ToManifestPath(
                        Path.Combine(chapterRelativeDirectory, fileName));
                    string fullPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativePath);
                    await File.WriteAllTextAsync(fullPath, item.Content, cancellationToken).ConfigureAwait(false);

                    segments.Add(new SegmentDocument
                    {
                        SegmentId = segmentId,
                        ChapterId = chapterId,
                        Title = item.Title,
                        ScriptRelativePath = relativePath,
                        Status = SegmentStatus.Draft,
                        SourceSha256 = SourceTextHasher.Compute(item.Content),
                    });
                    segmentOrder.Add(segmentId);
                }

                chapters.Add(new ChapterDocument
                {
                    ChapterId = chapterId,
                    Title = chapterTitle,
                    SegmentOrder = segmentOrder,
                });
                chapterOrder.Add(chapterId);
            }

            VoiceBenchProjectDocument updated = project with
            {
                Segments = segments,
                Chapters = chapters,
                ChapterOrder = chapterOrder,
            };
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            foreach (string directory in createdDirectories.AsEnumerable().Reverse())
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }

            throw;
        }
    }
}
