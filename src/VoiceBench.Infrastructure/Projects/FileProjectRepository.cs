using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class FileProjectRepository(
    TimeProvider? timeProvider = null,
    IAtomicFileCommitter? committer = null) : IProjectRepository
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ProjectDocumentMigrator _migrator = new(
        [
            new OneToTwoProjectDocumentMigration(),
            new TwoToThreeProjectDocumentMigration(),
            new ThreeToFourProjectDocumentMigration(),
        ]);
    private readonly IAtomicFileCommitter _committer = committer ?? new AtomicFileCommitter();

    public async Task<VoiceBenchProjectDocument> CreateAsync(
        string projectRoot,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string root = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(root);
        string manifestPath = Path.Combine(root, VoiceBenchProjectLayout.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            throw new IOException($"A VoiceBench project already exists at '{root}'.");
        }

        VoiceBenchProjectLayout.Ensure(root);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        VoiceBenchProjectDocument document = new()
        {
            ProjectId = Guid.NewGuid(),
            Name = name.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await SaveAsync(root, document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<VoiceBenchProjectDocument> LoadAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        string manifestPath = Path.Combine(root, VoiceBenchProjectLayout.ManifestFileName);
        JsonNode? node;
        await using (FileStream stream = new(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (node is not JsonObject jsonObject)
        {
            throw new ProjectSchemaException("The project manifest root must be a JSON object.");
        }

        int? storedVersion = jsonObject["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out int parsed)
            ? parsed
            : null;
        VoiceBenchProjectDocument document = _migrator.Deserialize(jsonObject, ProjectJson.SerializerOptions);
        if (storedVersion is int version && version < VoiceBenchProjectDocument.CurrentSchemaVersion)
        {
            await SaveAsync(root, document, cancellationToken).ConfigureAwait(false);
        }

        return document;
    }

    public async Task SaveAsync(
        string projectRoot,
        VoiceBenchProjectDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string root = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(root);
        ValidateRelativePaths(document);

        string manifestPath = Path.Combine(root, VoiceBenchProjectLayout.ManifestFileName);
        string temporaryPath = Path.Combine(
            root,
            $".{VoiceBenchProjectLayout.ManifestFileName}.{Guid.NewGuid():N}.tmp");
        VoiceBenchProjectDocument updated = document with { UpdatedAt = _timeProvider.GetUtcNow() };

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    updated,
                    ProjectJson.SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _committer.Commit(temporaryPath, manifestPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ProjectValidationResult> ValidateAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        List<ProjectValidationIssue> issues = [];
        VoiceBenchProjectDocument document;
        try
        {
            document = await LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or ProjectSchemaException)
        {
            return new ProjectValidationResult(
                [new ProjectValidationIssue("project.manifest-invalid", exception.Message)]);
        }

        HashSet<string> segmentIds = new(document.Segments.Select(segment => segment.SegmentId), StringComparer.Ordinal);
        if (segmentIds.Count != document.Segments.Count)
        {
            issues.Add(new ProjectValidationIssue("project.duplicate-segment-id", "Segment IDs must be unique."));
        }

        HashSet<string> chapterIds = new(document.Chapters.Select(chapter => chapter.ChapterId), StringComparer.Ordinal);
        if (chapterIds.Count != document.Chapters.Count)
        {
            issues.Add(new ProjectValidationIssue("project.duplicate-chapter-id", "Chapter IDs must be unique."));
        }

        if (document.ChapterOrder.Count != document.ChapterOrder.Distinct(StringComparer.Ordinal).Count())
        {
            issues.Add(new ProjectValidationIssue("project.duplicate-chapter-order-entry", "Chapter order contains duplicates."));
        }

        foreach (string orderedChapterId in document.ChapterOrder.Where(id => !chapterIds.Contains(id)))
        {
            issues.Add(new ProjectValidationIssue(
                "project.ordered-chapter-missing",
                $"Ordered chapter '{orderedChapterId}' is missing."));
        }


        foreach (ChapterDocument unorderedChapter in document.Chapters.Where(
            chapter => !document.ChapterOrder.Contains(chapter.ChapterId, StringComparer.Ordinal)))
        {
            issues.Add(new ProjectValidationIssue(
                "chapter.not-ordered",
                $"Chapter '{unorderedChapter.Title}' is not present in chapter order."));
        }

        HashSet<string> orderedSegmentIds = new(StringComparer.Ordinal);
        foreach (ChapterDocument chapter in document.Chapters)
        {
            if (chapter.SegmentOrder.Count != chapter.SegmentOrder.Distinct(StringComparer.Ordinal).Count())
            {
                issues.Add(new ProjectValidationIssue(
                    "chapter.duplicate-segment-order-entry",
                    $"Chapter '{chapter.Title}' contains a segment more than once."));
            }

            foreach (string orderedSegmentId in chapter.SegmentOrder)
            {
                if (!segmentIds.Contains(orderedSegmentId))
                {
                    issues.Add(new ProjectValidationIssue(
                        "chapter.ordered-segment-missing",
                        $"Ordered segment '{orderedSegmentId}' is missing.",
                        orderedSegmentId));
                    continue;
                }

                if (!orderedSegmentIds.Add(orderedSegmentId))
                {
                    issues.Add(new ProjectValidationIssue(
                        "chapter.segment-ordered-twice",
                        $"Segment '{orderedSegmentId}' belongs to more than one chapter.",
                        orderedSegmentId));
                }

                SegmentDocument segment = document.Segments.Single(item => item.SegmentId == orderedSegmentId);
                if (!string.Equals(segment.ChapterId, chapter.ChapterId, StringComparison.Ordinal))
                {
                    issues.Add(new ProjectValidationIssue(
                        "segment.chapter-mismatch",
                        $"Segment '{segment.Title}' does not point back to chapter '{chapter.Title}'.",
                        segment.SegmentId));
                }
            }
        }

        foreach (SegmentDocument unordered in document.Segments.Where(segment => !orderedSegmentIds.Contains(segment.SegmentId)))
        {
            issues.Add(new ProjectValidationIssue(
                "segment.not-ordered",
                $"Segment '{unordered.Title}' is not ordered inside a chapter.",
                unordered.SegmentId));
        }

        foreach (SegmentDocument segment in document.Segments)
        {
            if (!chapterIds.Contains(segment.ChapterId))
            {
                issues.Add(new ProjectValidationIssue(
                    "segment.chapter-missing",
                    $"Segment '{segment.Title}' points to missing chapter '{segment.ChapterId}'.",
                    segment.SegmentId));
            }

            try
            {
                string scriptPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath);
                if (!File.Exists(scriptPath))
                {
                    issues.Add(new ProjectValidationIssue(
                        "segment.script-missing",
                        $"Script file '{segment.ScriptRelativePath}' does not exist.",
                        segment.SegmentId));
                    continue;
                }

                string content = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
                ParsedScriptDocument parsed = new ProductionScriptParser().Parse(content);
                foreach (ScriptDocumentDiagnostic diagnostic in parsed.Diagnostics)
                {
                    issues.Add(new ProjectValidationIssue(
                        diagnostic.Code,
                        $"{diagnostic.Message} Line {diagnostic.SourceLine} in '{segment.ScriptRelativePath}'.",
                        segment.SegmentId));
                }

                string expectedAuthoringHash = string.IsNullOrWhiteSpace(segment.AuthoringSha256)
                    ? segment.SourceSha256
                    : segment.AuthoringSha256;
                if (!string.Equals(SourceTextHasher.Compute(content), expectedAuthoringHash, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ProjectValidationIssue(
                        "segment.authoring-changed",
                        $"Script file '{segment.ScriptRelativePath}' differs from its recorded authoring hash.",
                        segment.SegmentId));
                }

                if (!string.Equals(SourceTextHasher.Compute(parsed.NarrationText), segment.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ProjectValidationIssue(
                        "segment.source-changed",
                        $"Script file '{segment.ScriptRelativePath}' differs from its recorded source hash.",
                        segment.SegmentId));
                }
            }
            catch (InvalidDataException exception)
            {
                issues.Add(new ProjectValidationIssue("segment.path-invalid", exception.Message, segment.SegmentId));
            }
        }


        foreach (SourceReferenceDocument reference in document.SourceReferences)
        {
            try
            {
                string referencePath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, reference.RelativePath);
                if (!File.Exists(referencePath))
                {
                    issues.Add(new ProjectValidationIssue(
                        "reference.file-missing",
                        $"Reference file '{reference.RelativePath}' does not exist."));
                    continue;
                }

                string content = await File.ReadAllTextAsync(referencePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(SourceTextHasher.Compute(content), reference.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ProjectValidationIssue(
                        "reference.source-changed",
                        $"Reference file '{reference.RelativePath}' differs from its recorded hash."));
                }
            }
            catch (InvalidDataException exception)
            {
                issues.Add(new ProjectValidationIssue("reference.path-invalid", exception.Message));
            }
        }

        return new ProjectValidationResult(issues);
    }

    private static void ValidateRelativePaths(VoiceBenchProjectDocument document)
    {
        foreach (SourceReferenceDocument reference in document.SourceReferences)
        {
            ValidateProjectPath(reference.RelativePath, $"Reference '{reference.Name}'");
        }

        foreach (SegmentDocument segment in document.Segments)
        {
            ValidateProjectPath(segment.ScriptRelativePath, $"Segment '{segment.SegmentId}'");

            foreach (WritingRevisionRecord revision in segment.WritingRevisions)
            {
                ValidateProjectPath(revision.PreviousTextRelativePath, $"Revision '{revision.RevisionId}'");
                ValidateProjectPath(revision.AcceptedTextRelativePath, $"Revision '{revision.RevisionId}'");
            }

            foreach (RenderRecord render in segment.RenderHistory)
            {
                ValidateRenderPath(render.RawAudioRelativePath, segment.SegmentId);
                if (!string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath))
                {
                    ValidateRenderPath(render.ProcessedAudioRelativePath, segment.SegmentId);
                }

                if (!string.IsNullOrWhiteSpace(render.CleanSourceRelativePath))
                {
                    ValidateProjectPath(render.CleanSourceRelativePath, $"Render '{render.RenderId}' clean source");
                }

                if (!string.IsNullOrWhiteSpace(render.AuthoringSourceRelativePath))
                {
                    ValidateProjectPath(render.AuthoringSourceRelativePath, $"Render '{render.RenderId}' authoring source");
                }

                if (!string.IsNullOrWhiteSpace(render.SubmittedTextRelativePath))
                {
                    ValidateProjectPath(render.SubmittedTextRelativePath, $"Render '{render.RenderId}' submitted text");
                }

                if (!string.IsNullOrWhiteSpace(render.SubmittedTextMapRelativePath))
                {
                    ValidateProjectPath(render.SubmittedTextMapRelativePath, $"Render '{render.RenderId}' submitted map");
                }

                foreach (WordAlignmentRecord alignment in render.AlignmentHistory)
                {
                    ValidateProjectPath(alignment.AudioRelativePath, $"Alignment '{alignment.AlignmentId}' audio");
                    if (!string.IsNullOrWhiteSpace(alignment.ArtifactRelativePath))
                    {
                        ValidateProjectPath(alignment.ArtifactRelativePath, $"Alignment '{alignment.AlignmentId}' artifact");
                    }
                }
            }
        }
    }

    private static void ValidateProjectPath(string relativePath, string owner)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', '\\').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{owner} uses a non-relocatable project path.");
        }
    }

    private static void ValidateRenderPath(string relativePath, string segmentId)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Segment '{segmentId}' uses a non-relocatable render path.");
        }
    }
}

public interface IAtomicFileCommitter
{
    void Commit(string temporaryPath, string destinationPath);
}

public sealed class AtomicFileCommitter : IAtomicFileCommitter
{
    public void Commit(string temporaryPath, string destinationPath) =>
        File.Move(temporaryPath, destinationPath, overwrite: true);
}
