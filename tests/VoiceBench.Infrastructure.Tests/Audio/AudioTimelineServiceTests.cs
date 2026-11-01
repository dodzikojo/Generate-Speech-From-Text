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
    public async Task Processing_keeps_a_previously_pinned_raw_clip_and_its_alignment_usable()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        var render = await AddAlignmentAsync(directory.Root, project.Segments[0].RenderHistory[0],
            [new("One", TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.3), 0, 0), new("two", TimeSpan.FromSeconds(.6), TimeSpan.FromSeconds(.9), 1, 1)], 2);
        await repository.SaveAsync(directory.Root, project with { Segments = [project.Segments[0] with { RenderHistory = [render] }] });
        var service = new AudioTimelineService(repository);
        var timeline = await service.GetAsync(directory.Root, AudioTimelineScope.Chapter, "chapter");
        await service.ApplyAsync(directory.Root, AudioTimelineScope.Chapter, "chapter", timeline.Revision,
            [new TimelineEdit { Operation = TimelineEditOperation.TrimClip, ClipId = timeline.Clips[0].ClipId, SourceStartSeconds = .6, DurationSeconds = .4 }]);
        string processed = "04-generated-audio/processed.wav";
        await File.WriteAllBytesAsync(Path.Combine(directory.Root, processed), [3, 4]);
        await repository.UpdateAsync(directory.Root, p => p with { Segments = [p.Segments[0] with { RenderHistory = [render with { ProcessedAudioRelativePath = processed, AudioSha256 = Hash([3, 4]) }] }] });
        var plan = await service.BuildPlanAsync(directory.Root, AudioTimelineScope.Chapter, "chapter");
        Assert.Equal(render.RawAudioRelativePath, Assert.Single(plan.Clips).Asset.RelativePath);
        Assert.Equal("two.", Assert.Single(plan.Transcript).Text);
        Assert.True((await service.ValidateAsync(directory.Root, AudioTimelineScope.Chapter, "chapter")).CanExport);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transcript_follows_audible_words_across_interleaved_or_leading_silence(bool interleaved)
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        var render = await AddAlignmentAsync(directory.Root, project.Segments[0].RenderHistory[0] with { Duration = TimeSpan.FromSeconds(4) },
            [new("One", TimeSpan.FromSeconds(interleaved ? .2 : 3), TimeSpan.FromSeconds(interleaved ? .4 : 3.2), 0, 0),
             new("two", TimeSpan.FromSeconds(3.4), TimeSpan.FromSeconds(3.6), 1, 1)], 2);
        await File.WriteAllTextAsync(Path.Combine(directory.Root, "02-scripts/middle.md"), "Middle.");
        await File.WriteAllTextAsync(Path.Combine(directory.Root, "03-render-copies/middle.txt"), "Middle.");
        await File.WriteAllBytesAsync(Path.Combine(directory.Root, "04-generated-audio/middle.wav"), [3, 4]);
        var middle = await AddAlignmentAsync(directory.Root, new RenderRecord
        {
            RawAudioRelativePath = "04-generated-audio/middle.wav", AudioSha256 = Hash([3, 4]), SourceSha256 = SourceTextHasher.Compute("Middle."),
            CleanSourceRelativePath = "03-render-copies/middle.txt", Duration = TimeSpan.FromSeconds(1), Status = RenderStatus.Approved,
        }, [new("Middle", TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.3), 0, 0)], 1);
        project = AudioTimelineRules.RegisterRenderAssets(project with
        {
            Segments = [project.Segments[0] with { RenderHistory = [render] }, new SegmentDocument { SegmentId = "middle", ChapterId = "chapter", ScriptRelativePath = "02-scripts/middle.md", SourceSha256 = middle.SourceSha256, ApprovedRenderId = middle.RenderId, RenderHistory = [middle] }],
            Chapters = [project.Chapters[0] with { SegmentOrder = ["segment", "middle"] }],
        });
        var timeline = AudioTimelineRules.GetOrCreate(project, AudioTimelineScope.Chapter, "chapter");
        timeline = timeline with { LayoutMode = TimelineLayoutMode.Manual, Clips = timeline.Clips.Select(c => c.SegmentId == "middle" ? c with { StartSeconds = 1 } : c).ToList() };
        await repository.SaveAsync(directory.Root, project with { AudioTimelines = [timeline] });
        var plan = await new AudioTimelineService(repository).BuildPlanAsync(directory.Root, AudioTimelineScope.Chapter, "chapter");
        Assert.Equal(interleaved ? ["One", "Middle.", "two."] : ["Middle.", "One two."], plan.Transcript.Select(t => t.Text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Source_word_markers_stay_unplaced_when_the_word_is_muted_or_duplicated(bool muted)
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        var render = await AddAlignmentAsync(directory.Root, project.Segments[0].RenderHistory[0],
            [new("One", TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.3), 0, 0), new("two", TimeSpan.FromSeconds(.6), TimeSpan.FromSeconds(.9), 1, 1)], 2);
        project = AudioTimelineRules.RegisterRenderAssets(project with { Segments = [project.Segments[0] with { RenderHistory = [render] }] });
        var timeline = AudioTimelineRules.GetOrCreate(project, AudioTimelineScope.Chapter, "chapter");
        var clip = timeline.Clips[0];
        timeline = timeline with
        {
            Clips = muted ? [clip with { Muted = true }] : [clip, clip with { ClipId = Guid.NewGuid(), StartSeconds = 2 }],
            Markers = [new TimelineMarker { Name = "Word cue", Anchor = new TimelineAnchor { Kind = TimelineAnchorKind.SourceWord, SegmentId = "segment", WordOrdinal = 1 } }],
        };
        await repository.SaveAsync(directory.Root, project with { AudioTimelines = [timeline] });
        var plan = await new AudioTimelineService(repository).BuildPlanAsync(directory.Root, AudioTimelineScope.Chapter, "chapter");
        Assert.False(Assert.Single(plan.Markers).Anchor.Placed);
    }

    [Fact]
    public async Task First_mix_for_a_processed_take_pins_the_processed_file_and_hash()
    {
        using TemporaryDirectory directory = new();
        var repository = new FileProjectRepository();
        var project = await NarratedProjectAsync(repository, directory.Root);
        const string processed = "04-generated-audio/processed.wav";
        await File.WriteAllBytesAsync(Path.Combine(directory.Root, processed), [3, 4]);
        var render = project.Segments[0].RenderHistory[0] with { ProcessedAudioRelativePath = processed, AudioSha256 = Hash([3, 4]) };
        await repository.SaveAsync(directory.Root, project with { Segments = [project.Segments[0] with { RenderHistory = [render] }] });
        var plan = await new AudioTimelineService(repository).BuildPlanAsync(directory.Root, AudioTimelineScope.Chapter, "chapter");
        Assert.Equal(processed, Assert.Single(plan.Clips).Asset.RelativePath);
        Assert.Equal(Hash([3, 4]), plan.Clips[0].Asset.Sha256);
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
    private static async Task<RenderRecord> AddAlignmentAsync(string root, RenderRecord render, List<WordTiming> words, int total)
    {
        var record = new WordAlignmentRecord { Status = WordAlignmentStatus.Aligned, AudioSha256 = render.AudioSha256, AudioRelativePath = render.RawAudioRelativePath, ArtifactRelativePath = $"03-render-copies/{render.RenderId:N}.alignment.json" };
        var document = new WordAlignmentDocument { AlignmentId = record.AlignmentId, RenderId = render.RenderId, AudioRelativePath = record.AudioRelativePath, AudioSha256 = render.AudioSha256, SourceSha256 = render.SourceSha256, SubmittedTextSha256 = render.SubmittedTextSha256, TotalWordCount = total, MatchedWordCount = words.Count, Words = words };
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, record.ArtifactRelativePath), JsonSerializer.Serialize(document, ProjectJson.SerializerOptions));
        return render with { AlignmentHistory = [record] };
    }
}
