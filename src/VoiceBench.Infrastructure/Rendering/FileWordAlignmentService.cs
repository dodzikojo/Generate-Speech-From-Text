using System.Security.Cryptography;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Rendering;

public sealed class FileWordAlignmentService : IWordAlignmentService
{
    private readonly IProjectRepository _projects;
    private readonly WordAlignmentProviderRegistry _providers;
    private readonly ProviderCredentialResolver _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _createAlignmentId;

    public FileWordAlignmentService(
        IProjectRepository projects,
        WordAlignmentProviderRegistry providers,
        ProviderCredentialResolver credentials,
        TimeProvider? timeProvider = null,
        Func<Guid>? createAlignmentId = null)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createAlignmentId = createAlignmentId ?? Guid.NewGuid;
    }

    public async Task<WordAlignmentRecord> AlignAsync(
        string projectRoot,
        string segmentId,
        Guid renderId,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        VoiceBenchProjectDocument project = await _projects.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        (int segmentIndex, int renderIndex, SegmentDocument segment, RenderRecord render) =
            FindRender(project, segmentId, renderId);
        if (render.Status is RenderStatus.Failed or RenderStatus.Cancelled || string.IsNullOrWhiteSpace(render.RawAudioRelativePath))
        {
            throw new InvalidOperationException("Only a successfully generated take can be aligned.");
        }

        string audioRelativePath = render.ProcessedAudioRelativePath ?? render.RawAudioRelativePath;
        string audioPath = VoiceBenchProjectLayout.ResolveRelativePath(root, audioRelativePath);
        if (!File.Exists(audioPath)) throw new FileNotFoundException("The take audio required for alignment is missing.", audioPath);
        byte[] audio = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
        string audioHash = Convert.ToHexStringLower(SHA256.HashData(audio));
        SubmittedTextMapDocument map = await LoadOrBackfillMapAsync(
            root,
            project,
            segment,
            render,
            cancellationToken).ConfigureAwait(false);

        IWordAlignmentProvider provider;
        try
        {
            provider = _providers.GetRequired(render.ProviderId);
        }
        catch (WordAlignmentProviderException exception)
        {
            WordAlignmentRecord unsupported = CreatePending(render, audioRelativePath, audioHash) with
            {
                Status = WordAlignmentStatus.Unsupported,
                CompletedAt = _timeProvider.GetUtcNow(),
                FailureCode = $"provider_{exception.Kind.ToString().ToLowerInvariant()}",
                TotalWordCount = map.TotalNarrationWordCount,
            };
            await AppendAttemptAsync(root, project, segmentIndex, renderIndex, unsupported, cancellationToken)
                .ConfigureAwait(false);
            return unsupported;
        }

        WordAlignmentRecord pending = CreatePending(render, audioRelativePath, audioHash) with
        {
            ProviderId = provider.ProviderId,
            ModelId = provider.ModelId,
            TotalWordCount = map.TotalNarrationWordCount,
        };
        await AppendAttemptAsync(root, project, segmentIndex, renderIndex, pending, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            string? apiKey = await _credentials.ResolveAsync(provider.ProviderId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new WordAlignmentProviderException(
                    provider.ProviderId,
                    WordAlignmentFailureKind.Credentials,
                    $"No {provider.ProviderId} API key is configured.");
            }

            WordAlignmentProviderResult transcription = await provider.TranscribeAsync(
                new WordAlignmentProviderRequest(
                    audio,
                    Path.GetFileName(audioPath),
                    ResolveMediaType(audioPath),
                    map.SubmittedText),
                apiKey,
                cancellationToken).ConfigureAwait(false);
            WordSequenceAlignmentResult aligned = WordSequenceAligner.Align(
                map.Words,
                transcription.Words,
                map.TotalNarrationWordCount);
            if (aligned.MatchedWordCount == 0)
            {
                WordAlignmentRecord noMatches = pending with
                {
                    Status = WordAlignmentStatus.Failed,
                    CompletedAt = _timeProvider.GetUtcNow(),
                    FailureCode = "alignment_no_matches",
                };
                await ReplaceAttemptAsync(root, segmentId, renderId, noMatches, CancellationToken.None)
                    .ConfigureAwait(false);
                return noMatches;
            }

            DateTimeOffset completedAt = _timeProvider.GetUtcNow();
            WordAlignmentDocument document = new()
            {
                AlignmentId = pending.AlignmentId,
                RenderId = render.RenderId,
                ProviderId = provider.ProviderId,
                ModelId = provider.ModelId,
                CreatedAt = completedAt,
                AudioRelativePath = audioRelativePath,
                AudioSha256 = audioHash,
                SourceSha256 = render.SourceSha256,
                SubmittedTextSha256 = render.SubmittedTextSha256,
                AudioDuration = transcription.Duration,
                Coverage = aligned.Coverage,
                MatchedWordCount = aligned.MatchedWordCount,
                TotalWordCount = aligned.TotalWordCount,
                Words = [.. aligned.Words],
            };
            string artifact = await WriteAlignmentArtifactAsync(
                root,
                segmentId,
                render.TakeNumber,
                document,
                cancellationToken).ConfigureAwait(false);
            WordAlignmentRecord completed = pending with
            {
                Status = aligned.Coverage >= 0.95 ? WordAlignmentStatus.Aligned : WordAlignmentStatus.Partial,
                CompletedAt = completedAt,
                ArtifactRelativePath = artifact,
                Coverage = aligned.Coverage,
                MatchedWordCount = aligned.MatchedWordCount,
                TotalWordCount = aligned.TotalWordCount,
            };
            await ReplaceAttemptAsync(root, segmentId, renderId, completed, CancellationToken.None).ConfigureAwait(false);
            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WordAlignmentRecord cancelled = pending with
            {
                Status = WordAlignmentStatus.Failed,
                CompletedAt = _timeProvider.GetUtcNow(),
                FailureCode = "alignment_cancelled",
            };
            await ReplaceAttemptAsync(root, segmentId, renderId, cancelled, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            WordAlignmentRecord failed = pending with
            {
                Status = exception is WordAlignmentProviderException { Kind: WordAlignmentFailureKind.Unsupported }
                    ? WordAlignmentStatus.Unsupported
                    : WordAlignmentStatus.Failed,
                CompletedAt = _timeProvider.GetUtcNow(),
                FailureCode = exception is WordAlignmentProviderException providerException
                    ? $"provider_{providerException.Kind.ToString().ToLowerInvariant()}"
                    : "alignment_failed",
            };
            await ReplaceAttemptAsync(root, segmentId, renderId, failed, CancellationToken.None).ConfigureAwait(false);
            return failed;
        }
    }

    public async Task<int> RecoverInterruptedAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(projectRoot);
        VoiceBenchProjectDocument project = await _projects.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        int recovered = 0;
        List<SegmentDocument> segments = [];
        foreach (SegmentDocument segment in project.Segments)
        {
            List<RenderRecord> renders = [];
            foreach (RenderRecord render in segment.RenderHistory)
            {
                List<WordAlignmentRecord> history = render.AlignmentHistory
                    .Select(attempt => attempt.Status == WordAlignmentStatus.Pending
                        ? attempt with
                        {
                            Status = WordAlignmentStatus.Interrupted,
                            CompletedAt = _timeProvider.GetUtcNow(),
                            FailureCode = "alignment_interrupted",
                        }
                        : attempt)
                    .ToList();
                recovered += render.AlignmentHistory.Count(attempt => attempt.Status == WordAlignmentStatus.Pending);
                renders.Add(render with { AlignmentHistory = history });
            }

            segments.Add(segment with { RenderHistory = renders });
        }

        if (recovered > 0)
            await _projects.SaveAsync(root, project with { Segments = segments }, cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    public static async Task<WordAlignmentDocument> LoadDocumentAsync(
        string projectRoot,
        WordAlignmentRecord record,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(record.ArtifactRelativePath))
            throw new InvalidDataException("The alignment has no timestamp artifact.");
        string path = VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, record.ArtifactRelativePath);
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        WordAlignmentDocument document = await JsonSerializer.DeserializeAsync<WordAlignmentDocument>(stream, ProjectJson.SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The alignment timestamp artifact is empty.");
        if (document.SchemaVersion != WordAlignmentDocument.CurrentSchemaVersion
            || document.AlignmentId != record.AlignmentId
            || !string.Equals(document.ProviderId, record.ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.ModelId, record.ModelId, StringComparison.Ordinal)
            || !string.Equals(document.AudioRelativePath, record.AudioRelativePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.AudioSha256, record.AudioSha256, StringComparison.OrdinalIgnoreCase)
            || document.Coverage is < 0 or > 1
            || document.MatchedWordCount != document.Words.Count
            || document.TotalWordCount < document.MatchedWordCount
            || (document.Words.Count > 0 && document.AudioDuration < document.Words.Max(word => word.End))
            || document.Words.Any(word => word.Start < TimeSpan.Zero || word.End <= word.Start)
            || document.Words.Zip(document.Words.Skip(1), (left, right) =>
                right.Start < left.End || right.NarrationWordOrdinal <= left.NarrationWordOrdinal).Any(invalid => invalid))
        {
            throw new InvalidDataException("The alignment timestamp artifact is invalid or does not match its manifest record.");
        }

        return document;
    }

    private WordAlignmentRecord CreatePending(RenderRecord render, string audioRelativePath, string audioHash) => new()
    {
        AlignmentId = _createAlignmentId(),
        Status = WordAlignmentStatus.Pending,
        ProviderId = render.ProviderId,
        CreatedAt = _timeProvider.GetUtcNow(),
        AudioRelativePath = audioRelativePath,
        AudioSha256 = audioHash,
    };

    private async Task<SubmittedTextMapDocument> LoadOrBackfillMapAsync(
        string root,
        VoiceBenchProjectDocument project,
        SegmentDocument segment,
        RenderRecord render,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(render.SubmittedTextMapRelativePath))
        {
            string existingPath = VoiceBenchProjectLayout.ResolveRelativePath(root, render.SubmittedTextMapRelativePath);
            await using FileStream existing = new(existingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            SubmittedTextMapDocument? loaded = await JsonSerializer.DeserializeAsync<SubmittedTextMapDocument>(
                existing,
                ProjectJson.SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (loaded is not null
                && loaded.RenderId == render.RenderId
                && string.Equals(loaded.SourceSha256, render.SourceSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(loaded.SubmittedTextSha256, render.SubmittedTextSha256, StringComparison.OrdinalIgnoreCase))
            {
                ValidateSubmittedMap(loaded);
                return loaded;
            }
            throw new InvalidDataException("The take's submitted-word map does not match its immutable hashes.");
        }

        string? snapshotRelativePath = render.AuthoringSourceRelativePath ?? render.CleanSourceRelativePath;
        if (string.IsNullOrWhiteSpace(snapshotRelativePath))
            throw new InvalidDataException("This historical take has no trustworthy script snapshot. Rerender it to align words.");
        string snapshotPath = VoiceBenchProjectLayout.ResolveRelativePath(root, snapshotRelativePath);
        string authoring = await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(authoring);
        if (!string.Equals(SourceTextHasher.Compute(parsed.NarrationText), render.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This historical take's script snapshot does not match its narration hash. Rerender it to align words.");
        PreparedSpeechText prepared = LexiconSubstitution.Prepare(parsed, project.Lexicon);
        if (!string.Equals(SourceTextHasher.Compute(prepared.Text), render.SubmittedTextSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This historical take's exact submitted text cannot be reconstructed. Rerender it to align words.");
        return new SubmittedTextMapDocument
        {
            RenderId = render.RenderId,
            SourceSha256 = render.SourceSha256,
            SubmittedTextSha256 = render.SubmittedTextSha256,
            SubmittedText = prepared.Text,
            TotalNarrationWordCount = parsed.NarrationWordCount,
            Words = [.. prepared.Words],
        };
    }

    private static void ValidateSubmittedMap(SubmittedTextMapDocument map)
    {
        bool invalid = map.SchemaVersion != SubmittedTextMapDocument.CurrentSchemaVersion
            || map.TotalNarrationWordCount < 0
            || !string.Equals(
                SourceTextHasher.Compute(map.SubmittedText),
                map.SubmittedTextSha256,
                StringComparison.OrdinalIgnoreCase)
            || map.Words.Select((word, index) => (word, index)).Any(item =>
                item.word.SubmittedOrdinal != item.index
                || item.word.SubmittedStart < 0
                || item.word.SubmittedLength <= 0
                || item.word.SubmittedStart + item.word.SubmittedLength > map.SubmittedText.Length
                || !string.Equals(
                    map.SubmittedText.Substring(item.word.SubmittedStart, item.word.SubmittedLength),
                    item.word.Text,
                    StringComparison.Ordinal)
                || !string.Equals(WordTextNormalizer.Normalize(item.word.Text), item.word.Normalized, StringComparison.Ordinal)
                || item.word.NarrationWordOrdinals.Any(ordinal => ordinal < 0 || ordinal >= map.TotalNarrationWordCount));
        if (invalid)
            throw new InvalidDataException("The take's submitted-word map is invalid. Rerender it to align words.");
    }

    private async Task AppendAttemptAsync(
        string root,
        VoiceBenchProjectDocument project,
        int segmentIndex,
        int renderIndex,
        WordAlignmentRecord attempt,
        CancellationToken cancellationToken)
    {
        string segmentId = project.Segments[segmentIndex].SegmentId;
        Guid renderId = project.Segments[segmentIndex].RenderHistory[renderIndex].RenderId;
        await ProjectDocumentUpdater.UpdateAsync(_projects, root, latest =>
        {
            var found = FindRender(latest, segmentId, renderId);
            List<RenderRecord> renders = [.. found.Segment.RenderHistory];
            renders[found.RenderIndex] = found.Render with { AlignmentHistory = [.. found.Render.AlignmentHistory, attempt] };
            List<SegmentDocument> segments = [.. latest.Segments];
            segments[found.SegmentIndex] = found.Segment with { RenderHistory = renders };
            return latest with { Segments = segments };
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceAttemptAsync(
        string root,
        string segmentId,
        Guid renderId,
        WordAlignmentRecord attempt,
        CancellationToken cancellationToken)
    {
        await ProjectDocumentUpdater.UpdateAsync(_projects, root, project =>
        {
            var (segmentIndex, renderIndex, segment, render) = FindRender(project, segmentId, renderId);
            int attemptIndex = render.AlignmentHistory.FindIndex(item => item.AlignmentId == attempt.AlignmentId);
            if (attemptIndex < 0) throw new InvalidOperationException("The alignment attempt disappeared from the project manifest.");
            List<WordAlignmentRecord> attempts = [.. render.AlignmentHistory];
            attempts[attemptIndex] = attempt;
            List<RenderRecord> renders = [.. segment.RenderHistory];
            renders[renderIndex] = render with { AlignmentHistory = attempts };
            List<SegmentDocument> segments = [.. project.Segments];
            segments[segmentIndex] = segment with { RenderHistory = renders };
            return project with { Segments = segments };
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (int SegmentIndex, int RenderIndex, SegmentDocument Segment, RenderRecord Render) FindRender(
        VoiceBenchProjectDocument project,
        string segmentId,
        Guid renderId)
    {
        int segmentIndex = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        if (segmentIndex < 0) throw new KeyNotFoundException($"Segment '{segmentId}' was not found.");
        SegmentDocument segment = project.Segments[segmentIndex];
        int renderIndex = segment.RenderHistory.FindIndex(item => item.RenderId == renderId);
        if (renderIndex < 0) throw new KeyNotFoundException($"Render '{renderId}' was not found.");
        return (segmentIndex, renderIndex, segment, segment.RenderHistory[renderIndex]);
    }

    private static async Task<string> WriteAlignmentArtifactAsync(
        string root,
        string segmentId,
        int takeNumber,
        WordAlignmentDocument document,
        CancellationToken cancellationToken)
    {
        string safeSegmentId = MakeSafeFileName(segmentId);
        string directory = VoiceBenchProjectLayout.ResolveRelativePath(
            root,
            $"{VoiceBenchProjectLayout.SegmentsDirectory}/{safeSegmentId}");
        Directory.CreateDirectory(directory);
        for (int version = 1; version <= 9999; version++)
        {
            string path = Path.Combine(
                directory,
                $"{safeSegmentId}--take-{takeNumber:0000}--alignment-{version:000}.json");
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    32 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await JsonSerializer.SerializeAsync(stream, document, ProjectJson.SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                return VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(root, path));
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new IOException($"No word-alignment version slot is available for segment '{segmentId}'.");
    }

    private static string ResolveMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mp3",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        _ => "audio/wav",
    };

    private static string MakeSafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());
        safe = safe.Trim('-', '.');
        return safe.Length > 0 ? safe : throw new InvalidDataException("The segment ID cannot form an alignment file name.");
    }
}
