using System.Globalization;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Mastering;

public sealed record AlignedTakeSnapshot(
    string ChapterId,
    string ChapterTitle,
    SegmentDocument Segment,
    RenderRecord Render,
    string CleanSourceRelativePath,
    string? AuthoringSourceRelativePath,
    string SubmittedTextRelativePath);

public sealed class AssemblySnapshotService(IProjectRepository repository)
{
    public async Task<(VoiceBenchProjectDocument Project, IReadOnlyList<AlignedTakeSnapshot> Takes)> ResolveAsync(
        string projectRoot,
        VoiceBenchProjectDocument project,
        MasterScope scope,
        string? chapterId,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        IReadOnlyList<string> orderedIds = ProjectOrdering.GetOrderedSegmentIds(project, scope, chapterId);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(
            chapter => chapter.ChapterId,
            StringComparer.Ordinal);
        List<SegmentDocument> segments = [.. project.Segments];
        List<AlignedTakeSnapshot> snapshots = [];
        bool changed = false;

        foreach (string segmentId in orderedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int segmentIndex = segments.FindIndex(segment => segment.SegmentId == segmentId);
            SegmentDocument segment = segments[segmentIndex];
            int renderIndex = segment.RenderHistory.FindIndex(render => render.RenderId == segment.ApprovedRenderId);
            if (renderIndex < 0)
            {
                throw new InvalidOperationException($"Segment '{segment.Title}' has no approved take to assemble.");
            }

            RenderRecord render = segment.RenderHistory[renderIndex];
            (RenderRecord aligned, bool wasBackfilled) = await ResolveRenderAsync(
                root,
                project,
                segment,
                render,
                cancellationToken).ConfigureAwait(false);
            if (wasBackfilled)
            {
                List<RenderRecord> history = [.. segment.RenderHistory];
                history[renderIndex] = aligned;
                segment = segment with { RenderHistory = history };
                segments[segmentIndex] = segment;
                changed = true;
            }

            ChapterDocument chapter = chapters[segment.ChapterId];
            snapshots.Add(new AlignedTakeSnapshot(
                chapter.ChapterId,
                chapter.Title,
                segment,
                aligned,
                aligned.CleanSourceRelativePath!,
                aligned.AuthoringSourceRelativePath,
                aligned.SubmittedTextRelativePath!));
        }

        if (changed)
        {
            project = project with { Segments = segments };
            await repository.SaveAsync(root, project, cancellationToken).ConfigureAwait(false);
        }

        return (project, snapshots);
    }

    private static async Task<(RenderRecord Render, bool Backfilled)> ResolveRenderAsync(
        string projectRoot,
        VoiceBenchProjectDocument project,
        SegmentDocument segment,
        RenderRecord render,
        CancellationToken cancellationToken)
    {
        string canonicalPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath);
        string canonicalText = await File.ReadAllTextAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(canonicalText);
        if (parsed.HasErrors)
        {
            throw RerenderRequired(segment, "its production-cue syntax is invalid");
        }

        if (!string.Equals(SourceTextHasher.Compute(parsed.NarrationText), segment.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "the canonical narration changed outside VoiceBench");
        }

        bool cleanExists = await ValidateCleanSnapshotAsync(projectRoot, segment, render, cancellationToken)
            .ConfigureAwait(false);
        bool submittedExists = await ValidateSubmittedSnapshotAsync(projectRoot, segment, render, cancellationToken)
            .ConfigureAwait(false);
        bool authoringExists = await ValidateAuthoringSnapshotAsync(projectRoot, segment, render, cancellationToken)
            .ConfigureAwait(false);
        if (cleanExists && submittedExists && authoringExists) return (render, false);

        if (!string.Equals(SourceTextHasher.Compute(parsed.NarrationText), render.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "its clean source snapshot is missing and the current script no longer matches");
        }

        string submittedText = LexiconSubstitution.Apply(parsed.NarrationText, project.Lexicon);
        if (string.IsNullOrWhiteSpace(render.SubmittedTextSha256) ||
            !string.Equals(SourceTextHasher.Compute(submittedText), render.SubmittedTextSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "its exact submitted-text snapshot cannot be reconstructed safely");
        }

        string? directions = ResolveDirections(project, segment, render);
        if (!string.Equals(
            SourceTextHasher.Compute(directions ?? string.Empty),
            render.DirectionsSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "its submitted directions cannot be reconstructed safely");
        }

