namespace IssueLab.VoiceBench.Core;

public sealed record VoiceBenchProjectDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid ProjectId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public AudioStandard AudioStandard { get; init; } = AudioStandard.NarrationDefault;

    public List<string> SegmentOrder { get; init; } = [];

    public List<SegmentDocument> Segments { get; init; } = [];

    public List<VoicePreset> VoicePresets { get; init; } = [];

    public string? DefaultVoicePresetId { get; init; }

    public List<PronunciationEntry> Lexicon { get; init; } = [];
}

public sealed record SegmentDocument
{
    public string SegmentId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string ScriptRelativePath { get; init; } = string.Empty;

    public SegmentStatus Status { get; init; } = SegmentStatus.Draft;

    public string SourceSha256 { get; init; } = string.Empty;

    public string? VoicePresetId { get; init; }

    public string? Directions { get; init; }

    public Guid? ApprovedRenderId { get; init; }

    public List<RenderRecord> RenderHistory { get; init; } = [];
}

public sealed record RenderRecord
{
    public Guid RenderId { get; init; } = Guid.NewGuid();

    public int TakeNumber { get; init; }

    public RenderStatus Status { get; init; } = RenderStatus.Generated;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string Voice { get; init; } = string.Empty;

    public string SourceSha256 { get; init; } = string.Empty;

    public string SubmittedTextSha256 { get; init; } = string.Empty;

    public string DirectionsSha256 { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    /// <summary>
    /// Records the source revision against which an operator approved this take. This may
    /// differ from <see cref="SourceSha256"/> only after an explicit stale-take approval.
    /// </summary>
    public string? ApprovalSourceSha256 { get; init; }

    public string RawAudioRelativePath { get; init; } = string.Empty;

    public string? ProcessedAudioRelativePath { get; init; }

    public TimeSpan Duration { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public double? IntegratedLufs { get; init; }

    public double? TruePeakDbtp { get; init; }

    public string AudioSha256 { get; init; } = string.Empty;

    public string? ProviderResponseId { get; init; }
}

public sealed record VoicePreset
{
    public string PresetId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string Voice { get; init; } = string.Empty;

    public string? Directions { get; init; }

    public List<Guid> AuditionRenderIds { get; init; } = [];
}

public sealed record AudioStandard
{
    public static AudioStandard NarrationDefault { get; } = new();

    public int SampleRate { get; init; } = 48_000;

    public int Channels { get; init; } = 1;

    public int PcmBitsPerSample { get; init; } = 24;

    public double IntegratedLufs { get; init; } = -16;

    public double TruePeakDbtp { get; init; } = -1;

    public int PauseMilliseconds { get; init; } = 500;
}

public sealed record PronunciationEntry
{
    public string Written { get; init; } = string.Empty;

    public string Spoken { get; init; } = string.Empty;

    public bool MatchCase { get; init; }

    public bool WholeWord { get; init; } = true;
}

public enum SegmentStatus
{
    Draft,
    Ready,
    Rendering,
    Rendered,
    Approved,
    Stale,
    PickupRequired,
}

public enum RenderStatus
{
    Generated,
    Approved,
    Stale,
    Failed,
    Cancelled,
}
