using System.Security.Cryptography;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Audio;

public sealed class AudioTimelineServiceTests
{
    [Fact]
    public async Task Batch_is_one_undoable_command_and_stale_revision_is_rejected()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        await repository.CreateAsync(directory.Root, "Timeline");
        var transactions = new FileAuthoringTransactionManager();
        var service = new AudioTimelineService(repository, transactions);
        var first = await service.GetAsync(directory.Root, AudioTimelineScope.Compilation);
        Guid trackId = Guid.NewGuid();
        var changed = await service.ApplyAsync(directory.Root, AudioTimelineScope.Compilation, null, first.Revision,
            [new TimelineEdit { Operation = TimelineEditOperation.AddTrack, Track = new AudioTrackDocument { TrackId = trackId, Name = "Music", Kind = AudioTrackKind.Music } },
             new TimelineEdit { Operation = TimelineEditOperation.SetEndMarker, DurationSeconds = 10 }]);
        Assert.Equal(1, changed.Revision);
        Assert.Single((await transactions.GetStateAsync(directory.Root)).Transactions);
        await Assert.ThrowsAsync<TimelineRevisionConflictException>(() => service.ApplyAsync(directory.Root, AudioTimelineScope.Compilation, null, 0, [new TimelineEdit { Operation = TimelineEditOperation.SetEndMarker, DurationSeconds = 20 }]));
        Assert.Equal(10, (await service.GetAsync(directory.Root, AudioTimelineScope.Compilation)).EndMarkerSeconds);
        await transactions.UndoAsync(directory.Root);
        Assert.Empty((await service.GetAsync(directory.Root, AudioTimelineScope.Compilation)).Tracks);
        Assert.True((await service.GetAsync(directory.Root, AudioTimelineScope.Compilation)).Revision > changed.Revision);
        await Assert.ThrowsAsync<TimelineRevisionConflictException>(() => service.ApplyAsync(directory.Root, AudioTimelineScope.Compilation, null, 0, [new TimelineEdit { Operation = TimelineEditOperation.SetEndMarker, DurationSeconds = 20 }]));
        await transactions.RedoAsync(directory.Root);
        Assert.Single((await service.GetAsync(directory.Root, AudioTimelineScope.Compilation)).Tracks);
    }

    [Fact]
    public async Task Invalid_batch_rolls_back_all_edits_and_does_not_add_history()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        await repository.CreateAsync(directory.Root, "Timeline");
        var transactions = new FileAuthoringTransactionManager();
        var service = new AudioTimelineService(repository, transactions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(directory.Root, AudioTimelineScope.Compilation, null, 0,
            [new TimelineEdit { Operation = TimelineEditOperation.AddTrack, Track = new AudioTrackDocument { Name = "Music", Kind = AudioTrackKind.Music } },
             new TimelineEdit { Operation = TimelineEditOperation.MoveClip, ClipId = Guid.NewGuid(), StartSeconds = 10 }]));
        Assert.Empty((await service.GetAsync(directory.Root, AudioTimelineScope.Compilation)).Tracks);
        Assert.Empty((await transactions.GetStateAsync(directory.Root)).Transactions);
    }

    [Fact]
    public async Task Changed_asset_bytes_and_changed_narration_block_export_but_cue_only_edits_do_not()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        var service = new AudioTimelineService(repository);
        Assert.True((await service.ValidateAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).CanExport);
        await File.WriteAllTextAsync(Path.Combine(directory.Root, "02-scripts/script.md"), "@visual: Show the screen.\nOne two.");
        Assert.True((await service.ValidateAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).CanExport);
        await File.WriteAllTextAsync(Path.Combine(directory.Root, "02-scripts/script.md"), "Different narration.");
        Assert.Contains((await service.ValidateAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).Issues, i => i.Code == "timeline.source-changed");
        await File.WriteAllBytesAsync(Path.Combine(directory.Root, project.Segments[0].RenderHistory[0].RawAudioRelativePath), [9, 9]);
        Assert.Contains((await service.ValidateAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).Issues, i => i.Code == "timeline.asset-changed");
    }

    [Fact]
    public async Task Plan_freezes_ordered_chapters_pauses_and_continuous_overlay()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        var segment2 = project.Segments[0] with { SegmentId = "second", ChapterId = "chapter2" };
        var render2 = segment2.RenderHistory[0] with { RenderId = Guid.NewGuid() };
        segment2 = segment2 with { ApprovedRenderId = render2.RenderId, RenderHistory = [render2] };
        var music = new AudioAssetRecord { RelativePath = "04-generated-audio/music.wav", Sha256 = Hash([3, 4]), DurationSeconds = 10, SampleRate = 48_000, Channels = 2 };
        await File.WriteAllBytesAsync(Path.Combine(directory.Root, music.RelativePath), [3, 4]);
        var track = new AudioTrackDocument { Kind = AudioTrackKind.Music, Name = "Music" };
        var compilation = new AudioTimelineDocument { Scope = AudioTimelineScope.Compilation, Tracks = [track], Clips = [new AudioClipDocument { TrackId = track.TrackId, AssetId = music.AssetId, AssetSha256 = music.Sha256, DurationSeconds = 3, Anchor = new TimelineAnchor { Kind = TimelineAnchorKind.ProjectTime } }] };
        project = project with { ChapterOrder = ["chapter", "chapter2"], Chapters = [.. project.Chapters, new ChapterDocument { ChapterId = "chapter2", SegmentOrder = ["second"] }], Segments = [.. project.Segments, segment2], AudioAssets = [music], AudioTimelines = [compilation] };
        await repository.SaveAsync(directory.Root, project);
        var service = new AudioTimelineService(repository);
        var plan = await service.BuildPlanAsync(directory.Root, AudioTimelineScope.Compilation);
        Assert.Equal(3, plan.Clips.Count);
        Assert.Equal(3, plan.DurationSeconds);
        Assert.Equal([0d, 2d], plan.Clips.Where(c => c.Track.Kind == AudioTrackKind.Narration).Select(c => c.StartSeconds));
        Assert.Equal(["One two.", "One two."], plan.Transcript.Select(e => e.Text));
        await repository.SaveAsync(directory.Root, project with { ChapterOrder = ["chapter2", "chapter"] });
        Assert.Equal("segment", plan.Transcript[0].SegmentId);
    }

    [Fact]
    public async Task Undo_preserves_assets_registered_after_the_authoring_command()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        await repository.CreateAsync(directory.Root, "Timeline");
        var transactions = new FileAuthoringTransactionManager();
        var service = new AudioTimelineService(repository, transactions);
        await service.ApplyAsync(directory.Root, AudioTimelineScope.Compilation, null, 0, [new TimelineEdit { Operation = TimelineEditOperation.SetEndMarker, DurationSeconds = 5 }]);
        var asset = new AudioAssetRecord { RelativePath = "04-generated-audio/added.wav", Sha256 = "sha" };
        await repository.UpdateAsync(directory.Root, p => p with { AudioAssets = [asset] });
        await transactions.UndoAsync(directory.Root);
        Assert.Equal(asset.AssetId, Assert.Single((await repository.LoadAsync(directory.Root)).AudioAssets).AssetId);
    }

    [Fact]
    public async Task Migration_retains_exact_original_manifest_backup()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await repository.CreateAsync(directory.Root, "Old");
        string json = JsonSerializer.Serialize(project with { SchemaVersion = 4 }, ProjectJson.SerializerOptions);
        string manifest = Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName);
        await File.WriteAllTextAsync(manifest, json);
        Assert.Equal(5, (await repository.LoadAsync(directory.Root)).SchemaVersion);
        Assert.Equal(json, await File.ReadAllTextAsync(manifest + ".schema-4.bak"));
    }

    [Fact]
    public async Task Concurrent_service_instances_allow_only_one_revision_winner()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        await repository.CreateAsync(directory.Root, "Concurrent");
        var first = new AudioTimelineService(repository);
        var second = new AudioTimelineService(new FileProjectRepository());
        async Task<bool> Attempt(AudioTimelineService service, double end)
        {
            try { await service.ApplyAsync(directory.Root, AudioTimelineScope.Compilation, null, 0, [new TimelineEdit { Operation = TimelineEditOperation.SetEndMarker, DurationSeconds = end }]); return true; }
            catch (TimelineRevisionConflictException) { return false; }
        }
        bool[] results = await Task.WhenAll(Attempt(first, 1), Attempt(second, 2));
        Assert.Single(results, succeeded => succeeded);
        Assert.Single((await new FileAuthoringTransactionManager().GetStateAsync(directory.Root)).Transactions);
    }

    [Fact]
    public async Task Stale_unapproved_take_still_has_a_preview_graph_but_cannot_export()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        project = project with { Segments = [project.Segments[0] with { RenderHistory = [project.Segments[0].RenderHistory[0] with { Status = RenderStatus.Stale }] }] };
        await repository.SaveAsync(directory.Root, project);
        var service = new AudioTimelineService(repository);
        Assert.Single((await service.BuildPreviewPlanAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).Clips);
        Assert.Contains((await service.ValidateAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).Issues, i => i.Code == "timeline.take-unapproved");
    }

    [Fact]
    public async Task Old_schema_transaction_snapshots_are_migrated_when_restored()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await repository.CreateAsync(directory.Root, "Before");
        string manifest = Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(project with { SchemaVersion = 4 }, ProjectJson.SerializerOptions));
        var transactions = new FileAuthoringTransactionManager();
        await transactions.ExecuteAsync(directory.Root, "Rename legacy project", async token =>
        {
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(project with { SchemaVersion = 4, Name = "After" }, ProjectJson.SerializerOptions), token);
        });
        await repository.LoadAsync(directory.Root);
        await transactions.UndoAsync(directory.Root);
        var restored = await repository.LoadAsync(directory.Root);
        Assert.Equal(5, restored.SchemaVersion);
        Assert.Equal("Before", restored.Name);
        await transactions.RedoAsync(directory.Root);
        Assert.Equal("After", (await repository.LoadAsync(directory.Root)).Name);
    }

    private static async Task<VoiceBenchProjectDocument> NarratedProjectAsync(FileProjectRepository repository, string root)
    {
        var project = await repository.CreateAsync(root, "Narration");
        string text = "One two.";
        await File.WriteAllTextAsync(Path.Combine(root, "02-scripts/script.md"), text);
        await File.WriteAllTextAsync(Path.Combine(root, "03-render-copies/take.clean.md"), text);
        await File.WriteAllBytesAsync(Path.Combine(root, "04-generated-audio/take.wav"), [1, 2]);
        var render = new RenderRecord { RawAudioRelativePath = "04-generated-audio/take.wav", AudioSha256 = Hash([1, 2]), SourceSha256 = SourceTextHasher.Compute(text), CleanSourceRelativePath = "03-render-copies/take.clean.md", Duration = TimeSpan.FromSeconds(1), Status = RenderStatus.Approved, SampleRate = 48_000, Channels = 1 };
        project = project with { ChapterOrder = ["chapter"], Chapters = [new ChapterDocument { ChapterId = "chapter", SegmentOrder = ["segment"] }], Segments = [new SegmentDocument { SegmentId = "segment", ChapterId = "chapter", ScriptRelativePath = "02-scripts/script.md", SourceSha256 = render.SourceSha256, ApprovedRenderId = render.RenderId, RenderHistory = [render] }] };
        await repository.SaveAsync(root, project);
        return project;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
