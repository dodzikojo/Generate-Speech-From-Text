using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Credentials;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Providers;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Providers;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Rendering;

public sealed class SegmentRenderServiceTests
{
    [Fact]
    public async Task RenderAsync_CreatesExactCopyPersistsRawAudioAndUpdatesManifest()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProjectRepository(new FixedTimeProvider());
        var project = await repository.CreateAsync(directory.Root, "Training narration");
        var scriptRelativePath = "02-scripts/001-introduction--abc123.md";
        await File.WriteAllTextAsync(
            Path.Combine(directory.Root, scriptRelativePath),
            "ClashWise keeps BIM coordination clear.");
        var auditionOne = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var auditionTwo = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var segment = new SegmentDocument
        {
            SegmentId = "introduction",
            Title = "Introduction",
            ScriptRelativePath = scriptRelativePath,
            Status = SegmentStatus.Ready,
            SourceSha256 = SourceTextHasher.Compute("ClashWise keeps BIM coordination clear."),
            Directions = "Pause after the product name.",
        };
        var preset = new VoicePreset
        {
            PresetId = "openai-main",
            Name = "Main narrator",
            ProviderId = "openai",
            ModelId = "gpt-4o-mini-tts",
            Voice = "marin",
            Directions = "Read with calm authority.",
            AuditionRenderIds = [auditionOne, auditionTwo],
        };
        project = project with
        {
            SegmentOrder = [segment.SegmentId],
            Segments = [segment],
            VoicePresets = [preset],
            DefaultVoicePresetId = preset.PresetId,
            Lexicon =
            [
                new PronunciationEntry
                {
                    Written = "ClashWise",
                    Spoken = "Clash Wise",
                    MatchCase = true,
                },
                new PronunciationEntry
                {
                    Written = "BIM",
                    Spoken = "B I M",
                    MatchCase = true,
                },
            ],
        };
        await repository.SaveAsync(directory.Root, project);

        var provider = new RecordingSpeechProvider();
        var store = new StaticCredentialStore("provider-key");
        using var queue = new FileRenderQueue(
            Path.Combine(directory.Root, "07-reports", "render-queue.json"),
            new FixedTimeProvider(),
            () => Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        var service = new SegmentRenderService(
            repository,
            new SpeechProviderRegistry([provider]),
            new ProviderCredentialResolver(store, _ => null),
            queue,
            new ImmutableRenderStore(),
            new FixedTimeProvider(),
            () => Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));

        var result = await service.RenderAsync(directory.Root, segment.SegmentId);

