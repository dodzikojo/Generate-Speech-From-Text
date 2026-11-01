using System.Security.Cryptography;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Audio;

public sealed class AudioLoopExtentTests
{
    [Theory]
    [InlineData(AudioTimelineScope.Chapter)]
    [InlineData(AudioTimelineScope.Compilation)]
    public async Task LoopBedsDoNotDefineContentEndAndAreClampedToResolvedScope(AudioTimelineScope scope)
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository();
        var project = await SeedAsync(repository, dir.Root, scope, true, null);
        var service = new AudioTimelineService(repository);
        var plan = await service.BuildPreviewPlanAsync(dir.Root, scope, scope == AudioTimelineScope.Chapter ? "chapter" : null);
        Assert.Equal(1, plan.DurationSeconds);
        Assert.Equal(1, Assert.Single(plan.Clips, c => c.Loop).DurationSeconds);
        Assert.Equal(9, Assert.Single(plan.Clips, c => c.Loop).FadeOutOffsetSeconds);
        if (scope == AudioTimelineScope.Chapter)
            Assert.Equal(1, (await service.BuildPreviewPlanAsync(dir.Root, AudioTimelineScope.Compilation)).DurationSeconds);
        var timeline = Assert.Single(project.AudioTimelines);
        var shorterLoop = timeline.Clips.Single(c => c.Loop) with { StartSeconds = .5, DurationSeconds = .2 };
        await repository.SaveAsync(dir.Root, project with { AudioTimelines = [timeline with { Clips = [.. timeline.Clips.Where(c => !c.Loop), shorterLoop] }] });
        var shortPlan = await service.BuildPreviewPlanAsync(dir.Root, scope, scope == AudioTimelineScope.Chapter ? "chapter" : null);
        Assert.Equal(.2, Assert.Single(shortPlan.Clips, c => c.Loop).DurationSeconds);
        Assert.Equal(1, shortPlan.DurationSeconds);
    }

    [Theory]
    [InlineData(AudioTimelineScope.Chapter)]
    [InlineData(AudioTimelineScope.Compilation)]
    public async Task AllLoopMixRequiresExplicitEndInsteadOfInventingDuration(AudioTimelineScope scope)
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository();
        var project = await SeedAsync(repository, dir.Root, scope, false, null);
        var service = new AudioTimelineService(repository);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildPreviewPlanAsync(dir.Root, scope, scope == AudioTimelineScope.Chapter ? "chapter" : null));
        Assert.Contains("end marker", failure.Message);
        await repository.SaveAsync(dir.Root, project with { AudioTimelines = [project.AudioTimelines[0] with { EndMarkerSeconds = 2 }] });
        var plan = await service.BuildPreviewPlanAsync(dir.Root, scope, scope == AudioTimelineScope.Chapter ? "chapter" : null);
        Assert.Equal(2, plan.DurationSeconds); Assert.Equal(2, Assert.Single(plan.Clips).DurationSeconds);
    }

    private static async Task<VoiceBenchProjectDocument> SeedAsync(FileProjectRepository repository, string root, AudioTimelineScope scope, bool content, double? end)
    {
        var project = await repository.CreateAsync(root, "Loop extent");
        string relative = "01-source/bed.wav"; await File.WriteAllBytesAsync(Path.Combine(root, relative), [1, 2]);
        var asset = new AudioAssetRecord { RelativePath = relative, Sha256 = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2 })), DurationSeconds = 4, SampleRate = 48000, Channels = 2 };
        var track = new AudioTrackDocument { Kind = AudioTrackKind.Music };
        var loop = new AudioClipDocument { TrackId = track.TrackId, AssetId = asset.AssetId, AssetSha256 = asset.Sha256, Loop = true, DurationSeconds = 10, FadeOutSeconds = 1 };
        var timeline = new AudioTimelineDocument { Scope = scope, ChapterId = scope == AudioTimelineScope.Chapter ? "chapter" : null,
            EndMarkerSeconds = end, Tracks = [track], Clips = content ? [loop, loop with { ClipId = Guid.NewGuid(), Loop = false, DurationSeconds = 1, FadeOutSeconds = 0 }] : [loop] };
        project = project with { AudioAssets = [asset], AudioTimelines = [timeline],
            Chapters = scope == AudioTimelineScope.Chapter ? [new ChapterDocument { ChapterId = "chapter" }] : [],
            ChapterOrder = scope == AudioTimelineScope.Chapter ? ["chapter"] : [] };
        await repository.SaveAsync(root, project); return project;
    }
}
