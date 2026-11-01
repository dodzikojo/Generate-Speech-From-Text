using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;

namespace IssueLab.VoiceBench.Infrastructure.Rendering;

public sealed record SegmentRenderResult(
    RenderRecord Render,
    string RenderCopyRelativePath,
    TimeSpan EstimatedDuration,
    bool ExceedsFourMinuteGuidance)
{
    public WordAlignmentRecord? Alignment { get; init; }
}

public sealed class SegmentRenderService
{
    private readonly IProjectRepository _projects;
    private readonly SpeechProviderRegistry _providers;
    private readonly ProviderCredentialResolver _credentials;
    private readonly IRenderQueue _queue;
    private readonly ImmutableRenderStore _renderStore;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _createRenderId;
    private readonly IWordAlignmentService? _wordAlignment;

    public SegmentRenderService(
        IProjectRepository projects,
        SpeechProviderRegistry providers,
        ProviderCredentialResolver credentials,
        IRenderQueue queue,
        ImmutableRenderStore renderStore,
        TimeProvider? timeProvider = null,
        Func<Guid>? createRenderId = null,
        IWordAlignmentService? wordAlignment = null)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _renderStore = renderStore ?? throw new ArgumentNullException(nameof(renderStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createRenderId = createRenderId ?? Guid.NewGuid;
        _wordAlignment = wordAlignment;
    }

    public async Task<SegmentRenderResult> RenderAsync(
        string projectRoot,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var root = Path.GetFullPath(projectRoot);
        var project = await _projects.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        var segmentIndex = project.Segments.FindIndex(
            segment => string.Equals(segment.SegmentId, segmentId, StringComparison.Ordinal));
        if (segmentIndex < 0)
        {
            throw new KeyNotFoundException($"Segment '{segmentId}' does not exist in this VoiceBench project.");
        }

        var originalSegment = project.Segments[segmentIndex];
        if (originalSegment.Status == SegmentStatus.Rendering)
        {
            throw new InvalidOperationException($"Segment '{segmentId}' is already rendering.");
        }

        var scriptPath = VoiceBenchProjectLayout.ResolveRelativePath(root, originalSegment.ScriptRelativePath);
        var canonicalText = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(canonicalText);
        if (parsed.HasErrors)
        {
            throw new InvalidDataException(string.Join(
                " ",
                parsed.Diagnostics.Select(item => $"Line {item.SourceLine}: {item.Message}")));
        }

        if (string.IsNullOrWhiteSpace(parsed.NarrationText))
        {
            throw new InvalidDataException($"Segment '{segmentId}' contains no narration to render.");
        }

        string sourceHash = SourceTextHasher.Compute(parsed.NarrationText);
        string authoringHash = SourceTextHasher.Compute(canonicalText);
        var currentSegment = SegmentStateRules.WithSourceHashes(originalSegment, sourceHash, authoringHash);
        var presetId = currentSegment.VoicePresetId ?? project.DefaultVoicePresetId;
        if (string.IsNullOrWhiteSpace(presetId))
        {
            throw new InvalidOperationException($"Segment '{segmentId}' has no voice preset.");
        }

        var preset = project.VoicePresets.SingleOrDefault(
            item => string.Equals(item.PresetId, presetId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Voice preset '{presetId}' is missing from the project.");
        if (string.Equals(project.DefaultVoicePresetId, preset.PresetId, StringComparison.Ordinal)
            && !VoicePresetRules.CanSelectAsProjectDefault(preset))
        {
            throw new InvalidOperationException(
                $"Voice preset '{preset.Name}' must specify an exact provider, model, and voice.");
        }

        var provider = _providers.GetRequired(preset.ProviderId);
        var model = provider.Descriptor.Models.SingleOrDefault(
            item => string.Equals(item.ModelId, preset.ModelId, StringComparison.Ordinal))
            ?? throw new SpeechProviderException(
                provider.Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                $"Model '{preset.ModelId}' is not selectable for {provider.Descriptor.DisplayName}.");
        if (!model.Voices.Contains(preset.Voice, StringComparer.OrdinalIgnoreCase))
        {
            throw new SpeechProviderException(
                provider.Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                $"Voice '{preset.Voice}' is not selectable for '{model.ModelId}'.");
        }

        var directions = CombineDirections(preset.Directions, currentSegment.Directions);
        PreparedSpeechText prepared = LexiconSubstitution.Prepare(parsed, project.Lexicon);
        string submittedText = prepared.Text;
        var estimatedTokens = EstimateInputTokens(submittedText, directions);
        if (estimatedTokens > model.MaxInputTokens)
        {
            throw new SpeechProviderException(
                provider.Descriptor.ProviderId,
                SpeechProviderFailureKind.Validation,
                $"Estimated input is outside the {model.MaxInputTokens:N0}-token limit for '{model.ModelId}'.");
        }

        var activeJobs = await _queue.GetJobsAsync(cancellationToken).ConfigureAwait(false);
        if (activeJobs.Any(job => job.Status is RenderJobStatus.Queued or RenderJobStatus.InProgress))
        {
            throw new InvalidOperationException("The render queue already has active work. VoiceBench renders one job at a time.");
        }

        var apiKey = await _credentials.ResolveAsync(provider.Descriptor.ProviderId, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechProviderException(
                provider.Descriptor.ProviderId,
                SpeechProviderFailureKind.Credentials,
                $"No {provider.Descriptor.DisplayName} API key is configured.");
        }

        var firstTakeNumber = currentSegment.RenderHistory.Count == 0
            ? 1
            : currentSegment.RenderHistory.Max(render => render.TakeNumber) + 1;
        var renderId = _createRenderId();
        var renderCopy = await WriteRenderCopyAsync(
            root,
            currentSegment,
            preset,
            directions,
            canonicalText,
            parsed.NarrationText,
            submittedText,
            sourceHash,
            authoringHash,
            renderId,
            prepared,
            parsed.NarrationWordCount,
            firstTakeNumber,
            cancellationToken).ConfigureAwait(false);
        var takeNumber = renderCopy.TakeNumber;
        var createdAt = _timeProvider.GetUtcNow();
        var baseRecord = new RenderRecord
        {
            RenderId = renderId,
            TakeNumber = takeNumber,
            ProviderId = preset.ProviderId,
            ModelId = preset.ModelId,
            Voice = preset.Voice,
            SourceSha256 = sourceHash,
            AuthoringSourceSha256 = authoringHash,
            SubmittedTextSha256 = SourceTextHasher.Compute(submittedText),
            DirectionsSha256 = SourceTextHasher.Compute(directions ?? string.Empty),
            CreatedAt = createdAt,
            CleanSourceRelativePath = renderCopy.CleanSourceRelativePath,
            AuthoringSourceRelativePath = renderCopy.AuthoringSourceRelativePath,
            SubmittedTextRelativePath = renderCopy.RelativePath,
            SubmittedTextMapRelativePath = renderCopy.SubmittedMapRelativePath,
        };

        var queueJob = await _queue.EnqueueAsync(
            new RenderQueueRequest(
                root,
                currentSegment.SegmentId,
                preset.ProviderId,
                preset.ModelId,
                preset.Voice,
                renderCopy.RelativePath,
                takeNumber),
            cancellationToken).ConfigureAwait(false);
        var claimed = await _queue.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
        if (claimed?.JobId != queueJob.JobId)
        {
            throw new InvalidOperationException("The requested render could not claim its queue position.");
        }

        project = await ProjectDocumentUpdater.UpdateAsync(_projects, root, latest =>
        {
            int latestIndex = latest.Segments.FindIndex(item => item.SegmentId == currentSegment.SegmentId);
            if (latestIndex < 0) throw new InvalidOperationException("The segment was removed before rendering began.");
            return ReplaceSegment(latest, latestIndex, latest.Segments[latestIndex] with { Status = SegmentStatus.Rendering });
        }, cancellationToken).ConfigureAwait(false);
        var manifestRecorded = false;

        try
        {
            var synthesis = await provider.SynthesizeAsync(
                new SpeechSynthesisRequest(
                    preset.ModelId,
                    preset.Voice,
                    submittedText,
                    directions,
                    estimatedTokens),
                apiKey,
                cancellationToken).ConfigureAwait(false);
            var wave = WaveFileInspector.Read(synthesis.Audio);
            var rawRelativePath = await _renderStore.SaveRawAsync(
                root,
                currentSegment.SegmentId,
                synthesis.Audio,
                cancellationToken).ConfigureAwait(false);

            var record = baseRecord with
            {
                Status = RenderStatus.Generated,
                RawAudioRelativePath = rawRelativePath,
                Duration = wave.Duration,
                SampleRate = wave.SampleRate,
                Channels = wave.Channels,
                AudioSha256 = Convert.ToHexStringLower(SHA256.HashData(synthesis.Audio)),
                ProviderResponseId = synthesis.ProviderResponseId,
            };
            project = await ProjectDocumentUpdater.UpdateAsync(_projects, root, latest =>
            {
                int latestIndex = latest.Segments.FindIndex(item => item.SegmentId == currentSegment.SegmentId);
                if (latestIndex < 0) throw new InvalidOperationException("The segment was removed while audio was generated; its raw audio remains on disk.");
                SegmentDocument latestSegment = latest.Segments[latestIndex];
                bool currentSource = string.Equals(latestSegment.SourceSha256, record.SourceSha256, StringComparison.OrdinalIgnoreCase);
                return ReplaceSegment(latest, latestIndex, latestSegment with
                {
                    Status = currentSource ? SegmentStatus.Rendered : SegmentStatus.Stale,
                    RenderHistory = [.. latestSegment.RenderHistory, record],
                });
            }, cancellationToken).ConfigureAwait(false);
            manifestRecorded = true;
            await _queue.CompleteAsync(queueJob.JobId, rawRelativePath, cancellationToken).ConfigureAwait(false);

            WordAlignmentRecord? alignment = null;
            if (_wordAlignment is not null)
            {
                try
                {
                    alignment = await _wordAlignment.AlignAsync(
                        root,
                        currentSegment.SegmentId,
                        record.RenderId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The audio render is already durable. Cancellation stops only the secondary alignment pass.
                }
                catch
                {
                    // Alignment is deliberately non-fatal; the generated take remains valid and playable.
                }
            }

            var estimatedDuration = EstimateDuration(submittedText);
            return new SegmentRenderResult(
                record,
                renderCopy.RelativePath,
                estimatedDuration,
                estimatedDuration > TimeSpan.FromMinutes(4))
            {
                Alignment = alignment,
            };
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(
                root,
                project,
                segmentIndex,
                currentSegment,
                baseRecord,
                queueJob.JobId,
                "cancelled",
                cancelled: true,
                manifestRecorded).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var failureCode = exception is SpeechProviderException providerException
                ? $"provider_{providerException.Kind.ToString().ToLowerInvariant()}"
                : "render_failed";
            await RecordFailureAsync(
                root,
                project,
                segmentIndex,
                currentSegment,
                baseRecord,
                queueJob.JobId,
                failureCode,
                cancelled: false,
                manifestRecorded).ConfigureAwait(false);
            throw;
        }
    }

    public static int EstimateInputTokens(string text, string? directions = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var characters = text.Length + (directions?.Length ?? 0);
        return Math.Max(1, (int)Math.Ceiling(characters / 3.0));
    }

    public static TimeSpan EstimateDuration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return TimeSpan.FromMinutes(words / 150.0);
    }

    private async Task RecordFailureAsync(
        string projectRoot,
        VoiceBenchProjectDocument project,
        int segmentIndex,
        SegmentDocument currentSegment,
        RenderRecord baseRecord,
        Guid queueJobId,
        string failureCode,
        bool cancelled,
        bool manifestRecorded)
    {
        try
        {
            await _queue.FailAsync(queueJobId, failureCode, cancelled, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (manifestRecorded)
        {
            // The queue was already completed; the successful manifest remains authoritative.
        }
        catch (Exception exception) when (
            !manifestRecorded
            && exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original render failure if the recovery journal itself is unavailable.
        }

        if (manifestRecorded)
        {
            return;
        }

        var failedRecord = baseRecord with
        {
            Status = cancelled ? RenderStatus.Cancelled : RenderStatus.Failed,
        };
        try
        {
            await ProjectDocumentUpdater.UpdateAsync(_projects, projectRoot, latest =>
            {
                int index = latest.Segments.FindIndex(item => item.SegmentId == currentSegment.SegmentId);
                if (index < 0) return latest;
                SegmentDocument segment = latest.Segments[index];
                return ReplaceSegment(latest, index, segment with
                {
                    Status = cancelled ? currentSegment.Status : SegmentStatus.PickupRequired,
                    RenderHistory = [.. segment.RenderHistory, failedRecord],
                });
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original render failure. The journal remains the recovery record.
        }
    }

    private static VoiceBenchProjectDocument ReplaceSegment(
        VoiceBenchProjectDocument project,
        int segmentIndex,
        SegmentDocument segment)
    {
        var segments = project.Segments.ToList();
        segments[segmentIndex] = segment;
        return project with { Segments = segments };
    }

    private static string? CombineDirections(string? presetDirections, string? segmentDirections)
    {
        var values = new[] { presetDirections, segmentDirections }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        return values.Length == 0 ? null : string.Join("\n\n", values);
    }

    private sealed record RenderCopyPaths(
        string RelativePath,
        string CleanSourceRelativePath,
        string AuthoringSourceRelativePath,
        string SubmittedMapRelativePath,
        int TakeNumber);

    private static async Task<RenderCopyPaths> WriteRenderCopyAsync(
        string projectRoot,
        SegmentDocument segment,
        VoicePreset preset,
        string? directions,
        string authoringText,
        string narrationText,
        string submittedText,
        string sourceHash,
        string authoringHash,
        Guid renderId,
        PreparedSpeechText prepared,
        int narrationWordCount,
        int firstTakeNumber,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(projectRoot, VoiceBenchProjectLayout.RenderCopiesDirectory);
        Directory.CreateDirectory(directory);
        var safeSegmentId = MakeSafeFileName(segment.SegmentId);
        var safeProviderId = MakeSafeFileName(preset.ProviderId);
        var generatedSegmentDirectory = VoiceBenchProjectLayout.ResolveRelativePath(
            projectRoot,
            $"{VoiceBenchProjectLayout.SegmentsDirectory}/{safeSegmentId}");
        Directory.CreateDirectory(generatedSegmentDirectory);

        for (var takeNumber = firstTakeNumber; takeNumber <= 999_999; takeNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = $"{safeSegmentId}--take-{takeNumber:0000}--{safeProviderId}.md";
            var path = Path.Combine(directory, fileName);
            var cleanSourcePath = Path.Combine(
                directory,
                $"{safeSegmentId}--take-{takeNumber:0000}--{safeProviderId}.clean.md");
            var authoringSourcePath = Path.Combine(
                directory,
                $"{safeSegmentId}--take-{takeNumber:0000}--{safeProviderId}.authoring.md");
            var submittedMapPath = Path.Combine(
                generatedSegmentDirectory,
                $"{safeSegmentId}--take-{takeNumber:0000}--{safeProviderId}.submitted-map.json");
            var content = BuildRenderCopy(
                segment,
                preset,
                directions,
                submittedText,
                sourceHash,
                authoringHash,
                takeNumber);

            bool submittedCreated = false;
            bool cleanCreated = false;
            bool authoringCreated = false;
            bool mapCreated = false;
            try
            {
                await WriteImmutableTextAsync(path, content, cancellationToken).ConfigureAwait(false);
                submittedCreated = true;
                await WriteImmutableTextAsync(cleanSourcePath, narrationText, cancellationToken).ConfigureAwait(false);
                cleanCreated = true;
                await WriteImmutableTextAsync(authoringSourcePath, authoringText, cancellationToken).ConfigureAwait(false);
                authoringCreated = true;
                SubmittedTextMapDocument map = new()
                {
                    RenderId = renderId,
                    SourceSha256 = sourceHash,
                    SubmittedTextSha256 = SourceTextHasher.Compute(submittedText),
                    SubmittedText = submittedText,
                    TotalNarrationWordCount = narrationWordCount,
                    Words = [.. prepared.Words],
                };
                await WriteImmutableJsonAsync(submittedMapPath, map, cancellationToken).ConfigureAwait(false);
                mapCreated = true;

                return new RenderCopyPaths(
                    VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, path)),
                    VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, cleanSourcePath)),
                    VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, authoringSourcePath)),
                    VoiceBenchProjectLayout.ToManifestPath(Path.GetRelativePath(projectRoot, submittedMapPath)),
                    takeNumber);
            }
            catch (IOException)
            {
                bool collision = (!submittedCreated && File.Exists(path))
                    || (!cleanCreated && File.Exists(cleanSourcePath))
                    || (!authoringCreated && File.Exists(authoringSourcePath))
                    || (!mapCreated && File.Exists(submittedMapPath));
                if (submittedCreated) File.Delete(path);
                if (cleanCreated) File.Delete(cleanSourcePath);
                if (authoringCreated) File.Delete(authoringSourcePath);
                if (mapCreated) File.Delete(submittedMapPath);
                if (!collision) throw;
                // Keep every earlier render artifact immutable and advance the take number.
            }
            catch
            {
                if (submittedCreated) File.Delete(path);
                if (cleanCreated) File.Delete(cleanSourcePath);
                if (authoringCreated) File.Delete(authoringSourcePath);
                if (mapCreated) File.Delete(submittedMapPath);
                throw;
            }
        }

        throw new IOException($"No render-copy version slot is available for segment '{segment.SegmentId}'.");
    }

    private static async Task WriteImmutableJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, ProjectJson.SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteImmutableTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using StreamWriter writer = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string BuildRenderCopy(
        SegmentDocument segment,
        VoicePreset preset,
        string? directions,
        string submittedText,
        string sourceHash,
        string authoringHash,
        int takeNumber) => $$"""
        # VoiceBench render copy

        - Segment: {{segment.SegmentId}}
        - Take: {{takeNumber.ToString(CultureInfo.InvariantCulture)}}
        - Provider: {{preset.ProviderId}}
        - Model: {{preset.ModelId}}
        - Voice: {{preset.Voice}}
        - Source SHA-256: {{sourceHash}}
        - Authoring SHA-256: {{authoringHash}}

        ## Directions

        {{directions ?? "(none)"}}

        ## Exact submitted text

        {{submittedText}}
        """;

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray())
            .Trim('-', '.');
        return safe.Length > 0
            ? safe
            : throw new InvalidDataException("A render identifier cannot be converted to a safe file name.");
    }
}
