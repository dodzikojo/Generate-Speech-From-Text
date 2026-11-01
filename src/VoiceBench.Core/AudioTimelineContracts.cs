using System.Text.Json.Serialization;

namespace IssueLab.VoiceBench.Core;

public static class TimelineTime
{
    public const int SampleRate = 48_000;
    public static long ToFrames(double seconds) => double.IsFinite(seconds)
        ? checked((long)Math.Round(seconds * SampleRate, MidpointRounding.AwayFromZero))
        : throw new ArgumentOutOfRangeException(nameof(seconds));
    public static double ToSeconds(long frames) => frames / (double)SampleRate;
}

public enum AudioTimelineScope { Chapter, Compilation }
public enum AudioTrackKind { Narration, Music, Effects }
public enum AudioAssetOrigin { GeneratedTake, ImportedRecording, ImportedMedia }
public enum TimelineLayoutMode { Automatic, Manual }
public enum TimelineAnchorKind { ProjectTime, ChapterStart, SourceWord }
public enum TimelineEditOperation
{
    AddTrack, UpdateTrack, DeleteTrack, ReorderTrack, AddClip, UpdateClip, DeleteClip,
    MoveClip, TrimClip, SplitClip, DuplicateClip, CloseGap, SetEnvelope,
    SetMarker, DeleteMarker, SetEndMarker, OmitSegment, IncludeSegment,
    SetLayoutMode, AcceptReplacement, RejectReplacement,
}

/// <summary>Immutable provenance. A changed file is a new asset, never an update to this record.</summary>
public sealed record AudioAssetRecord
{
    public Guid AssetId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public AudioAssetOrigin Origin { get; init; } = AudioAssetOrigin.ImportedMedia;
    public string? SegmentId { get; init; }
    public Guid? RenderId { get; init; }
    public string SourceSha256 { get; init; } = string.Empty;
    public string? CleanSourceRelativePath { get; init; }
    public string? AuthoringSourceRelativePath { get; init; }
    public string AuthoringSourceSha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record AudioTimelineDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid TimelineId { get; init; } = Guid.NewGuid();
    public AudioTimelineScope Scope { get; init; }
    public string? ChapterId { get; init; }
    public long Revision { get; init; }
    public TimelineLayoutMode LayoutMode { get; init; } = TimelineLayoutMode.Automatic;
    public List<AudioTrackDocument> Tracks { get; init; } = [];
    public List<AudioClipDocument> Clips { get; init; } = [];
    public List<TimelineMarker> Markers { get; init; } = [];
    public List<string> OmittedSegmentIds { get; init; } = [];
    public List<string> PlaceholderSegmentIds { get; init; } = [];
    public List<TakeReplacementProposal> ReplacementProposals { get; init; } = [];
    public List<Guid> RejectedReplacementProposalIds { get; init; } = [];
    public long? EndMarkerFrame { get; init; }
    [JsonIgnore]
    public double? EndMarkerSeconds { get => EndMarkerFrame is long frame ? TimelineTime.ToSeconds(frame) : null; init => EndMarkerFrame = value is double seconds ? TimelineTime.ToFrames(seconds) : null; }
}

public sealed record AudioTrackDocument
{
    public Guid TrackId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public AudioTrackKind Kind { get; init; }
    public int Order { get; init; }
    public bool Locked { get; init; }
    public bool Muted { get; init; }
    /// <summary>Monitoring only; exports always ignore solo.</summary>
    public bool Solo { get; init; }
    public double GainDb { get; init; }
    public double Pan { get; init; }
    public bool DuckUnderNarration { get; init; }
    public double DuckDepthDb { get; init; } = 8;
    public double DuckAttackSeconds { get; init; } = .05;
    public double DuckReleaseSeconds { get; init; } = .3;
}

public sealed record AudioClipDocument
{
    public Guid ClipId { get; init; } = Guid.NewGuid();
    public Guid TrackId { get; init; }
    public Guid AssetId { get; init; }
    public string AssetSha256 { get; init; } = string.Empty;
    public Guid? RenderId { get; init; }
    public string? SegmentId { get; init; }
    public string SourceSha256 { get; init; } = string.Empty;
    public string? CleanSourceRelativePath { get; init; }
    public string Name { get; init; } = string.Empty;
    public long StartFrame { get; init; }
    public long SourceStartFrame { get; init; }
    public long DurationFrames { get; init; }
    [JsonIgnore]
    public double StartSeconds { get => TimelineTime.ToSeconds(StartFrame); init => StartFrame = TimelineTime.ToFrames(value); }
    [JsonIgnore]
    public double SourceStartSeconds { get => TimelineTime.ToSeconds(SourceStartFrame); init => SourceStartFrame = TimelineTime.ToFrames(value); }
    [JsonIgnore]
    public double DurationSeconds { get => TimelineTime.ToSeconds(DurationFrames); init => DurationFrames = TimelineTime.ToFrames(value); }
    public double GainDb { get; init; }
    public double Pan { get; init; }
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    public bool Muted { get; init; }
    /// <summary>Explicit repeat at original speed; no time stretch.</summary>
    public bool Loop { get; init; }
    public TimelineAnchor Anchor { get; init; } = new();
    public List<VolumeEnvelopePoint> Envelope { get; init; } = [];
    public List<RetainedWordRange> RetainedWordRanges { get; init; } = [];
}

public sealed record TimelineAnchor
{
    public TimelineAnchorKind Kind { get; init; }
    public string? ChapterId { get; init; }
    public string? SegmentId { get; init; }
    public int? WordOrdinal { get; init; }
    public double OffsetSeconds { get; init; }
    public bool Placed { get; init; } = true;
}

