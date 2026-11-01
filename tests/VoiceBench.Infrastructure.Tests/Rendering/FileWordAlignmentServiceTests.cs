using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Providers;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Rendering;

public sealed class FileWordAlignmentServiceTests
{
    [Fact]
    public async Task AlignAsync_persists_versioned_timestamps_and_updates_only_alignment_history()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        RecordingAlignmentProvider provider = new(
        [
            new RecognizedWordTiming("Hello", TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.4)),
            new RecognizedWordTiming("world", TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.9)),
        ]);
        FileWordAlignmentService service = CreateService(repository, provider);

        WordAlignmentRecord result = await service.AlignAsync(directory.Root, segmentId, renderId);

        Assert.Equal(WordAlignmentStatus.Aligned, result.Status);
        Assert.Equal("Hello world", provider.Request?.SubmittedText);
        Assert.NotEmpty(provider.Request?.Audio ?? []);
        Assert.Equal(1, result.Coverage);
        Assert.NotNull(result.ArtifactRelativePath);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, result.ArtifactRelativePath!)));
        VoiceBenchProjectDocument reloaded = await repository.LoadAsync(directory.Root);
        RenderRecord render = Assert.Single(Assert.Single(reloaded.Segments).RenderHistory);
        Assert.Equal(RenderStatus.Generated, render.Status);
        Assert.Equal(result, Assert.Single(render.AlignmentHistory));
        WordAlignmentDocument document = await FileWordAlignmentService.LoadDocumentAsync(directory.Root, result);
        Assert.Equal([0, 1], document.Words.Select(word => word.NarrationWordOrdinal));
    }

    [Fact]
    public async Task AlignAsync_provider_failure_leaves_generated_take_playable_and_retryable()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        RecordingAlignmentProvider provider = new([])
        {
            Failure = new WordAlignmentProviderException(
                "openai",
                WordAlignmentFailureKind.RateLimited,
                "rate limited"),
        };
        FileWordAlignmentService service = CreateService(repository, provider);

        WordAlignmentRecord result = await service.AlignAsync(directory.Root, segmentId, renderId);

        Assert.Equal(WordAlignmentStatus.Failed, result.Status);
        Assert.Equal("provider_ratelimited", result.FailureCode);
        RenderRecord render = Assert.Single(Assert.Single((await repository.LoadAsync(directory.Root)).Segments).RenderHistory);
        Assert.Equal(RenderStatus.Generated, render.Status);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, render.RawAudioRelativePath)));
    }

    [Fact]
    public async Task AlignAsync_classifies_nonzero_coverage_as_partial()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        FileWordAlignmentService service = CreateService(
            repository,
            new RecordingAlignmentProvider(
                [new RecognizedWordTiming("Hello", TimeSpan.Zero, TimeSpan.FromSeconds(0.4))]));

        WordAlignmentRecord result = await service.AlignAsync(directory.Root, segmentId, renderId);

        Assert.Equal(WordAlignmentStatus.Partial, result.Status);
        Assert.Equal(0.5, result.Coverage);
        Assert.Equal(1, result.MatchedWordCount);
    }

    [Fact]
    public async Task AlignAsync_classifies_zero_coverage_as_failed_without_an_artifact()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        FileWordAlignmentService service = CreateService(
            repository,
            new RecordingAlignmentProvider(
                [new RecognizedWordTiming("Different", TimeSpan.Zero, TimeSpan.FromSeconds(0.4))]));

        WordAlignmentRecord result = await service.AlignAsync(directory.Root, segmentId, renderId);

        Assert.Equal(WordAlignmentStatus.Failed, result.Status);
        Assert.Equal("alignment_no_matches", result.FailureCode);
        Assert.Null(result.ArtifactRelativePath);
    }

    [Fact]
    public async Task RecoverInterruptedAsync_changes_abandoned_pending_attempts_only()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        VoiceBenchProjectDocument project = await repository.LoadAsync(directory.Root);
        SegmentDocument segment = Assert.Single(project.Segments);
        RenderRecord render = Assert.Single(segment.RenderHistory);
        render = render with
        {
            AlignmentHistory =
            [
                new WordAlignmentRecord
                {
                    ProviderId = "openai",
                    ModelId = "whisper-1",
                    AudioRelativePath = render.RawAudioRelativePath,
                    Status = WordAlignmentStatus.Pending,
                },
            ],
        };
        await repository.SaveAsync(directory.Root, project with
        {
            Segments = [segment with { RenderHistory = [render] }],
        });
        FileWordAlignmentService service = CreateService(repository, new RecordingAlignmentProvider([]));

        int recovered = await service.RecoverInterruptedAsync(directory.Root);

        Assert.Equal(1, recovered);
        WordAlignmentRecord attempt = Assert.Single(Assert.Single(Assert.Single(
            (await repository.LoadAsync(directory.Root)).Segments).RenderHistory).AlignmentHistory);
        Assert.Equal(WordAlignmentStatus.Interrupted, attempt.Status);
        Assert.Equal("alignment_interrupted", attempt.FailureCode);
    }

    [Fact]
    public async Task AlignAsync_retry_appends_a_new_immutable_artifact()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        FileWordAlignmentService service = CreateService(
            repository,
            new RecordingAlignmentProvider(
            [
                new RecognizedWordTiming("Hello", TimeSpan.Zero, TimeSpan.FromSeconds(0.4)),
                new RecognizedWordTiming("world", TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.8)),
            ]));

        WordAlignmentRecord first = await service.AlignAsync(directory.Root, segmentId, renderId);
        WordAlignmentRecord second = await service.AlignAsync(directory.Root, segmentId, renderId);

        Assert.NotEqual(first.AlignmentId, second.AlignmentId);
        Assert.NotEqual(first.ArtifactRelativePath, second.ArtifactRelativePath);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, first.ArtifactRelativePath!)));
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, second.ArtifactRelativePath!)));
        RenderRecord render = Assert.Single(Assert.Single((await repository.LoadAsync(directory.Root)).Segments).RenderHistory);
        Assert.Equal(2, render.AlignmentHistory.Count);
    }

    [Fact]
    public async Task AlignAsync_blocks_an_unverifiable_historical_take_with_rerender_guidance()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        (string segmentId, Guid renderId) = await SeedTakeAsync(directory.Root, repository);
        VoiceBenchProjectDocument project = await repository.LoadAsync(directory.Root);
        SegmentDocument segment = Assert.Single(project.Segments);
        RenderRecord render = Assert.Single(segment.RenderHistory) with
        {
            SubmittedTextMapRelativePath = null,
            CleanSourceRelativePath = null,
            AuthoringSourceRelativePath = null,
        };
        await repository.SaveAsync(directory.Root, project with
        {
            Segments = [segment with { RenderHistory = [render] }],
        });
        FileWordAlignmentService service = CreateService(repository, new RecordingAlignmentProvider([]));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.AlignAsync(directory.Root, segmentId, renderId));

        Assert.Contains("Rerender", exception.Message, StringComparison.OrdinalIgnoreCase);
        RenderRecord unchanged = Assert.Single(Assert.Single((await repository.LoadAsync(directory.Root)).Segments).RenderHistory);
        Assert.Empty(unchanged.AlignmentHistory);
    }

    private static FileWordAlignmentService CreateService(
        FileProjectRepository repository,
        IWordAlignmentProvider provider) => new(
            repository,
            new WordAlignmentProviderRegistry([provider]),
            new ProviderCredentialResolver(new StaticCredentialStore("key"), _ => null));

    private static async Task<(string SegmentId, Guid RenderId)> SeedTakeAsync(
        string root,
        FileProjectRepository repository)
    {
        VoiceBenchProjectDocument project = await repository.CreateAsync(root, "Alignment test");
        const string segmentId = "intro";
        const string narration = "Hello world";
        string scriptRelative = "02-scripts/intro.md";
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, scriptRelative), narration);
        string audioRelative = "04-generated-audio/segments/intro/intro--take-0001.raw.wav";
        string audioPath = VoiceBenchProjectLayout.ResolveRelativePath(root, audioRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllBytesAsync(audioPath, ProviderTestAudio.Wave());
        Guid renderId = Guid.NewGuid();
        PreparedSpeechText prepared = LexiconSubstitution.Prepare(new ProductionScriptParser().Parse(narration), []);
        SubmittedTextMapDocument map = new()
        {
            RenderId = renderId,
            SourceSha256 = SourceTextHasher.Compute(narration),
            SubmittedTextSha256 = SourceTextHasher.Compute(narration),
            SubmittedText = narration,
            TotalNarrationWordCount = 2,
            Words = [.. prepared.Words],
        };
        string mapRelative = "03-render-copies/intro.submitted-map.json";
        await File.WriteAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(root, mapRelative),
            JsonSerializer.Serialize(map, ProjectJson.SerializerOptions));
        SegmentDocument segment = new()
        {
            SegmentId = segmentId,
            ChapterId = "chapter",
            Title = "Intro",
            ScriptRelativePath = scriptRelative,
            SourceSha256 = map.SourceSha256,
            AuthoringSha256 = map.SourceSha256,
            Status = SegmentStatus.Rendered,
            RenderHistory =
            [
                new RenderRecord
                {
                    RenderId = renderId,
                    TakeNumber = 1,
                    Status = RenderStatus.Generated,
                    ProviderId = "openai",
                    ModelId = "gpt-4o-mini-tts",
                    Voice = "marin",
                    SourceSha256 = map.SourceSha256,
                    AuthoringSourceSha256 = map.SourceSha256,
                    SubmittedTextSha256 = map.SubmittedTextSha256,
                    RawAudioRelativePath = audioRelative,
                    SubmittedTextMapRelativePath = mapRelative,
                },
            ],
        };
        await repository.SaveAsync(root, project with
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", Title = "Chapter", SegmentOrder = [segmentId] }],
            Segments = [segment],
        });
        return (segmentId, renderId);
    }

    private sealed class RecordingAlignmentProvider(IReadOnlyList<RecognizedWordTiming> words) : IWordAlignmentProvider
    {
        public string ProviderId => "openai";
        public string ModelId => "whisper-1";
        public Exception? Failure { get; init; }
        public WordAlignmentProviderRequest? Request { get; private set; }

        public Task<WordAlignmentProviderResult> TranscribeAsync(
            WordAlignmentProviderRequest request,
            string apiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            if (Failure is not null) return Task.FromException<WordAlignmentProviderResult>(Failure);
            return Task.FromResult(new WordAlignmentProviderResult(words, TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class StaticCredentialStore(string value) : ICredentialStore
    {
        public Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(value);
        public Task SetAsync(string providerId, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