        string safeSegment = SafeFilePart(segment.SegmentId);
        string baseName = $"{safeSegment}--render-{render.RenderId:N}.backfill";
        string cleanRelativePath = render.CleanSourceRelativePath ?? VoiceBenchProjectLayout.ToManifestPath(
            Path.Combine(VoiceBenchProjectLayout.RenderCopiesDirectory, $"{baseName}.clean.md"));
        string submittedRelativePath = render.SubmittedTextRelativePath ?? VoiceBenchProjectLayout.ToManifestPath(
            Path.Combine(VoiceBenchProjectLayout.RenderCopiesDirectory, $"{baseName}.submitted.md"));
        string? authoringRelativePath = render.AuthoringSourceRelativePath;
        string authoringHash = string.IsNullOrWhiteSpace(render.AuthoringSourceSha256)
            ? render.SourceSha256
            : render.AuthoringSourceSha256;
        bool canBackfillAuthoring = string.Equals(
            SourceTextHasher.Compute(canonicalText),
            authoringHash,
            StringComparison.OrdinalIgnoreCase);
        if (!authoringExists && canBackfillAuthoring)
        {
            authoringRelativePath ??= VoiceBenchProjectLayout.ToManifestPath(
                Path.Combine(VoiceBenchProjectLayout.RenderCopiesDirectory, $"{baseName}.authoring.md"));
            await WriteBackfillAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, authoringRelativePath),
                canonicalText,
                cancellationToken).ConfigureAwait(false);
        }

        if (!cleanExists)
        {
            await WriteBackfillAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, cleanRelativePath),
                parsed.NarrationText,
                cancellationToken).ConfigureAwait(false);
        }

        if (!submittedExists)
        {
            string audit = BuildSubmittedAudit(segment, render, directions, submittedText);
            await WriteBackfillAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, submittedRelativePath),
                audit,
                cancellationToken).ConfigureAwait(false);
        }

        return (render with
        {
            CleanSourceRelativePath = cleanRelativePath,
            AuthoringSourceRelativePath = authoringRelativePath,
            AuthoringSourceSha256 = authoringHash,
            SubmittedTextRelativePath = submittedRelativePath,
        }, true);
    }

    private static async Task<bool> ValidateAuthoringSnapshotAsync(
        string projectRoot,
        SegmentDocument segment,
        RenderRecord render,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(render.AuthoringSourceRelativePath)) return false;
        string path = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, render.AuthoringSourceRelativePath);
        if (!File.Exists(path)) return false;
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        string expected = string.IsNullOrWhiteSpace(render.AuthoringSourceSha256)
            ? render.SourceSha256
            : render.AuthoringSourceSha256;
        if (!string.Equals(SourceTextHasher.Compute(text), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "its authoring snapshot does not match the recorded authoring hash");
        }

        return true;
    }

    private static async Task<bool> ValidateCleanSnapshotAsync(
        string projectRoot,
        SegmentDocument segment,
        RenderRecord render,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(render.CleanSourceRelativePath)) return false;
        string path = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, render.CleanSourceRelativePath);
        if (!File.Exists(path)) return false;
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(SourceTextHasher.Compute(text), render.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "its clean source snapshot does not match the recorded source hash");
        }

        return true;
    }

    private static async Task<bool> ValidateSubmittedSnapshotAsync(
        string projectRoot,
        SegmentDocument segment,
        RenderRecord render,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(render.SubmittedTextRelativePath)) return false;
        string path = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, render.SubmittedTextRelativePath);
        if (!File.Exists(path)) return false;
        string audit = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        const string directionsMarker = "## Directions\n\n";
        const string submittedMarker = "\n\n## Exact submitted text\n\n";
        int directionsStart = audit.IndexOf(directionsMarker, StringComparison.Ordinal);
        int submittedStart = audit.IndexOf(submittedMarker, StringComparison.Ordinal);
        if (directionsStart < 0 || submittedStart <= directionsStart)
        {
            throw RerenderRequired(segment, "its submitted-text audit is malformed");
        }

        string directions = audit[(directionsStart + directionsMarker.Length)..submittedStart];
        if (string.Equals(directions, "(none)", StringComparison.Ordinal)) directions = string.Empty;
        string submittedText = audit[(submittedStart + submittedMarker.Length)..];
        if (!string.Equals(SourceTextHasher.Compute(directions), render.DirectionsSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SourceTextHasher.Compute(submittedText), render.SubmittedTextSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw RerenderRequired(segment, "its submitted-text audit does not match the recorded hashes");
        }

        return true;
    }

    private static string? ResolveDirections(
        VoiceBenchProjectDocument project,
        SegmentDocument segment,
        RenderRecord render)
    {
        VoicePreset? preset = null;
        string? presetId = segment.VoicePresetId ?? project.DefaultVoicePresetId;
        if (!string.IsNullOrWhiteSpace(presetId))
        {
            preset = project.VoicePresets.SingleOrDefault(item => item.PresetId == presetId);
        }

        preset ??= project.VoicePresets.SingleOrDefault(item =>
            item.ProviderId == render.ProviderId &&
            item.ModelId == render.ModelId &&
            item.Voice == render.Voice);
        string[] values = [preset?.Directions ?? string.Empty, segment.Directions ?? string.Empty];
        string[] present = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();
        return present.Length == 0 ? null : string.Join("\n\n", present);
    }

    private static string BuildSubmittedAudit(
        SegmentDocument segment,
        RenderRecord render,
        string? directions,
        string submittedText) => $$"""
        # VoiceBench submitted-text audit

        - Segment: {{segment.SegmentId}}
        - Take: {{render.TakeNumber.ToString(CultureInfo.InvariantCulture)}}
        - Provider: {{render.ProviderId}}
        - Model: {{render.ModelId}}
        - Voice: {{render.Voice}}
        - Source SHA-256: {{render.SourceSha256}}
        - Submitted SHA-256: {{render.SubmittedTextSha256}}

        ## Directions

        {{directions ?? "(none)"}}

        ## Exact submitted text

        {{submittedText}}
        """;

    private static async Task WriteBackfillAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using StreamWriter writer = new(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            string existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing, content, StringComparison.Ordinal)) throw;
        }
    }

    private static InvalidOperationException RerenderRequired(SegmentDocument segment, string reason) => new(
        $"Segment '{segment.Title}' must be rerendered before assembly because {reason}.");

    private static string SafeFilePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "segment" : safe;
    }
}
