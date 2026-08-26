using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class FileProjectRepository(
    TimeProvider? timeProvider = null,
    IAtomicFileCommitter? committer = null) : IProjectRepository
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ProjectDocumentMigrator _migrator = new();
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
        string manifestPath = Path.Combine(Path.GetFullPath(projectRoot), VoiceBenchProjectLayout.ManifestFileName);
        await using FileStream stream = new(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (node is not JsonObject jsonObject)
        {
            throw new ProjectSchemaException("The project manifest root must be a JSON object.");
        }

        return _migrator.Deserialize(jsonObject, ProjectJson.SerializerOptions);
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

        HashSet<string> ids = new(document.Segments.Select(segment => segment.SegmentId), StringComparer.Ordinal);
        if (ids.Count != document.Segments.Count)
        {
            issues.Add(new ProjectValidationIssue("project.duplicate-segment-id", "Segment IDs must be unique."));
        }

        if (document.SegmentOrder.Count != document.SegmentOrder.Distinct(StringComparer.Ordinal).Count())
        {
            issues.Add(new ProjectValidationIssue("project.duplicate-order-entry", "Segment order contains duplicates."));
        }

        foreach (string orderedId in document.SegmentOrder.Where(id => !ids.Contains(id)))
        {
            issues.Add(new ProjectValidationIssue(
                "project.ordered-segment-missing",
                $"Ordered segment '{orderedId}' is missing.",
                orderedId));
        }

        foreach (SegmentDocument segment in document.Segments)
        {
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
                if (!string.Equals(SourceTextHasher.Compute(content), segment.SourceSha256, StringComparison.OrdinalIgnoreCase))
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

        return new ProjectValidationResult(issues);
    }

    private static void ValidateRelativePaths(VoiceBenchProjectDocument document)
    {
        foreach (SegmentDocument segment in document.Segments)
        {
            if (Path.IsPathRooted(segment.ScriptRelativePath) ||
                segment.ScriptRelativePath.Split('/', '\\').Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Segment '{segment.SegmentId}' uses a non-relocatable script path.");
            }

            foreach (RenderRecord render in segment.RenderHistory)
            {
                ValidateRenderPath(render.RawAudioRelativePath, segment.SegmentId);
                if (!string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath))
                {
                    ValidateRenderPath(render.ProcessedAudioRelativePath, segment.SegmentId);
                }
            }
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
