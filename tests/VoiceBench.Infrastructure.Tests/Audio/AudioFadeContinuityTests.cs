using IssueLab.VoiceBench.Audio;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Audio;

public sealed class AudioFadeContinuityTests
{
    [Fact]
    public async Task ParameterUpdatePlanDoesNotOpenVerifiedMediaButNormalPreviewStillVerifiesIt()
    {
        using TemporaryDirectory dir = new();
        var (repository, _, _) = await FixtureAsync(dir.Root);
        var service = new AudioTimelineService(repository);
        var verified = await service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation);
        string path = VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, verified.Clips[0].Asset.RelativePath);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var parameters = await service.BuildParameterUpdatePlanAsync(dir.Root, AudioTimelineScope.Compilation);
        Assert.Equal(verified.Clips[0].Asset, parameters.Clips[0].Asset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation));
    }
    [Theory]
    [InlineData(240)]
    [InlineData(840)]
    public async Task SplitInsideFadePreservesEveryMixedSampleIncludingVolumeEnvelope(int splitFrame)
    {
        using TemporaryDirectory dir = new();
        var (repository, assets, original) = await FixtureAsync(dir.Root);
        var service = new AudioTimelineService(repository);
        var originalPlan = await service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation);
        float[] expected = await RenderAsync(dir.Root, assets, originalPlan);
        await service.ApplyAsync(dir.Root, AudioTimelineScope.Compilation, null, 0,
            [new TimelineEdit { Operation = TimelineEditOperation.SplitClip, ClipId = original.ClipId, SplitSeconds = TimelineTime.ToSeconds(splitFrame) }]);
        var splitPlan = await service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation);
        Assert.Equal(expected, await RenderAsync(dir.Root, assets, splitPlan));
    }

    [Fact]
    public async Task TrimPreservesEveryRetainedFadeAndEnvelopeSample()
    {
        using TemporaryDirectory dir = new();
        var (repository, assets, original) = await FixtureAsync(dir.Root);
        var service = new AudioTimelineService(repository);
        float[] expected = await RenderAsync(dir.Root, assets, await service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation));
        await service.ApplyAsync(dir.Root, AudioTimelineScope.Compilation, null, 0,
            [new TimelineEdit { Operation = TimelineEditOperation.TrimClip, ClipId = original.ClipId,
                StartSeconds = TimelineTime.ToSeconds(120), SourceStartSeconds = TimelineTime.ToSeconds(120), DurationSeconds = TimelineTime.ToSeconds(720) }]);
        var plan = await service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation);
        float[] actual = await RenderAsync(dir.Root, assets, plan);
        Assert.Equal(expected.AsSpan(240, 1440).ToArray(), actual.AsSpan(240, 1440).ToArray());
    }

    private static async Task<(FileProjectRepository Repository, AudioAssetService Assets, AudioClipDocument Clip)> FixtureAsync(string root)
    {
        var repository = new FileProjectRepository(); await repository.CreateAsync(root, "Fade continuity");
        var assets = new AudioAssetService(repository, new AudioAssetServiceTests.AudioProcessFake(), AudioAssetServiceTests.Tools);
        string source = Path.Combine(root, "source.wav"); await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var asset = await assets.ImportAsync(root, source);
        var track = new AudioTrackDocument { Kind = AudioTrackKind.Effects };
        var clip = new AudioClipDocument { TrackId = track.TrackId, AssetId = asset.AssetId, AssetSha256 = asset.Sha256,
            DurationFrames = 960, FadeInSeconds = .015, FadeOutSeconds = .005,
            Envelope = [new(0, -12), new(.01, 0), new(.02, -6)] };
        var project = await repository.LoadAsync(root);
        await repository.SaveAsync(root, project with { AudioTimelines = [new AudioTimelineDocument { Scope = AudioTimelineScope.Compilation, Tracks = [track], Clips = [clip] }] });
        return (repository, assets, clip);
    }
    private static async Task<float[]> RenderAsync(string root, AudioAssetService assets, MixRenderPlan plan)
    {
        using AudioGraph graph = await new AudioGraphFactory(assets).CreateAsync(root, plan);
        float[] samples = new float[plan.DurationFrames * 2];
        graph.Render(0, samples);
        return samples;
    }
}