public sealed record VolumeEnvelopePoint(double OffsetSeconds, double GainDb);
public sealed record RetainedWordRange(int FirstWordOrdinal, int LastWordOrdinal);
public sealed record TimelineMarker
{
    public Guid MarkerId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public double PositionSeconds { get; init; }
    public TimelineAnchor Anchor { get; init; } = new();
}

public sealed record MixOutputStandard
{
    public static MixOutputStandard Default { get; } = new();
    public int SampleRate { get; init; } = 48_000;
    public int Channels { get; init; } = 2;
    public int PcmBitsPerSample { get; init; } = 24;
    public double IntegratedLufs { get; init; } = -16;
    public double TruePeakDbtp { get; init; } = -1;
}

/// <summary>Typed, JSON-friendly command. Only fields used by Operation are read.</summary>
public sealed record TimelineEdit
{
    public TimelineEditOperation Operation { get; init; }
    public Guid? TrackId { get; init; }
    public Guid? ClipId { get; init; }
    public Guid? NewClipId { get; init; }
    public Guid? MarkerId { get; init; }
    public Guid? ProposalId { get; init; }
    public string? SegmentId { get; init; }
    public AudioTrackDocument? Track { get; init; }
    public AudioClipDocument? Clip { get; init; }
    public TimelineMarker? Marker { get; init; }
    public int? Order { get; init; }
    public double? StartSeconds { get; init; }
    public double? SourceStartSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public double? SplitSeconds { get; init; }
    public List<VolumeEnvelopePoint>? Envelope { get; init; }
    public TimelineLayoutMode? LayoutMode { get; init; }
    public bool ConfirmedReview { get; init; }
}

public sealed record TakeReplacementProposal
{
    public Guid ProposalId { get; init; } = Guid.NewGuid();
    public Guid ClipId { get; init; }
    public Guid CurrentRenderId { get; init; }
    public Guid ProposedAssetId { get; init; }
    public Guid ProposedRenderId { get; init; }
    public bool CanTransferCuts { get; init; }
    public string? Reason { get; init; }
}

public sealed record MixEligibilityIssue(string Code, string Message, Guid? ClipId = null, string? SegmentId = null);
public sealed record MixEligibilityResult(IReadOnlyList<MixEligibilityIssue> Issues)
{
    public bool CanExport => Issues.Count == 0;
}

/// <summary>Frozen graph; no project state is consulted while rendering it.</summary>
public sealed record MixRenderPlan
{
    public Guid PlanId { get; init; } = Guid.NewGuid();
    public Guid ProjectId { get; init; }
    public AudioTimelineScope Scope { get; init; }
    public string? ChapterId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public MixOutputStandard OutputStandard { get; init; } = MixOutputStandard.Default;
    public IReadOnlyList<MixRenderClip> Clips { get; init; } = Array.Empty<MixRenderClip>();
    public IReadOnlyList<TimelineMarker> Markers { get; init; } = Array.Empty<TimelineMarker>();
    public IReadOnlyList<MixTranscriptEntry> Transcript { get; init; } = Array.Empty<MixTranscriptEntry>();
    public long DurationFrames { get; init; }
    [JsonIgnore]
    public double DurationSeconds { get => TimelineTime.ToSeconds(DurationFrames); init => DurationFrames = TimelineTime.ToFrames(value); }
}

public sealed record MixRenderClip
{
    public Guid ClipId { get; init; }
    public AudioAssetRecord Asset { get; init; } = new();
    public AudioTrackDocument Track { get; init; } = new();
    public long StartFrame { get; init; }
    public long SourceStartFrame { get; init; }
    public long DurationFrames { get; init; }
    [JsonIgnore]
    public double StartSeconds { get => TimelineTime.ToSeconds(StartFrame); init => StartFrame = TimelineTime.ToFrames(value); }
    [JsonIgnore]
    public double SourceStartSeconds { get => TimelineTime.ToSeconds(SourceStartFrame); init => SourceStartFrame = TimelineTime.ToFrames(value); }
    [JsonIgnore]
    public double DurationSeconds { get => TimelineTime.ToSeconds(DurationFrames); init => DurationFrames = TimelineTime.ToFrames(value); }
    public double GainDb { get; init; }
    public double Pan { get; init; }
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    public bool Muted { get; init; }
    public bool Loop { get; init; }
    public IReadOnlyList<VolumeEnvelopePoint> Envelope { get; init; } = Array.Empty<VolumeEnvelopePoint>();
    public IReadOnlyList<RetainedWordRange> RetainedWordRanges { get; init; } = Array.Empty<RetainedWordRange>();
    public IReadOnlyList<WordTiming> AlignedWords { get; init; } = Array.Empty<WordTiming>();
    public IReadOnlyList<ProductionCue> ProductionCues { get; init; } = Array.Empty<ProductionCue>();
}

public sealed record MixTranscriptEntry(Guid ClipId, string SegmentId, Guid RenderId,
    double StartSeconds, IReadOnlyList<RetainedWordRange> WordRanges, string Text);

public interface IAudioTimelineService
{
    Task<AudioTimelineDocument> GetAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default);
    Task<AudioTimelineDocument> ApplyAsync(string projectRoot, AudioTimelineScope scope, string? chapterId,
        long expectedRevision, IReadOnlyList<TimelineEdit> edits, CancellationToken cancellationToken = default);
    Task<MixEligibilityResult> ValidateAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default);
    Task<MixRenderPlan> BuildPlanAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default);
    Task<MixRenderPlan> BuildPreviewPlanAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default);
}

public sealed class TimelineRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Timeline revision {expectedRevision} no longer matches current revision {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
