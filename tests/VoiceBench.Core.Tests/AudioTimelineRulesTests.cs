using System.Text.Json;
using System.Text.Json.Nodes;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class AudioTimelineRulesTests
{
    private static readonly Guid Take = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Track = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Frame_positions_round_trip_without_floating_point_drift()
    {
        AudioClipDocument clip = new() { StartFrame = 345_678_901_234, DurationFrames = 93_441 };
        string json = JsonSerializer.Serialize(clip, ProjectJson.SerializerOptions);
        AudioClipDocument restored = JsonSerializer.Deserialize<AudioClipDocument>(json, ProjectJson.SerializerOptions)!;
        Assert.Equal(clip.StartFrame, restored.StartFrame);
        Assert.DoesNotContain("startSeconds", json, StringComparison.Ordinal);
        Assert.Equal(48_000, new AudioClipDocument { StartSeconds = 1.000000001 }.StartFrame);
    }

    [Fact]
    public void Migration_creates_pinned_assets_and_automatic_chapter_mix_without_changing_takes()
    {
        var original = Fixture();
        JsonObject json = JsonSerializer.SerializeToNode(original with { SchemaVersion = 4 }, ProjectJson.SerializerOptions)!.AsObject();
        json.Remove("audioAssets"); json.Remove("audioTimelines");
        var migrated = new ProjectDocumentMigrator([new FourToFiveProjectDocumentMigration()]).Deserialize(json);
        Assert.Equal(5, migrated.SchemaVersion);
        var asset = Assert.Single(migrated.AudioAssets);
        Assert.Equal(Take, asset.RenderId);
        Assert.Equal("audio-hash", asset.Sha256);
        Assert.Equal("04-generated-audio/take.wav", asset.RelativePath);
        var chapter = Assert.Single(migrated.AudioTimelines, t => t.Scope == AudioTimelineScope.Chapter);
        Assert.Equal(Take, Assert.Single(chapter.Clips).RenderId);
        Assert.Equal(JsonSerializer.Serialize(original.Segments[0].RenderHistory[0], ProjectJson.SerializerOptions), JsonSerializer.Serialize(migrated.Segments[0].RenderHistory[0], ProjectJson.SerializerOptions));
        Assert.Equal(4, json["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Manual_layout_never_places_new_segments_implicitly()
    {
        var project = Fixture();
        var timeline = AudioTimelineRules.GetOrCreate(project, AudioTimelineScope.Chapter, "chapter") with { LayoutMode = TimelineLayoutMode.Manual };
        project = project with { AudioTimelines = [timeline], Segments = [.. project.Segments, project.Segments[0] with { SegmentId = "new" }], Chapters = [project.Chapters[0] with { SegmentOrder = ["segment", "new"] }] };
        var refreshed = AudioTimelineRules.GetOrCreate(project, AudioTimelineScope.Chapter, "chapter");
        Assert.Single(refreshed.Clips);
        Assert.Contains("new", refreshed.PlaceholderSegmentIds);
    }

    [Fact]
    public void Narration_trim_requires_reliable_boundaries_and_derives_retained_words()
    {
        var project = Fixture();
        var timeline = Timeline(project);
        var alignment = Alignment();
        var clip = timeline.Clips[0];
        Assert.Throws<InvalidOperationException>(() => AudioTimelineRules.Apply(project, timeline,
            [new TimelineEdit { Operation = TimelineEditOperation.TrimClip, ClipId = clip.ClipId, SourceStartSeconds = .4, DurationSeconds = .6 }], new Dictionary<Guid, WordAlignmentDocument> { [Take] = alignment }));
        var trimmed = AudioTimelineRules.Apply(project, timeline,
            [new TimelineEdit { Operation = TimelineEditOperation.TrimClip, ClipId = clip.ClipId, SourceStartSeconds = .6, DurationSeconds = .4 }], new Dictionary<Guid, WordAlignmentDocument> { [Take] = alignment });
        Assert.Equal(new RetainedWordRange(1, 1), Assert.Single(trimmed.Clips[0].RetainedWordRanges));
        Assert.Equal(TimelineLayoutMode.Manual, trimmed.LayoutMode);
    }

    [Fact]
    public void Partial_alignment_gaps_are_speech_and_cannot_be_cut_or_overlapped()
    {
        var alignment = Alignment() with { TotalWordCount = 3, MatchedWordCount = 2, Coverage = 2d / 3, Words = [Alignment().Words[0], Alignment().Words[1] with { NarrationWordOrdinal = 2 }] };
        Assert.False(AudioTimelineRules.CanCutAt(alignment, .3, 1));
        var project = Fixture();
        var timeline = Timeline(project);
        var duplicate = timeline.Clips[0] with { ClipId = Guid.NewGuid(), StartSeconds = .31, SourceStartSeconds = 0, DurationSeconds = .29 };
        var issues = AudioTimelineRules.Validate(project, timeline with { Clips = [.. timeline.Clips, duplicate] }, new Dictionary<Guid, WordAlignmentDocument> { [Take] = alignment }, false);
        Assert.Contains(issues.Issues, i => i.Code == "timeline.spoken-overlap");
    }

    [Fact]
    public void Fully_aligned_verified_silence_can_overlap_but_speech_cannot()
    {
        var project = Fixture();
        var timeline = Timeline(project);
        var clip = timeline.Clips[0] with { DurationSeconds = .6 };
        var tail = clip with { ClipId = Guid.NewGuid(), StartSeconds = .3, SourceStartSeconds = .6, DurationSeconds = .4 };
        var alignments = new Dictionary<Guid, WordAlignmentDocument> { [Take] = Alignment() };
        Assert.DoesNotContain(AudioTimelineRules.Validate(project, timeline with { Clips = [clip, tail] }, alignments, false).Issues, i => i.Code == "timeline.spoken-overlap");
        Assert.Contains(AudioTimelineRules.Validate(project, timeline with { Clips = [clip, tail with { StartSeconds = .2 }] }, alignments, false).Issues, i => i.Code == "timeline.spoken-overlap");
    }

    [Fact]
    public void Asset_or_take_hash_mismatch_is_rejected_even_when_text_is_current()
    {
        var project = Fixture();
        var timeline = Timeline(project);
        var result = AudioTimelineRules.Validate(project, timeline with { Clips = [timeline.Clips[0] with { AssetSha256 = "changed" }] }, null, false);
        Assert.Contains(result.Issues, issue => issue.Code == "timeline.asset-pin-mismatch");
    }

    [Fact]
    public void Replacement_requires_review_and_matching_source_before_cut_transfer()
    {
        var project = Fixture();
        var timeline = Timeline(project);
        var replacement = project.AudioAssets[0] with { AssetId = Guid.NewGuid(), RenderId = Guid.NewGuid(), Sha256 = "replacement" };
        project = project with { AudioAssets = [.. project.AudioAssets, replacement] };
        var proposal = new TakeReplacementProposal { ClipId = timeline.Clips[0].ClipId, ProposedAssetId = replacement.AssetId, ProposedRenderId = replacement.RenderId!.Value };
        timeline = timeline with { ReplacementProposals = [proposal] };
        Assert.Throws<InvalidOperationException>(() => AudioTimelineRules.Apply(project, timeline, [new TimelineEdit { Operation = TimelineEditOperation.AcceptReplacement, ProposalId = proposal.ProposalId }]));
    }

    [Fact]
    public void Reviewed_replacement_maps_cuts_by_source_word_instead_of_old_audio_time()
    {
        var project = Fixture();
        var timeline = Timeline(project);
        var clip = timeline.Clips[0] with { SourceStartSeconds = .6, DurationSeconds = .4, RetainedWordRanges = [new(1, 1)] };
        var replacement = project.AudioAssets[0] with { AssetId = Guid.NewGuid(), RenderId = Guid.NewGuid(), Sha256 = "replacement", DurationSeconds = 2 };
        project = project with { AudioAssets = [.. project.AudioAssets, replacement] };
        var proposal = new TakeReplacementProposal { ClipId = clip.ClipId, ProposedAssetId = replacement.AssetId, ProposedRenderId = replacement.RenderId!.Value, CanTransferCuts = true };
        timeline = timeline with { Clips = [clip], ReplacementProposals = [proposal] };
        var mapped = Alignment() with { RenderId = replacement.RenderId.Value, AudioSha256 = replacement.Sha256, Words = Alignment().Words.Select(w => w with { Start = w.Start * 2, End = w.End * 2 }).ToList() };
        var replaced = AudioTimelineRules.Apply(project, timeline,
            [new TimelineEdit { Operation = TimelineEditOperation.AcceptReplacement, ProposalId = proposal.ProposalId, ConfirmedReview = true }],
            new Dictionary<Guid, WordAlignmentDocument> { [Take] = Alignment(), [replacement.RenderId.Value] = mapped });
        Assert.Equal(1.2, replaced.Clips[0].SourceStartSeconds);
        Assert.Equal(.8, replaced.Clips[0].DurationSeconds);
        Assert.Equal(new RetainedWordRange(1, 1), Assert.Single(replaced.Clips[0].RetainedWordRanges));
    }

    [Fact]
    public void Rejected_take_proposal_does_not_reappear_on_read()
    {
        var project = Fixture();
        var timeline = Timeline(project);
        var render = project.Segments[0].RenderHistory[0] with { RenderId = Guid.NewGuid(), AudioSha256 = "new" };
        project = AudioTimelineRules.RegisterRenderAssets(project with { AudioTimelines = [timeline], Segments = [project.Segments[0] with { ApprovedRenderId = render.RenderId, RenderHistory = [.. project.Segments[0].RenderHistory, render] }] });
        timeline = AudioTimelineRules.GetOrCreate(project, AudioTimelineScope.Chapter, "chapter");
        Guid proposal = Assert.Single(timeline.ReplacementProposals).ProposalId;
        timeline = AudioTimelineRules.Apply(project, timeline, [new TimelineEdit { Operation = TimelineEditOperation.RejectReplacement, ProposalId = proposal }]);
        Assert.Empty(AudioTimelineRules.GetOrCreate(project with { AudioTimelines = [timeline] }, AudioTimelineScope.Chapter, "chapter").ReplacementProposals);
    }

    private static VoiceBenchProjectDocument Fixture()
    {
        var render = new RenderRecord { RenderId = Take, Status = RenderStatus.Approved, AudioSha256 = "audio-hash", RawAudioRelativePath = "04-generated-audio/take.wav", SourceSha256 = "source-hash", CleanSourceRelativePath = "03-render-copies/take.clean.md", Duration = TimeSpan.FromSeconds(1), SampleRate = 48_000, Channels = 1 };
        return new VoiceBenchProjectDocument
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", SegmentOrder = ["segment"] }],
            Segments = [new SegmentDocument { SegmentId = "segment", ChapterId = "chapter", ApprovedRenderId = Take, SourceSha256 = "source-hash", RenderHistory = [render] }],
            AudioAssets = [new AudioAssetRecord { AssetId = Take, RenderId = Take, SegmentId = "segment", Origin = AudioAssetOrigin.GeneratedTake, RelativePath = render.RawAudioRelativePath, Sha256 = render.AudioSha256, SourceSha256 = render.SourceSha256, CleanSourceRelativePath = render.CleanSourceRelativePath, DurationSeconds = 1 }],
        };
    }

    private static AudioTimelineDocument Timeline(VoiceBenchProjectDocument project) => AudioTimelineRules.GetOrCreate(project, AudioTimelineScope.Chapter, "chapter");
    private static WordAlignmentDocument Alignment() => new()
    {
        RenderId = Take, AudioSha256 = "audio-hash", SourceSha256 = "source-hash", TotalWordCount = 2, MatchedWordCount = 2, Coverage = 1,
        Words = [new WordTiming("one", TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(.3), 0, 0), new WordTiming("two", TimeSpan.FromSeconds(.6), TimeSpan.FromSeconds(.9), 1, 1)],
    };
}