        Assert.Equal("Clash Wise keeps B I M coordination clear.", provider.Request?.Text);
        Assert.Equal(
            "Read with calm authority.\n\nPause after the product name.",
            provider.Request?.Directions);
        Assert.Equal("provider-key", provider.ApiKey);
        var renderCopy = await File.ReadAllTextAsync(Path.Combine(directory.Root, result.RenderCopyRelativePath));
        Assert.Contains("## Exact submitted text", renderCopy, StringComparison.Ordinal);
        Assert.Contains("Clash Wise keeps B I M coordination clear.", renderCopy, StringComparison.Ordinal);
        Assert.Contains("gpt-4o-mini-tts", renderCopy, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directory.Root, result.Render.RawAudioRelativePath)));

        var reloaded = await repository.LoadAsync(directory.Root);
        var rendered = Assert.Single(reloaded.Segments);
        Assert.Equal(SegmentStatus.Rendered, rendered.Status);
        Assert.Equal(RenderStatus.Generated, Assert.Single(rendered.RenderHistory).Status);
        Assert.Equal("dddddddd-dddd-dddd-dddd-dddddddddddd", result.Render.RenderId.ToString());
        Assert.Equal(RenderJobStatus.Completed, Assert.Single(await queue.GetJobsAsync()).Status);
    }

    [Fact]
    public async Task RenderAsync_DoesNotSubstituteAnUnregisteredProvider()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProjectRepository(new FixedTimeProvider());
        var project = await repository.CreateAsync(directory.Root, "No fallback");
        var scriptRelativePath = "02-scripts/segment.md";
        await File.WriteAllTextAsync(Path.Combine(directory.Root, scriptRelativePath), "Narration.");
        project = project with
        {
            Segments =
            [
                new SegmentDocument
                {
                    SegmentId = "segment",
                    ScriptRelativePath = scriptRelativePath,
                    SourceSha256 = SourceTextHasher.Compute("Narration."),
                    VoicePresetId = "missing-provider",
                },
            ],
            SegmentOrder = ["segment"],
            VoicePresets =
            [
                new VoicePreset
                {
                    PresetId = "missing-provider",
                    Name = "Unavailable",
                    ProviderId = "gemini",
                    ModelId = "gemini-3.1-flash-tts-preview",
                    Voice = "Kore",
                },
            ],
        };
        await repository.SaveAsync(directory.Root, project);
        using var queue = new FileRenderQueue(Path.Combine(directory.Root, "queue.json"));
        var service = new SegmentRenderService(
            repository,
            new SpeechProviderRegistry([new RecordingSpeechProvider()]),
            new ProviderCredentialResolver(new StaticCredentialStore("key"), _ => null),
            queue,
            new ImmutableRenderStore());

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RenderAsync(directory.Root, "segment"));

        Assert.Contains("will not substitute", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await queue.GetJobsAsync());
    }

    [Fact]
    public async Task RenderAsync_ProviderFailureIsJournaledAndAddedToHistory()
    {
        using var directory = new TemporaryDirectory();
        var repository = new FileProjectRepository(new FixedTimeProvider());
        var project = await repository.CreateAsync(directory.Root, "Failure history");
        var scriptRelativePath = "02-scripts/segment.md";
        await File.WriteAllTextAsync(Path.Combine(directory.Root, scriptRelativePath), "Narration.");
        project = project with
        {
            Segments =
            [
                new SegmentDocument
                {
                    SegmentId = "segment",
                    ScriptRelativePath = scriptRelativePath,
                    SourceSha256 = SourceTextHasher.Compute("Narration."),
                    VoicePresetId = "preset",
                    Status = SegmentStatus.Ready,
                },
            ],
            SegmentOrder = ["segment"],
            VoicePresets =
            [
                new VoicePreset
                {
                    PresetId = "preset",
                    Name = "Main",
                    ProviderId = "openai",
                    ModelId = "gpt-4o-mini-tts",
                    Voice = "marin",
                },
            ],
        };
        await repository.SaveAsync(directory.Root, project);
        var provider = new RecordingSpeechProvider
        {
            Failure = new SpeechProviderException(
                "openai",
                SpeechProviderFailureKind.RateLimited,
                "rate limited"),
        };
        using var queue = new FileRenderQueue(Path.Combine(directory.Root, "queue.json"));
        var service = new SegmentRenderService(
            repository,
            new SpeechProviderRegistry([provider]),
            new ProviderCredentialResolver(new StaticCredentialStore("key"), _ => null),
            queue,
            new ImmutableRenderStore(),
            new FixedTimeProvider());

        await Assert.ThrowsAsync<SpeechProviderException>(
            () => service.RenderAsync(directory.Root, "segment"));

        var failedSegment = Assert.Single((await repository.LoadAsync(directory.Root)).Segments);
        Assert.Equal(SegmentStatus.PickupRequired, failedSegment.Status);
        Assert.Equal(RenderStatus.Failed, Assert.Single(failedSegment.RenderHistory).Status);
        var failedJob = Assert.Single(await queue.GetJobsAsync());
        Assert.Equal(RenderJobStatus.Failed, failedJob.Status);
        Assert.Equal("provider_ratelimited", failedJob.FailureCode);
    }

    private sealed class RecordingSpeechProvider : ISpeechProvider
    {
        public SpeechProviderDescriptor Descriptor { get; } = new(
            "openai",
            "OpenAI",
            [
                new SpeechModelDescriptor(
                    "gpt-4o-mini-tts",
                    "GPT-4o mini TTS",
                    2_000,
                    ["marin"]),
            ]);

        public SpeechSynthesisRequest? Request { get; private set; }

        public string? ApiKey { get; private set; }

        public Exception? Failure { get; init; }

        public Task<SpeechSynthesisResult> SynthesizeAsync(
            SpeechSynthesisRequest request,
            string apiKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            ApiKey = apiKey;
            if (Failure is not null)
            {
                return Task.FromException<SpeechSynthesisResult>(Failure);
            }

            return Task.FromResult(new SpeechSynthesisResult(
                ProviderTestAudio.Wave(),
                "audio/wav",
                24_000,
                1,
                "provider-response"));
        }
    }

    private sealed class StaticCredentialStore(string? value) : ICredentialStore
    {
        public Task<string?> GetAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task SetAsync(string providerId, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
