using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Infrastructure.Projects;

public sealed class ChapterAuthoringService(
    IProjectRepository repository,
    Func<string>? stableIdFactory = null,
    TimeProvider? timeProvider = null)
{
    private readonly Func<string> _stableIdFactory = stableIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<VoiceBenchProjectDocument> CreateBlankChapterAsync(
        string projectRoot,
        string title,
        string? brief = null,
        CancellationToken cancellationToken = default) =>
        CreateChapterFromDraftAsync(
            projectRoot,
            new ChapterDraft(title, [new ChapterDraftSegment("Opening", string.Empty)], "Blank chapter"),
            brief,
            [],
            cancellationToken);

    public async Task<VoiceBenchProjectDocument> CreateChapterFromDraftAsync(
        string projectRoot,
        ChapterDraft draft,
        string? brief,
        IReadOnlyList<string> referenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Title);
        if (draft.Segments.Count == 0 || draft.Segments.Any(segment => string.IsNullOrWhiteSpace(segment.Title)))
        {
            throw new InvalidDataException("A chapter requires at least one titled segment.");
        }

        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        string chapterId = _stableIdFactory();
        string chapterDirectoryName = ScriptSegmentNaming.CreateDirectoryName(
            project.ChapterOrder.Count + 1,
            draft.Title,
            chapterId);
        string chapterRelativeDirectory = VoiceBenchProjectLayout.ToManifestPath(
            Path.Combine(VoiceBenchProjectLayout.ScriptsDirectory, chapterDirectoryName));
        string finalDirectory = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, chapterRelativeDirectory);
        string temporaryDirectory = $"{finalDirectory}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temporaryDirectory);

        List<SegmentDocument> newSegments = [];
        try
        {
            foreach ((ChapterDraftSegment item, int index) in draft.Segments.Select((item, index) => (item, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string segmentId = _stableIdFactory();
                string fileName = ScriptSegmentNaming.CreateFileName(index + 1, item.Title, segmentId);
                string temporaryPath = Path.Combine(temporaryDirectory, fileName);
                await File.WriteAllTextAsync(temporaryPath, item.Text, cancellationToken).ConfigureAwait(false);
                string relativePath = VoiceBenchProjectLayout.ToManifestPath(
                    Path.Combine(chapterRelativeDirectory, fileName));
                newSegments.Add(new SegmentDocument
                {
                    SegmentId = segmentId,
                    ChapterId = chapterId,
                    Title = item.Title.Trim(),
                    ScriptRelativePath = relativePath,
                    SourceSha256 = SourceTextHasher.Compute(item.Text),
                    Status = SegmentStatus.Draft,
                });
            }

            Directory.Move(temporaryDirectory, finalDirectory);
            ChapterDocument chapter = new()
            {
                ChapterId = chapterId,
                Title = draft.Title.Trim(),
                Brief = string.IsNullOrWhiteSpace(brief) ? null : brief.Trim(),
                ReferenceIds = referenceIds.Distinct(StringComparer.Ordinal).ToList(),
                SegmentOrder = newSegments.Select(segment => segment.SegmentId).ToList(),
            };
            VoiceBenchProjectDocument updated = project with
            {
                ChapterOrder = [.. project.ChapterOrder, chapterId],
                Chapters = [.. project.Chapters, chapter],
                Segments = [.. project.Segments, .. newSegments],
            };
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            if (Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, recursive: true);
            throw;
        }
    }

    public async Task<VoiceBenchProjectDocument> AddSegmentAsync(
        string projectRoot,
        string chapterId,
        string title,
        string text = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int chapterIndex = project.Chapters.FindIndex(chapter => chapter.ChapterId == chapterId);
        if (chapterIndex < 0) throw new InvalidOperationException($"Chapter '{chapterId}' does not exist.");
        ChapterDocument chapter = project.Chapters[chapterIndex];
        string segmentId = _stableIdFactory();
        string chapterDirectory = Path.GetDirectoryName(
            VoiceBenchProjectLayout.ResolveRelativePath(
                projectRoot,
                project.Segments.FirstOrDefault(segment => segment.ChapterId == chapterId)?.ScriptRelativePath
                    ?? VoiceBenchProjectLayout.ToManifestPath(Path.Combine(
                        VoiceBenchProjectLayout.ScriptsDirectory,
                        ScriptSegmentNaming.CreateDirectoryName(chapterIndex + 1, chapter.Title, chapter.ChapterId),
                        "placeholder.md"))))!;
        Directory.CreateDirectory(chapterDirectory);
        string fileName = ScriptSegmentNaming.CreateFileName(chapter.SegmentOrder.Count + 1, title, segmentId);
        string fullPath = Path.Combine(chapterDirectory, fileName);
        string relativePath = VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, fullPath));
        await File.WriteAllTextAsync(fullPath, text, cancellationToken).ConfigureAwait(false);
        try
        {
            SegmentDocument segment = new()
            {
                SegmentId = segmentId,
                ChapterId = chapterId,
                Title = title.Trim(),
                ScriptRelativePath = relativePath,
                SourceSha256 = SourceTextHasher.Compute(text),
            };
            List<ChapterDocument> chapters = [.. project.Chapters];
            chapters[chapterIndex] = chapter with { SegmentOrder = [.. chapter.SegmentOrder, segmentId] };
            VoiceBenchProjectDocument updated = project with
            {
                Chapters = chapters,
                Segments = [.. project.Segments, segment],
            };
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            throw;
        }
    }

    public async Task<VoiceBenchProjectDocument> DuplicateChapterAsync(
        string projectRoot,
        string chapterId,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        ChapterDocument chapter = project.Chapters.SingleOrDefault(item => item.ChapterId == chapterId)
            ?? throw new InvalidOperationException($"Chapter '{chapterId}' does not exist.");
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(
            segment => segment.SegmentId,
            StringComparer.Ordinal);
        List<ChapterDraftSegment> draftSegments = [];
        foreach (string segmentId in chapter.SegmentOrder)
        {
            SegmentDocument segment = segments[segmentId];
            string text = await File.ReadAllTextAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath),
                cancellationToken).ConfigureAwait(false);
            draftSegments.Add(new ChapterDraftSegment(segment.Title, text));
        }

        return await CreateChapterFromDraftAsync(
            projectRoot,
            new ChapterDraft($"{chapter.Title} copy", draftSegments, "Duplicated locally"),
            chapter.Brief,
            chapter.ReferenceIds,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceBenchProjectDocument> DuplicateSegmentAsync(
        string projectRoot,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        SegmentDocument source = project.Segments.SingleOrDefault(segment => segment.SegmentId == segmentId)
            ?? throw new InvalidOperationException($"Segment '{segmentId}' does not exist.");
        string text = await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, source.ScriptRelativePath),
            cancellationToken).ConfigureAwait(false);
        return await AddSegmentAsync(
            projectRoot,
            source.ChapterId,
            $"{source.Title} copy",
            text,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceBenchProjectDocument> DeleteSegmentAsync(
        string projectRoot,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        SegmentDocument segment = project.Segments.SingleOrDefault(item => item.SegmentId == segmentId)
            ?? throw new InvalidOperationException($"Segment '{segmentId}' does not exist.");
        int chapterIndex = project.Chapters.FindIndex(item => item.ChapterId == segment.ChapterId);
        if (chapterIndex < 0) throw new InvalidOperationException($"Chapter '{segment.ChapterId}' does not exist.");
        ChapterDocument chapter = project.Chapters[chapterIndex];
        List<ChapterDocument> chapters = [.. project.Chapters];
        chapters[chapterIndex] = chapter with
        {
            SegmentOrder = chapter.SegmentOrder.Where(id => id != segmentId).ToList(),
        };
        VoiceBenchProjectDocument updated = project with
        {
            Chapters = chapters,
            Segments = project.Segments.Where(item => item.SegmentId != segmentId).ToList(),
        };
        List<TrashedScript> moved = MoveScriptsToTrash(projectRoot, [segment]);
        try
        {
            RemoveEmptyScriptDirectories(moved);
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            RestoreTrashedScripts(moved);
            throw;
        }
    }

    public async Task<VoiceBenchProjectDocument> DeleteChapterAsync(
        string projectRoot,
        string chapterId,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        ChapterDocument chapter = project.Chapters.SingleOrDefault(item => item.ChapterId == chapterId)
            ?? throw new InvalidOperationException($"Chapter '{chapterId}' does not exist.");
        HashSet<string> segmentIds = chapter.SegmentOrder.ToHashSet(StringComparer.Ordinal);
        List<SegmentDocument> removedSegments = project.Segments
            .Where(item => segmentIds.Contains(item.SegmentId) || item.ChapterId == chapterId)
            .ToList();
        VoiceBenchProjectDocument updated = project with
        {
            ChapterOrder = project.ChapterOrder.Where(id => id != chapterId).ToList(),
            Chapters = project.Chapters.Where(item => item.ChapterId != chapterId).ToList(),
            Segments = project.Segments.Where(item => item.ChapterId != chapterId && !segmentIds.Contains(item.SegmentId)).ToList(),
        };
        List<TrashedScript> moved = MoveScriptsToTrash(projectRoot, removedSegments);
        try
        {
            RemoveEmptyScriptDirectories(moved);
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            RestoreTrashedScripts(moved);
            throw;
        }
    }

    public async Task<VoiceBenchProjectDocument> SplitSegmentAsync(
        string projectRoot,
        string segmentId,
        int cursor,
        string expectedSourceSha256,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int segmentIndex = project.Segments.FindIndex(segment => segment.SegmentId == segmentId);
        if (segmentIndex < 0) throw new InvalidOperationException($"Segment '{segmentId}' does not exist.");
        SegmentDocument segment = project.Segments[segmentIndex];
        string originalPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath);
        string originalText = await File.ReadAllTextAsync(originalPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(SourceTextHasher.Compute(originalText), expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The script changed after the split position was chosen.");
        }

        if (cursor <= 0 || cursor >= originalText.Length)
        {
            throw new InvalidOperationException("Place the split cursor inside the segment text.");
        }

        string first = originalText[..cursor].TrimEnd();
        string second = originalText[cursor..].TrimStart();
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            throw new InvalidOperationException("A split cannot create an empty segment.");
        }

        int chapterIndex = project.Chapters.FindIndex(chapter => chapter.ChapterId == segment.ChapterId);
        ChapterDocument chapter = project.Chapters[chapterIndex];
        int orderIndex = chapter.SegmentOrder.IndexOf(segmentId);
        string newSegmentId = _stableIdFactory();
        string newTitle = $"{segment.Title} (part 2)";
        string fileName = ScriptSegmentNaming.CreateFileName(orderIndex + 2, newTitle, newSegmentId);
        string relativeDirectory = Path.GetDirectoryName(segment.ScriptRelativePath) ?? VoiceBenchProjectLayout.ScriptsDirectory;
        string newRelativePath = VoiceBenchProjectLayout.ToManifestPath(Path.Combine(relativeDirectory, fileName));
        string newPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, newRelativePath);
        string temporaryPath = $"{originalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, first, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, originalPath, overwrite: true);
            await using (FileStream stream = new(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await using (StreamWriter writer = new(stream))
            {
                await writer.WriteAsync(second.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            SegmentDocument firstSegment = SegmentStateRules.WithSourceHash(segment, SourceTextHasher.Compute(first));
            SegmentDocument secondSegment = new()
            {
                SegmentId = newSegmentId,
                ChapterId = segment.ChapterId,
                Title = newTitle,
                ScriptRelativePath = newRelativePath,
                SourceSha256 = SourceTextHasher.Compute(second),
                VoicePresetId = segment.VoicePresetId,
                Directions = segment.Directions,
            };
            List<SegmentDocument> segments = [.. project.Segments];
            segments[segmentIndex] = firstSegment;
            segments.Insert(segmentIndex + 1, secondSegment);
            List<string> order = [.. chapter.SegmentOrder];
            order.Insert(orderIndex + 1, newSegmentId);
            List<ChapterDocument> chapters = [.. project.Chapters];
            chapters[chapterIndex] = chapter with { SegmentOrder = order };
            VoiceBenchProjectDocument updated = project with { Segments = segments, Chapters = chapters };
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            await File.WriteAllTextAsync(originalPath, originalText, CancellationToken.None).ConfigureAwait(false);
            if (File.Exists(newPath)) File.Delete(newPath);
            throw;
        }
    }

    public async Task<VoiceBenchProjectDocument> UpdateChapterAsync(
        string projectRoot,
        string chapterId,
        string? title = null,
        bool? includedInCompilation = null,
        ChapterStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int index = project.Chapters.FindIndex(chapter => chapter.ChapterId == chapterId);
        if (index < 0) throw new InvalidOperationException($"Chapter '{chapterId}' does not exist.");
        ChapterDocument current = project.Chapters[index];
        List<ChapterDocument> chapters = [.. project.Chapters];
        chapters[index] = current with
        {
            Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
            IncludedInCompilation = includedInCompilation ?? current.IncludedInCompilation,
            Status = status ?? current.Status,
        };
        VoiceBenchProjectDocument updated = project with { Chapters = chapters };
        await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<VoiceBenchProjectDocument> MoveChapterAsync(
        string projectRoot,
        string chapterId,
        int offset,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        List<string> order = [.. project.ChapterOrder];
        Move(order, chapterId, offset);
        VoiceBenchProjectDocument updated = project with { ChapterOrder = order };
        await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<VoiceBenchProjectDocument> MoveSegmentAsync(
        string projectRoot,
        string chapterId,
        string segmentId,
        int offset,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int chapterIndex = project.Chapters.FindIndex(chapter => chapter.ChapterId == chapterId);
        if (chapterIndex < 0) throw new InvalidOperationException($"Chapter '{chapterId}' does not exist.");
        ChapterDocument chapter = project.Chapters[chapterIndex];
        List<string> order = [.. chapter.SegmentOrder];
        Move(order, segmentId, offset);
        List<ChapterDocument> chapters = [.. project.Chapters];
        chapters[chapterIndex] = chapter with { SegmentOrder = order };
        VoiceBenchProjectDocument updated = project with { Chapters = chapters };
        await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<VoiceBenchProjectDocument> RenameSegmentAsync(
        string projectRoot,
        string segmentId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int index = project.Segments.FindIndex(segment => segment.SegmentId == segmentId);
        if (index < 0) throw new InvalidOperationException($"Segment '{segmentId}' does not exist.");
        List<SegmentDocument> segments = [.. project.Segments];
        segments[index] = segments[index] with { Title = title.Trim() };
        VoiceBenchProjectDocument updated = project with { Segments = segments };
        await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<VoiceBenchProjectDocument> ApplyWritingProposalAsync(
        string projectRoot,
        string segmentId,
        string expectedSourceSha256,
        WritingProposal proposal,
        WritingAction action,
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        int index = project.Segments.FindIndex(segment => segment.SegmentId == segmentId);
        if (index < 0) throw new InvalidOperationException($"Segment '{segmentId}' does not exist.");
        SegmentDocument segment = project.Segments[index];
        if (!string.Equals(segment.SourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The segment changed after the AI proposal was created. Generate a new proposal.");
        }

        string scriptPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath);
        string previousText = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        string diskHash = SourceTextHasher.Compute(previousText);
        if (!string.Equals(diskHash, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The script was edited externally after the AI proposal was created.");
        }

        Guid revisionId = Guid.NewGuid();
        string stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string revisionRelativeDirectory = VoiceBenchProjectLayout.ToManifestPath(Path.Combine(
            VoiceBenchProjectLayout.ScriptRevisionsDirectory,
            segmentId,
            $"{stamp}--{revisionId:N}"));
        string revisionDirectory = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, revisionRelativeDirectory);
        Directory.CreateDirectory(revisionDirectory);
        string previousPath = Path.Combine(revisionDirectory, "before.md");
        string acceptedPath = Path.Combine(revisionDirectory, "accepted.md");
        string temporaryScriptPath = $"{scriptPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(previousPath, previousText, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(acceptedPath, proposal.ProposedText, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(temporaryScriptPath, proposal.ProposedText, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryScriptPath, scriptPath, overwrite: true);

            string acceptedHash = SourceTextHasher.Compute(proposal.ProposedText);
            WritingRevisionRecord revision = new()
            {
                RevisionId = revisionId,
                Action = action,
                ProviderId = providerId,
                ModelId = modelId,
                CreatedAt = _timeProvider.GetUtcNow(),
                PreviousTextRelativePath = VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, previousPath)),
                AcceptedTextRelativePath = VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, acceptedPath)),
                PreviousSha256 = diskHash,
                AcceptedSha256 = acceptedHash,
            };
            SegmentDocument updatedSegment = SegmentStateRules.WithSourceHash(segment, acceptedHash) with
            {
                WritingRevisions = [.. segment.WritingRevisions, revision],
            };
            List<SegmentDocument> segments = [.. project.Segments];
            segments[index] = updatedSegment;
            VoiceBenchProjectDocument updated = project with { Segments = segments };
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            if (File.Exists(temporaryScriptPath)) File.Delete(temporaryScriptPath);
            await File.WriteAllTextAsync(scriptPath, previousText, CancellationToken.None).ConfigureAwait(false);
            if (Directory.Exists(revisionDirectory)) Directory.Delete(revisionDirectory, recursive: true);
            throw;
        }
    }

    public async Task<VoiceBenchProjectDocument> RestoreRevisionAsync(
        string projectRoot,
        string segmentId,
        Guid revisionId,
        bool restorePreviousText = false,
        CancellationToken cancellationToken = default)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        SegmentDocument segment = project.Segments.SingleOrDefault(item => item.SegmentId == segmentId)
            ?? throw new InvalidOperationException($"Segment '{segmentId}' does not exist.");
        WritingRevisionRecord revision = segment.WritingRevisions.SingleOrDefault(item => item.RevisionId == revisionId)
            ?? throw new InvalidOperationException($"Writing revision '{revisionId}' does not exist.");
        string relativePath = restorePreviousText
            ? revision.PreviousTextRelativePath
            : revision.AcceptedTextRelativePath;
        string restored = await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativePath),
            cancellationToken).ConfigureAwait(false);
        return await ApplyWritingProposalAsync(
            projectRoot,
            segmentId,
            segment.SourceSha256,
            new WritingProposal(restored, "Restored a local writing revision", []),
            WritingAction.Custom,
            "local",
            "revision-restore",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceBenchProjectDocument> ImportReferenceAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Writing references must be Markdown or text files.");
        }

        string text = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        string referenceId = _stableIdFactory();
        string fileName = $"{ScriptSegmentNaming.CreateDirectoryName(project.SourceReferences.Count + 1, Path.GetFileNameWithoutExtension(sourcePath), referenceId)}{extension.ToLowerInvariant()}";
        string relativePath = VoiceBenchProjectLayout.ToManifestPath(Path.Combine(
            VoiceBenchProjectLayout.SourceReferencesDirectory,
            fileName));
        string fullPath = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, relativePath);
        await File.WriteAllTextAsync(fullPath, text, cancellationToken).ConfigureAwait(false);
        try
        {
            SourceReferenceDocument reference = new()
            {
                ReferenceId = referenceId,
                Name = Path.GetFileName(sourcePath),
                RelativePath = relativePath,
                Sha256 = SourceTextHasher.Compute(text),
            };
            VoiceBenchProjectDocument updated = project with
            {
                SourceReferences = [.. project.SourceReferences, reference],
            };
            await repository.SaveAsync(projectRoot, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            throw;
        }
    }

    private static void Move(List<string> order, string id, int offset)
    {
        int current = order.IndexOf(id);
        if (current < 0) throw new InvalidOperationException($"'{id}' is not in the current order.");
        int target = Math.Clamp(current + offset, 0, order.Count - 1);
        if (target == current) return;
        order.RemoveAt(current);
        order.Insert(target, id);
    }

    private List<TrashedScript> MoveScriptsToTrash(string projectRoot, IEnumerable<SegmentDocument> segments)
    {
        string trashDirectory = VoiceBenchProjectLayout.ResolveRelativePath(
            projectRoot,
            VoiceBenchProjectLayout.ScriptTrashDirectory);
        Directory.CreateDirectory(trashDirectory);
        string stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        List<TrashedScript> moved = [];
        try
        {
            foreach (SegmentDocument segment in segments)
            {
                string source = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath);
                if (!File.Exists(source)) continue;
                string destination = Path.Combine(
                    trashDirectory,
                    $"{stamp}--{segment.SegmentId}--{Path.GetFileName(source)}");
                if (File.Exists(destination)) destination = Path.Combine(
                    trashDirectory,
                    $"{stamp}--{segment.SegmentId}--{Guid.NewGuid():N}--{Path.GetFileName(source)}");
                File.Move(source, destination);
                moved.Add(new TrashedScript(source, destination));
            }

            return moved;
        }
        catch
        {
            RestoreTrashedScripts(moved);
            throw;
        }
    }

    private static void RestoreTrashedScripts(IEnumerable<TrashedScript> moved)
    {
        foreach (TrashedScript item in moved.Reverse())
        {
            if (!File.Exists(item.TrashPath)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
            File.Move(item.TrashPath, item.OriginalPath, overwrite: true);
        }
    }

    private static void RemoveEmptyScriptDirectories(IEnumerable<TrashedScript> moved)
    {
        foreach (string directory in moved
                     .Select(item => Path.GetDirectoryName(item.OriginalPath))
                     .OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private sealed record TrashedScript(string OriginalPath, string TrashPath);
}
